import { useCallback, useEffect, useState } from 'react';
import { Link, useParams } from 'react-router';
import {
  ApiError,
  PARTY_GUESTBOOK_TEXT_LIMITS,
  getPartyGuestbook,
  getPartyGuestContext,
  isPartyGuestbookSubmittable,
  partyGuestbookAuthorRemaining,
  partyGuestbookBodyRemaining,
  submitPartyGuestbookEntry,
  type PartyGuestbookPage as GuestbookPage,
} from '@nubarca/api-client';
import { useI18n } from '../i18n';
import { PRODUCT_NAME } from '../brand/brand';
import { LanguageSwitcher } from '../components/LanguageSwitcher';
import './PartyContribution.css';
import './PartyGuestbook.css';

// PUBLIC, unauthenticated. THE PARTY'S GUEST BOOK, on the party's own view
// token — the one on the QR.
//
// It is a separate surface from the contribution page next door, and
// deliberately so. That page is where a guest gives something to the evening: a
// photograph for the album, a greeting for the screen. This is where they leave
// something for the person the party is for, to be read afterwards. The words
// on each say which, and nothing written here ever appears on a television.
//
// The composer disappears once the party is over while the pages stay: a
// keepsake outlives the evening, and a form that accepted nothing would be
// worse than no form.

const WORDMARK = {
  src: '/brand/nubarca-wordmark-on-dark-480w.png',
  width: 480,
  height: 135,
} as const;

type Status =
  | { kind: 'loading' }
  | { kind: 'ready'; page: GuestbookPage }
  | { kind: 'unavailable' }
  | { kind: 'error' };

export function PartyGuestbookPublicPage() {
  const { token } = useParams<{ token: string }>();
  const { t } = useI18n();
  const [status, setStatus] = useState<Status>({ kind: 'loading' });
  const [partyTitle, setPartyTitle] = useState<string | null>(null);

  const load = useCallback((signal?: AbortSignal) => {
    if (!token) return;
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

  // The party's NAME, so the page can say who the dedication is for. Its own
  // request because the book's route answers about the book: a failure here
  // costs the heading a name and nothing else.
  useEffect(() => {
    if (!token) return undefined;
    const ctrl = new AbortController();
    getPartyGuestContext(token, ctrl.signal)
      .then((context) => { if (!ctrl.signal.aborted) setPartyTitle(context.title); })
      .catch(() => { /* the heading falls back to a generic one */ });
    return () => ctrl.abort();
  }, [token]);

  return (
    <main className="party-contribution party-guestbook">
      <div className="party-contribution-shell">
        <div className="party-contribution-topbar">
          <img
            className="party-contribution-logo"
            src={WORDMARK.src}
            alt={PRODUCT_NAME}
            width={WORDMARK.width}
            height={WORDMARK.height}
          />
          <LanguageSwitcher className="language-switcher language-switcher-public" compact />
        </div>

        <header className="party-contribution-head">
          <h1 className="party-contribution-title">{t('partyGuestbookPublic.title')}</h1>
          <p className="party-contribution-help">
            {partyTitle
              ? t('partyGuestbookPublic.forParty', { party: partyTitle })
              : t('partyGuestbookPublic.subtitle')}
          </p>
        </header>

        {token && (
          <p className="party-guestbook-back">
            <Link to={`/party/${encodeURIComponent(token)}`}>
              ← {t('partyGuestbookPublic.backToParty')}
            </Link>
          </p>
        )}

        {status.kind === 'loading' && (
          <p className="party-contribution-intro" role="status">{t('common.loading')}</p>
        )}

        {status.kind === 'unavailable' && (
          <p className="party-contribution-intro" data-testid="party-guestbook-unavailable">
            {t('partyGuestbookPublic.unavailable')}
          </p>
        )}

        {status.kind === 'error' && (
          <div className="party-contribution-intro" data-testid="party-guestbook-error">
            <p role="alert">{t('partyGuestbookPublic.loadError')}</p>
            <button
              type="button"
              className="party-contribution-secondary"
              onClick={() => load()}
            >
              {t('common.retry')}
            </button>
          </div>
        )}

        {status.kind === 'ready' && token && (
          <>
            {status.page.canWrite ? (
              <GuestbookComposer
                token={token}
                partyTitle={partyTitle}
                onWritten={() => load()}
              />
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
                      {/* TEXT, always. Never dangerouslySetInnerHTML and never
                          a Markdown renderer: this is what a stranger typed. */}
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
        )}
      </div>
    </main>
  );
}

type Phase =
  | { kind: 'writing' }
  | { kind: 'sending' }
  | { kind: 'sent'; pending: boolean };

/**
 * The composer.
 *
 * Counts with the SAME rules the server validates by, so a guest never watches
 * the counter say "40 left" and the submit fail. Whitespace and zero-width
 * padding collapse before counting, which is why the number stops moving while
 * somebody keeps typing spaces.
 */
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
