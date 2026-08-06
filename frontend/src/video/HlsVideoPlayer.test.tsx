import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { HlsVideoPlayer, probeVideoPlayback } from './HlsVideoPlayer';
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

// Video-hls slice 3 — the /video contract probe: the endpoint speaks either
// the adaptive contract (200 master playlist | 202 preparing) or the legacy
// Range-enabled byte stream (206 for the 1-byte probe), and the player picks
// its rendering mode from the answer.
describe('probeVideoPlayback', () => {
  it('classifies a 202 as preparing', async () => {
    mockFetchResponse(202);
    expect((await probeVideoPlayback('/api/files/x/video')).mode).toBe('preparing');
  });

  it('carries the 202 Retry-After through to the caller', async () => {
    mockFetchResponse(202, { 'retry-after': '2' });
    expect(await probeVideoPlayback('/api/files/x/video'))
      .toEqual({ mode: 'preparing', retryAfter: '2' });
  });

  it('reports a missing Retry-After as absent rather than inventing one', async () => {
    mockFetchResponse(202);
    expect((await probeVideoPlayback('/api/files/x/video')).retryAfter).toBeNull();
  });

  it('classifies a 200 mpegurl master as hls', async () => {
    mockFetchResponse(200, { 'content-type': 'application/vnd.apple.mpegurl' });
    expect((await probeVideoPlayback('/api/files/x/video')).mode).toBe('hls');
  });

  it('classifies a 206 byte-range answer as the legacy direct stream', async () => {
    mockFetchResponse(206, { 'content-type': 'video/mp4' });
    expect((await probeVideoPlayback('/api/files/x/video')).mode).toBe('direct');
  });

  it('classifies a 200 video answer as the legacy direct stream', async () => {
    mockFetchResponse(200, { 'content-type': 'video/quicktime' });
    expect((await probeVideoPlayback('/api/files/x/video')).mode).toBe('direct');
  });

  it('classifies 404 and network failures as error', async () => {
    mockFetchResponse(404);
    expect((await probeVideoPlayback('/api/files/x/video')).mode).toBe('error');

    vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new TypeError('net')));
    expect((await probeVideoPlayback('/api/files/x/video')).mode).toBe('error');
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

    expect(canPlay).toHaveBeenCalledWith('application/vnd.apple.mpegurl');
    // Native playback: the element gets the master URL directly, and the level
    // policy does not apply — WebKit owns its own ladder there.
    expect(video.getAttribute('src')).toBe('/api/files/x/video');
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
