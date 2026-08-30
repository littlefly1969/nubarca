import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { HlsVideoPlayer, probeVideoPlayback, type VideoPlayerHandle } from './HlsVideoPlayer';
import { videoPlaybackModeFor } from './webPlaybackMode';
import { AuthedWrapper } from '../test-utils';

// Scope note: hls.js is loaded through a dynamic import, and under this jsdom
// harness that import resolves to the REAL library for the component even when
// the test file itself mocks it — so the MSE branch cannot be driven from here.
// The two decisions inside that branch are therefore pure modules with their
// own exhaustive tests (hlsLevelSelection.ts, hlsRecovery.ts), and the wiring
// is exercised for real in the browser matrix. What this file covers is
// everything observable without MSE: contract classification, the preparing
// state and its polling, the direct-stream path, and the quality badge.

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
});

function mockFetchResponse(status: number, headers: Record<string, string> = {}) {
  vi.stubGlobal('fetch', vi.fn().mockResolvedValue({
    status,
    headers: new Headers(headers),
  }));
}

/** Advance fake timers, letting React flush the state updates that result. */
async function advance(ms: number) {
  await act(async () => { await vi.advanceTimersByTimeAsync(ms); });
}

/** Render and wait for the <video> element to exist. */
async function renderPlayer(fileId = 'x') {
  const view = render(<AuthedWrapper><HlsVideoPlayer fileId={fileId} /></AuthedWrapper>);
  const video = await waitFor(() => {
    const v = document.querySelector('video');
    expect(v).not.toBeNull();
    return v!;
  });
  return { view, video };
}

// The /video contract probe: the endpoint speaks either the adaptive contract
// (200 master playlist | 202 preparing) or the legacy Range-enabled byte
// stream (206 for the 1-byte probe). The probe returns the CANONICAL verdict
// (videoDelivery.ts) and the player renders it through videoPlaybackModeFor —
// the classification itself is proven row for row, against the same fixture
// mobile and TV use, in videoDeliveryParity.test.ts.
describe('probeVideoPlayback', () => {
  it('classifies a 202 as preparing and parses its Retry-After', async () => {
    mockFetchResponse(202, { 'retry-after': '2' });
    expect(await probeVideoPlayback('/api/files/x/video'))
      .toEqual({ kind: 'preparing', retryAfterMs: 2000 });
  });

  it('reports a missing Retry-After as absent rather than inventing one', async () => {
    mockFetchResponse(202);
    expect(await probeVideoPlayback('/api/files/x/video'))
      .toEqual({ kind: 'preparing', retryAfterMs: null });
  });

  it('classifies a 200 mpegurl master as hls', async () => {
    mockFetchResponse(200, { 'content-type': 'application/vnd.apple.mpegurl' });
    expect(await probeVideoPlayback('/api/files/x/video'))
      .toEqual({ kind: 'ready', mode: 'hls' });
  });

  it('classifies a 206 byte-range answer as progressive', async () => {
    mockFetchResponse(206, { 'content-type': 'video/mp4' });
    expect(await probeVideoPlayback('/api/files/x/video'))
      .toEqual({ kind: 'ready', mode: 'progressive' });
  });

  it('classifies a 200 video answer as progressive', async () => {
    mockFetchResponse(200, { 'content-type': 'video/quicktime' });
    expect(await probeVideoPlayback('/api/files/x/video'))
      .toEqual({ kind: 'ready', mode: 'progressive' });
  });

  it('keeps 404 and a network failure as DISTINCT verdicts', async () => {
    // Both render as the viewer's one error state, but a missing file and a
    // dropped connection are not the same thing: only the second is retried.
    mockFetchResponse(404);
    expect(await probeVideoPlayback('/api/files/x/video')).toEqual({ kind: 'not-found' });
    expect(videoPlaybackModeFor({ kind: 'not-found' })).toBe('error');

    vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new TypeError('net')));
    expect(await probeVideoPlayback('/api/files/x/video'))
      .toEqual({ kind: 'transient-error' });
    expect(videoPlaybackModeFor({ kind: 'transient-error' })).toBe('error');
  });

  it('sends the 1-byte range header so a legacy probe never downloads the file', async () => {
    mockFetchResponse(206, { 'content-type': 'video/mp4' });
    await probeVideoPlayback('/api/files/x/video');
    const call = vi.mocked(fetch).mock.calls[0]!;
    expect((call[1] as RequestInit).headers).toMatchObject({ Range: 'bytes=0-0' });
  });
});

