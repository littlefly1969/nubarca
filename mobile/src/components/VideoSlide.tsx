// VideoSlide: one video in the viewer pager, driven by an explicit `active`
// flag from the parent.
//
// LIFECYCLE CONTRACT (acceptance BLOCKER):
//   * mounting does NOT autoplay — the pager keeps neighbor slides mounted;
//   * active=true starts playback, active=false pauses it;
//   * unmount always stops the audio and releases the player;
//   * keep-awake is held only by the ACTIVE playing video.
// Two audios can therefore never play at once: only the focused slide is ever
// allowed to reach the playing state.

import React, { useEffect, useMemo, useRef, useState } from 'react';
import { StyleSheet, Text, View } from 'react-native';
import {
  useVideoPlayer,
  VideoView,
  type VideoPlayer,
} from 'expo-video';
import { activateKeepAwakeAsync, deactivateKeepAwake } from 'expo-keep-awake';
import { AuthedImage } from './AuthedImage';
import { ErrorState, LoadingState } from '../ui/states';
import { useI18n } from '../i18n';
import { sessionCookieSource } from '../api/sessionAccess';
import {
  VIDEO_PROBE_ATTEMPT_TIMEOUT_MS,
  createManagedProbe,
  resolveExpoVideoSource,
  type VideoContainer,
  type VideoProbeFetch,
} from '../media/videoProbe';
import type { ViewerSlide } from '../media/viewerSequence';
import {
  recallPosition,
  rememberPosition,
  restorablePosition,
} from '../media/videoPosition';
import {
  playerStatusFor,
  probeStateForOutcome,
  refreshVideoSourceCookie,
  shouldPlayVideo,
  snapshotPlayerStatus,
  videoPresentation,
  type VideoProbeState,
} from './videoPlayback';

