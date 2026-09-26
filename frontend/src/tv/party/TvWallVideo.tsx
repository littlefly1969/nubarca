import { useCallback, useEffect, useRef, useState, type MutableRefObject } from 'react';
import { useI18n } from '../../i18n';
import { probeVideoPlayback } from '../../video/HlsVideoPlayer';
import { attachHlsSource } from '../../video/attachHls';
import { INITIAL_POLL_STATE, planNextProbe, type VideoDeliveryPollState } from '../../video/videoDelivery';
import { videoPlaybackModeFor, type VideoPlaybackMode } from '../../video/webPlaybackMode';
import { beginVideoRotation, onVideoEnded, onVideoProgress } from '../semantics/partySlideshow';
import { tvLog } from '../diagnostics';

// A VIDEO ON THE DISPLAY — the browser's counterpart of the app's TvVideoPlayer.
//
// It plays the real media the paired television is authorised for (the
// /api/tv video route, which the TV session cookie reaches), never a poster in
// its place: the same /video contract every NubArca client probes, HLS where
// the server has a ladder and the progressive stream where it does not, the
// ladder attached natively or through hls.js.
//
// What makes it a TELEVISION player rather than the viewer's:
//
//   * no controls of its own when the slideshow drives it (`playing` given) —
//     one play state governs the photo countdown and the video alike;
//   * the party's cap is MEDIA time, read from the element's own clock, and
//     the end and the cap advance the wall EXACTLY ONCE between them;
//   * AUTOPLAY is what a wall does. A browser that refuses sound without a
//     gesture gets the video muted rather than a frozen poster, and the first
//     key or click brings the sound back;
//   * nothing it can meet stops the wall for ever: an unplayable file, a
//     transcode that never finishes, a stream that stalls mid-clip — each is
//     reported as not ready, and the slideshow's grace window moves on.

export type TvVideoReadyState = 'probing' | 'preparing' | 'ready' | 'error';

export interface TvVideoControls {
  togglePlay(): void;
  seekBy(seconds: number): void;
}

interface Props {
  readonly videoUrl: string;
  readonly posterUrl: string | null;
  /** Controlled by the slideshow, or undefined for a video opened to watch. */
  readonly playing?: boolean;
  /** The party's cap in seconds of playback, or null to play to the end. */
  readonly maxPlaybackSeconds?: number | null;
  onEnded(): void;
  onCapReached?(): void;
  onReadyStateChange?(state: TvVideoReadyState): void;
  /** The element paused on its own (a device went away, a media key) while controlled. */
  onExternalPause?(): void;
  readonly controlsRef?: MutableRefObject<TvVideoControls | null>;
}

/** A controlled video that shows no progress for this long is treated as broken. */
export const STALL_MS = 20_000;