describe('HlsVideoPlayer preparation state', () => {
  it('shows the poster and a status line while the ladder is preparing', async () => {
    mockFetchResponse(202, { 'retry-after': '2' });
    render(<AuthedWrapper><HlsVideoPlayer fileId="x" /></AuthedWrapper>);
    expect(await screen.findByRole('status')).toBeInTheDocument();
    expect(document.querySelector('img')?.getAttribute('src')).toBe('/api/files/x/poster');
    // No media element until there is something to play.
    expect(document.querySelector('video')).toBeNull();
  });

  it('re-probes on the bounded ramp rather than once', async () => {
    vi.useFakeTimers();
    try {
      mockFetchResponse(202);
      render(<AuthedWrapper><HlsVideoPlayer fileId="x" /></AuthedWrapper>);
      await vi.waitFor(() => expect(fetch).toHaveBeenCalledTimes(1));

      // First step is 1.5s: nothing at 1.4s, a probe just after.
      await advance(1400);
      expect(fetch).toHaveBeenCalledTimes(1);
      await advance(200);
      expect(fetch).toHaveBeenCalledTimes(2);

      // Second step is 2.5s, measured from when the first timer fired.
      await advance(2300);
      expect(fetch).toHaveBeenCalledTimes(2);
      await advance(300);
      expect(fetch).toHaveBeenCalledTimes(3);
    } finally {
      vi.useRealTimers();
    }
  });

  it('waits longer when the server asks it to', async () => {
    vi.useFakeTimers();
    try {
      mockFetchResponse(202, { 'retry-after': '12' });
      render(<AuthedWrapper><HlsVideoPlayer fileId="x" /></AuthedWrapper>);
      await vi.waitFor(() => expect(fetch).toHaveBeenCalledTimes(1));

      await advance(5000);
      expect(fetch).toHaveBeenCalledTimes(1); // the ramp alone would have fired
      await advance(7100);
      expect(fetch).toHaveBeenCalledTimes(2);
    } finally {
      vi.useRealTimers();
    }
  });

  it('stops polling as soon as the ladder is ready', async () => {
    vi.useFakeTimers();
    try {
      const fetchMock = vi.fn()
        .mockResolvedValueOnce({ status: 202, headers: new Headers() })
        .mockResolvedValue({
          status: 200,
          headers: new Headers({ 'content-type': 'video/mp4' }),
        });
      vi.stubGlobal('fetch', fetchMock);
      render(<AuthedWrapper><HlsVideoPlayer fileId="x" /></AuthedWrapper>);
      await vi.waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(1));

      await advance(1600);
      expect(fetchMock).toHaveBeenCalledTimes(2);
      // Ready now: no further probes, however long we wait.
      await advance(60_000);
      expect(fetchMock).toHaveBeenCalledTimes(2);
    } finally {
      vi.useRealTimers();
    }
  });

  it('rides out a transient 5xx without flashing an error, then settles', async () => {
    // The shared policy (videoDelivery.ts): a temporary boundary is retried on
    // the same ramp WITHOUT changing what is on screen, so a single flaky
    // request never replaces the poster with an error the next probe clears.
    vi.useFakeTimers();
    try {
      const fetchMock = vi.fn()
        .mockResolvedValueOnce({ status: 503, headers: new Headers() })
        .mockResolvedValue({
          status: 206,
          headers: new Headers({ 'content-type': 'video/mp4' }),
        });
      vi.stubGlobal('fetch', fetchMock);
      render(<AuthedWrapper><HlsVideoPlayer fileId="x" /></AuthedWrapper>);
      await vi.waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(1));

      // Still the poster, no error: the retry has not even run yet.
      expect(screen.queryByRole('alert')).toBeNull();
      await advance(1600);
      expect(fetchMock).toHaveBeenCalledTimes(2);
      await vi.waitFor(() => expect(document.querySelector('video')).not.toBeNull());
    } finally {
      vi.useRealTimers();
    }
  });

  it('gives up on a persistent transient failure instead of retrying forever', async () => {
    vi.useFakeTimers();
    try {
      mockFetchResponse(503);
      render(<AuthedWrapper><HlsVideoPlayer fileId="x" /></AuthedWrapper>);
      await vi.waitFor(() => expect(fetch).toHaveBeenCalledTimes(1));

      // Three bounded retries on the shared ramp: 1.5s, 2.5s, 5s.
      await advance(1600);
      await advance(2600);
      await advance(5100);
      expect(fetch).toHaveBeenCalledTimes(4);
      await vi.waitFor(() => expect(screen.getByRole('alert')).toBeInTheDocument());

      // And then it stops — unlike a 202, which has no attempt ceiling.
      await advance(60_000);
      expect(fetch).toHaveBeenCalledTimes(4);
    } finally {
      vi.useRealTimers();
    }
  });

  it('never gives up on a 202, however long the transcode takes', async () => {
    vi.useFakeTimers();
    try {
      mockFetchResponse(202);
      render(<AuthedWrapper><HlsVideoPlayer fileId="x" /></AuthedWrapper>);
      await vi.waitFor(() => expect(fetch).toHaveBeenCalledTimes(1));

      // Well past any attempt count a consumer might have invented.
      for (let i = 0; i < 30; i += 1) await advance(5100);
      expect(vi.mocked(fetch).mock.calls.length).toBeGreaterThan(20);
      expect(screen.queryByRole('alert')).toBeNull();
      expect(screen.getByRole('status')).toBeInTheDocument();
    } finally {
      vi.useRealTimers();
    }
  });

  it('aborts the in-flight probe when the player unmounts', async () => {
    const abortSpy = vi.fn();
    vi.stubGlobal('fetch', vi.fn().mockImplementation((_url, init: RequestInit) => {
      (init.signal as AbortSignal).addEventListener('abort', abortSpy);
      return new Promise(() => { /* never settles */ });
    }));

    const view = render(<AuthedWrapper><HlsVideoPlayer fileId="x" /></AuthedWrapper>);
    await waitFor(() => expect(fetch).toHaveBeenCalled());
    view.unmount();
    expect(abortSpy).toHaveBeenCalled();
  });

  it('cancels a scheduled re-probe when the player unmounts', async () => {
    vi.useFakeTimers();
    try {
      mockFetchResponse(202);
      const view = render(<AuthedWrapper><HlsVideoPlayer fileId="x" /></AuthedWrapper>);
      await vi.waitFor(() => expect(fetch).toHaveBeenCalledTimes(1));
      view.unmount();
      await advance(60_000);
      expect(fetch).toHaveBeenCalledTimes(1);
    } finally {
      vi.useRealTimers();
    }
  });

  it('aborts and re-probes when the file changes', async () => {
    mockFetchResponse(206, { 'content-type': 'video/mp4' });
    const view = render(<AuthedWrapper><HlsVideoPlayer fileId="a" /></AuthedWrapper>);
    await waitFor(() => expect(fetch).toHaveBeenCalledTimes(1));
    expect(vi.mocked(fetch).mock.calls[0][0]).toBe('/api/files/a/video');

    view.rerender(<AuthedWrapper><HlsVideoPlayer fileId="b" /></AuthedWrapper>);
    await waitFor(() => expect(fetch).toHaveBeenCalledTimes(2));
    expect(vi.mocked(fetch).mock.calls[1][0]).toBe('/api/files/b/video');
  });
});

