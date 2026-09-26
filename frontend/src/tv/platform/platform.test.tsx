import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, render } from '@testing-library/react';
import { useState } from 'react';
import { mapTvKey } from './displayPlatform';
import { usePoll } from './usePoll';

describe('the remote, read as a keyboard', () => {
  it.each([
    ['ArrowUp', 'up'], ['ArrowDown', 'down'], ['ArrowLeft', 'left'], ['ArrowRight', 'right'],
    ['Enter', 'select'], [' ', 'select'], ['Escape', 'back'], ['Backspace', 'back'],
    ['BrowserBack', 'back'], ['GoBack', 'back'], ['MediaPlayPause', 'playPause'],
    ['MediaTrackNext', 'next'], ['MediaTrackPrevious', 'prev'], ['ContextMenu', 'menu'],
  ])('%s is %s', (key, action) => {
    expect(mapTvKey({ key })).toBe(action);
  });

  it('ignores everything else rather than guessing', () => {
    for (const key of ['a', 'Tab', 'F11', 'Shift', 'KEYCODE_DPAD_CENTER']) expect(mapTvKey({ key })).toBeNull();
  });
});

describe('one poll, one request at a time', () => {
  beforeEach(() => { vi.useFakeTimers(); });
  afterEach(() => { cleanup(); vi.useRealTimers(); });

  function Harness(props: {
    read: (signal: AbortSignal) => Promise<number>;
    onValue: (value: number) => void;
    onError?: (error: unknown, failures: number) => 'stop' | void;
    expose: (refresh: () => void) => void;
  }) {
    const [enabled] = useState(true);
    const refresh = usePoll({ enabled, intervalMs: 1_000, timeoutMs: 5_000, ...props });
    props.expose(refresh);
    return null;
  }

  async function advance(ms: number) {
    await act(async () => { await vi.advanceTimersByTimeAsync(ms); });
  }

  it('never overlaps, and a refresh asked for mid-flight runs right after', async () => {
    let inFlight = 0;
    let maxInFlight = 0;
    let n = 0;
    const values: number[] = [];
    let refresh = () => {};
    const read = () => {
      inFlight += 1;
      maxInFlight = Math.max(maxInFlight, inFlight);
      const value = ++n;
      return new Promise<number>((resolve) => setTimeout(() => { inFlight -= 1; resolve(value); }, 300));
    };
    render(<Harness read={read} onValue={(v) => values.push(v)} expose={(r) => { refresh = r; }} />);
    await advance(100);
    refresh();
    refresh();
    await advance(250);
    expect(values).toEqual([1]);
    await advance(300);
    // The coalesced refresh ran at once, alone, and its answer came last.
    expect(values).toEqual([1, 2]);
    expect(maxInFlight).toBe(1);
  });

  it('stops for good when told the answer is final', async () => {
    let n = 0;
    const read = () => { n += 1; return Promise.reject(new Error('401')); };
    render(<Harness read={read} onValue={() => {}} onError={() => 'stop'} expose={() => {}} />);
    await advance(10_000);
    expect(n).toBe(1);
  });

  it('gives up on a read that never answers', async () => {
    const aborted: boolean[] = [];
    let n = 0;
    const read = (signal: AbortSignal) => {
      n += 1;
      return new Promise<number>((_, reject) => {
        signal.addEventListener('abort', () => { aborted.push(true); reject(new Error('aborted')); });
      });
    };
    render(<Harness read={read} onValue={() => {}} expose={() => {}} />);
    await advance(5_000 + 1_000 + 10);
    expect(aborted).toEqual([true]);
    expect(n).toBe(2);
  });
});
