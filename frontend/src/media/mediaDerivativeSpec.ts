// Frontend mirror of the backend media-derivative contract
// (src/NubArca.Api/Files/MediaDerivativeSpec / VideoPreviewStripSpec). Only
// the values the browser genuinely needs live here; they are documented as a
// deliberate cross-boundary contract in docs/media-derivatives.md. Keep this in
// sync with the backend spec when the derivative geometry changes.

// Number of frames the video preview strip packs into one horizontal JPEG
// sprite. The strip is animated purely in CSS by stepping the background
// position across this many equal cells, so the animation is independent of the
// per-frame pixel size — only the count matters to the browser.
export const VIDEO_PREVIEW_FRAME_COUNT = 6;

// Fallback aspect ratio for a video whose real pixel dimensions are unknown.
// Video tiles otherwise use the source's real ratio (posters are now generated
// at source aspect ratio, not a fixed 16:9 stage); this 16:9 value only applies
// when width/height are missing/invalid. See mediaAspectRatio.getMediaAspectRatio.
export const VIDEO_TILE_ASPECT_RATIO = 16 / 9;
