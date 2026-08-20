import { useCallback, useEffect, useRef, useState } from 'react';
import * as Updates from 'expo-updates';
import {
  BackHandler,
  Pressable,
  StyleSheet,
  Text,
  View,
  useTVEventHandler,
  useWindowDimensions,
  type HWEvent,
} from 'react-native';
import { colors, overlayQrSize, overscan, spacing } from '../theme';
import {
  clearTvActiveFaceSearch,
  getTvActiveFaceSearch,
  listTvAlbumItems,
  type TvAlbumItem,
} from '../api/tv';
import { ApiError, loadTvMedia } from '../api/client';
import { SlideImage } from '../components/SlideImage';
import {
  TvVideoPlayer, TV_VIDEO_SEEK_SECONDS,
  type TvVideoControls, type TvVideoReadyState,
} from '../components/TvVideoPlayer';
import { actionableEventType } from '../lib/remoteEvent';
import { mapViewerRemoteEvent } from '../video/remoteMap';
import { FaceFilterIndicator } from '../components/FaceFilterIndicator';
import { OverlayQrCorners } from '../components/OverlayQrCorners';
import { useMenuOverlay } from '../lib/useMenuOverlay';
import { useScreenAwake } from '../lib/useScreenAwake';
import { shouldKeepPhotoSlideshowAwake } from '../video/wakePolicy';
import { useHostActive } from '../lib/useHostActive';
import { remapIndexById, sameItemIds } from '../lib/liveItems';
import {
  photoRotationActive, photoSlideMs, resolvePlayPause, shouldArmPreparingGrace,
  videoPlaybackProps, VIDEO_PREPARING_GRACE_MS, type PartySlideshowTiming,
} from '../lib/partySlideshow';
import { useI18n } from '../i18n';
import { tvDebug } from '../debug';

// Live-refresh interval for a PartyMode album's items (10-20s band).
const PARTY_ITEMS_POLL_MS = 15_000;

// Poll interval for the album's active party face filter (matches the grid).
const FACE_SEARCH_POLL_MS = 6_000;

interface Props {
  items: TvAlbumItem[];
  startIndex: number;
  autoPlay?: boolean;
  onClose: () => void;
  // Present when opened from a PartyMode album: enables live refresh of the
  // slideshow so guest uploads appear mid-playback, and the active face-filter
  // poll (the slideshow narrows to the matching subset while one is active).
  albumId?: string;
  albumName?: string;
  partyEnabled?: boolean;
  partyUrl?: string | null;
  partyUploadUrl?: string | null;
  // Owner-configured party slideshow timing, refreshed by the same poll that
  // brings new guest uploads. null for a non-party album (historical timing).
  partySlideshow?: PartySlideshowTiming | null;
  onSessionInvalid?: () => void;
}

