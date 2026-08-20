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
import { mapViewerRemoteEvent } from '../../video/remoteMap';
import { useScreenAwake } from '../../lib/useScreenAwake';
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
// Modes, and what owns the remote in each:
//   PHOTO → LEFT/RIGHT previous/next, play/pause toggles the slideshow.
//   VIDEO → SELECT / play-pause toggles playback, REWIND / FAST_FORWARD (and
//           LEFT/RIGHT) seek, UP/DOWN change item.
//   BACK  → always navigation: stop playback, return to the grid. It is never
//           spent as a playback control.
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
}

export function PersonalMediaViewer({
  items, startIndex, totalCount, hasMore, onNeedMore, onClose,
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

  // Show it briefly on entry so the viewer knows where they are, then let the
  // shared idle window take it away. Runs once per viewer, NOT per item.
  useEffect(() => { showChrome(); }, [showChrome]);

  const clamped = Math.min(index, Math.max(0, items.length - 1));
  const item = items[clamped];
  const isVideo = item?.kind === 'video';

  // Keep the panel awake while media is on screen. Unmounting the viewer (exit,
  // lock, revocation, session loss) always releases through the hook's cleanup.
  useScreenAwake(true);

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
  useEffect(() => {
    if (!slideshow || isVideo) return;
    const timer = setTimeout(goNext, SLIDE_MS);
    return () => clearTimeout(timer);
  }, [slideshow, isVideo, clamped, goNext]);

  // The viewer owns the WHOLE remote while it is up.
  const onTVEvent = useCallback((evt: HWEvent) => {
    if (!evt || evt.eventKeyAction === 0) return;
    switch (mapViewerRemoteEvent(evt.eventType, isVideo)) {
      case 'prev': goPrev(); break;
      case 'next': goNext(); break;
      case 'toggle-play':
        if (isVideo) controlsRef.current?.togglePlay();
        else setSlideshow((p) => !p);
        break;
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
  }, [isVideo, goNext, goPrev, chrome]);
  useTVEventHandler(onTVEvent);

  // BACK leaves playback. Stopping FIRST is the point: the grid must never be
  // drawn over a video that is still audible.
  useEffect(() => {
    const onBackPress = () => {
      // BACK stays NAVIGATION here. Unlike the album viewer, this one has no
      // focusable overlay controls to dismiss first, so swallowing a BACK to
      // hide decoration would make leaving the viewer take two presses.
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
          {!isVideo && slideshow && (
            <View style={[styles.pill, { top: inset.y }]} pointerEvents="none">
              <Text style={styles.pillText}>{t('viewer.slideshow')}</Text>
            </View>
          )}
        </>
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
});