// The quality badge shows the DECODED height actually playing and tracks the
// media element's `resize` event, so it updates live on adaptive switches.
describe('HlsVideoPlayer quality badge', () => {
  it('shows the current rendition height and follows switches', async () => {
    mockFetchResponse(206, { 'content-type': 'video/mp4' }); // legacy direct mode
    const { video } = await renderPlayer();

    // No badge before the element knows its dimensions.
    expect(screen.queryByText(/\d+p/)).toBeNull();

    Object.defineProperty(video, 'videoWidth', { value: 1920, configurable: true });
    Object.defineProperty(video, 'videoHeight', { value: 1080, configurable: true });
    fireEvent(video, new Event('resize'));
    expect(await screen.findByText('1080p')).toBeInTheDocument();

    // Adaptive down-switch → the badge follows. This is the mechanism that
    // makes the badge report what is PLAYING rather than what was requested at
    // startup: it is driven by the element's decoded size, not by our own
    // level choice.
    Object.defineProperty(video, 'videoWidth', { value: 854, configurable: true });
    Object.defineProperty(video, 'videoHeight', { value: 480, configurable: true });
    fireEvent(video, new Event('resize'));
    expect(await screen.findByText('480p')).toBeInTheDocument();
    expect(screen.queryByText('1080p')).toBeNull();
  });

  it('labels a portrait video by its short side (1080×1920 → 1080p, not 1920p)', async () => {
    mockFetchResponse(206, { 'content-type': 'video/mp4' });
    const { video } = await renderPlayer();

    Object.defineProperty(video, 'videoWidth', { value: 1080, configurable: true });
    Object.defineProperty(video, 'videoHeight', { value: 1920, configurable: true });
    fireEvent(video, new Event('resize'));
    expect(await screen.findByText('1080p')).toBeInTheDocument();
  });
});

