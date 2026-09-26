import { useCallback, useEffect, useRef, useState } from 'react';
import {
  ApiError,
  advanceTvPartyBoundary,
  clearTvActiveFaceSearch,
  completeTvPartyChallenge,
  getTvActiveFaceSearch,
  getTvPartyPlayback,
  listTvAlbumItems,
  listTvPartyMessages,
  type TvAlbumItem,
  type TvPartyMessage,
  type TvPartyPlayback,
} from '@nubarca/api-client';
import { useI18n } from '../../i18n';
import { useDisplayPlatform } from '../platform/displayPlatform';
import { useLatest, usePageVisible } from '../platform/hooks';
import { usePoll } from '../platform/usePoll';
import { remapIndexById, sameItemIds } from '../semantics/liveItems';
import {
  photoSlideMs, resolvePlayPause, shouldArmPreparingGrace, videoPlaybackProps,
  VIDEO_PREPARING_GRACE_MS, type PartySlideshowTiming,
} from '../semantics/partySlideshow';
import {
  beginHeroRotation, deferBoundary, discardBoundary, heroCandidates, heroEligible,
  nextHero, onMediaBoundary, remapRibbonIndex, ribbonRotating, ribbonVisible,
  sameMessages, settleBoundary,
  HERO_DURATION_MS, MESSAGES_POLL_MS, NO_BOUNDARY_DEBT, RIBBON_ROTATE_MS,
  type BoundaryDebt, type HeroRotation,
} from '../semantics/partyMessages';
import { mapViewerRemoteEvent, remoteEventFor, VIDEO_SEEK_SECONDS } from '../semantics/remoteMap';
import { tvLog } from '../diagnostics';
import { TvWallVideo, type TvVideoControls, type TvVideoReadyState } from './TvWallVideo';
import {
  TvChallengeCard, TvFaceIndicator, TvGuestHubQr, TvHeroCard, TvRibbon,
} from './TvPartyOverlays';
import { useMenuOverlay } from './useMenuOverlay';

// THE VIEWER — photographs and videos, one at a time, the way a NubArca
// television shows them. The browser's counterpart of the app's ViewerScreen,
// and like it, the ONE viewer: a party album opened by hand from the album list
// and the party a display is ASSIGNED to both run through here, so the wall is
// the same wall however it was reached.
//
// For a party album it is the whole party: guest uploads arriving mid-show
// (15 s), the greetings band and the Hero cards (5 s), the challenge that HOLDS
// the wall (5 s), a guest's face search narrowing the show (6 s), the party's
// own photo dwell and video cap. Every WHEN comes from ../semantics, the port
// held to the app's rules by nativeParity.test.ts; this file only wires them.
//
// Every poll is single-flight and re-reads at once on a resume, so a machine
// that slept does not show a pre-standby party for a quarter of a minute.

const PARTY_ITEMS_POLL_MS = 15_000;
const FACE_SEARCH_POLL_MS = 6_000;
const PARTY_PLAYBACK_POLL_MS = 5_000;

interface FaceFilter {
  searchId: string;
  faceThumbnailUrl: string | null;
  items: TvAlbumItem[];
}

export interface TvViewerProps {
  items: TvAlbumItem[];
  startIndex: number;
  autoPlay?: boolean;
  /** BACK at the viewer's root. */
  onClose: () => void;
  albumId?: string;
  albumName?: string;
  partyEnabled?: boolean;
  partyUrl?: string | null;
  partySlideshow?: PartySlideshowTiming | null;
  onSessionInvalid?: () => void;
  /** The party has nothing to show any more; defaults to onClose. */
  onEmpty?: () => void;
  /** The party's album answered 404; defaults to onClose. */
  onGone?: () => void;
  /** Show the overlay (buttons, code, counter) when the viewer opens. */
  initialOverlay?: boolean;
  /** A new value re-reads every party feed at once (a resume). */
  refreshKey?: unknown;
}

function statusOf(error: unknown): number | null {
  return error instanceof ApiError ? error.status : null;
}

