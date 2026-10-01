import { useCallback, useEffect, useRef, useState } from 'react';
import {
  ApiError,
  getPartyGuestbook,
  type PartyGuestbookPage as GuestbookPage,
} from '@nubarca/api-client';
import { useI18n } from '../i18n';
import { PartyGuestbookComposer } from './PartyGuestbookComposer';
import { PartyGuestbookMemoryCard } from './PartyGuestbookMemoryCard';
import '../pages/PartyGuestbook.css';

// THE BOOK ITSELF: the memories in it, and the way to add one.
//
// One component behind two doors. A guest reaches it from the contribution
// page, on the upload token, beside the photographs and the greeting; anybody
// holding the party's own link reaches the same book at /party/<token>/
// guestbook, on the view token, which is how a keepsake stays readable after
// the evening. Both write with the token they arrived on, and the server
// accepts either — so this needs to know nothing about which door was used.
//
// This file only ORCHESTRATES: it reads the book and decides whether the page
// shows the book or the composer. Drawing a memory is PartyGuestbookMemoryCard's
// job and making one is PartyGuestbookComposer's, so either can be reused
// without this page — a television drawing the book needs the first and none
// of the rest.
//
// A book, not a feed: one memory per row at every width, no counts, no
// reactions. Reading and writing are separate questions: `canWrite` hides the
// call to leave a memory when the party is over while the pages stay.

type Status =
  | { kind: 'loading' }
  | { kind: 'ready'; page: GuestbookPage }
  | { kind: 'unavailable' }
  | { kind: 'error' };

export function PartyGuestbookPanel({
  token, remaining = null,
}: {
  token: string;
  /**
   * Memories this guest has left, when the caller already knows — the
   * contribution page does, from its session. Null asks the book itself.
   */
  remaining?: number | null;
}) {
  const { t } = useI18n();
  const [status, setStatus] = useState<Status>({ kind: 'loading' });
  const [composing, setComposing] = useState(false);
  // What the last publish said is left, which is newer than either the
  // caller's session or the page read before it.
  const [leftAfterPublish, setLeftAfterPublish] = useState<number | null | undefined>(undefined);
  const ctaRef = useRef<HTMLButtonElement>(null);
  const returnFocus = useRef(false);

  const load = useCallback((signal?: AbortSignal, quiet = false) => {
    if (!quiet) setStatus({ kind: 'loading' });
    getPartyGuestbook(token, signal)
      .then((page) => { if (!signal?.aborted) setStatus({ kind: 'ready', page }); })
      .catch((err: unknown) => {
        if (signal?.aborted || quiet) return;
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

  // Back from the composer, focus returns to where the guest left from.
  useEffect(() => {
    if (!composing && returnFocus.current) {
      returnFocus.current = false;
      ctaRef.current?.focus();
    }
  }, [composing]);

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

  const left = leftAfterPublish !== undefined
    ? leftAfterPublish
    : remaining ?? status.page.remaining ?? null;
  const canStart = status.page.canWrite && left !== 0;

  if (composing) {
    return (
      <PartyGuestbookComposer
        token={token}
        remaining={left}
        onPublished={(submission) => {
          setLeftAfterPublish(submission.remaining ?? null);
          // Quietly, behind the success screen: the book is reloaded so the
          // new memory is there when the guest goes back to it.
          load(undefined, true);
        }}
        onClose={() => { returnFocus.current = true; setComposing(false); }}
      />
    );
  }

  return (
    <>
      {status.page.canWrite ? (
        <div className="guestbook-invite" data-testid="party-guestbook-invite">
          <p className="guestbook-invite-help">{t('partyGuestbookPublic.leaveMemoryHelp')}</p>
          {left !== null && (
            <p className="party-contribution-quota" data-testid="party-guestbook-remaining" aria-live="polite">
              {t('partyGuestbookPublic.entriesLeft', { count: String(left) })}
            </p>
          )}
          <button
            ref={ctaRef}
            type="button"
            className="party-contribution-primary"
            onClick={() => setComposing(true)}
            disabled={!canStart}
            data-testid="party-guestbook-start"
          >
            {t('partyGuestbookPublic.leaveMemory')}
          </button>
        </div>
      ) : (
        <p className="party-contribution-intro" data-testid="party-guestbook-closed">
          {t('partyGuestbookPublic.closed')}
        </p>
      )}

      <section className="party-guestbook-entries" aria-labelledby="party-guestbook-entries-title">
        <h2 id="party-guestbook-entries-title" className="party-guestbook-entries-title">
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
                <PartyGuestbookMemoryCard memory={entry} testId={`party-guestbook-memory-${entry.id}`} />
              </li>
            ))}
          </ul>
        )}
      </section>
    </>
  );
}
