import { useCallback, useEffect, useState } from 'react';
import {
  ApiError,
  PARTY_GUESTBOOK_TEXT_LIMITS,
  getPartyGuestbook,
  isPartyGuestbookSubmittable,
  partyGuestbookAuthorRemaining,
  partyGuestbookBodyRemaining,
  submitPartyGuestbookEntry,
  type PartyGuestbookPage as GuestbookPage,
} from '@nubarca/api-client';
import { useI18n } from '../i18n';
import '../pages/PartyGuestbook.css';

// THE BOOK ITSELF: what is written in it, and the form for adding to it.
//
// One component behind two doors. A guest reaches it from the contribution
// page, on the upload token, beside the photographs and the greeting; anybody
// holding the party's own link reaches the same book at /party/<token>/
// guestbook, on the view token, which is how a keepsake stays readable after
// the evening. Both write with the token they arrived on, and the server
// accepts either — so this needs to know nothing about which door was used.
//
// Reading and writing are separate questions: `canWrite` closes the composer
// when the party is over while the pages stay, because a form that accepted
// nothing would be worse than no form.

type Status =
  | { kind: 'loading' }
  | { kind: 'ready'; page: GuestbookPage }
  | { kind: 'unavailable' }
  | { kind: 'error' };

export function PartyGuestbookPanel({
  token, partyTitle = null, remaining = null,
}: {
  token: string;
  /** Named in the composer's heading when the caller knows it. */
  partyTitle?: string | null;
  /**
   * Dedications this guest has left, when the caller already knows — the
   * contribution page does, from its session. Null asks the book itself.
   */
  remaining?: number | null;
}) {
  const { t } = useI18n();
  const [status, setStatus] = useState<Status>({ kind: 'loading' });

  const load = useCallback((signal?: AbortSignal) => {
    setStatus({ kind: 'loading' });
    getPartyGuestbook(token, signal)
      .then((page) => { if (!signal?.aborted) setStatus({ kind: 'ready', page }); })
      .catch((err: unknown) => {
        if (signal?.aborted) return;
        // 404 is the ONE answer for an unknown token, a revoked party and a
        // party that keeps no book. The surface says the same thing to all
        // three, exactly as every other public Party surface does.
        setStatus({ kind: err instanceof ApiError && err.status === 404 ? 'unavailable' : 'error' });
      });
  }, [token]);

  useEffect(() => {
    const ctrl = new AbortController();
    load(ctrl.signal);
    return () => ctrl.abort();
  }, [load]);

  if (status.kind === 'loading') {
    return <p className="party-contribution-intro" role="status">{t('common.loading')}</p>;
  }

  if (status.kind === 'unavailable') {
    return (
      <p className="party-contribution-intro" data-testid="party-guestbook-unavailable">
        {t('partyGuestbookPublic.unavailable')}
      </p>
    );
  }

  if (status.kind === 'error') {
    return (
      <div className="party-contribution-intro" data-testid="party-guestbook-error">
        <p role="alert">{t('partyGuestbookPublic.loadError')}</p>
        <button type="button" className="party-contribution-secondary" onClick={() => load()}>
          {t('common.retry')}
        </button>
      </div>
    );
  }

  // What the caller knows beats what the book last reported: the contribution
  // page's session is read at the same moment as the rest of its quotas.
  const left = remaining ?? status.page.remaining ?? null;

  return (
    <>
      {status.page.canWrite ? (
        <>
          {left !== null && (
            <p
              className="party-contribution-quota"
              data-testid="party-guestbook-remaining"
              aria-live="polite"
            >
              {t('partyGuestbookPublic.entriesLeft', { count: String(left) })}
            </p>
          )}
          <GuestbookComposer
            token={token}
            partyTitle={partyTitle}
            onWritten={() => load()}
          />
        </>
      ) : (
        <p className="party-contribution-intro" data-testid="party-guestbook-closed">
          {t('partyGuestbookPublic.closed')}
        </p>
      )}

      <section className="party-guestbook-entries" aria-live="polite">
        <h2 className="party-guestbook-entries-title">
          {t('partyGuestbookPublic.entriesTitle')}
        </h2>
        {status.page.entries.length === 0 ? (
          <p className="party-contribution-intro" data-testid="party-guestbook-empty">
            {t('partyGuestbookPublic.empty')}
          </p>
        ) : (
          <ul className="party-guestbook-list" data-testid="party-guestbook-list">
            {status.page.entries.map((entry) => (
              <li key={entry.id} className="party-guestbook-entry">
                {/* TEXT, always. Never dangerouslySetInnerHTML and never a
                    Markdown renderer: this is what a stranger typed. */}
                <p className="party-guestbook-body">{entry.body}</p>
                <p className="party-guestbook-author">
                  {entry.authorDisplayName ?? t('partyGuestbookPublic.anonymous')}
                </p>
              </li>
            ))}
          </ul>
        )}
      </section>
    </>
  );
}

type Phase =
  | { kind: 'writing' }
  | { kind: 'sending' }
  | { kind: 'sent'; pending: boolean };

