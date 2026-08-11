// What a browsing tile is allowed to show for a video — pure, node-testable.
//
// THE REPORTED DEFECT AND WHAT CAUSED IT
// --------------------------------------
// Video tiles did not reliably show a useful preview. The audit found one
// concrete, screen-side cause: the Personal Videos grid passed the poster URL to
// AuthedTilePreview with NO fallback, so anything that stopped the poster
// arriving — a derivative not generated yet, a slow first FFmpeg extraction, a
// transient failure that the client then memoizes for 45 seconds — left a bare
// "media unavailable" box. The Party grid already fell back to the item's
// thumbnail; the personal one did not. (For contrast: the backend does generate
// posters on demand, and falls back to a deliberately-visible synthetic
// film-strip frame when no FFmpeg provider is configured, so a permanently
// posterless video is not the expected state.)
//
// THE POLICY
// ----------
//  1. poster — the still the server derives for this video;
//  2. a safe derived still fallback, when one exists for this item;
//  3. an explicit, intentional VIDEO PLACEHOLDER.
//
// Step 3 is the part that matters for the product: a failed preview must be a
// tile that clearly reads "video, no preview yet", never a blank focusable
// rectangle the user cannot tell from a broken app.
//
// WHAT IS NOT ALLOWED
// -------------------
// No VideoView, no player, and no decode of the original in a browsing tile.
// A grid is many tiles; a player per tile is how a constrained Fire Stick runs
// out of codecs and memory. Tiles use derived STILL IMAGES only.
//
// `previewStripUrl` is deliberately NOT used as the fallback. It is a real image
// derivative, but it is a SPRITE: six 480x270 cells in one horizontal JPEG
// (VideoPreviewStripSpec). Rendered into a tile it is a 2880x270 strip — at
// `contain` a hairline band, at `cover` an arbitrary crop of one frame. It is
// the wrong shape for this job, so the fallback is the item's own still where
// one exists and the placeholder otherwise.

export type VideoTilePreview =
  | { kind: 'poster'; path: string; fallbackPath: string | null }
  | { kind: 'placeholder' };

export interface VideoTileSources {
  // The video's poster derivative. Null when the DTO carries none at all.
  posterUrl: string | null;
  // A still already derived for this item that is safe to show at tile size —
  // e.g. the small thumbnail a mixed-media DTO carries. Null when there is none.
  stillFallbackUrl?: string | null;
  // The 6-cell sprite. Accepted so callers can pass the DTO through unchanged,
  // and ignored on purpose; see above.
  previewStripUrl?: string | null;
}

export function videoTilePreview(sources: VideoTileSources): VideoTilePreview {
  const poster = nonEmpty(sources.posterUrl);
  const still = nonEmpty(sources.stillFallbackUrl);
  if (poster !== null) {
    // A fallback identical to the primary is not a fallback: it would re-request
    // the same URL the loader has just memoized as failed.
    return { kind: 'poster', path: poster, fallbackPath: still === poster ? null : still };
  }
  if (still !== null) return { kind: 'poster', path: still, fallbackPath: null };
  return { kind: 'placeholder' };
}

function nonEmpty(value: string | null | undefined): string | null {
  return typeof value === 'string' && value.trim().length > 0 ? value : null;
}
