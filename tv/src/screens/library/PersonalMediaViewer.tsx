import { useCallback, useEffect, useRef, useState } from 'react';
import {
  BackHandler,
  StyleSheet,
  Text,
  View,
  useTVEventHandler,
  useWindowDimensions,
  type HWEvent,
} from 'react-native';
import { colors, font, overscan, spacing } from '../../theme';
import { SlideImage } from '../../components/SlideImage';
import {
  TvVideoPlayer,
  TV_VIDEO_SEEK_SECONDS,
  type TvVideoControls,
} from '../../components/TvVideoPlayer';
import { mapViewerRemoteEvent, viewerBarActions, type ViewerBarAction } from '../../video/remoteMap';
import { ViewerActionBar, ViewerFlash } from '../../components/ViewerActionBar';
import type { TvSelectionStore } from '../../lib/tvSelection';
import { useTvSelection } from '../../lib/useTvSelection';
import { useScreenAwake } from '../../lib/useScreenAwake';
import { useHostState } from '../../lib/useHostActive';
import { shouldKeepPhotoSlideshowAwake, shouldRotateSlideshow } from '../../video/wakePolicy';
import { actionableEventType } from '../../lib/remoteEvent';
import { useI18n } from '../../i18n';
import { useMenuOverlay } from '../../lib/useMenuOverlay';
import { formatPosition } from '../../personal/pagingTotals';
import type { TvPersonalMediaItem } from '../../api/personalMedia';

// The ONE Personal Area viewer, for photos and videos alike.
//
// It is a full-screen surface containing NO focusable views, which is the whole
// input-ownership story: the native focus engine has nothing to move, so this
// screen can own every directional key without competing with it. That is the
// condition under which LEFT/RIGHT is allowed to mean "seek" on a video
// (see video/remoteMap.ts) — the same event could never also drive grid focus,
// because the grid is not a focus destination while the viewer is up.
//
// Modes, and what owns the remote in each (the selection contract in
// video/remoteMap.ts):
//   PHOTO → OK selects / deselects (a running slideshow keeps running),
//           LEFT/RIGHT previous/next (pausing the slideshow), play/pause
//           starts, pauses and resumes the slideshow.
//   VIDEO → SELECT / play-pause toggles playback, REWIND / FAST_FORWARD (and
//           LEFT/RIGHT) seek, DOWN next item.
//   UP    → opens the ACTION BAR: the one place this viewer has focusable
//           views. While it is open the native focus engine owns the D-pad
//           (the bar traps it), so this screen stops reading directions.
//   BACK  → closes the action bar when it is open; otherwise navigation: stop
//           playback, return to the grid on the same item, selection intact.
//   MENU  → toggles the ambient chrome (name, counter, slideshow pill).
//   HOME  → never intercepted. It is a system action; the player's AppState
//           handling is what stops audio when the launcher takes over.
//
// AMBIENT CHROME
// --------------
// The name, the position counter and the slideshow pill used to be permanent.
// On a television that is furniture: a photo the viewer wants to look at,
// permanently captioned. They are now shown briefly on entry and then hidden by
// the SHARED useMenuOverlay idle window — the same controller the album/Party
// viewer already uses, so there is one interaction model and one timer
// implementation rather than two that drift.
//
// The timer is deliberately NOT re-armed on every slideshow advance. A 9-second
// slide under a 10-second idle window would re-arm forever and the overlay
// would simply never go away, which is the defect wearing a different hat.
// Remote ACTIVITY re-arms it; the clock advancing on its own does not.
//
// Exactly one video is ever mounted, and TvVideoPlayer is keyed by source, so
// moving between items releases the old native player before creating the new
// one. Nothing here pre-creates a player for the neighbouring item.

const SLIDE_MS = 9000;
// Ask for the next page this many items before the end of the loaded set.
const NEED_MORE_AHEAD = 3;