export function VideoSlide({
  slide,
  active,
}: {
  slide: ViewerSlide;
  active: boolean;
}): React.JSX.Element {
  const { t } = useI18n();
  const activeRef = useRef(active);
  activeRef.current = active;

  // Preserve the exact authorized URL, but take the owner cookie from the LIVE
  // manual jar. A viewer can stay open across ASP.NET sliding-cookie renewal;
  // replaying the grid-time snapshot eventually turns valid media into a false
  // 401/403. Memoization keeps the player identity stable while the cookie is.
  const source = useMemo(
    () => refreshVideoSourceCookie(slide.videoSource, sessionCookieSource().current),
    // `active` deliberately latches a fresh cookie at each focus transition.
    // A later status event must not replace a player halfway through playback.
    // It is an explicit lifecycle signal rather than an accidentally unread
    // dependency.
    [active, slide.videoSource],
  );

  // RANGE PROBE over the CANONICAL delivery contract (media/videoDelivery.ts,
  // byte-identical to the copies web and TV use): sends Range: bytes=0-0 with
  // the session cookie, aborts each attempt as soon as the response head is
  // known, and classifies exactly as the other two consumers do — 200/206 is
  // always playable and the MIME only says hls vs progressive, 202 keeps
  // preparing on the shared backoff for as long as the transcode takes, and
  // 404 / auth / transient / protocol stay distinct verdicts. The outcome
  // resolves BOTH availability and container; the expo-video player mounts
  // only on a confirmed ready.
  const [probeState, setProbeState] = useState<VideoProbeState>(
    source ? 'idle' : 'unavailable',
  );
  const [probeAttempt, setProbeAttempt] = useState(0);
  const readySourceRef = useRef<{
    source: NonNullable<typeof source>;
    container: VideoContainer;
  } | null>(null);

  // Resolved expo-video source: exists ONLY on a confirmed ready, carrying
  // contentType:'hls' exactly when the server answered HLS.
  const [resolvedContainer, setResolvedContainer] = useState<VideoContainer | null>(null);

  useEffect(() => {
    if (!source) {
      setProbeState('unavailable');
      setResolvedContainer(null);
      return;
    }

    // FlatList keeps neighbours mounted. Probing those invisible videos both
    // wastes connections and can exhaust transient server/network budgets
    // during a long swipe session. Playback readiness belongs to the focused
    // slide only.
    if (!active) return;

    const cached = readySourceRef.current;
    if (cached !== null && cached.source === source) {
      setResolvedContainer(cached.container);
      setProbeState('ready');
      return;
    }

    let cancelled = false;
    setProbeState('probing');
    setResolvedContainer(null);
    // ONE MANAGED PROBE per effect instance: its AbortController bounds every
    // attempt in time and lets cleanup KILL the in-flight request and the
    // retry delay outright, instead of only ignoring a late result.
    const probe = createManagedProbe(source, {
      attemptTimeoutMs: VIDEO_PROBE_ATTEMPT_TIMEOUT_MS,
      // The promise settles only on the TERMINAL verdict; without this the
      // transient 202 never reaches the UI and the slide reads
      // "Caricamento..." for the whole ladder-preparation wait instead of
      // switching to its dedicated "preparing" branch. There is no attempt
      // ceiling: a long transcode stays "preparing" until it is ready.
      onPreparing: () => {
        if (!cancelled) setProbeState('preparing');
      },
      fetchImpl: ((
        uri: string,
        init: { headers: Record<string, string>; signal: AbortSignal },
      ) => fetch(uri, init as RequestInit)) as unknown as VideoProbeFetch,
    });
    void probe.outcome.then((outcome) => {
      // Defence-in-depth: a verdict landing in the same tick as unmount — or
      // the cancelled-probe settlement itself — must never touch state.
      // Cancellation is its own outcome and maps to null, so it is never
      // surfaced as unavailable/error here.
      if (cancelled) return;
      const next = probeStateForOutcome(outcome);
      if (next === null) return;
      setProbeState(next);
      const container = outcome.kind === 'ready' ? outcome.mode : null;
      setResolvedContainer(container);
      if (container !== null) {
        readySourceRef.current = { source, container };
      }
    });
    return () => {
      cancelled = true;
      probe.cancel();
    };
  }, [active, probeAttempt, source]);

  // ONE builder for both containers (media/videoProbe.ts): the probed URL is
  // the played URL — a shared album keeps its server-provided album-scoped
  // route — and contentType is ALWAYS declared, progressive included, because
  // ExoPlayer cannot infer a container from an extension-less /video URL.
  const expoSource = useMemo(() => {
    if (probeState !== 'ready') return null;
    if (!source || resolvedContainer === null) return null;
    return resolveExpoVideoSource(source, { kind: 'ready', mode: resolvedContainer });
  }, [source, probeState, resolvedContainer]);

  const player: VideoPlayer = useVideoPlayer(expoSource, (p) => {
    // Mount ≠ autoplay. Playback is owned exclusively by the `active` effect:
    // an unfocused neighbor must never start making noise.
    p.loop = false;
    // Rotating the phone makes the pager re-measure and remount its cells, so
    // this is a NEW player with no idea where the old one was — which is why
    // a rotation used to restart the video from zero. The position is recalled
    // from a store that outlives the slide, and `restorablePosition` refuses
    // one belonging to a different video, so changing item can never seek the
    // new video to the old one's timestamp.
    if (expoSource !== null) {
      const resume = restorablePosition(
        recallPosition(expoSource.uri),
        expoSource.uri,
        // The duration is not on the slide; the player learns it when it
        // loads. Passing null means the end-of-video guard is skipped here —
        // and it is not needed on this path anyway, because a position is only
        // ever recorded from a player that was actually playing.
        null,
      );
      if (resume !== null) p.currentTime = resume;
    }
  });
  const [playerStatus, setPlayerStatus] = useState(() => snapshotPlayerStatus(player));
  const nativeStatus = playerStatusFor(playerStatus, player);

  const uri = expoSource?.uri ?? null;
  useEffect(() => {
    const statusSub = player.addListener('statusChange', (status) => {
      setPlayerStatus(snapshotPlayerStatus(player, status.status));
    });
    // Subscribe first, then take an authoritative NOW snapshot. This closes the
    // race where readyToPlay was reached before the listener existed, and also
    // resets status ownership when useVideoPlayer creates a new instance.
    setPlayerStatus(snapshotPlayerStatus(player));

    const playingSub = player.addListener('playingChange', ({ isPlaying }) => {
      // Keep-awake belongs to the ACTIVE playing video only.
      if (isPlaying && activeRef.current) {
        void activateKeepAwakeAsync();
      } else {
        deactivateKeepAwake();
      }
    });

    return () => {
      statusSub.remove();
      playingSub.remove();
      deactivateKeepAwake();
      // Record where this player was BEFORE it goes away — a remount (rotation,
      // recycling) creates a new one, and the old position is unreachable
      // afterwards. Reading a released player throws, hence the guard.
      try {
        if (uri !== null) rememberPosition(uri, player.currentTime);
      } catch {
        /* released before we could read it; the next mount just starts over */
      }
      // Unmount always stops the audio, even during the teardown window.
      try {
        player.pause();
      } catch {
        /* already released */
      }
    };
  }, [player, uri]);

  // Focus drives playback. Pausing on !active is what makes simultaneous
  // playback impossible while the pager keeps neighbors mounted.
  useEffect(() => {
    if (!active) {
      try {
        player.pause();
      } catch {
        /* released */
      }
      return;
    }
    if (shouldPlayVideo(active, expoSource !== null, nativeStatus)) {
      void player.play();
    }
  }, [active, expoSource, nativeStatus, player]);

  const presentation = videoPresentation(
    source !== null,
    probeState,
    expoSource !== null,
    nativeStatus,
  );

  if (presentation === 'unavailable') {
    // HLS off / item gone / no playable source: poster + explicit message.
    return (
      <View style={styles.centerDark}>
        {slide.posterUrl ? (
          <AuthedImage
            path={slide.posterUrl}
            style={styles.poster}
            accessibilityLabel=""
            resizeMode="contain"
          />
        ) : null}
        <Text style={styles.errorText}>{t('player.unavailable')}</Text>
      </View>
    );
  }

  if (presentation === 'error') {
    return (
      <View style={styles.centerDark}>
        <ErrorState
          title={t('player.playbackError')}
          onRetry={() => {
            if (probeState === 'error') {
              readySourceRef.current = null;
              setProbeAttempt((attempt) => attempt + 1);
              return;
            }
            if (expoSource !== null) {
              setPlayerStatus(snapshotPlayerStatus(player, 'loading'));
              void player.replaceAsync(expoSource).catch(() => {
                setPlayerStatus(snapshotPlayerStatus(player, 'error'));
              });
            }
          }}
        />
      </View>
    );
  }

  if (presentation !== 'ready') {
    return (
      <View style={styles.centerDark}>
        <LoadingState />
        <Text style={styles.loadingText}>
          {presentation === 'preparing' ? t('player.preparing') : t('player.loading')}
        </Text>
      </View>
    );
  }

  return (
    <View style={[styles.full, styles.dark]}>
      <VideoView
        player={player}
        contentFit="contain"
        allowsFullscreen
        allowsPictureInPicture={false}
        style={styles.full}
      />
    </View>
  );
}

const styles = StyleSheet.create({
  full: { width: '100%', height: '100%' },
  dark: { backgroundColor: '#0A0F1A' },
  centerDark: {
    flex: 1,
    backgroundColor: '#0A0F1A',
    alignItems: 'center',
    justifyContent: 'center',
  },
  loadingText: { color: '#F5F7FB', marginTop: 12, fontSize: 14 },
  poster: { width: '86%', height: '52%', borderRadius: 12, marginBottom: 16 },
  errorText: { color: '#F5F7FB', padding: 24, textAlign: 'center' },
});