describe('HlsVideoPlayer direct playback', () => {
  it('sets the source declaratively and keeps the poster', async () => {
    mockFetchResponse(206, { 'content-type': 'video/mp4' });
    const { video } = await renderPlayer();
    expect(video.getAttribute('src')).toBe('/api/files/x/video');
    expect(video.getAttribute('poster')).toBe('/api/files/x/poster');
    expect(video.hasAttribute('controls')).toBe(true);
  });

  it('uses native HLS on WebKit instead of MSE', async () => {
    mockFetchResponse(200, { 'content-type': 'application/vnd.apple.mpegurl' });
    const canPlay = vi.spyOn(HTMLVideoElement.prototype, 'canPlayType')
      .mockReturnValue('maybe');
    const { video } = await renderPlayer();

    // Both facts are established by the ATTACH EFFECT, not by the render that
    // mounts the element — and `renderPlayer` returns as soon as the <video>
    // is in the DOM, which in the direct-stream mode it also is. Asserting
    // straight away therefore reads the state between React's commit and its
    // passive-effect flush: fast enough locally, and a real failure on a loaded
    // CI runner. Waiting is not a workaround here, it is the actual contract —
    // "once the player has attached the source".
    await waitFor(() => {
      expect(canPlay).toHaveBeenCalledWith('application/vnd.apple.mpegurl');
      // Native playback: the element gets the master URL directly, and the
      // level policy does not apply — WebKit owns its own ladder there.
      expect(video.getAttribute('src')).toBe('/api/files/x/video');
    });
  });

  it('shows a first-frame indicator until the element can paint', async () => {
    mockFetchResponse(206, { 'content-type': 'video/mp4' });
    const { video } = await renderPlayer();
    expect(document.querySelector('.media-viewer-video-spinner')).not.toBeNull();
    fireEvent(video, new Event('loadeddata'));
    await waitFor(() => {
      expect(document.querySelector('.media-viewer-video-spinner')).toBeNull();
    });
  });
});

// NUBARCA-GOOGLE-CAST-01 — the player bridge and the local/remote handover.
//
// "Local and TV audio play simultaneously" is a completion blocker for the Cast
// slice, and the DOM node that would cause it lives in here. The two mechanisms
// are asserted separately because they cover different moments: autoplay is what
// a video mounted DURING a cast would do, and the pause is what an already
// playing video has to be told.
describe('HlsVideoPlayer casting handover', () => {
  it('exposes position, duration and transport through the ref instead of the DOM', async () => {
    mockFetchResponse(206, { 'content-type': 'video/mp4' });
    const handle = { current: null as VideoPlayerHandle | null };
    render(
      <AuthedWrapper><HlsVideoPlayer fileId="x" playerRef={handle} /></AuthedWrapper>,
    );
    const video = await waitFor(() => {
      const v = document.querySelector('video');
      expect(v).not.toBeNull();
      return v!;
    });

    // jsdom never loads media, so the element's own values are the contract.
    Object.defineProperty(video, 'duration', { value: 600, configurable: true });
    video.currentTime = 42;
    const pause = vi.spyOn(video, 'pause').mockImplementation(() => {});

    expect(handle.current).not.toBeNull();
    expect(handle.current!.getCurrentTime()).toBe(42);
    expect(handle.current!.getDuration()).toBe(600);
    expect(handle.current!.isPaused()).toBe(true);

    handle.current!.pause();
    expect(pause).toHaveBeenCalled();

    handle.current!.seek(120);
    expect(video.currentTime).toBe(120);
    // Clamped to the real duration rather than seeking past the end.
    handle.current!.seek(99_999);
    expect(video.currentTime).toBe(600);
  });

  it('answers safely before the element exists', async () => {
    // 202: the ladder is preparing, so there is no <video> at all yet.
    mockFetchResponse(202);
    const handle = { current: null as VideoPlayerHandle | null };
    render(
      <AuthedWrapper><HlsVideoPlayer fileId="x" playerRef={handle} /></AuthedWrapper>,
    );

    await waitFor(() => { expect(handle.current).not.toBeNull(); });
    expect(handle.current!.getCurrentTime()).toBe(0);
    expect(handle.current!.getDuration()).toBe(0);
    expect(handle.current!.isPaused()).toBe(true);
    // Neither of these may throw.
    handle.current!.pause();
    handle.current!.seek(10);
  });

  it('does not autoplay while the video is playing on a receiver', async () => {
    mockFetchResponse(206, { 'content-type': 'video/mp4' });
    render(
      <AuthedWrapper>
        <HlsVideoPlayer fileId="x" suppressLocalPlayback />
      </AuthedWrapper>,
    );
    const video = await waitFor(() => {
      const v = document.querySelector('video');
      expect(v).not.toBeNull();
      return v!;
    });

    expect(video.hasAttribute('autoplay')).toBe(false);
  });

  it('undoes a stray local play while casting', async () => {
    mockFetchResponse(206, { 'content-type': 'video/mp4' });
    render(
      <AuthedWrapper>
        <HlsVideoPlayer fileId="x" suppressLocalPlayback />
      </AuthedWrapper>,
    );
    const video = await waitFor(() => {
      const v = document.querySelector('video');
      expect(v).not.toBeNull();
      return v!;
    });
    const pause = vi.spyOn(video, 'pause').mockImplementation(() => {});

    fireEvent(video, new Event('play'));

    expect(pause).toHaveBeenCalled();
  });
});