interface Props {
  items: TvPersonalMediaItem[];
  startIndex: number;
  // Server-authoritative total for the current query — the counter denominator,
  // never items.length.
  totalCount: number;
  hasMore: boolean;
  onNeedMore: () => void;
  // Reports the index the user ended on so the grid restores focus to it.
  onClose: (currentIndex: number) => void;
  // The Personal Area selection. Absent → OK keeps the slideshow semantics.
  selection?: TvSelectionStore | null;
}

export function PersonalMediaViewer({
  items, startIndex, totalCount, hasMore, onNeedMore, onClose, selection = null,
}: Props) {
  const { t } = useI18n();
  const { width, height } = useWindowDimensions();
  const inset = overscan(width, height);
  const [index, setIndex] = useState(startIndex);
  const [slideshow, setSlideshow] = useState(false);
  // The shared overlay controller — same timer, same MENU semantics, same
  // OVERLAY_IDLE_MS as the album viewer.
  const chrome = useMenuOverlay();
  const showChrome = chrome.show;
  useTvSelection(selection);
  const selectable = selection !== null;
  // The action bar (UP). A ref too, so the identity-stable remote handler and
  // the Back handler read the latest value without re-subscribing.
  const [barOpen, setBarOpen] = useState(false);
  const barOpenRef = useRef(false);
  barOpenRef.current = barOpen;
  // A discreet one-line confirmation of what OK just did ("✓ Selected"). The
  // key re-mounts it on every press so a repeated OK restarts its fade.
  const [flash, setFlash] = useState<{ text: string; key: number } | null>(null);
  const clearFlash = useCallback(() => setFlash(null), []);

  // Show it briefly on entry so the viewer knows where they are, then let the
  // shared idle window take it away. Runs once per viewer, NOT per item.
  useEffect(() => { showChrome(); }, [showChrome]);

  const clamped = Math.min(index, Math.max(0, items.length - 1));
  const item = items[clamped];
  const isVideo = item?.kind === 'video';

  // KEEP-AWAKE, exactly once and only while something is actually moving.
  //
  // This was `useScreenAwake(true)` for the viewer's whole lifetime, which kept
  // a television lit because a photograph was open. Video is deliberately not
  // here: expo-video's keepScreenOnWhilePlaying already tracks real playback,
  // and a second lock would be a second authority to get stuck.
  const hostState = useHostState();
  const hostActive = hostState === 'active';
  const wakeInputs = {
    kind: (isVideo ? 'video' : 'photo') as 'video' | 'photo',
    slideshowPlaying: slideshow,
    hostActive,
  };
  useScreenAwake(shouldKeepPhotoSlideshowAwake(wakeInputs));

  const itemsRef = useRef(items);
  itemsRef.current = items;
  const indexRef = useRef(clamped);
  indexRef.current = clamped;
  const hasMoreRef = useRef(hasMore);
  hasMoreRef.current = hasMore;
  const onNeedMoreRef = useRef(onNeedMore);
  onNeedMoreRef.current = onNeedMore;
  const controlsRef = useRef<TvVideoControls | null>(null);

  const goNext = useCallback(() => {
    const length = itemsRef.current.length;
    if (length === 0) return;
    setIndex((i) => {
      const current = Math.min(i, length - 1);
      if (current >= length - 1) {
        // At the end: with more pages pending stay put (the request below
        // extends the list); with the full set loaded, loop.
        return hasMoreRef.current ? current : 0;
      }
      return current + 1;
    });
  }, []);

  const goPrev = useCallback(() => {
    const length = itemsRef.current.length;
    if (length === 0) return;
    setIndex((i) => Math.max(0, Math.min(i, length - 1) - 1));
  }, []);

  // Nearing the end of the loaded set requests the next cursor page (the parent
  // ignores duplicate requests while one is in flight).
  useEffect(() => {
    if (hasMore && clamped >= items.length - 1 - NEED_MORE_AHEAD) onNeedMoreRef.current();
  }, [clamped, items.length, hasMore]);

  // Photo slideshow timer. A VIDEO is exempt: it advances when playback ends,
  // not on a clock, so a nine-second timer cannot cut a video short.
  // Same inputs as the wake lock, by construction: a slideshow that keeps
  // advancing photographs behind HOME is doing work nobody can see, and it
  // would drag the wake lock along with it.
  // Background changes INTENT, not just the timer: returning must find the
  // slideshow paused on the same item, waiting for SELECT — never restarting by
  // itself. 'inactive' does not, so a momentary overlay is not a decision.
  useEffect(() => {
    if (hostState === 'background') setSlideshow(false);
  }, [hostState]);

  const rotating = shouldRotateSlideshow(wakeInputs);
  useEffect(() => {
    if (!rotating) return;
    const timer = setTimeout(goNext, SLIDE_MS);
    return () => clearTimeout(timer);
  }, [rotating, clamped, goNext]);

  const togglePlay = useCallback(() => {
    if (isVideo) controlsRef.current?.togglePlay();
    else setSlideshow((p) => !p);
  }, [isVideo]);

  const toggleSelected = useCallback(() => {
    const id = itemsRef.current[indexRef.current]?.id;
    if (!selection || id === undefined) return;
    const text = selection.toggle(id) ? t('viewer.selected') : t('viewer.deselected');
    setFlash((prev) => ({ text, key: (prev?.key ?? 0) + 1 }));
  }, [selection, t]);

  // The viewer owns the WHOLE remote while it is up — except the D-pad and OK
  // while the action bar is open, which belong to the bar's focus scope.
  const onTVEvent = useCallback((evt: HWEvent) => {
    // ONE phase rule for the whole app (lib/remoteEvent.ts): key-down is
    // dropped, key-up and unknown-phase runtimes each act exactly once.
    const eventType = actionableEventType(evt);
    if (eventType === null) return;
    const action = mapViewerRemoteEvent(eventType, isVideo, { selectable });
    if (barOpenRef.current && action !== 'toggle-play' && action !== 'toggle-overlay') {
      chrome.bump();
      return;
    }
    switch (action) {
      // Manual navigation PAUSES a running slideshow: the person has taken
      // over, and the next photo must not be swept away nine seconds later.
      case 'prev': if (selectable) setSlideshow(false); goPrev(); break;
      case 'next': if (selectable) setSlideshow(false); goNext(); break;
      case 'toggle-play': togglePlay(); break;
      // Selecting never touches the slideshow: it keeps running.
      case 'toggle-select': toggleSelected(); break;
      case 'open-actions': setBarOpen(true); showChrome(); break;
      case 'seek-back': controlsRef.current?.seekBy(-TV_VIDEO_SEEK_SECONDS); break;
      case 'seek-forward': controlsRef.current?.seekBy(TV_VIDEO_SEEK_SECONDS); break;
      case 'toggle-overlay': chrome.toggle(); return;
      case 'none':
        break;
    }
    // Real remote activity re-arms the idle window while the chrome is up; a
    // slideshow tick does not, which is what stops a 9s slide from pinning a
    // 10s overlay open forever.
    chrome.bump();
  }, [isVideo, selectable, goNext, goPrev, chrome, togglePlay, toggleSelected, showChrome]);
  useTVEventHandler(onTVEvent);

  const onBarAction = useCallback((action: ViewerBarAction) => {
    chrome.bump();
    switch (action) {
      case 'play-pause':
        togglePlay();
        // Starting the slideshow is a request to WATCH: the bar steps aside.
        setBarOpen(false);
        break;
      case 'select': toggleSelected(); break;
      case 'prev': setSlideshow(false); goPrev(); break;
      case 'next': setSlideshow(false); goNext(); break;
      case 'clear-selection': selection?.clear(); break;
    }
  }, [chrome, togglePlay, toggleSelected, goPrev, goNext, selection]);

  // BACK leaves playback. Stopping FIRST is the point: the grid must never be
  // drawn over a video that is still audible.
  useEffect(() => {
    const onBackPress = () => {
      // The action bar is the only thing BACK dismisses first; with it closed
      // BACK is NAVIGATION — swallowing it to hide passive decoration would
      // make leaving the viewer take two presses.
      if (barOpenRef.current) { setBarOpen(false); return true; }
      controlsRef.current?.stop();
      onClose(indexRef.current);
      return true;
    };
    const sub = BackHandler.addEventListener('hardwareBackPress', onBackPress);
    return () => sub.remove();
  }, [onClose]);

  if (!item) return null;

  return (
    <View style={styles.container}>
      {isVideo && item.videoUrl !== null ? (
        <TvVideoPlayer
          videoPath={item.videoUrl}
          posterPath={item.viewerImageUrl}
          personal
          onEnded={goNext}
          controlsRef={controlsRef}
        />
      ) : (
        <SlideImage path={item.viewerImageUrl} personal />
      )}

      {/* Ambient chrome. The media underneath is never interrupted by it
          appearing or disappearing — these are absolutely positioned,
          pointer-transparent overlays, not a layout change. */}
      {chrome.visible && (
        <>
          <View style={[styles.counter, { bottom: inset.y, right: inset.x }]} pointerEvents="none">
            <Text style={styles.counterText}>{formatPosition(clamped, totalCount)}</Text>
          </View>
          <View style={[styles.name, { bottom: inset.y, left: inset.x }]} pointerEvents="none">
            <Text style={styles.nameText} numberOfLines={1}>{item.displayName}</Text>
          </View>
          {!isVideo && slideshow && !barOpen && (
            <View style={[styles.pill, { top: inset.y }]} pointerEvents="none">
              <Text style={styles.pillText}>{t('viewer.slideshow')}</Text>
            </View>
          )}
        </>
      )}

      {/* The selection mark of the item on screen: always shown while it is
          picked (not only with the chrome), so OK never looks like it did
          nothing. */}
      {selection !== null && selection.has(item.id) && (
        <View style={[styles.mark, { top: inset.y, left: inset.x }]} pointerEvents="none">
          <Text style={styles.markText}>✓</Text>
        </View>
      )}
      {flash !== null && (
        <ViewerFlash key={flash.key} text={flash.text} bottom={inset.y + 56} onDone={clearFlash} />
      )}

      {barOpen && selection !== null && (
        <ViewerActionBar
          top={inset.y}
          actions={viewerBarActions({ isVideo, selectedCount: selection.count })}
          isVideo={isVideo}
          slideshowRunning={slideshow}
          selected={selection.has(item.id)}
          selectedCount={selection.count}
          onAction={onBarAction}
          onActivity={chrome.bump}
        />
      )}
    </View>
  );
}

