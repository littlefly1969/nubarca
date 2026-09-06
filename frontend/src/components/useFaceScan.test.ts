import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, renderHook } from '@testing-library/react';
import { SCAN_MIN_PASSES, SCAN_PASS_MS, useFaceScan } from './useFaceScan';

/* The gate itself.
 *
 * The component tests cover what a guest sees; these cover the rule underneath,
 * including the case the UI cannot reach — a selfie replaced while a scan is
 * still sweeping. The input is hidden during a scan, so that path arrives here
 * through the effect that watches the chosen file, and this is where it can be
 * tested honestly rather than through a control nobody can click.
 */

function allowMotion(reduced: boolean) {
  vi.stubGlobal('matchMedia', (query: string) => ({
    matches: reduced && query.includes('prefers-reduced-motion'),
    media: query,
    addEventListener: () => {},
    removeEventListener: () => {},
    addListener: () => {},
    removeListener: () => {},
    onchange: null,
    dispatchEvent: () => false,
  }));
}

const FULL_SCAN = SCAN_PASS_MS * SCAN_MIN_PASSES;

describe('the minimum-scan gate', () => {
  beforeEach(() => {
    allowMotion(false);
    vi.useFakeTimers();
  });
  afterEach(() => {
    cleanup();
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  it('holds an answer that arrives at once', () => {
    const settled = vi.fn();
    const { result } = renderHook(() => useFaceScan<string>(settled));

    act(() => result.current.begin());
    act(() => result.current.settle('answer'));
    // Instant, and deliberately not shown yet.
    expect(settled).not.toHaveBeenCalled();
    expect(result.current.scanning).toBe(true);

    act(() => { vi.advanceTimersByTime(FULL_SCAN - 10); });
    expect(settled).not.toHaveBeenCalled();

    act(() => { vi.advanceTimersByTime(20); });
    expect(settled).toHaveBeenCalledWith('answer');
    expect(result.current.scanning).toBe(false);
  });

  it('waits for an answer that arrives late, however long the sweeping goes on', () => {
    const settled = vi.fn();
    const { result } = renderHook(() => useFaceScan<string>(settled));

    act(() => result.current.begin());
    act(() => { vi.advanceTimersByTime(FULL_SCAN * 4); });
    // The passes are long done; the gate is BOTH conditions, not the later one
    // of two that already happened.
    expect(settled).not.toHaveBeenCalled();
    expect(result.current.scanning).toBe(true);

    act(() => result.current.settle('late'));
    expect(settled).toHaveBeenCalledWith('late');
  });

  it('discards a scan when the selfie is replaced mid-sweep', () => {
    const settled = vi.fn();
    const { result } = renderHook(() => useFaceScan<string>(settled));

    act(() => result.current.begin());
    act(() => result.current.settle('for the old selfie'));
    act(() => { vi.advanceTimersByTime(SCAN_PASS_MS); });

    // A different selfie: the answer describes a face that is no longer on
    // screen, and must never surface later as if it did.
    act(() => result.current.reset());
    act(() => { vi.advanceTimersByTime(FULL_SCAN * 3); });
    expect(settled).not.toHaveBeenCalled();
    expect(result.current.scanning).toBe(false);
  });

  it('leaves no timer behind on unmount', () => {
    const settled = vi.fn();
    const { result, unmount } = renderHook(() => useFaceScan<string>(settled));

    act(() => result.current.begin());
    act(() => result.current.settle('answer'));
    unmount();

    // A guest who closed the sheet gets no late answer to a question they
    // withdrew — and no state update on a component that is gone.
    act(() => { vi.advanceTimersByTime(FULL_SCAN * 3); });
    expect(settled).not.toHaveBeenCalled();
  });

  it('makes a reduced-motion guest wait too — the sweep is stilled, not skipped', () => {
    allowMotion(true);
    const settled = vi.fn();
    const { result } = renderHook(() => useFaceScan<string>(settled));

    act(() => result.current.begin());
    act(() => result.current.settle('answer'));
    // The setting asks for less movement, not for a different sequence: the
    // guest still watches the tile find their face and hold on it, which is
    // the part worth seeing. The CSS is what stills the line.
    expect(settled).not.toHaveBeenCalled();

    act(() => { vi.advanceTimersByTime(FULL_SCAN + 10); });
    expect(settled).toHaveBeenCalledWith('answer');
  });
});
