import { useEffect, useState } from 'react';

interface VideoModalProps {
  fileId: string;
  fileName: string;
  onClose(): void;
}

// Slice 62: minimal authenticated video player. Source streams from
// /api/files/{id}/video, which is owner-scoped, server-detected-video-only,
// and Range-enabled. Cookie auth is carried by the browser same-origin.
// No autoplay, no analytics, no captions UI — just <video controls>.
export function VideoModal({ fileId, fileName, onClose }: VideoModalProps) {
  const [failed, setFailed] = useState(false);

  // Escape closes — matches the lightbox keybinding model.
  useEffect(() => {
    function onKey(event: KeyboardEvent) {
      if (event.key === 'Escape') onClose();
    }
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [onClose]);

  const src = `/api/files/${fileId}/video`;

  return (
    <div
      className="lightbox-overlay"
      role="dialog"
      aria-modal="true"
      aria-label={`Video: ${fileName}`}
      onClick={onClose}
    >
      <div className="lightbox-content" onClick={(e) => e.stopPropagation()}>
        <header className="lightbox-header">
          <span className="lightbox-title" title={fileName}>{fileName}</span>
          <button
            type="button"
            className="lightbox-close"
            aria-label="Close video"
            onClick={onClose}
          >
            ×
          </button>
        </header>
        <div className="lightbox-stage">
          {failed ? (
            <div className="lightbox-error" role="alert">
              Could not play this video.
            </div>
          ) : (
            <video
              className="lightbox-image"
              controls
              src={src}
              aria-label={`Video player for ${fileName}`}
              onError={() => setFailed(true)}
            />
          )}
        </div>
      </div>
    </div>
  );
}

// Frontend-side video-extension heuristic. The backend's /video endpoint is
// authoritative (server-detected video gate + 404 otherwise); this helper
// only decides whether to OFFER a Play button on a file row. Spoofed
// extensions just yield an error state on click — they cannot bypass the
// server gate.
const VIDEO_EXTENSIONS = ['.mp4', '.m4v', '.webm', '.mov'];

export function looksLikeVideo(fileName: string): boolean {
  const lower = fileName.toLowerCase();
  return VIDEO_EXTENSIONS.some((ext) => lower.endsWith(ext));
}