const styles = StyleSheet.create({
  container: {
    position: 'absolute',
    top: 0,
    left: 0,
    right: 0,
    bottom: 0,
    backgroundColor: '#000000',
  },
  counter: {
    position: 'absolute',
    paddingHorizontal: spacing.md,
    paddingVertical: spacing.xs,
    borderRadius: 999,
    backgroundColor: 'rgba(0,0,0,0.78)',
  },
  counterText: { color: colors.text, fontSize: font.caption, fontWeight: '700' },
  name: {
    position: 'absolute',
    maxWidth: '60%',
    paddingHorizontal: spacing.md,
    paddingVertical: spacing.xs,
    borderRadius: 999,
    backgroundColor: 'rgba(0,0,0,0.78)',
  },
  nameText: { color: colors.text, fontSize: font.caption },
  pill: {
    position: 'absolute',
    alignSelf: 'center',
    paddingHorizontal: spacing.md,
    paddingVertical: spacing.xs,
    borderRadius: 999,
    backgroundColor: 'rgba(0,0,0,0.78)',
  },
  pillText: { color: colors.text, fontSize: font.caption, fontWeight: '700' },
  mark: {
    position: 'absolute',
    width: 44,
    height: 44,
    borderRadius: 22,
    alignItems: 'center',
    justifyContent: 'center',
    borderWidth: 3,
    borderColor: '#ffffff',
    backgroundColor: colors.accent,
  },
  markText: { color: '#ffffff', fontSize: font.caption, fontWeight: '800' },
});
