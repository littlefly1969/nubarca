// Sanitized, default-OFF client diagnostics for the TV app.
//
// Flip TV_DEBUG_MEDIA to true locally (never commit it enabled) to trace media
// loading + remote events on a device via `adb logcat`. Output is deliberately
// leak-free: it carries only the media VARIANT (thumbnail/preview/poster), an
// OPAQUE cache key, timings, byte counts, queue depth, and failure class —
// never cookies, tokens, ids, or URLs.
export const TV_DEBUG_MEDIA = false;

export function tvDebug(...parts: Array<string | number | boolean>): void {
  if (!TV_DEBUG_MEDIA) return;
  // eslint-disable-next-line no-console
  console.info('[tv]', parts.join(' '));
}
