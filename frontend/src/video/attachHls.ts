import { readDisplayContext, selectInitialLevel } from './hlsLevelSelection';
import { EMPTY_BUDGET, classifyFatalError, planRecovery } from './hlsRecovery';

// Attaching an adaptive (HLS) source to a <video>, the ONE way the web does it.
//
// Shared by the viewer's player and the television's wall player, so both play
// the same ladder the same way: Safari and every browser with native HLS get
// the master playlist as the element's `src` (WebKit owns the ladder there);
// everything else gets hls.js over MSE, imported DYNAMICALLY so the library is
// downloaded only when an adaptive video is actually on screen.
//
// hls.js starts at a level chosen for the display and the connection
// (hlsLevelSelection.ts) and ABR owns every switch after that. A fatal error
// goes through the bounded recovery in hlsRecovery.ts; only when that budget is
// spent does the caller hear about it.

export interface AttachHlsOptions {
  /**
   * True when the element fills the viewport (fullscreen playback, a
   * television), so the viewport IS the watched size for the start level.
   */
  readonly fillsViewport: boolean;
  /** Recovery gave up, or neither native HLS nor MSE exists. */
  onFatal(): void;
}

/** Attach `url` to `video`. The returned function detaches and destroys whatever was created. */
export function attachHlsSource(
  video: HTMLVideoElement, url: string, options: AttachHlsOptions,
): () => void {
  if (video.canPlayType('application/vnd.apple.mpegurl')) {
    video.src = url;
    return () => {
      video.removeAttribute('src');
      try { video.load(); } catch { /* jsdom and old engines */ }
    };
  }

  let destroyed = false;
  let hls: { destroy(): void } | null = null;
  let recoveries = EMPTY_BUDGET;

  void import('hls.js').then(({ default: Hls }) => {
    if (destroyed) return;
    if (!Hls.isSupported()) {
      options.onFatal();
      return;
    }
    const instance = new Hls({
      // Cap automatic selection at what the element can actually show.
      capLevelToPlayerSize: true,
      // Loading starts by hand once the start level is known.
      autoStartLoad: false,
    });

    instance.on(Hls.Events.MANIFEST_PARSED, (_event, data) => {
      if (destroyed) return;
      instance.startLevel = selectInitialLevel(
        data.levels.map((l) => ({ width: l.width, height: l.height, bitrate: l.bitrate })),
        readDisplayContext(video, options.fillsViewport),
      );
      instance.startLoad();
    });

    instance.on(Hls.Events.ERROR, (_event, data) => {
      if (destroyed || !data.fatal) return;
      const plan = planRecovery(classifyFatalError(data.type, Hls.ErrorTypes), recoveries);
      recoveries = plan.budget;
      if (plan.action === 'restart-load') instance.startLoad();
      else if (plan.action === 'recover-media') instance.recoverMediaError();
      else options.onFatal();
    });

    instance.loadSource(url);
    instance.attachMedia(video);
    hls = instance;
  }).catch(() => {
    if (!destroyed) options.onFatal();
  });

  return () => {
    destroyed = true;
    hls?.destroy();
  };
}
