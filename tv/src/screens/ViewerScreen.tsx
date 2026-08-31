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
  listTvPartyMessages,
  getTvPartyPlayback,
  advanceTvPartyBoundary,
  completeTvPartyChallenge,
  type TvAlbumItem,
  type TvPartyMessage,
  type TvPartyPlayback,
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
import { PartyMessageRibbon } from '../components/PartyMessageRibbon';
import { PartyHeroMessage } from '../components/PartyHeroMessage';
import { PartyChallengeHold } from '../components/PartyChallengeHold';
import { useMenuOverlay } from '../lib/useMenuOverlay';
import { useScreenAwake } from '../lib/useScreenAwake';
import { shouldKeepPhotoSlideshowAwake, shouldRotateSlideshow } from '../video/wakePolicy';
import { useHostState } from '../lib/useHostActive';
import { remapIndexById, sameItemIds } from '../lib/liveItems';
import {
  photoSlideMs, resolvePlayPause, shouldArmPreparingGrace,
  videoPlaybackProps, VIDEO_PREPARING_GRACE_MS, type PartySlideshowTiming,
} from '../lib/partySlideshow';
import {
  beginHeroRotation, deferBoundary, discardBoundary, heroCandidates, heroEligible,
  nextHero, onMediaBoundary, remapRibbonIndex, ribbonRotating, ribbonVisible,
  sameMessages, settleBoundary,
  HERO_DURATION_MS, MESSAGES_POLL_MS, NO_BOUNDARY_DEBT, RIBBON_ROTATE_MS,
  type BoundaryDebt, type HeroRotation,
} from '../lib/partyMessages';
import { useI18n } from '../i18n';
import { tvDebug } from '../debug';

// Live-refresh interval for a PartyMode album's items (10-20s band).
const PARTY_ITEMS_POLL_MS = 15_000;

