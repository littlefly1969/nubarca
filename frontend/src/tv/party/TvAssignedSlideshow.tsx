import { useCallback, useEffect, useRef, useState } from 'react';
import { ApiError, listTvAlbumItems, type TvAlbumItems } from '@nubarca/api-client';
import { useI18n } from '../../i18n';
import { backoffMs } from '../semantics/assignmentView';
import { useLatest, usePageVisible } from '../platform/hooks';
import { startDeadline, type Deadline } from '../platform/usePoll';
import { tvLog } from '../diagnostics';
import { TvViewer } from './TvViewer';
import { TvPartySurface } from './TvPartyOverlays';

// The ASSIGNED party's slideshow — the presentation whenever no game holds the
// screen. The browser's counterpart of the app's PartySlideshowScreen, and like
// it a THIN ADAPTER over the one viewer: everything the wall does is the
// viewer's. What "assigned" adds is only this:
//
//   * it opens by itself, in autoplay, on the album the SERVER named — nobody
//     navigates to it;
//   * a party with nothing to show yet waits for its first photograph;
//   * a party whose album is gone (404) fails CLOSED through the shell — never
//     another album, never the general experience;
//   * BACK at its root does not leave the party (the owner decides that, on
//     the web): it hands back whatever the viewer passes up, which the shell
//     uses to leave fullscreen and nothing else;
//   * coming back to a page that was out of sight starts the show again,
//     because nobody is holding a remote in front of a party screen.
//
// Mounted by the shell under the server's assignment key, so another party is
// another mount: no photograph, Hero, face filter or challenge of the previous
// party survives into this one.

interface Props {
  readonly albumId: string;
  readonly albumName: string | null;
  /** BACK at the root: never leaves the assignment. */
  readonly onBack: () => void;
  /** The album answered 404: the assigned party is not readable any more. */
  readonly onGone: () => void;
  readonly onSessionInvalid: () => void;
  /** A new value re-reads everything at once (a resume). */
  readonly refreshKey: unknown;
}

type Load =
  | { kind: 'loading' }
  | { kind: 'empty' }
  | { kind: 'ready'; detail: TvAlbumItems };

/** How often an empty party is asked whether its first photograph has arrived. */
export const EMPTY_POLL_MS = 15_000;

export function TvAssignedSlideshow({ albumId, albumName, onBack, onGone, onSessionInvalid, refreshKey }: Props) {
  const { t } = useI18n();
  const [load, setLoad] = useState<Load>({ kind: 'loading' });
  const [run, setRun] = useState(0);
  const waiting = load.kind !== 'ready';
  const onEmpty = useCallback(() => setLoad({ kind: 'empty' }), []);
  const callbacks = useLatest({ onGone, onSessionInvalid });

  // Until there is something to show: load, retry what failed for a reason that
  // is not an answer, keep asking an empty party. A resume asks at once. Every
  // attempt has a deadline: a request that never answers is a transient
  // failure like any other, not a screen that waits for ever. One attempt at a
  // time — a resume cancels the one in the air before it asks again.
  useEffect(() => {
    if (!waiting) return;
    let cancelled = false;
    let timer: ReturnType<typeof setTimeout> | undefined;
    let attempt = 0;
    let inFlight: Deadline | null = null;
    const fetchItems = () => {
      const deadline = startDeadline();
      inFlight = deadline;
      listTvAlbumItems(albumId, deadline.signal)
        .then((detail) => {
          if (cancelled) return;
          if (detail.items.length === 0) {
            setLoad({ kind: 'empty' });
            timer = setTimeout(fetchItems, EMPTY_POLL_MS);
            return;
          }
          setLoad({ kind: 'ready', detail });
        })
        .catch((error: unknown) => {
          if (cancelled) return;
          const status = error instanceof ApiError ? error.status : null;
          if (status === 401) { callbacks.current.onSessionInvalid(); return; }
          if (status === 404) {
            tvLog('tv.party.gone');
            callbacks.current.onGone();
            return;
          }
          timer = setTimeout(fetchItems, backoffMs(attempt++));
        })
        .finally(() => {
          deadline.clear();
          if (inFlight === deadline) inFlight = null;
        });
    };
    fetchItems();
    return () => {
      cancelled = true;
      inFlight?.abort();
      if (timer) clearTimeout(timer);
    };
  }, [waiting, albumId, refreshKey, callbacks]);

  // BACK while there is no viewer to own the keys.
  useEffect(() => {
    if (!waiting) return;
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape' || event.key === 'Backspace' || event.key === 'BrowserBack') {
        event.preventDefault();
        onBack();
      }
    };
    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, [waiting, onBack]);

  // A page that went out of sight paused the viewer (a person's slideshow stays
  // paused); a PARTY wall starts again from a fresh viewer when it returns.
  const visible = usePageVisible();
  const wasVisible = useRef(visible);
  useEffect(() => {
    if (!wasVisible.current && visible) setRun((n) => n + 1);
    wasVisible.current = visible;
  }, [visible]);

  if (load.kind !== 'ready') {
    return (
      <TvPartySurface
        albumName={albumName}
        message={load.kind === 'empty' ? t('tv.partyWaiting') : t('tv.partyLoading')}
        busy={load.kind === 'loading'}
        testId="tv-party-slideshow-waiting"
      />
    );
  }

  const { detail } = load;
  return (
    <div className="tvd-wall" data-testid="tv-party-slideshow">
      <TvViewer
        key={run}
        items={detail.items}
        startIndex={0}
        autoPlay
        albumId={albumId}
        albumName={albumName ?? detail.name}
        partyEnabled={detail.partyEnabled}
        partyUrl={detail.partyUrl}
        partySlideshow={detail.partySlideshow ?? null}
        onClose={onBack}
        onEmpty={onEmpty}
        onGone={onGone}
        onSessionInvalid={onSessionInvalid}
        refreshKey={refreshKey}
      />
    </div>
  );
}
