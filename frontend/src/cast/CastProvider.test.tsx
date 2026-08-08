import { useState } from 'react';
import { act, cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { PERMISSIONS } from '@nubarca/api-client';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  AuthedWrapper,
  emptyResponse,
  installFetchMock,
  jsonResponse,
  MEMBER_PERMISSIONS,
  type InstalledFetchMock,
} from '../test-utils';
import { CastProvider } from './CastProvider';
import { CastMiniController } from './CastMiniController';
import { CastVideoControl } from './CastVideoControl';
import { createFakeCastSdk, type FakeCastSdk } from './castTestDouble';
import { useCast } from './useCast';

// The environment probes and the SDK load are the parts of this feature that
// depend on a real browser and a real network. They are replaced wholesale so
// every other behaviour — grant lifecycle, position handoff, receiver mirroring,
// teardown — is exercised for real rather than mocked around.
const environment = {
  supported: true,
  secure: true,
  reachable: true,
};
let fake: FakeCastSdk;
let sdkLoadStatus: 'ready' | 'failed' | 'unsupported' = 'ready';

vi.mock('./googleCastSdk', async () => {
  const actual = await vi.importActual<typeof import('./googleCastSdk')>('./googleCastSdk');
  return {
    ...actual,
    browserSupportsCastSender: () => environment.supported,
    isSecureCastOrigin: () => environment.secure,
    isReceiverReachableOrigin: () => environment.reachable,
    loadGoogleCastSdk: async () =>
      sdkLoadStatus === 'ready'
        ? { status: 'ready' as const, sdk: { framework: fake.framework, chrome: fake.chrome } }
        : { status: sdkLoadStatus },
  };
});

const FILE_ID = '11111111-1111-1111-1111-111111111111';
const GRANT_ID = '22222222-2222-2222-2222-222222222222';
const TOKEN = 'secret-token-value';

function grantBody() {
  return {
    grantId: GRANT_ID,
    expiresAt: '2026-08-09T00:00:00Z',
    contentPath: `/api/cast/media/${GRANT_ID}/video?token=${TOKEN}`,
    posterPath: `/api/cast/media/${GRANT_ID}/poster?token=${TOKEN}`,
    contentType: 'application/vnd.apple.mpegurl',
    streamType: 'BUFFERED',
    mode: 'hls',
  };
}

// A local player stand-in. The real one is HlsVideoPlayer; what the Cast code
// depends on is only the handle, so the test drives the same contract.
function Harness({
  positionSeconds = 42,
  onPause,
  permissions = MEMBER_PERMISSIONS,
}: {
  positionSeconds?: number;
  onPause?: () => void;
  permissions?: readonly string[];
}) {
  return (
    <AuthedWrapper permissions={permissions}>
      <CastProvider>
        <CastVideoControl
          fileId={FILE_ID}
          title="Vacanza 2026"
          getPositionSeconds={() => positionSeconds}
          onHandoff={onPause}
        />
        <CastMiniController />
      </CastProvider>
    </AuthedWrapper>
  );
}