// Slideshow / single-item viewer. Remote-first: the D-pad drives it directly.
//
// Interaction model (consistent with the album grid):
//  - Overlay HIDDEN (default): ONLY the photo. LEFT = previous, RIGHT = next,
//    SELECT = start / pause / resume the slideshow, MENU = show the overlay,
//    BACK = exit to the grid. The play/pause media key and REWIND /
//    FAST_FORWARD are ACCELERATORS for exactly those actions, never additional
//    features: a remote with only the five-way keys must lose nothing, and
//    SELECT used to be a no-op here, which meant a slideshow could not be
//    started at all on a remote without a transport key.
//  - Overlay VISIBLE: purely INFORMATIONAL — party QR cards bottom-left/right
//    and a small centered playback-state + "current / total" pill at the top
//    (no buttons or filenames, nothing that can clip).
//    LEFT/RIGHT keep changing the photo (the counter updates live), MENU hides
//    it, BACK hides it first, and it auto-hides after ~10s of inactivity.
//
// Images use the aspect-preserving SlideImage (MEDIUM preview / poster, never
// the original). When playing, advances every SLIDE_MS and loops; manual
// Prev/Next re-arms that timer. For a PartyMode album the item list is polled:
// new uploads append and the CURRENT item stays visible (tracked by id), so
// playback is not reset.
export function ViewerScreen({
  items: initialItems, startIndex, autoPlay = false, onClose,
  albumId, albumName, partyEnabled = false, partyUrl = null, partyUploadUrl = null,
  partySlideshow = null, onSessionInvalid,
}: Props) {
  const { t } = useI18n();
  const { width, height } = useWindowDimensions();
  const inset = overscan(width, height);
  const qrSize = overlayQrSize(height);
  // KEEP-AWAKE, exactly once and only when something is actually moving.
  //
  // This used to be `useScreenAwake(true)` for the viewer's whole lifetime,
  // which kept a television lit because a picture was on screen. The platform's
  // own ambient/screensaver behaviour exists precisely to stop that.
  //
  // Video is NOT here on purpose: expo-video's keepScreenOnWhilePlaying already
  // tracks real playback, so adding a second lock would create two authorities
  // and the redundant one is always the one that gets stuck holding it.
  // The FULL album list (live-refreshed for a party album). When opened from a
  // face-filtered grid this starts as the filtered snapshot and is corrected by
  // the immediate party refresh below.
  const [items, setItems] = useState(initialItems);
  const [index, setIndex] = useState(startIndex);
  const [playing, setPlaying] = useState(autoPlay);
  // Which KIND of session this is. Opening with autoplay starts a rotating
  // slideshow; selecting one tile opens a manual view of it. The distinction
  // decides who owns playback — see lib/partySlideshow. A manual PHOTO viewer
  // can still be promoted with the play key, which is how it has always
  // behaved, but that is now an explicit transition rather than something
  // inferred from `playing` happening to become true.
  const [slideshowMode, setSlideshowMode] = useState(autoPlay);

  // The one keep-awake decision (video/wakePolicy.ts). True only while a photo
  // slideshow is actually rotating in the foreground.
  const hostActive = useHostActive();
  // Face-filter mode (polled below): while active, LEFT/RIGHT/auto-advance
  // navigate ONLY the matching subset; the search id is needed to delete the
  // search on BACK. Starting the slideshow from a filtered grid lands here too
  // (the immediate poll re-adopts the same active filter).
  // Timing is STATE, not just a prop: the party poll below refreshes it, so an
  // owner changing the duration mid-party takes effect on the next poll without
  // the viewer closing, reopening or losing its place.
  const [timing, setTiming] = useState<PartySlideshowTiming | null>(partySlideshow);
  // Typed readiness of the CURRENT video, for the preparing-grace policy.
  const [videoReady, setVideoReady] = useState<TvVideoReadyState>('probing');
  const [faceFilter, setFaceFilter] = useState<{
    searchId: string;
    faceThumbnailUrl: string | null;
    items: TvAlbumItem[];
  } | null>(null);
  const {
    visible: overlayVisible,
    visibleRef: overlayVisibleRef,
    toggle: toggleOverlay,
    hide: hideOverlay,
    bump: bumpOverlay,
  } = useMenuOverlay();

  // What the slideshow actually navigates: the matching subset in face-filter
  // mode, the full album otherwise. `index` is relative to THIS list.
  const displayItems = faceFilter?.items ?? items;
  const item = displayItems[Math.min(index, Math.max(0, displayItems.length - 1))];

  // Applied here, where the current item's kind is finally known. Video is
  // absent by design — expo-video's keepScreenOnWhilePlaying owns that case.
  useScreenAwake(shouldKeepPhotoSlideshowAwake({
    kind: item?.mediaType === 'video' ? 'video' : 'photo',
    slideshowPlaying: slideshowMode && playing,
    hostActive,
  }));

  // Refs so the (stable) TV-event / poll callbacks read the latest values.
  const itemsRef = useRef(items);
  const indexRef = useRef(index);
  const displayItemsRef = useRef(displayItems);
  const faceFilterRef = useRef(faceFilter);
  useEffect(() => { itemsRef.current = items; }, [items]);
  useEffect(() => { indexRef.current = index; }, [index]);
  useEffect(() => { faceFilterRef.current = faceFilter; }, [faceFilter]);
  displayItemsRef.current = displayItems;

  // Video-hls slice 4: whether the CURRENT item is a video decides the remote
  // mapping (see src/video/remoteMap.ts); the player exposes play/pause + seek
  // through this ref while a video is mounted.
  const isVideoRef = useRef(false);
  isVideoRef.current = item?.mediaType === 'video';
  const slideshowModeRef = useRef(slideshowMode);
  slideshowModeRef.current = slideshowMode;
  const videoControlsRef = useRef<TvVideoControls | null>(null);

  const goNext = useCallback(() => {
    const len = displayItemsRef.current.length;
    setIndex((i) => (len === 0 ? 0 : (i + 1) % len));
  }, []);

  const goPrev = useCallback(() => {
    const len = displayItemsRef.current.length;
    setIndex((i) => (len === 0 ? 0 : (i - 1 + len) % len));
  }, []);

  const togglePlay = useCallback(() => setPlaying((p) => !p), []);

  // Face-filter transitions preserve the current photo: when the filter
  // activates mid-slideshow and the current photo matches, stay on it (else
  // move to the first matching photo); when it exits, remain on the same photo
  // within the restored full album where possible.
  const prevFaceFilterRef = useRef(faceFilter);
  useEffect(() => {
    const prev = prevFaceFilterRef.current;
    prevFaceFilterRef.current = faceFilter;
    if (prev === faceFilter) return;
    const prevItems = prev?.items ?? itemsRef.current;
    const nextItems = faceFilter?.items ?? itemsRef.current;
    if (nextItems.length === 0) return;
    const clamped = Math.min(indexRef.current, Math.max(0, prevItems.length - 1));
    const currentId = prevItems[clamped]?.id;
    let nextIndex = currentId ? nextItems.findIndex((it) => it.id === currentId) : -1;
    if (nextIndex < 0) nextIndex = 0;
    setIndex(nextIndex);
  }, [faceFilter]);

  // Remote handling. TVEventHandler is a global native listener (dispatched from
  // ReactRootView.dispatchKeyEvent), so events reach us regardless of which view
  // is focused.
  //
  // IMPORTANT (Fire TV / Android TV): the native side dispatches remote events on
  // KEY UP ONLY (eventKeyAction === 1); key-down events exist only behind a
  // react-native feature flag. So we act on everything EXCEPT an explicit
  // key-down (0). Long presses arrive as 'longLeft'/'longRight'/... and are
  // mapped like their short variants.
  //
  // MENU is the ONLY command that shows/hides the overlay (KEYCODE_MENU → the
  // 'menu' eventType — verified in ReactAndroidHWInputDeviceHelper's
  // KEY_EVENTS_ACTIONS map). The overlay has NO focusable controls, so
  // LEFT/RIGHT always drive the slideshow — visible overlay included (the
  // counter updates live); any activity just re-arms its auto-hide window.
  // SELECT starts / pauses / resumes the slideshow — the five-way route.
  // The event→action mapping is the pure, unit-tested src/video/remoteMap.ts:
  //   PHOTO — LEFT/RIGHT prev/next, SELECT (and playPause) start/pause/resume
  //           the slideshow, REWIND/FAST_FORWARD are prev/next accelerators.
  //   VIDEO — SELECT/playPause toggle playback, LEFT/RIGHT seek ±10 s,
  //           REWIND/FAST_FORWARD seek, UP/DOWN change item.
  const onTVEvent = useCallback((evt: HWEvent) => {
    const eventType = actionableEventType(evt);
    if (eventType === null) return; // one action per physical press
    const type = evt.eventType;
    const isVideo = isVideoRef.current;
    tvDebug('remote', type, 'video', isVideo, 'overlay', overlayVisibleRef.current);
    switch (mapViewerRemoteEvent(type, isVideo)) {
      case 'toggle-overlay':
        toggleOverlay();
        return;
      case 'next': goNext(); break;
      case 'prev': goPrev(); break;
      case 'toggle-play':
        // In a slideshow there is exactly ONE play state, and it governs the
        // photo countdown and the video player alike. Talking to the player
        // directly here is what previously let a video sit paused under a pill
        // still reading "playing" — and then made the next photo auto-advance.
        switch (resolvePlayPause({ slideshowMode: slideshowModeRef.current, isVideo })) {
          case 'toggle-slideshow':
            togglePlay();
            break;
          case 'toggle-video-player':
            videoControlsRef.current?.togglePlay();
            break;
          case 'promote-to-slideshow':
            setSlideshowMode(true);
            setPlaying(true);
            break;
        }
        break;
      case 'seek-back':
        videoControlsRef.current?.seekBy(-TV_VIDEO_SEEK_SECONDS);
        break;
      case 'seek-forward':
        videoControlsRef.current?.seekBy(TV_VIDEO_SEEK_SECONDS);
        break;
      case 'none':
        // Still counts as activity for the overlay auto-hide.
        break;
    }
    bumpOverlay();
  }, [goNext, goPrev, togglePlay, toggleOverlay, bumpOverlay, overlayVisibleRef]);
  useTVEventHandler(onTVEvent);

  // BACK while face-filter mode is active: delete THIS search server-side
  // (id-scoped + idempotent — a concurrent phone cancel or a newer activation is
  // never disturbed) and restore the full-album slideshow; the transition effect
  // above keeps the same photo when possible. Only the NEXT press closes the
  // viewer (existing behavior).
  const exitFaceFilter = useCallback(() => {
    const current = faceFilterRef.current;
    setFaceFilter(null);
    if (albumId) {
      void clearTvActiveFaceSearch(albumId, current?.searchId).catch(() => { /* best effort */ });
    }
  }, [albumId]);

  // Hardware Back: hide the overlay first, then exit face-filter mode, then
  // exit to the grid.
  useEffect(() => {
    const onBack = () => {
      if (overlayVisibleRef.current) {
        hideOverlay();
        return true;
      }
      if (faceFilterRef.current) {
        exitFaceFilter();
        return true;
      }
      onClose();
      return true;
    };
    const sub = BackHandler.addEventListener('hardwareBackPress', onBack);
    return () => sub.remove();
  }, [onClose, hideOverlay, overlayVisibleRef, exitFaceFilter]);

  // Auto-advance while playing; re-armed on each index change (so manual Prev/Next
  // resets it) and loops at the end of the CURRENT display list. A VIDEO is
  // exempt from the timer: it advances when playback ENDS (TvVideoPlayer's
  // onEnded → goNext), never mid-play.
  const currentIsVideo = item?.mediaType === 'video';
  const photoMs = photoSlideMs(partyEnabled ? timing : null);
  const rotating = photoRotationActive({ slideshowMode, playing });
  useEffect(() => {
    if (!rotating || displayItems.length === 0 || currentIsVideo) return;
    const timer = setTimeout(() => {
      const len = displayItemsRef.current.length;
      setIndex((i) => (len === 0 ? 0 : (i + 1) % len));
    }, photoMs);
    return () => clearTimeout(timer);
    // `photoMs` is a dependency, so a timing change re-arms the CURRENT photo's
    // timer in place rather than moving to another item.
  }, [rotating, index, displayItems.length, currentIsVideo, photoMs]);

  // A video that cannot become playable must not freeze an autoplaying party
  // wall. The grace window is armed only while the slideshow is actually
  // rotating; paused or manual viewing keeps whatever the user is looking at,
  // and the item is retried normally the next time round.
  const armPreparingGrace = shouldArmPreparingGrace({
    slideshowMode,
    partyEnabled,
    playing,
    isVideo: currentIsVideo,
    videoReady: videoReady === 'ready',
  });
  useEffect(() => {
    if (!armPreparingGrace) return;
    const timer = setTimeout(() => goNext(), VIDEO_PREPARING_GRACE_MS);
    return () => clearTimeout(timer);
  }, [armPreparingGrace, index, goNext]);

  // A new item is a new readiness question.
  useEffect(() => { setVideoReady('probing'); }, [item?.id]);

  // Warm ONLY the next and previous previews (low priority, so the current image
  // always wins the download pool). Never prefetches the whole album.
  useEffect(() => {
    if (displayItems.length < 2) return;
    const warm = (it: TvAlbumItem | undefined) => {
      const p = it ? (it.mediaType === 'video' ? it.posterUrl : it.previewUrl) : null;
      if (p) void loadTvMedia(p, { priority: 'low' }).catch(() => { /* warm-up only */ });
    };
    warm(displayItems[(index + 1) % displayItems.length]);
    warm(displayItems[(index - 1 + displayItems.length) % displayItems.length]);
  }, [index, displayItems]);

  // Live refresh for a PartyMode album (immediate + every PARTY_ITEMS_POLL_MS).
  // Maintains the FULL album list: while face-filter mode is active the display
  // list is the filter's, so only `items` updates; otherwise the current item
  // stays stable by id and new uploads append. An empty/revoked album (404) or
  // session loss (401) exits cleanly.
  useEffect(() => {
    if (!partyEnabled || !albumId) return;
    const refresh = () => {
      listTvAlbumItems(albumId)
        .then((detail) => {
          if (detail.items.length === 0) { onClose(); return; }
          // Adopt refreshed timing regardless of whether the item list moved —
          // a settings change is not an item change.
          setTiming((current) => {
            const next = detail.partySlideshow;
            if (current === next) return current;
            if (current && next
              && current.photoSeconds === next.photoSeconds
              && current.maxVideoSeconds === next.maxVideoSeconds) return current;
            return next;
          });
          const prev = itemsRef.current;
          if (sameItemIds(prev, detail.items)) return;
          if (faceFilterRef.current) {
            setItems(detail.items); // display list is the filter's — index untouched
            return;
          }
          const currentId = prev[indexRef.current]?.id;
          setItems(detail.items);
          setIndex(remapIndexById(detail.items, currentId, indexRef.current));
        })
        .catch((err) => {
          if (err instanceof ApiError && err.status === 404) { onClose(); return; }
          if (err instanceof ApiError && err.status === 401) { onSessionInvalid?.(); }
          // transient: keep the current slideshow
        });
    };
    refresh();
    const timer = setInterval(refresh, PARTY_ITEMS_POLL_MS);
    return () => clearInterval(timer);
  }, [partyEnabled, albumId, onClose, onSessionInvalid]);

  // Poll the album's active party face filter (same contract as the grid): only
  // an EXPLICITLY activated search arrives; a newer server-accepted activation
  // replaces the previous one; cleared/expired/deleted → full album restored.
  useEffect(() => {
    if (!partyEnabled || !albumId) {
      setFaceFilter(null);
      return;
    }
    let cancelled = false;
    const poll = () => {
      getTvActiveFaceSearch(albumId)
        .then((active) => {
          if (cancelled) return;
          setFaceFilter((prev) => {
            if (active.active && active.searchId && active.items.length > 0) {
              return prev && prev.searchId === active.searchId
                && prev.faceThumbnailUrl === active.faceThumbnailUrl
                && sameItemIds(prev.items, active.items)
                ? prev
                : {
                  searchId: active.searchId,
                  faceThumbnailUrl: active.faceThumbnailUrl,
                  items: active.items,
                };
            }
            return prev ? null : prev;
          });
        })
        .catch((err) => {
          if (err instanceof ApiError && err.status === 401) { onSessionInvalid?.(); }
          // transient / 404: keep current state
        });
    };
    poll();
    const timer = setInterval(poll, FACE_SEARCH_POLL_MS);
    return () => { cancelled = true; clearInterval(timer); };
  }, [partyEnabled, albumId, onSessionInvalid]);

  const isVideo = item?.mediaType === 'video';
  const showParty = partyEnabled && (Boolean(partyUrl) || Boolean(partyUploadUrl));

  return (
    <View style={styles.container}>
      {/* Video-hls slice 4: videos get the real player (poster + preparing
          state handled inside); photos keep the aspect-preserving SlideImage. */}
      {isVideo && item?.videoUrl ? (
        <TvVideoPlayer
          key={item.id}
          videoPath={item.videoUrl}
          posterPath={item.posterUrl ?? null}
          onEnded={goNext}
          onCapReached={goNext}
          // The cap bounds ROTATION and the controlled play state exists only
          // while something is rotating, so a video opened from the grid to
          // watch keeps its own controls and plays to its end. Both decisions
          // come from one tested policy rather than from `partyEnabled` alone.
          {...videoPlaybackProps({ slideshowMode, partyEnabled, playing, timing })}
          onReadyStateChange={setVideoReady}
          controlsRef={videoControlsRef}
        />
      ) : (
        <SlideImage path={isVideo ? item?.posterUrl ?? null : item?.previewUrl ?? null} />
      )}

      {/* Transparent full-screen focus anchor — ALWAYS mounted (the overlay has
          no focusable controls), so the screen reliably owns the D-pad on
          Android TV. SELECT is handled by the global TVEventHandler above for
          BOTH kinds — slideshow play/pause on a photo, playback play/pause on a
          video — before focus handling matters. MENU owns the overlay. */}
      <Pressable
        focusable
        hasTVPreferredFocus
        accessibilityLabel={isVideo ? t('viewer.videoNavHint') : t('viewer.navHint')}
        onPress={() => { /* handled by the global TV event mapping */ }}
        style={styles.capture}
      />

      {/* Everything below only renders while the MENU overlay is visible. */}
      {overlayVisible && (
        <>
          {showParty && (
            <OverlayQrCorners
              partyUrl={partyUrl}
              partyUploadUrl={partyUploadUrl}
              insetX={inset.x}
              insetY={inset.y}
              qrSize={qrSize}
            />
          )}

          {/* Face-filter indicator (same shared component as the grid): only
              while face-filter mode is active. Non-focusable, top-center —
              nothing existing moves. */}
          {faceFilter !== null && (
            <View style={[styles.faceIndicatorRow, { top: inset.y + 56 }]} pointerEvents="none">
              <FaceFilterIndicator
                faceThumbnailUrl={faceFilter.faceThumbnailUrl}
                albumName={albumName ?? ''}
              />
            </View>
          )}

          {/* The ONLY top chrome: playback state plus "current / total".
              It reflects the same `playing` state toggled by the media key and
              stays non-focusable, so remote semantics do not change. */}
          {displayItems.length > 0 && (
            <View style={[styles.counterPill, { top: inset.y }]} pointerEvents="none">
              <Text style={[styles.playbackText, playing ? styles.playingText : styles.pausedText]} numberOfLines={1}>
                {playing ? t('viewer.playing') : t('viewer.paused')}
              </Text>
              <Text style={styles.separatorText}>•</Text>
              <Text style={styles.counterText} numberOfLines={1}>
                {Math.min(index, displayItems.length - 1) + 1} / {displayItems.length}
              </Text>
              {!Updates.isEmbeddedLaunch && (
                <>
                  <Text style={styles.separatorText}>•</Text>
                  <Text style={styles.otaText} numberOfLines={1}>OTA ✓</Text>
                </>
              )}
            </View>
          )}
        </>
      )}
    </View>
  );
}

const styles = StyleSheet.create({
  container: { flex: 1, backgroundColor: '#05070b' },
  capture: { position: 'absolute', top: 0, left: 0, right: 0, bottom: 0 },
  faceIndicatorRow: { position: 'absolute', left: 0, right: 0, alignItems: 'center' },
  counterPill: {
    position: 'absolute',
    alignSelf: 'center',
    flexDirection: 'row',
    alignItems: 'center',
    paddingHorizontal: spacing.md,
    paddingVertical: spacing.xs,
    borderRadius: 999,
    backgroundColor: 'rgba(0,0,0,0.78)',
  },
  counterText: {
    color: colors.text,
    fontSize: 26,
    fontWeight: '800',
  },
  playbackText: {
    fontSize: 24,
    fontWeight: '900',
  },
  playingText: { color: '#7ee787' },
  pausedText: { color: '#ffd166' },
  otaText: { color: '#58d6ff', fontSize: 22, fontWeight: '900' },
  separatorText: {
    color: colors.muted,
    fontSize: 22,
    marginHorizontal: spacing.sm,
  },
});