export function TvViewer({
  items: initialItems, startIndex, autoPlay = false, onClose,
  albumId, albumName, partyEnabled = false, partyUrl = null, partySlideshow = null,
  onSessionInvalid, onEmpty, onGone, initialOverlay = false, refreshKey,
}: TvViewerProps) {
  const { t } = useI18n();
  const platform = useDisplayPlatform();
  const [items, setItems] = useState(initialItems);
  const [index, setIndex] = useState(startIndex);
  const [playing, setPlaying] = useState(autoPlay);
  const [slideshowMode, setSlideshowMode] = useState(autoPlay);
  const [timing, setTiming] = useState<PartySlideshowTiming | null>(partySlideshow);
  const [videoReady, setVideoReady] = useState<TvVideoReadyState>('probing');
  const [faceFilter, setFaceFilter] = useState<FaceFilter | null>(null);
  const [messages, setMessages] = useState<TvPartyMessage[]>([]);
  const [ribbonIndex, setRibbonIndex] = useState(0);
  const [hero, setHero] = useState<TvPartyMessage | null>(null);
  const [partyPlayback, setPartyPlayback] = useState<TvPartyPlayback | null>(null);
  const visible = usePageVisible();
  const {
    visible: overlayVisible, visibleRef: overlayVisibleRef,
    show: showOverlay, hide: hideOverlay, toggle: toggleOverlay, bump: bumpOverlay,
  } = useMenuOverlay(initialOverlay);

  const heroRotationRef = useRef<HeroRotation>(beginHeroRotation());
  const boundariesSinceHeroRef = useRef(0);
  const boundaryDebtRef = useRef<BoundaryDebt>(NO_BOUNDARY_DEBT);
  const videoControlsRef = useRef<TvVideoControls | null>(null);
  const mountedRef = useRef(true);
  useEffect(() => () => { mountedRef.current = false; }, []);

  const displayItems = faceFilter?.items ?? items;
  const safeIndex = Math.min(index, Math.max(0, displayItems.length - 1));
  const item = displayItems[safeIndex];
  const isVideo = item?.mediaType === 'video';
  const challengeHeld = partyPlayback?.mode === 'challenge_hold';

  // A page that goes out of sight changes what is WANTED, not merely the
  // timers — exactly as the app does behind HOME. The assigned slideshow mounts
  // a fresh viewer when the page returns, so a party wall starts again by
  // itself; a person who hid a tab finds their own slideshow paused.
  useEffect(() => {
    if (!visible) setPlaying(false);
  }, [visible]);

  const refs = useLatest({ items, index, displayItems, faceFilter, messages, playing, slideshowMode, isVideo, partyPlayback });

  const goNext = useCallback(() => {
    const len = refs.current.displayItems.length;
    setIndex((i) => (len === 0 ? 0 : (i + 1) % len));
  }, [refs]);

  const goPrev = useCallback(() => {
    const len = refs.current.displayItems.length;
    setIndex((i) => (len === 0 ? 0 : (i - 1 + len) % len));
  }, [refs]);

  // EVERY automatic advance comes through here: a photo's dwell elapsing, a
  // video ending or reaching its cap. When a Hero falls due the index is NOT
  // advanced — the card is laid over what is on screen and the advance is owed.
  const ordinaryMediaBoundary = useCallback(() => {
    const outcome = onMediaBoundary({
      boundariesSinceHero: boundariesSinceHeroRef.current,
      eligible: heroEligible({
        partyEnabled,
        slideshowMode: refs.current.slideshowMode,
        playing: refs.current.playing,
        faceFilterActive: refs.current.faceFilter !== null,
        candidateCount: heroCandidates(refs.current.messages).length,
      }),
    });
    boundariesSinceHeroRef.current = outcome.boundariesSinceHero;
    if (outcome.kind === 'hero') {
      const pick = nextHero(heroRotationRef.current, refs.current.messages);
      if (pick.message !== null) {
        heroRotationRef.current = pick.rotation;
        boundaryDebtRef.current = deferBoundary();
        setHero(pick.message);
        return;
      }
    }
    goNext();
  }, [partyEnabled, goNext, refs]);

  // A party boundary is also the server's: it answers whether a challenge now
  // HOLDS the wall, in which case nothing advances.
  const handleMediaBoundary = useCallback(() => {
    if (!partyEnabled || !albumId) {
      ordinaryMediaBoundary();
      return;
    }
    void advanceTvPartyBoundary(albumId)
      .then((snapshot) => {
        if (!mountedRef.current) return;
        setPartyPlayback(snapshot);
        if (snapshot.mode !== 'challenge_hold') ordinaryMediaBoundary();
      })
      .catch((error: unknown) => {
        if (!mountedRef.current) return;
        if (statusOf(error) === 401) {
          onSessionInvalid?.();
          return;
        }
        ordinaryMediaBoundary();
      });
  }, [partyEnabled, albumId, ordinaryMediaBoundary, onSessionInvalid]);

  const dismissHeroForManualNavigation = useCallback(() => {
    boundaryDebtRef.current = discardBoundary();
    setHero(null);
  }, []);

  // Face-filter transitions keep the photograph that is on screen when it
  // belongs to the new list, and otherwise land on the first one.
  const previousFilter = useRef(faceFilter);
  useEffect(() => {
    const previous = previousFilter.current;
    previousFilter.current = faceFilter;
    if (previous === faceFilter) return;
    const previousItems = previous?.items ?? refs.current.items;
    const nextItems = faceFilter?.items ?? refs.current.items;
    if (nextItems.length === 0) return;
    const clamped = Math.min(refs.current.index, Math.max(0, previousItems.length - 1));
    const currentId = previousItems[clamped]?.id;
    const found = currentId ? nextItems.findIndex((it) => it.id === currentId) : -1;
    setIndex(found < 0 ? 0 : found);
  }, [faceFilter, refs]);

  const exitFaceFilter = useCallback(() => {
    const current = refs.current.faceFilter;
    setFaceFilter(null);
    if (albumId) void clearTvActiveFaceSearch(albumId, current?.searchId).catch(() => { /* best effort */ });
  }, [albumId, refs]);

  const completeChallenge = useCallback(() => {
    if (!albumId) return;
    void completeTvPartyChallenge(albumId)
      .then((snapshot) => {
        if (!mountedRef.current) return;
        setPartyPlayback(snapshot);
        if (snapshot.mode === 'media') goNext();
      })
      .catch(() => { /* the persisted hold stays authoritative */ });
  }, [albumId, goNext]);

  const togglePlay = useCallback(() => {
    switch (resolvePlayPause({ slideshowMode: refs.current.slideshowMode, isVideo: refs.current.isVideo })) {
      case 'toggle-slideshow':
        setPlaying((p) => !p);
        break;
      case 'toggle-video-player':
        videoControlsRef.current?.togglePlay();
        break;
      case 'promote-to-slideshow':
        setSlideshowMode(true);
        setPlaying(true);
        break;
    }
  }, [refs]);

  // BACK from the remote: the overlay first, then the face filter, then out.
  const back = useCallback(() => {
    if (overlayVisibleRef.current) {
      hideOverlay();
      return;
    }
    if (refs.current.faceFilter) {
      exitFaceFilter();
      return;
    }
    onClose();
  }, [overlayVisibleRef, hideOverlay, exitFaceFilter, onClose, refs]);

  // The on-screen Back button is pressed by somebody who can already see the
  // overlay — it goes back, it does not merely hide what they are looking at.
  const backButton = useCallback(() => {
    if (refs.current.faceFilter) exitFaceFilter();
    else onClose();
  }, [exitFaceFilter, onClose, refs]);

  // THE REMOTE. The viewer owns the keys while it is mounted; the mapping is
  // the app's (remoteMap), fed by the platform's reading of the keyboard.
  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      const key = platform.mapKey(event);
      if (key === null) return;
      event.preventDefault();
      if (key === 'back') {
        back();
        return;
      }
      const held = refs.current.partyPlayback?.mode === 'challenge_hold';
      const video = held ? false : refs.current.isVideo;
      switch (mapViewerRemoteEvent(remoteEventFor(key), video)) {
        case 'toggle-overlay':
          toggleOverlay();
          return;
        case 'next':
          if (held) { completeChallenge(); return; }
          dismissHeroForManualNavigation();
          goNext();
          break;
        case 'prev':
          if (held) return;
          dismissHeroForManualNavigation();
          goPrev();
          break;
        case 'toggle-play':
          if (held) return;
          togglePlay();
          break;
        case 'seek-back':
          videoControlsRef.current?.seekBy(-VIDEO_SEEK_SECONDS);
          break;
        case 'seek-forward':
          videoControlsRef.current?.seekBy(VIDEO_SEEK_SECONDS);
          break;
        case 'none':
          break;
      }
      bumpOverlay();
    };
    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, [platform, back, toggleOverlay, bumpOverlay, completeChallenge, dismissHeroForManualNavigation,
    goNext, goPrev, togglePlay, refs]);

  // A pointer that moves is somebody looking for the controls.
  useEffect(() => {
    const onMove = () => showOverlay();
    window.addEventListener('mousemove', onMove, { passive: true });
    return () => window.removeEventListener('mousemove', onMove);
  }, [showOverlay]);

  // THE PHOTO DWELL: the party's own timing, held while a Hero or a challenge
  // is on screen, stopped while the page is out of sight. A video is exempt —
  // it reaches its boundary by ending or by its cap.
  const photoMs = photoSlideMs(partyEnabled ? timing : null);
  const rotating = visible && slideshowMode && playing && !isVideo && hero === null && !challengeHeld;
  useEffect(() => {
    if (!rotating || displayItems.length === 0) return;
    const timer = setTimeout(handleMediaBoundary, photoMs);
    return () => clearTimeout(timer);
  }, [rotating, index, displayItems.length, photoMs, handleMediaBoundary]);

  // A video that cannot become playable must not freeze a rotating party wall.
  const armPreparingGrace = shouldArmPreparingGrace({
    slideshowMode, partyEnabled, playing, isVideo, videoReady: videoReady === 'ready',
  });
  useEffect(() => {
    if (!armPreparingGrace) return;
    const timer = setTimeout(() => {
      tvLog('tv.video.skipped', { state: videoReady });
      goNext();
    }, VIDEO_PREPARING_GRACE_MS);
    return () => clearTimeout(timer);
  }, [armPreparingGrace, index, goNext, videoReady]);

  useEffect(() => { setVideoReady('probing'); }, [item?.id]);

  // Warm only the neighbours' pictures.
  useEffect(() => {
    if (displayItems.length < 2) return;
    for (const neighbour of [displayItems[(safeIndex + 1) % displayItems.length],
      displayItems[(safeIndex - 1 + displayItems.length) % displayItems.length]]) {
      const url = neighbour?.mediaType === 'video' ? neighbour.posterUrl : neighbour?.previewUrl;
      if (url) new Image().src = url;
    }
  }, [safeIndex, displayItems]);

  const partyLive = partyEnabled && Boolean(albumId);
  const whenEmpty = onEmpty ?? onClose;
  const whenGone = onGone ?? onClose;

  // LIVE ITEMS: guest uploads append, the photograph on screen stays by id.
  usePoll({
    enabled: partyLive,
    intervalMs: PARTY_ITEMS_POLL_MS,
    refreshKey,
    read: (signal) => listTvAlbumItems(albumId!, signal),
    onValue: (detail) => {
      if (detail.items.length === 0) {
        whenEmpty();
        return;
      }
      const next = detail.partySlideshow ?? null;
      setTiming((current) => (current !== null && next !== null
        && current.photoSeconds === next.photoSeconds
        && current.maxVideoSeconds === next.maxVideoSeconds) || current === next ? current : next);
      const previous = refs.current.items;
      if (sameItemIds(previous, detail.items)) return;
      if (refs.current.faceFilter) {
        setItems(detail.items);
        return;
      }
      const currentId = previous[refs.current.index]?.id;
      setItems(detail.items);
      setIndex(remapIndexById(detail.items, currentId, refs.current.index));
    },
    onError: (error) => {
      const status = statusOf(error);
      if (status === 404) {
        tvLog('tv.party.gone');
        whenGone();
        return 'stop';
      }
      if (status === 401) {
        onSessionInvalid?.();
        return 'stop';
      }
      return undefined;
    },
  });

  // GREETINGS: their own clock, never merged into the media list.
  usePoll({
    enabled: partyLive,
    intervalMs: MESSAGES_POLL_MS,
    refreshKey,
    read: (signal) => listTvPartyMessages(albumId!, signal),
    onValue: (feed) => {
      setMessages((previous) => (sameMessages(previous, feed.messages) ? previous : feed.messages));
      // A Hero the server stopped sending leaves the screen now.
      setHero((current) => (current !== null
        && !feed.messages.some((m) => m.id === current.id && m.isHero) ? null : current));
    },
    onError: (error) => {
      if (statusOf(error) === 401) {
        onSessionInvalid?.();
        return 'stop';
      }
      return undefined;
    },
  });

  // THE CHALLENGE HOLD, restored from the server after any reload.
  usePoll({
    enabled: partyLive,
    intervalMs: PARTY_PLAYBACK_POLL_MS,
    refreshKey,
    read: (signal) => getTvPartyPlayback(albumId!, signal),
    onValue: (snapshot) => setPartyPlayback(snapshot),
    onError: (error) => {
      if (statusOf(error) === 401) {
        onSessionInvalid?.();
        return 'stop';
      }
      return undefined;
    },
  });

  // THE FACE FILTER a guest sent to this screen.
  usePoll({
    enabled: partyLive,
    intervalMs: FACE_SEARCH_POLL_MS,
    refreshKey,
    read: (signal) => getTvActiveFaceSearch(albumId!, signal),
    onValue: (active) => {
      setFaceFilter((previous) => {
        if (active.active && active.searchId && active.items.length > 0) {
          return previous && previous.searchId === active.searchId
            && previous.faceThumbnailUrl === active.faceThumbnailUrl
            && sameItemIds(previous.items, active.items)
            ? previous
            : { searchId: active.searchId, faceThumbnailUrl: active.faceThumbnailUrl, items: active.items };
        }
        return previous ? null : previous;
      });
    },
    onError: (error) => {
      if (statusOf(error) === 401) {
        onSessionInvalid?.();
        return 'stop';
      }
      return undefined;
    },
  });
  useEffect(() => {
    if (!partyLive) {
      setFaceFilter(null);
      setMessages([]);
      setPartyPlayback(null);
    }
  }, [partyLive]);

  // The Hero holds for its time, then comes down; settling what it owed is the
  // single consumer's job below.
  useEffect(() => {
    if (hero === null) return;
    const timer = setTimeout(() => setHero(null), HERO_DURATION_MS);
    return () => clearTimeout(timer);
  }, [hero]);

  // A Hero the viewer no longer has any business showing comes down early:
  // a face filter or leaving the slideshow makes the owed advance moot; a mere
  // pause keeps it owed.
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
  useEffect(() => {
    const settled = settleBoundary(boundaryDebtRef.current, { heroVisible: hero !== null, slideshowMode, playing });
    boundaryDebtRef.current = settled.debt;
    if (settled.advance) goNext();
  }, [hero, slideshowMode, playing, goNext]);

  // The band stays on the message being read across a refresh.
  //
  // The id of what is ON SCREEN is recorded after each commit, in an effect
  // declared AFTER the remap — never during render. Recording it during render
  // (as the app's viewer does) overwrites it with whatever the NEW list holds
  // at the old position before the remap reads it, so a greeting arriving at
  // the top of the feed yanked the band to itself: the very jump the remap
  // exists to prevent. (The app's ViewerScreen has this defect; it is fixed
  // here and reported for the next TV release rather than shipped as an OTA.)
  const ribbonShown = ribbonVisible({
    partyEnabled, messageCount: messages.length, overlayVisible, heroVisible: hero !== null,
  });
  const ribbonMessage = messages.length > 0 ? messages[Math.min(ribbonIndex, messages.length - 1)] : null;
  const ribbonMessageId = useRef<string | undefined>(undefined);
  useEffect(() => {
    // Read NOW: an updater runs at the next render, when the ref already holds
    // what this render put on screen.
    const reading = ribbonMessageId.current;
    setRibbonIndex((previous) => remapRibbonIndex(messages, reading, previous));
  }, [messages]);
  useEffect(() => {
    ribbonMessageId.current = ribbonMessage?.id;
  });
  const rotateRibbon = ribbonRotating({ visible: ribbonShown, messageCount: messages.length });
  useEffect(() => {
    if (!rotateRibbon) return;
    const timer = setInterval(() => {
      setRibbonIndex((i) => (i + 1) % Math.max(1, refs.current.messages.length));
    }, RIBBON_ROTATE_MS);
    return () => clearInterval(timer);
  }, [rotateRibbon, refs]);

  const chromeClass = `tv-chrome ${overlayVisible ? '' : 'tv-chrome-hidden'}`.trim();

  return (
    <div
      className="tv-viewer tvd-viewer"
      role="dialog"
      aria-label={item?.name ?? t('tv.mediaViewer')}
      data-testid={faceFilter ? 'tv-face-viewer' : 'tv-viewer'}
      data-playing={playing}
    >
      <div className="tv-viewer-stage">
        {isVideo && item?.videoUrl ? (
          <TvWallVideo
            key={item.id}
            videoUrl={item.videoUrl}
            posterUrl={item.posterUrl ?? null}
            onEnded={handleMediaBoundary}
            onCapReached={handleMediaBoundary}
            {...videoPlaybackProps({
              slideshowMode, partyEnabled, playing: playing && hero === null && !challengeHeld, timing,
            })}
            onReadyStateChange={setVideoReady}
            onExternalPause={slideshowMode ? () => setPlaying(false) : undefined}
            controlsRef={videoControlsRef}
          />
        ) : (
          item && (
            <img
              key={item.id}
              className="tv-viewer-media tvd-media tvd-fade"
              src={isVideo ? item.posterUrl ?? undefined : item.previewUrl}
              alt={item.name}
              draggable={false}
            />
          )
        )}
      </div>

      {ribbonShown && <TvRibbon message={ribbonMessage} />}
      <TvHeroCard message={hero} />
      <TvChallengeCard challenge={partyPlayback?.activeChallenge ?? null} onNext={completeChallenge} />

      {faceFilter && (
        <div className={`tv-viewer-topbar ${chromeClass}`}>
          <TvFaceIndicator
            faceThumbnailUrl={faceFilter.faceThumbnailUrl}
            albumName={albumName ?? ''}
            count={faceFilter.items.length}
            onShowAll={exitFaceFilter}
          />
        </div>
      )}
      {partyEnabled && <TvGuestHubQr partyUrl={partyUrl} hidden={!overlayVisible} />}
      <div className={`tv-viewer-bar ${chromeClass}`}>
        <button type="button" onClick={backButton}>{t('tv.viewerBack')}</button>
        <button type="button" onClick={() => { dismissHeroForManualNavigation(); goPrev(); }}>{t('tv.prev')}</button>
        <button type="button" aria-pressed={playing} onClick={togglePlay}>
          {playing ? t('tv.pause') : t('tv.play')}
        </button>
        <button type="button" onClick={() => { dismissHeroForManualNavigation(); goNext(); }}>{t('tv.next')}</button>
        <span className="tv-viewer-caption" data-testid="tv-viewer-counter">
          {playing ? t('tv.viewerPlaying') : t('tv.viewerPaused')} · {safeIndex + 1} / {displayItems.length}
          {item ? ` · ${item.name}` : ''}
        </span>
      </div>
    </div>
  );
}