describe('Cast', () => {
  let fetchMock: InstalledFetchMock;
  let preparingResponses = 0;

  beforeEach(() => {
    environment.supported = true;
    environment.secure = true;
    environment.reachable = true;
    sdkLoadStatus = 'ready';
    preparingResponses = 0;
    fake = createFakeCastSdk();
    fetchMock = installFetchMock({
      [`POST /api/cast/videos/${FILE_ID}/grant`]: () => {
        if (preparingResponses > 0) {
          preparingResponses -= 1;
          return new Response(null, { status: 202, headers: { 'retry-after': '1' } });
        }
        return jsonResponse(grantBody(), 201);
      },
      [`DELETE /api/cast/grants/${GRANT_ID}`]: () => emptyResponse(204),
    });
  });

  afterEach(() => {
    cleanup();
    vi.unstubAllGlobals();
    window.localStorage.clear();
    window.sessionStorage.clear();
  });

  // ── availability ──────────────────────────────────────────────────────

  it('offers nothing at all without cast.access', async () => {
    render(<Harness permissions={MEMBER_PERMISSIONS.filter((p) => p !== PERMISSIONS.castAccess)} />);

    await waitFor(() => {
      expect(screen.queryByTestId('cast-control')).not.toBeInTheDocument();
    });
    expect(screen.queryByTestId('cast-unavailable')).not.toBeInTheDocument();
  });

  it('explains an unsupported browser instead of hiding the control', async () => {
    environment.supported = false;
    render(<Harness />);

    const disabled = await screen.findByTestId('cast-unavailable');
    expect(disabled).toBeDisabled();
    expect(disabled.getAttribute('title')).toMatch(/Chrome/);
  });

  it('explains an insecure origin', async () => {
    environment.secure = false;
    render(<Harness />);

    const disabled = await screen.findByTestId('cast-unavailable');
    expect(disabled).toBeDisabled();
    expect(disabled.getAttribute('title')).toMatch(/HTTPS/);
  });

  it('explains an origin the television cannot resolve', async () => {
    environment.reachable = false;
    render(<Harness />);

    const disabled = await screen.findByTestId('cast-unavailable');
    expect(disabled.getAttribute('title')).toMatch(/localhost/);
  });

  it('asks for the Default Media Receiver and an origin-scoped auto-join', async () => {
    render(<Harness />);

    await waitFor(() => { expect(fake.options).not.toBeNull(); });
    expect(fake.options).toEqual({
      receiverApplicationId: 'CC1AD845',
      autoJoinPolicy: 'origin_scoped',
    });
  });

  // ── casting ───────────────────────────────────────────────────────────

  it('mints a grant, pauses local playback and hands the position to the receiver',
    async () => {
      const onPause = vi.fn();
      render(<Harness positionSeconds={42} onPause={onPause} />);
      const launcher = await screen.findByTestId('cast-launcher');

      await userEvent.click(launcher);
      act(() => { fake.connect('Google TV soggiorno'); });

      await waitFor(() => { expect(fake.loadRequests).toHaveLength(1); });

      // Local audio stops BEFORE the receiver is asked to play.
      expect(onPause).toHaveBeenCalled();

      const request = fake.loadRequests[0];
      expect(request.currentTime).toBe(42);
      expect(request.autoplay).toBe(true);
      expect(request.media.contentType).toBe('application/vnd.apple.mpegurl');
      // Absolutised against THIS page's origin, never a server-supplied host.
      expect(request.media.contentId).toBe(
        `${window.location.origin}/api/cast/media/${GRANT_ID}/video?token=${TOKEN}`);
      // The receiver never sees a NubArca cookie or an owner endpoint.
      expect(request.media.contentId).not.toContain('/api/files/');
    });

  it('shows a preparing state while the HLS ladder is produced, then loads', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    try {
      preparingResponses = 1;
      render(<Harness />);
      const launcher = await screen.findByTestId('cast-launcher');

      await userEvent.click(launcher);
      act(() => { fake.connect(); });

      expect(await screen.findByTestId('cast-preparing')).toHaveTextContent(/Preparazione/);

      await act(async () => { await vi.advanceTimersByTimeAsync(3000); });
      await waitFor(() => { expect(fake.loadRequests).toHaveLength(1); });
    } finally {
      vi.useRealTimers();
    }
  });

  it('reports a receiver that cannot decode the media', async () => {
    fake = createFakeCastSdk({ rejectLoad: true });
    render(<Harness />);
    const launcher = await screen.findByTestId('cast-launcher');

    await userEvent.click(launcher);
    act(() => { fake.connect(); });

    expect(await screen.findByTestId('cast-error')).toHaveTextContent(/non è in grado/);
  });

  // ── remote mirroring ──────────────────────────────────────────────────

  async function castAndConnect(onPause?: () => void) {
    render(<Harness onPause={onPause} />);
    const launcher = await screen.findByTestId('cast-launcher');
    await userEvent.click(launcher);
    act(() => { fake.connect('Google TV soggiorno'); });
    await waitFor(() => { expect(fake.loadRequests).toHaveLength(1); });
  }

  it('shows the mini controller once something is playing on a receiver', async () => {
    await castAndConnect();

    const mini = await screen.findByTestId('cast-mini-controller');
    expect(mini).toHaveTextContent('Vacanza 2026');
    expect(mini).toHaveTextContent('Google TV soggiorno');
  });

  it('reflects a pause initiated on the receiver', async () => {
    await castAndConnect();
    await screen.findByTestId('cast-mini-controller');

    // The TV remote, the Google Home app or another sender: to the framework
    // they are the same state change.
    act(() => { fake.receiverUpdate({ isPaused: true }); });

    await waitFor(() => {
      expect(screen.getByTestId('cast-mini-playpause')).toHaveAttribute('aria-label', 'Riproduci');
    });
  });

  it('reflects a seek initiated on the receiver', async () => {
    await castAndConnect();
    await screen.findByTestId('cast-mini-controller');

    act(() => { fake.receiverUpdate({ currentTime: 305, duration: 600 }); });

    await waitFor(() => {
      expect(screen.getByTestId('cast-mini-clock')).toHaveTextContent('5:05 / 10:00');
    });
  });

  it('reflects a volume change initiated on the receiver', async () => {
    await castAndConnect();
    await screen.findByTestId('cast-mini-controller');

    act(() => { fake.receiverUpdate({ volumeLevel: 0.25 }); });

    await waitFor(() => {
      expect(screen.getByTestId('cast-mini-volume')).toHaveValue('25');
    });
  });

  it('commits a volume change to the receiver', async () => {
    await castAndConnect();
    const slider = await screen.findByTestId('cast-mini-volume');

    act(() => {
      Object.getOwnPropertyDescriptor(
        window.HTMLInputElement.prototype, 'value')!.set!.call(slider, '40');
      slider.dispatchEvent(new Event('change', { bubbles: true }));
    });

    await waitFor(() => { expect(fake.player.volumeLevel).toBeCloseTo(0.4); });
  });

  it('reflects a mute initiated on the receiver', async () => {
    await castAndConnect();
    await screen.findByTestId('cast-mini-controller');

    act(() => { fake.receiverUpdate({ isMuted: true }); });

    await waitFor(() => {
      expect(screen.getByTestId('cast-mini-mute')).toHaveAttribute('aria-pressed', 'true');
    });
  });

  it('sends local control input to the receiver', async () => {
    await castAndConnect();
    await screen.findByTestId('cast-mini-controller');
    expect(fake.player.isPaused).toBe(false);

    await userEvent.click(screen.getByTestId('cast-mini-playpause'));
    expect(fake.player.isPaused).toBe(true);

    await userEvent.click(screen.getByTestId('cast-mini-mute'));
    expect(fake.player.isMuted).toBe(true);
  });

  it('commits a scrub to the receiver', async () => {
    await castAndConnect();
    await screen.findByTestId('cast-mini-controller');
    act(() => { fake.receiverUpdate({ duration: 600 }); });

    const scrubber = await screen.findByTestId('cast-mini-seek');
    act(() => {
      Object.getOwnPropertyDescriptor(
        window.HTMLInputElement.prototype, 'value')!.set!.call(scrubber, '120');
      scrubber.dispatchEvent(new Event('change', { bubbles: true }));
    });

    await waitFor(() => { expect(fake.player.currentTime).toBe(120); });
  });

  // ── teardown ──────────────────────────────────────────────────────────

  it('stops the receiver and revokes the grant when the user stops casting', async () => {
    await castAndConnect();
    await screen.findByTestId('cast-mini-controller');

    await userEvent.click(screen.getByTestId('cast-mini-stop'));

    await waitFor(() => {
      expect(fetchMock.calls.some(
        (c) => c.method === 'DELETE' && c.url === `/api/cast/grants/${GRANT_ID}`)).toBe(true);
    });
    expect(fake.mediaStopCalls).toBeGreaterThan(0);
    expect(fake.endSessionCalls).toBeGreaterThan(0);
    await waitFor(() => {
      expect(screen.queryByTestId('cast-mini-controller')).not.toBeInTheDocument();
    });
  });

  it('revokes the grant when the receiver disappears without an explicit stop', async () => {
    await castAndConnect();
    await screen.findByTestId('cast-mini-controller');

    act(() => { fake.disconnect(); });

    await waitFor(() => {
      expect(fetchMock.calls.some(
        (c) => c.method === 'DELETE' && c.url === `/api/cast/grants/${GRANT_ID}`)).toBe(true);
    });
    await waitFor(() => {
      expect(screen.queryByTestId('cast-mini-controller')).not.toBeInTheDocument();
    });
  });

  // ── the session outlives the viewer ───────────────────────────────────

  it('keeps casting when the viewer that started it unmounts', async () => {
    function ViewerHarness() {
      const [open, setOpen] = useState(true);
      return (
        <AuthedWrapper>
          <CastProvider>
            {open && (
              <CastVideoControl
                fileId={FILE_ID}
                title="Vacanza 2026"
                getPositionSeconds={() => 42}
              />
            )}
            <button type="button" data-testid="close-viewer" onClick={() => { setOpen(false); }}>
              close
            </button>
            <CastMiniController />
          </CastProvider>
        </AuthedWrapper>
      );
    }

    render(<ViewerHarness />);
    await userEvent.click(await screen.findByTestId('cast-launcher'));
    act(() => { fake.connect('Google TV soggiorno'); });
    await screen.findByTestId('cast-mini-controller');

    await userEvent.click(screen.getByTestId('close-viewer'));

    // The viewer is gone; the cast is not.
    expect(screen.queryByTestId('cast-control')).not.toBeInTheDocument();
    expect(await screen.findByTestId('cast-mini-controller')).toHaveTextContent('Vacanza 2026');
    expect(fake.endSessionCalls).toBe(0);
    expect(fetchMock.calls.some((c) => c.method === 'DELETE')).toBe(false);
  });

  it('hands the last known remote position back when the receiver drops', async () => {
    let handoff: number | null | undefined;
    function Probe() {
      const cast = useCast();
      return (
        <button type="button" data-testid="probe"
          onClick={() => { handoff = cast?.consumeHandoff(FILE_ID); }}>
          probe
        </button>
      );
    }

    render(
      <AuthedWrapper>
        <CastProvider>
          <CastVideoControl fileId={FILE_ID} title="Vacanza 2026" getPositionSeconds={() => 42} />
          <Probe />
        </CastProvider>
      </AuthedWrapper>,
    );
    await userEvent.click(await screen.findByTestId('cast-launcher'));
    act(() => { fake.connect(); });
    await waitFor(() => { expect(fake.loadRequests).toHaveLength(1); });

    // The film runs on for a while, then the receiver goes away.
    act(() => { fake.receiverUpdate({ currentTime: 517 }); });
    act(() => { fake.disconnect(); });

    await userEvent.click(screen.getByTestId('probe'));

    // Never zero: losing the position is what makes a dropped connection feel
    // like data loss rather than an interruption.
    expect(handoff).toBe(517);
  });

  // ── the secret ────────────────────────────────────────────────────────

  it('never writes the bearer token to web storage', async () => {
    await castAndConnect();
    await screen.findByTestId('cast-mini-controller');

    const dump = [
      JSON.stringify({ ...window.localStorage }),
      JSON.stringify({ ...window.sessionStorage }),
    ].join('|');

    expect(dump).not.toContain(TOKEN);
    expect(dump).not.toContain(GRANT_ID);
    // The URL bar is untouched too: nothing puts the secret into history.
    expect(window.location.href).not.toContain(TOKEN);
  });
});
