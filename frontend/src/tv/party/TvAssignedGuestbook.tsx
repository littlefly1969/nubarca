import { useState } from 'react';
import { ApiError, getTvGuestbook, type PartyGuestbookEntry } from '@nubarca/api-client';
import { useI18n } from '../../i18n';
import { PartyGuestbookTvStage } from '../../party/PartyGuestbookTvStage';
import { backoffMs } from '../semantics/assignmentView';
import { GUESTBOOK_TV_POLL_MS } from '../semantics/partyGuestbook';
import { useLatest, usePageVisible } from '../platform/hooks';
import { usePoll } from '../platform/usePoll';
import { tvLog } from '../diagnostics';
import { TvPartySurface } from './TvPartyOverlays';

// The ASSIGNED party's guest book — the presentation while the regia has put
// the book on the screen. The browser's counterpart of the app's
// PartyGuestbookScreen, and like the slideshow a THIN ADAPTER: the drawing is
// PartyGuestbookTvStage's; what "assigned" adds is only this.
//
//   * It is authorised by THIS television's session and nothing else — no game
//     grant, no party or guest token. The server answers only while its
//     presentation is the guest book; a 404 means the party moved on, and the
//     control plane is asked at once.
//   * One reading of the book at a time (usePoll): no overlapping requests,
//     a deadline on each, a resume reads at once, unmount aborts.
//   * A failed reading keeps the last frame and tries again; a fresh one is
//     always the new truth — memories join, hidden ones leave.
//   * A book with nothing visible left hands the screen back: the control
//     plane is asked, and the server's slideshow takes over.
//
// Mounted by the shell under the server's assignment key, so another party is
// another mount: nothing of the previous party's book survives.

interface Props {
  readonly albumName: string | null;
  readonly onSessionInvalid: () => void;
  /** Ask the control plane for an immediate re-read: the book says the party moved. */
  readonly onRequestAssignment: () => void;
  readonly refreshKey: unknown;
}

export function TvAssignedGuestbook({ albumName, onSessionInvalid, onRequestAssignment, refreshKey }: Props) {
  const { t } = useI18n();
  const [entries, setEntries] = useState<PartyGuestbookEntry[] | null>(null);
  const callbacks = useLatest({ onSessionInvalid, onRequestAssignment });
  // A page out of sight reads nothing; shown again, it reads at once.
  const visible = usePageVisible();

  usePoll({
    enabled: visible,
    intervalMs: GUESTBOOK_TV_POLL_MS,
    refreshKey,
    read: (signal) => getTvGuestbook(signal),
    onValue: (book) => {
      setEntries(book.entries);
      // Nothing visible left: the server's presentation is already the
      // slideshow, or is about to be. Ask now rather than at the next tick.
      if (book.entries.length === 0) callbacks.current.onRequestAssignment();
    },
    onError: (error) => {
      const status = error instanceof ApiError ? error.status : null;
      if (status === 401) {
        callbacks.current.onSessionInvalid();
        return 'stop';
      }
      if (status === 404) {
        // Not the guest book any more (or not this party): the control plane
        // knows what is, and this surface is about to be replaced.
        tvLog('tv.party.guestbook.moved');
        callbacks.current.onRequestAssignment();
      }
      // Anything else is transient: the last frame stays.
      return undefined;
    },
    retryDelayMs: (failures) => backoffMs(failures - 1),
  });

  if (entries === null || entries.length === 0) {
    return (
      <TvPartySurface
        albumName={albumName}
        message={t('tv.guestbookLoading')}
        busy={entries === null}
        testId="tv-party-guestbook-waiting"
      />
    );
  }

  return <PartyGuestbookTvStage entries={entries} testId="tv-party-guestbook" />;
}