// Poll interval for the album's active party face filter (matches the grid).
const FACE_SEARCH_POLL_MS = 6_000;
const PARTY_PLAYBACK_POLL_MS = 5_000;

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

  // ONE host signal for this screen. 'inactive' is not active (timers and wake
  // locks stop for a blip, which is free); only a genuine 'background' changes
  // what the USER asked for.
  const hostState = useHostState();
  const hostActive = hostState === 'active';
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
  // The guest MESSAGE feed, polled separately and faster than the media list.
  // It is its OWN state, never merged into `items`: a message is not a slide,
  // and TvAlbumItem stays `image | video`.
  const [messages, setMessages] = useState<TvPartyMessage[]>([]);
  const [ribbonIndex, setRibbonIndex] = useState(0);
  // The Hero currently holding the screen, or null. While it is non-null the
  // media index is FROZEN — see the boundary handler for why that is what makes
  // the carousel resume with nothing lost and nothing repeated.
  const [hero, setHero] = useState<TvPartyMessage | null>(null);
  const [partyPlayback, setPartyPlayback] = useState<TvPartyPlayback | null>(null);
  const partyPlaybackRef = useRef<TvPartyPlayback | null>(null);
  partyPlaybackRef.current = partyPlayback;
  const heroRotationRef = useRef<HeroRotation>(beginHeroRotation());
  const boundariesSinceHeroRef = useRef(0);
  // The advance a Hero postponed. The ledger and the rule for spending it are
  // in lib/partyMessages, where the exactly-once property is a pure test rather
  // than a claim about this component's effects.
  const boundaryDebtRef = useRef<BoundaryDebt>(NO_BOUNDARY_DEBT);

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

  // ONE WakeInputs value, consumed by BOTH the wake lock and the rotation
  // timer, so the two cannot answer differently. Video is absent by design:
  // expo-video's keepScreenOnWhilePlaying owns that case.
  const wakeInputs = {
    kind: (item?.mediaType === 'video' ? 'video' : 'photo') as 'video' | 'photo',
    slideshowPlaying: slideshowMode && playing,
    hostActive,
  };
  useScreenAwake(shouldKeepPhotoSlideshowAwake(wakeInputs));

  // BACKGROUND CHANGES INTENT, not merely timers.
  //
  // Gating the timer on host state alone would restart the slideshow the moment
  // the user came back — and for a party VIDEO it would do worse: the recreated
  // player would see `playing === true` from this state and start audio by
  // itself, walking straight past shouldAutoResume(). The parent slideshow
  // state is the authority for "is playback wanted", so THAT is what a genuine
  // background transition changes.
  //
  // 'inactive' deliberately does not: a momentary overlay must not silently
  // turn the user's slideshow off.
  useEffect(() => {
    if (hostState === 'background') setPlaying(false);
  }, [hostState]);

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
  const messagesRef = useRef(messages);
  messagesRef.current = messages;
  const playingRef = useRef(playing);
  playingRef.current = playing;
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

  // EVERY point at which the slideshow would advance on its own goes through
  // here: a photo's dwell elapsing, a video ending, a video reaching its cap.
  // Manual LEFT/RIGHT deliberately does NOT — a Hero belongs to the autoplay
  // wall, and somebody pressing a direction key is steering it themselves.
  //
  // When a Hero is due the index is NOT advanced: the card is laid over the
  // media that is already there, and the advance happens when the card
  // finishes. That is what makes "the carousel resumes from exactly the right
  // place" true by construction rather than by arithmetic — there is no second
  // place that moves the index.
  const ordinaryMediaBoundary = useCallback(() => {
    const outcome = onMediaBoundary({
      boundariesSinceHero: boundariesSinceHeroRef.current,
      eligible: heroEligible({
        partyEnabled,
        slideshowMode: slideshowModeRef.current,
        playing: playingRef.current,
        faceFilterActive: faceFilterRef.current !== null,
        candidateCount: heroCandidates(messagesRef.current).length,
      }),
    });
    boundariesSinceHeroRef.current = outcome.boundariesSinceHero;

    if (outcome.kind === 'hero') {
      const pick = nextHero(heroRotationRef.current, messagesRef.current);
      if (pick.message !== null) {
        heroRotationRef.current = pick.rotation;
        // The advance is now OWED. Whatever ends the card — its timer, the
        // server withdrawing it, or the wall resuming after a pause — the debt
        // is settled by the single consumer below.
        boundaryDebtRef.current = deferBoundary();
        setHero(pick.message);
        return;
      }
    }
    goNext();
  }, [partyEnabled, goNext]);

  const handleMediaBoundary = useCallback(() => {
    if (!partyEnabled || !albumId) { ordinaryMediaBoundary(); return; }
    void advanceTvPartyBoundary(albumId)
      .then((snapshot) => {
        setPartyPlayback(snapshot);
        if (snapshot.mode !== 'challenge_hold') ordinaryMediaBoundary();
      })
      .catch(() => ordinaryMediaBoundary());
  }, [partyEnabled, albumId, ordinaryMediaBoundary]);

  const dismissHeroForManualNavigation = useCallback(() => {
    boundaryDebtRef.current = discardBoundary();
    setHero(null);
  }, []);

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
    const challengeHeld = partyPlaybackRef.current?.mode === 'challenge_hold';
    const isVideo = challengeHeld ? false : isVideoRef.current;
    tvDebug('remote', type, 'video', isVideo, 'overlay', overlayVisibleRef.current);
    switch (mapViewerRemoteEvent(type, isVideo)) {
      case 'toggle-overlay':
        toggleOverlay();
        return;
      // Manual navigation is the person taking over. Any Hero on screen comes
      // down with it, and the advance the card was holding is discarded — they
      // have just chosen where to be, and settling an old debt on top of that
      // would skip the item they asked for.
      case 'next':
        if (challengeHeld && albumId) {
          void completeTvPartyChallenge(albumId).then((snapshot) => {
            setPartyPlayback(snapshot);
            if (snapshot.mode === 'media') goNext();
          }).catch(() => { /* persisted hold remains authoritative */ });
          return;
        }
        dismissHeroForManualNavigation(); goNext(); break;
      case 'prev':
        if (challengeHeld) return;
        dismissHeroForManualNavigation(); goPrev(); break;
      case 'toggle-play':
        if (challengeHeld) return;
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
  }, [goNext, goPrev, togglePlay, toggleOverlay, bumpOverlay, overlayVisibleRef, albumId,
    dismissHeroForManualNavigation]);
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
  // The SAME inputs as the wake lock — including host state. This previously
  // called photoRotationActive({ slideshowMode, playing }), which knows nothing
  // about the foreground, so photographs kept advancing behind HOME while the
  // wake lock (correctly) let go. Two lifecycle authorities for one behaviour.
  // `hero === null` is part of the condition, so a Hero HOLDS the wall rather
  // than racing the dwell timer: the photo underneath does not advance out from
  // behind the card.
  const challengeHeld = partyPlayback?.mode === 'challenge_hold';
  const rotating = shouldRotateSlideshow(wakeInputs) && !currentIsVideo && hero === null && !challengeHeld;
  useEffect(() => {
    if (!rotating || displayItems.length === 0 || currentIsVideo) return;
    const timer = setTimeout(handleMediaBoundary, photoMs);
    return () => clearTimeout(timer);
    // `photoMs` is a dependency, so a timing change re-arms the CURRENT photo's
    // timer in place rather than moving to another item.
  }, [rotating, index, displayItems.length, currentIsVideo, photoMs, handleMediaBoundary]);

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

  // A Hero holds the screen for a fixed time, then simply comes down. It does
  // NOT advance the wall itself — settling the deferred boundary is the single
  // consumer's job, and two callers of goNext() is exactly the double advance
  // this structure exists to make unrepresentable.
  useEffect(() => {
    if (hero === null) return;
    const timer = setTimeout(() => setHero(null), HERO_DURATION_MS);
    return () => clearTimeout(timer);
  }, [hero]);

  // A Hero the viewer no longer has any business showing comes down early.
  // Which of the two things happens to the DEFERRED ADVANCE depends on why:
  //
  //   the viewer changed what it is looking at (a guest activated a face
  //   filter, or the session stopped being a slideshow) — the deferred advance
  //   is moot. The face-filter effect re-picks the index from the new display
  //   list, and a manual session's index belongs to the person driving it, so
  //   settling an old debt here would move something out from under them.
  //
  //   the wall was merely PAUSED — the advance is still owed. Discarding it is
  //   what would strand a finished video: it can produce no further boundary,
  //   so the debt is kept and the consumer settles it when playback resumes.
  useEffect(() => {
    if (hero === null) return;
    if (faceFilter !== null || !slideshowMode) {
      boundaryDebtRef.current = discardBoundary();
      setHero(null);
      return;
    }
    if (!playing) setHero(null);
  }, [hero, faceFilter, playing, slideshowMode]);

  // THE SINGLE CONSUMER of a deferred boundary.
  //
  // Everything that ends a Hero does so by setting it to null; this is the one
  // place that then performs the advance, and it writes the settled ledger back
  // BEFORE acting on it. That is what makes "every boundary is consumed at most
  // once" a property of the structure rather than something each call site has
  // to remember — a card that times out in the same tick the poll withdraws it
  // still produces exactly one advance.
  useEffect(() => {
    const settled = settleBoundary(boundaryDebtRef.current, {
      heroVisible: hero !== null,
      slideshowMode,
      playing,
    });
    boundaryDebtRef.current = settled.debt;
    if (settled.advance) goNext();
  }, [hero, slideshowMode, playing, goNext]);

  // Live refresh of the guest MESSAGE feed. Faster than the media poll (5s vs
  // 15s) because "I typed it on my phone and it appeared on the television" is
  // the experience this feature exists to deliver, and the payload is a few
  // hundred bytes.
  //
  // Deliberately its OWN effect and its own timer: a message arriving must not
  // disturb the media slideshow, and a media refresh must not reset the ribbon.
  useEffect(() => {
    if (!partyEnabled || !albumId) {
      setMessages([]);
      return;
    }
    let cancelled = false;
    const poll = () => {
      listTvPartyMessages(albumId)
        .then((feed) => {
          if (cancelled) return;
          setMessages((prev) => (sameMessages(prev, feed.messages) ? prev : feed.messages));
          // A Hero the server has stopped sending — hidden, rejected, or its
          // party revoked — leaves the screen on this poll rather than serving
          // out its six seconds.
          setHero((current) => (current !== null
            && !feed.messages.some((m) => m.id === current.id && m.isHero)
            ? null
            : current));
        })
        .catch((err) => {
          if (err instanceof ApiError && err.status === 401) { onSessionInvalid?.(); }
          // 404 (album gone / not on TV) and transient failures both keep the
          // current feed; the media poll owns closing the viewer.
        });
    };
    poll();
    const timer = setInterval(poll, MESSAGES_POLL_MS);
    return () => { cancelled = true; clearInterval(timer); };
  }, [partyEnabled, albumId, onSessionInvalid]);

  // Keep the ribbon on the SAME message across a refresh, by id. Without this
  // the band would jump back to the first message every time anybody wrote
  // anything, which at a party is every few seconds.
  const ribbonMessageIdRef = useRef<string | undefined>(undefined);
  useEffect(() => {
    setRibbonIndex((previous) =>
      remapRibbonIndex(messages, ribbonMessageIdRef.current, previous));
  }, [messages]);

  const ribbonShown = ribbonVisible({
    partyEnabled,
    messageCount: messages.length,
    overlayVisible,
    heroVisible: hero !== null,
  });
  const ribbonMessage = messages.length > 0
    ? messages[Math.min(ribbonIndex, messages.length - 1)]
    : null;
  ribbonMessageIdRef.current = ribbonMessage?.id;

  // Rotate the band. A single message simply stays put — crossfading a message
  // into itself is a flicker carrying no information.
  const rotateRibbon = ribbonRotating({ visible: ribbonShown, messageCount: messages.length });
  useEffect(() => {
    if (!rotateRibbon) return;
    const timer = setInterval(() => {
      setRibbonIndex((i) => (i + 1) % Math.max(1, messagesRef.current.length));
    }, RIBBON_ROTATE_MS);
    return () => clearInterval(timer);
  }, [rotateRibbon]);

  // Reconnect-safe authoritative challenge state. The TV never invents a
  // challenge locally: refresh/restart asks the current Party link and restores
  // the same active card.
  useEffect(() => {
    if (!partyEnabled || !albumId) { setPartyPlayback(null); return; }
    let cancelled = false;
    const poll = () => {
      getTvPartyPlayback(albumId).then((snapshot) => {
        if (!cancelled) setPartyPlayback(snapshot);
      }).catch((err) => {
        if (err instanceof ApiError && err.status === 401) onSessionInvalid?.();
      });
    };
    poll();
    const timer = setInterval(poll, PARTY_PLAYBACK_POLL_MS);
    return () => { cancelled = true; clearInterval(timer); };
  }, [partyEnabled, albumId, onSessionInvalid]);

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
          // A video reaches a boundary at its natural end (or the owner's
          // configured cap), never early: nothing here shortens a clip to make
          // room for a message. A Hero that fell due mid-video simply waits for
          // the boundary the video was going to reach anyway.
          onEnded={handleMediaBoundary}
          onCapReached={handleMediaBoundary}
          // The cap bounds ROTATION and the controlled play state exists only
          // while something is rotating, so a video opened from the grid to
          // watch keeps its own controls and plays to its end. Both decisions
          // come from one tested policy rather than from `partyEnabled` alone.
          // A Hero is an editorial pause BETWEEN two media, not an overlay over
          // a running one. A card raised at a video's CAP would otherwise leave
          // the clip playing — audio and all — behind an opaque scrim, because
          // the cap is a boundary the slideshow observes rather than something
          // that stops the player. Withholding the controlled play intent for
          // the card's duration uses the player's existing authority instead of
          // introducing a second one; the wall then advances to the NEXT media,
          // so the paused clip is never resumed.
          {...videoPlaybackProps({
            slideshowMode, partyEnabled, playing: playing && hero === null && !challengeHeld, timing,
          })}
          onReadyStateChange={setVideoReady}
          // EXTERNAL pause reconciliation, for the CONTROLLED slideshow only.
          // When the output route disappears the player pauses for real; without
          // this the slideshow still believed it was playing, and the next
          // SELECT was spent flipping that stale `true` to `false` instead of
          // resuming — the video needed two presses.
          //
          // A manually opened video is deliberately excluded: it has no parent
          // playback intent, so inventing one here would be a second authority
          // for something the player already owns.
          onExternalPause={slideshowMode ? () => setPlaying(false) : undefined}
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

      {/* The Elegant Ribbon. Below the photograph, above nothing focusable, and
          absent entirely while the MENU overlay owns the lower corners. */}
      {ribbonShown && <PartyMessageRibbon message={ribbonMessage} />}

      {/* The Hero card, over everything except the MENU overlay. */}
      <PartyHeroMessage message={hero} />
      <PartyChallengeHold challenge={partyPlayback?.activeChallenge ?? null} />

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
