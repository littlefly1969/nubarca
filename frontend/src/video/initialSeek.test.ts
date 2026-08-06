import { describe, expect, it } from 'vitest';
import { resolveInitialSeekSeconds } from './HlsVideoPlayer';

// VSEM-03: the pure clamping rule behind the semantic playback handoff. A
// stale, malformed or out-of-range timestamp must degrade to normal playback,
// never to a broken player.
describe('resolveInitialSeekSeconds', () => {
  it('converts a valid timestamp to seconds', () => {
    expect(resolveInitialSeekSeconds(42_000, 65)).toBe(42);
    expect(resolveInitialSeekSeconds(1_500, 65)).toBe(1.5);
  });

  it('returns null when there is no timestamp', () => {
    expect(resolveInitialSeekSeconds(null, 65)).toBeNull();
    expect(resolveInitialSeekSeconds(undefined, 65)).toBeNull();
  });

  it('rejects negative and non-finite timestamps', () => {
    expect(resolveInitialSeekSeconds(-1, 65)).toBeNull();
    expect(resolveInitialSeekSeconds(Number.NaN, 65)).toBeNull();
    expect(resolveInitialSeekSeconds(Number.POSITIVE_INFINITY, 65)).toBeNull();
  });

  it('does nothing while the duration is unknown', () => {
    // Before metadata the browser reports NaN; seeking then is ignored anyway.
    expect(resolveInitialSeekSeconds(42_000, Number.NaN)).toBeNull();
    expect(resolveInitialSeekSeconds(42_000, 0)).toBeNull();
  });

  it('clamps a timestamp at or beyond the end to just inside it', () => {
    // A manifest from a previous segmentation version can outlive its video's
    // real duration; land on a playable frame instead of an instant `ended`.
    expect(resolveInitialSeekSeconds(65_000, 65)).toBeCloseTo(64.75, 5);
    expect(resolveInitialSeekSeconds(999_000, 65)).toBeCloseTo(64.75, 5);
  });

  it('keeps a timestamp that is comfortably inside the duration', () => {
    expect(resolveInitialSeekSeconds(64_000, 65)).toBe(64);
  });

  it('accepts the very first frame', () => {
    expect(resolveInitialSeekSeconds(0, 65)).toBe(0);
  });
});