function GuestbookComposer({
  token, partyTitle, onWritten,
}: {
  token: string;
  partyTitle: string | null;
  onWritten(): void;
}) {
  const { t } = useI18n();
  const [author, setAuthor] = useState('');
  const [body, setBody] = useState('');
  const [phase, setPhase] = useState<Phase>({ kind: 'writing' });
  const [error, setError] = useState<string | null>(null);

  const bodyRemaining = partyGuestbookBodyRemaining(body);
  const authorRemaining = partyGuestbookAuthorRemaining(author);
  const canSend = isPartyGuestbookSubmittable(body, author);
  const busy = phase.kind === 'sending';

  const send = async () => {
    if (!canSend) return;
    setPhase({ kind: 'sending' });
    setError(null);
    try {
      const result = await submitPartyGuestbookEntry(token, {
        // Sent raw: the SERVER normalises and is the authority on what is
        // stored. Trimming here too would only create a second opinion.
        authorDisplayName: author.length > 0 ? author : null,
        body,
      });
      setPhase({ kind: 'sent', pending: result.status === 'pending' });
      setAuthor('');
      setBody('');
      onWritten();
    } catch (err: unknown) {
      setPhase({ kind: 'writing' });
      if (err instanceof ApiError && err.status === 409) {
        // `guestbook_disabled`: the host closed the book while this page was
        // open. Not a mistake the guest made, and not something retrying fixes.
        setError(t('partyGuestbookPublic.disabled'));
      } else if (err instanceof ApiError && err.status === 400) {
        setError(t('partyGuestbookPublic.rejected'));
      } else if (err instanceof ApiError && err.status === 429) {
        setError(t('partyGuestbookPublic.tooMany'));
      } else {
        setError(t('partyGuestbookPublic.failed'));
      }
    }
  };

  if (phase.kind === 'sent') {
    return (
      <div className="party-dedication-sent" data-testid="party-guestbook-sent" role="status">
        <span className="party-dedication-sent-icon" aria-hidden="true">
          <svg viewBox="0 0 24 24"><path d="m6.5 12.4 3.6 3.6 7.4-7.6" /></svg>
        </span>
        <p className="party-dedication-sent-title">{t('partyGuestbookPublic.sentTitle')}</p>
        <p className="party-dedication-sent-body">
          {phase.pending
            ? t('partyGuestbookPublic.sentPending')
            : t('partyGuestbookPublic.sentVisible')}
        </p>
        <div className="party-dedication-sent-actions">
          <button
            type="button"
            className="party-contribution-primary"
            data-testid="party-guestbook-write-another"
            onClick={() => setPhase({ kind: 'writing' })}
          >
            {t('partyGuestbookPublic.writeAnother')}
          </button>
        </div>
      </div>
    );
  }

  return (
    <form
      className="party-dedication"
      data-testid="party-guestbook-form"
      onSubmit={(e) => { e.preventDefault(); void send(); }}
    >
      <h2 className="party-dedication-title">
        {partyTitle
          ? t('partyGuestbookPublic.composerFor', { party: partyTitle })
          : t('partyGuestbookPublic.composer')}
      </h2>
      <p className="party-dedication-intro">{t('partyGuestbookPublic.composerHelp')}</p>

      <label className="party-dedication-field">
        <span className="party-dedication-label">{t('partyGuestbookPublic.nameLabel')}</span>
        <input
          type="text"
          value={author}
          onChange={(e) => setAuthor(e.target.value)}
          placeholder={t('partyGuestbookPublic.namePlaceholder')}
          disabled={busy}
          data-testid="party-guestbook-name"
          // No maxLength: the browser counts UTF-16 units and would cut an
          // emoji in half at the boundary. The counter and the server agree on
          // code points instead.
          autoComplete="off"
        />
      </label>
      {authorRemaining < 0 && (
        <p className="party-dedication-error" role="alert">
          {t('partyGuestbookPublic.nameOverLimit', {
            max: String(PARTY_GUESTBOOK_TEXT_LIMITS.authorDisplayName),
          })}
        </p>
      )}

      <label className="party-dedication-field party-dedication-field--text">
        <span className="party-dedication-label">{t('partyGuestbookPublic.bodyLabel')}</span>
        <textarea
          value={body}
          onChange={(e) => setBody(e.target.value)}
          placeholder={t('partyGuestbookPublic.bodyPlaceholder')}
          rows={6}
          disabled={busy}
          data-testid="party-guestbook-body"
        />
      </label>

      <p
        className={bodyRemaining < 0 ? 'party-dedication-counter over' : 'party-dedication-counter'}
        data-testid="party-guestbook-counter"
        aria-live="polite"
      >
        {bodyRemaining >= 0
          ? t('partyGuestbookPublic.remaining', { count: String(bodyRemaining) })
          : t('partyGuestbookPublic.overLimit', {
            max: String(PARTY_GUESTBOOK_TEXT_LIMITS.body),
          })}
      </p>

      <button
        type="submit"
        className="party-contribution-primary party-dedication-submit"
        data-testid="party-guestbook-submit"
        disabled={busy || !canSend}
      >
        {busy ? t('partyGuestbookPublic.sending') : t('partyGuestbookPublic.send')}
      </button>

      {error && <p className="party-dedication-error" role="alert">{error}</p>}
    </form>
  );
}