export function TvWallVideo({
  videoUrl, posterUrl, playing, maxPlaybackSeconds = null,
  onEnded, onCapReached, onReadyStateChange, onExternalPause, controlsRef,
}: Props) {
  const { t } = useI18n();
  const [mode, setMode] = useState<VideoPlaybackMode | 'probing'>('probing');
  const [ready, setReady] = useState<TvVideoReadyState>('probing');
  const [mutedByPolicy, setMutedByPolicy] = useState(false);
  const videoRef = useRef<HTMLVideoElement | null>(null);
  const rotation = useRef(beginVideoRotation(maxPlaybackSeconds));
  const requestedPause = useRef(false);
  const controlled = playing !== undefined;

  const callbacks = useRef({ onEnded, onCapReached, onReadyStateChange, onExternalPause });
  callbacks.current = { onEnded, onCapReached, onReadyStateChange, onExternalPause };

  const readyRef = useRef<TvVideoReadyState>('probing');
  const report = useCallback((next: TvVideoReadyState) => {
    if (readyRef.current === next) return;
    readyRef.current = next;
    setReady(next);
    callbacks.current.onReadyStateChange?.(next);
  }, []);

  // The cap can change under a playing video (the host edits the party): the
  // latch keeps whether this video has already advanced.
  useEffect(() => {
    rotation.current = { ...rotation.current, capSeconds: maxPlaybackSeconds };
  }, [maxPlaybackSeconds]);

  // Probe the canonical /video contract, re-probing a transcode that is still
  // being prepared on the shared ramp.
  useEffect(() => {
    const controller = new AbortController();
    let timer: ReturnType<typeof setTimeout> | null = null;
    let poll: VideoDeliveryPollState = INITIAL_POLL_STATE;
    setMode('probing');
    report('probing');
    const probe = async () => {
      const verdict = await probeVideoPlayback(videoUrl, controller.signal);
      if (controller.signal.aborted) return;
      const plan = planNextProbe(verdict, poll);
      if (plan.action === 'settle') {
        const settled = videoPlaybackModeFor(plan.verdict);
        setMode(settled);
        if (settled === 'error') {
          tvLog('tv.video.error', { kind: plan.verdict.kind });
          report('error');
        }
        return;
      }
      if (plan.surface === 'preparing') {
        setMode('preparing');
        report('preparing');
      }
      poll = plan.state;
      timer = setTimeout(() => { void probe(); }, plan.delayMs);
    };
    void probe();
    return () => {
      controller.abort();
      if (timer) clearTimeout(timer);
    };
  }, [videoUrl, report]);

  const fail = useCallback((kind: string) => {
    tvLog('tv.video.error', { kind });
    report('error');
  }, [report]);

  // Attach the source for the mode the probe settled on.
  useEffect(() => {
    const video = videoRef.current;
    if (!video) return;
    if (mode === 'direct') {
      video.src = videoUrl;
      return () => {
        video.removeAttribute('src');
        try { video.load(); } catch { /* jsdom */ }
      };
    }
    if (mode === 'hls') {
      return attachHlsSource(video, videoUrl, { fillsViewport: true, onFatal: () => fail('hls-fatal') });
    }
    return undefined;
  }, [mode, videoUrl, fail]);

  // PLAY: attempted with sound; a browser that refuses sound without a gesture
  // is given a muted video instead of a still frame.
  const play = useCallback(() => {
    const video = videoRef.current;
    if (!video) return;
    requestedPause.current = false;
    const attempt = video.play();
    if (!attempt || typeof attempt.catch !== 'function') return;
    attempt.catch((error: unknown) => {
      if (!(error instanceof DOMException) || error.name !== 'NotAllowedError') return;
      if (video.muted) return;
      video.muted = true;
      setMutedByPolicy(true);
      void video.play()?.catch(() => { /* the grace window will move the wall on */ });
    });
  }, []);

  const pause = useCallback(() => {
    const video = videoRef.current;
    if (!video) return;
    requestedPause.current = true;
    video.pause();
  }, []);

  // The slideshow's play state is the player's.
  const wantsPlay = playing ?? true;
  const playable = mode === 'direct' || mode === 'hls';
  useEffect(() => {
    if (!playable) return;
    if (wantsPlay) play();
    else pause();
  }, [playable, wantsPlay, play, pause]);

  // Sound back on the first gesture, which is exactly what the browser wanted.
  useEffect(() => {
    if (!mutedByPolicy) return;
    const unmute = () => {
      const video = videoRef.current;
      if (video) video.muted = false;
      setMutedByPolicy(false);
    };
    window.addEventListener('keydown', unmute, { once: true });
    window.addEventListener('pointerdown', unmute, { once: true });
    return () => {
      window.removeEventListener('keydown', unmute);
      window.removeEventListener('pointerdown', unmute);
    };
  }, [mutedByPolicy]);

  // A controlled video that stops moving is broken, not slow: report it so the
  // wall's grace window can move on instead of holding a frozen frame.
  const lastProgress = useRef(0);
  useEffect(() => {
    if (!controlled || !wantsPlay || ready !== 'ready') return;
    lastProgress.current = Date.now();
    const timer = window.setInterval(() => {
      const video = videoRef.current;
      if (!video || video.paused || video.ended) return;
      if (Date.now() - lastProgress.current > STALL_MS) fail('stalled');
    }, 1_000);
    return () => window.clearInterval(timer);
  }, [controlled, wantsPlay, ready, fail]);

  if (controlsRef) {
    controlsRef.current = {
      togglePlay: () => {
        const video = videoRef.current;
        if (!video) return;
        if (video.paused) play();
        else pause();
      },
      seekBy: (seconds) => {
        const video = videoRef.current;
        if (!video || !Number.isFinite(video.duration)) return;
        video.currentTime = Math.max(0, Math.min(video.duration, video.currentTime + seconds));
      },
    };
  }
  useEffect(() => () => {
    if (controlsRef) controlsRef.current = null;
  }, [controlsRef]);

  // Release the media on the way out: a wall mounts hundreds of these.
  useEffect(() => () => {
    const video = videoRef.current;
    if (!video) return;
    video.pause();
  }, []);

  const onTimeUpdate = () => {
    const video = videoRef.current;
    if (!video) return;
    lastProgress.current = Date.now();
    const step = onVideoProgress(rotation.current, video.currentTime);
    rotation.current = step.state;
    if (step.advance) callbacks.current.onCapReached?.();
  };

  const onVideoEnd = () => {
    const step = onVideoEnded(rotation.current);
    rotation.current = step.state;
    if (step.advance) callbacks.current.onEnded();
  };

  const onPause = () => {
    const video = videoRef.current;
    if (!video || video.ended) return;
    if (requestedPause.current) return;
    if (controlled) callbacks.current.onExternalPause?.();
  };

  if (mode === 'probing' || mode === 'preparing' || mode === 'error') {
    return (
      <div className="tvd-video" data-testid="tv-wall-video" data-state={mode}>
        {posterUrl && <img className="tvd-media" src={posterUrl} alt="" draggable={false} />}
      </div>
    );
  }

  return (
    <div className="tvd-video" data-testid="tv-wall-video" data-state={ready}>
      <video
        ref={videoRef}
        className="tvd-media"
        poster={posterUrl ?? undefined}
        controls={!controlled}
        controlsList="nodownload"
        playsInline
        preload="auto"
        onLoadedData={() => report('ready')}
        onPlaying={() => report('ready')}
        onTimeUpdate={onTimeUpdate}
        onEnded={onVideoEnd}
        onPause={onPause}
        onError={() => fail('media-error')}
        data-testid="tv-wall-video-element"
      />
      {mutedByPolicy && (
        <p className="tvd-sound-hint" role="status" data-testid="tv-sound-hint">🔇 {t('tv.soundHint')}</p>
      )}
    </div>
  );
}
