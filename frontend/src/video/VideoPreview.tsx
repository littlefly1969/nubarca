import { useEffect, useState, type CSSProperties } from 'react';
import { VIDEO_PREVIEW_FRAME_COUNT } from '../media/mediaDerivativeSpec';

interface VideoPreviewProps {
  posterUrl: string;
  previewStripUrl?: string | null;
  active: boolean;
  className?: string;
  // 'contain' (default) letterboxes the poster over a blurred backdrop — right
  // for a 16:9 stage (TV, viewer). 'cover' fills the stage (cropping) — right
  // for a uniform square grid tile, where letterbox bands look wrong.
  fit?: 'contain' | 'cover';
}

// One visual stage for web hover and TV focus. The strip is not requested
// until activation, so scrolling a large gallery only loads static posters.
// A failed request stays failed for this mount; the server also persists a
// diagnostic so later mounts cannot trigger another FFmpeg attempt.
export function VideoPreview({
  posterUrl,
  previewStripUrl,
  active,
  className = '',
  fit = 'contain',
}: VideoPreviewProps) {
  const [posterFailed, setPosterFailed] = useState(false);
  const [stripRequested, setStripRequested] = useState(false);
  const [stripReady, setStripReady] = useState(false);
  const [stripFailed, setStripFailed] = useState(false);

  useEffect(() => {
    setPosterFailed(false);
    setStripRequested(false);
    setStripReady(false);
    setStripFailed(false);
  }, [posterUrl, previewStripUrl]);

  useEffect(() => {
    if (!active || !previewStripUrl || stripFailed || stripRequested) return;
    // Avoid generating/loading a strip when the pointer merely crosses a card.
    const timer = window.setTimeout(() => setStripRequested(true), 300);
    return () => window.clearTimeout(timer);
  }, [active, previewStripUrl, stripFailed, stripRequested]);

  // The sprite is FRAME_COUNT cells wide; background-size scales one cell to the
  // stage so the CSS step animation (steps(FRAME_COUNT - 1)) walks the frames.
  // Driving the width from the shared constant keeps the strip geometry and the
  // backend's frame count from drifting apart (per-frame pixel size is
  // irrelevant — the sprite is addressed in cell fractions).
  const stripStyle: CSSProperties | undefined = previewStripUrl
    ? {
        backgroundImage: `url("${previewStripUrl.replaceAll('"', '%22')}")`,
        backgroundSize: `${VIDEO_PREVIEW_FRAME_COUNT * 100}% 100%`,
      }
    : undefined;

  return (
    <span className={`video-preview-stage video-preview-fit-${fit} ${active && stripReady ? 'is-previewing' : ''} ${className}`.trim()}>
      {!posterFailed && (
        <>
          {/* Only the letterboxing 'contain' stage has empty space to fill. A
              'cover' poster reaches every edge, so the backdrop is not rendered
              at all there — not merely hidden — and the grid never carries a
              blurred duplicate layer. */}
          {fit === 'contain' && (
            <img
              className="video-preview-backdrop"
              src={posterUrl}
              alt=""
              aria-hidden="true"
              onError={() => setPosterFailed(true)}
            />
          )}
          <img
            className="video-preview-poster"
            src={posterUrl}
            alt=""
            loading="lazy"
            decoding="async"
            onError={() => setPosterFailed(true)}
          />
        </>
      )}
      {stripRequested && previewStripUrl && !stripFailed && (
        <img
          className="video-preview-preload"
          src={previewStripUrl}
          alt=""
          aria-hidden="true"
          onLoad={() => setStripReady(true)}
          onError={() => { setStripFailed(true); setStripReady(false); }}
        />
      )}
      {stripReady && <span className="video-preview-strip" style={stripStyle} aria-hidden="true" />}
      <span className="video-preview-shade" aria-hidden="true" />
    </span>
  );
}
