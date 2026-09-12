import { useCallback, useEffect, useRef, useState } from 'react';
import { BackHandler } from 'react-native';
import { listTvAlbumItems, type TvAlbumItems } from '../api/tv';
import { ApiError } from '../api/client';
import { useI18n } from '../i18n';
import { useHostState } from '../lib/useHostActive';
import { backoffMs } from '../lib/assignmentView';
import { PartyNativeSurface } from '../components/PartyNativeSurface';
import { ViewerScreen } from './ViewerScreen';
import { tvDebug } from '../debug';

// The assigned party's SLIDESHOW — the presentation whenever no game is taking
// the screen.
//
// This is a THIN ADAPTER over the existing ViewerScreen, and deliberately
// nothing more. Photos, videos and HLS, guest uploads arriving mid-show, the
// greetings ribbon and Hero cards, the older challenge-hold playback, the live
// refresh, the dwell timings, the wake policy, the media cache and the video
// lifecycle all stay the viewer's, exactly as they are when somebody opens a
// party album by hand. What this adds is only what "assigned" means:
//
//   * it opens by itself, in autoplay, on the album the SERVER named;
//   * a party with nothing to show yet waits for its first photograph instead
//     of closing, because there is no grid to close to;
//   * a party whose album is gone (404) fails CLOSED through the parent — it
//     never falls back to another album or to the general experience;
//   * BACK at its root closes the app (onExit), because an assigned television
//     is left only by the server changing the assignment;
//   * coming back from HOME starts the show again, because nobody is holding
//     a remote in front of a party screen.
//
// Mounted and unmounted by the parent from the server's presentation: a game
// taking the screen unmounts it (and with it every player and wake lock), and
// the game finishing mounts a fresh one.

interface Props {
  readonly albumId: string;
  readonly albumName: string | null;
  /** BACK at the root of an assigned party. */
  readonly onExit: () => void;
  /** The album answered 404: the assigned party is no longer readable. */
  readonly onGone: () => void;
  readonly onSessionInvalid: () => void;
}

type Load =
  | { kind: 'loading' }
  | { kind: 'empty' }
  | { kind: 'ready'; detail: TvAlbumItems };

/** How often an empty party is asked whether its first photograph has arrived. */
const EMPTY_POLL_MS = 15_000;

export function PartySlideshowScreen({ albumId, albumName, onExit, onGone, onSessionInvalid }: Props) {
  const { t } = useI18n();
  const [load, setLoad] = useState<Load>({ kind: 'loading' });
  // Bumped to start the show again from the top — see the HOME handling below.
  const [run, setRun] = useState(0);
  const waiting = load.kind !== 'ready';
  // Stable, because the viewer's live refresh is keyed on it: a new function
  // every render would restart that poll every render.
  const onEmpty = useCallback(() => setLoad({ kind: 'empty' }), []);

  const onGoneRef = useRef(onGone);
  onGoneRef.current = onGone;
  const onSessionInvalidRef = useRef(onSessionInvalid);
  onSessionInvalidRef.current = onSessionInvalid;

  // Until there is something to show: load the album, retry what failed for a
  // reason that is not an answer, and keep asking an empty party.
  useEffect(() => {
    if (!waiting) return;
    let cancelled = false;
    let timer: ReturnType<typeof setTimeout> | undefined;
    let attempt = 0;
    const fetchItems = () => {
      listTvAlbumItems(albumId)
        .then((detail) => {
          if (cancelled) return;
          if (detail.items.length === 0) {
            setLoad({ kind: 'empty' });
            timer = setTimeout(fetchItems, EMPTY_POLL_MS);
            return;
          }
          tvDebug('party', 'slideshow-entered', detail.items.length);
          setLoad({ kind: 'ready', detail });
        })
        .catch((err: unknown) => {
          if (cancelled) return;
          if (err instanceof ApiError && err.status === 401) { onSessionInvalidRef.current(); return; }
          if (err instanceof ApiError && err.status === 404) { onGoneRef.current(); return; }
          timer = setTimeout(fetchItems, backoffMs(attempt++));
        });
    };
    fetchItems();
    return () => {
      cancelled = true;
      if (timer) clearTimeout(timer);
    };
  }, [waiting, albumId]);

  // BACK while there is no viewer to own it. The viewer registers its own
  // (overlay → face filter → onExit) once it is mounted.
  useEffect(() => {
    if (!waiting) return;
    const sub = BackHandler.addEventListener('hardwareBackPress', () => {
      onExit();
      return true;
    });
    return () => sub.remove();
  }, [waiting, onExit]);

  // The viewer's own policy is that a slideshow paused by HOME stays paused
  // until somebody presses SELECT — right for a person, wrong for a party
  // screen nobody is holding a remote for. So a return from the BACKGROUND
  // mounts a fresh viewer, which starts in autoplay like the first one did.
  const hostState = useHostState();
  const hostRef = useRef(hostState);
  useEffect(() => {
    const before = hostRef.current;
    hostRef.current = hostState;
    if (before === 'background' && hostState === 'active') setRun((n) => n + 1);
  }, [hostState]);

  if (load.kind !== 'ready') {
    return (
      <PartyNativeSurface
        albumName={albumName}
        message={load.kind === 'empty' ? t('partySlideshow.waiting') : t('partySlideshow.loading')}
        busy={load.kind === 'loading'}
        testID="party-slideshow-waiting"
      />
    );
  }

  const { detail } = load;
  return (
    <ViewerScreen
      key={run}
      items={detail.items}
      startIndex={0}
      autoPlay
      albumId={albumId}
      albumName={albumName ?? detail.name}
      partyEnabled={detail.partyEnabled}
      partyUrl={detail.partyUrl}
      partyUploadUrl={detail.partyUploadUrl}
      partySlideshow={detail.partySlideshow}
      onClose={onExit}
      onEmpty={onEmpty}
      onGone={onGone}
      onSessionInvalid={onSessionInvalid}
    />
  );
}
