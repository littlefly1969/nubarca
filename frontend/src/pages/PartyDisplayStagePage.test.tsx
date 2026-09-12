import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router';
import { I18nProvider } from '../i18n';
import { errorResponse, installFetchMock, jsonResponse } from '../test-utils';
import { PartyDisplayStagePage } from './PartyDisplayStagePage';
import { PartyTvStagePage } from './PartyTvStagePage';

// The display surface: the same show, a different authorisation.
//
// Everything asserted here is about the SECOND half of that sentence. The
// scenes themselves are already covered by PartyTvStagePage.test.tsx and are
// deliberately not re-tested — they are the same component, and duplicating
// their assertions here would be the same mistake as duplicating the component.

const GRANT = 'g'.repeat(43);
const DISPLAY = '/api/party-display/game';
const QR = '/api/party-display/join-qr';

function snapshot(over: Record<string, unknown> = {}) {
  return {
    albumName: 'Festa', status: 'lobby', phase: 'lobby', version: 0,
    roundNumber: 0, totalChallenges: 3, phaseEndsAt: null, challenge: null,
    roundId: null, voting: null, myVote: null, ...over,
  };
}

function activity(over: Record<string, unknown> = {}) {
  return {
    id: 'c1', title: 'Canta', body: 'Sali sul tavolo.', kind: 'dare',
    mediaUrl: null, durationSeconds: null, votingMode: 'binary', voteQuestion: null,
    ...over,
  };
}

function mount(hash = `#grant=${GRANT}`) {
  window.history.replaceState(null, '', `/party-display/stage${hash}`);
  render(
    <I18nProvider>
      <MemoryRouter initialEntries={['/party-display/stage']}>
        <PartyDisplayStagePage />
      </MemoryRouter>
    </I18nProvider>,
  );
}

beforeEach(() => {
  vi.useFakeTimers({ shouldAdvanceTime: true });
  // jsdom has no object URLs.
  let n = 0;
  Object.defineProperty(URL, 'createObjectURL',
    { value: vi.fn(() => `blob:${n++}`), configurable: true, writable: true });
  Object.defineProperty(URL, 'revokeObjectURL',
    { value: vi.fn(), configurable: true, writable: true });
});
afterEach(() => { vi.useRealTimers(); cleanup(); vi.unstubAllGlobals(); });

describe('the party display surface', () => {
  it('reads the grant from the fragment and strips it from history', async () => {
    installFetchMock({ [`GET ${DISPLAY}`]: () => jsonResponse(snapshot()) });
    mount();

    await screen.findByTestId('party-tv-stage');
    // A fragment is never sent to a server, and it must not survive in history
    // either: a reload has no credential and asks the shell for another.
    expect(window.location.hash).toBe('');
    expect(window.location.pathname).toBe('/party-display/stage');
  });

  it('sends the grant as a header and never as a query parameter', async () => {
    const mock = installFetchMock({ [`GET ${DISPLAY}`]: () => jsonResponse(snapshot()) });
    mount();
    await screen.findByTestId('party-tv-stage');

    const call = mock.calls.find((c) => c.url.includes('/api/party-display/game'))!;
    expect(call.url).not.toContain(GRANT);
    expect(call.url).not.toContain('grant=');
    expect(new Headers(call.init?.headers).get('X-Party-Display-Grant')).toBe(GRANT);
  });

  it('persists the grant nowhere', async () => {
    installFetchMock({ [`GET ${DISPLAY}`]: () => jsonResponse(snapshot()) });
    mount();
    await screen.findByTestId('party-tv-stage');

    expect(JSON.stringify(window.localStorage)).not.toContain(GRANT);
    expect(JSON.stringify(window.sessionStorage)).not.toContain(GRANT);
    expect(document.cookie).not.toContain(GRANT);
  });

  it('renders the canonical stage — the same component the public route uses', async () => {
    installFetchMock({
      [`GET ${DISPLAY}`]: () => jsonResponse(snapshot({
        status: 'live', phase: 'challenge_reveal', roundNumber: 1, roundId: 'r1',
        challenge: activity(),
      })),
    });
    mount();

    // The class families and test ids come from PartyTvStage, so this passing
    // is what "one renderer" means in practice.
    const stage = await screen.findByTestId('party-tv-stage');
    expect(stage).toHaveAttribute('data-scene', 'reveal');
    expect(await screen.findByTestId('party-stage-card')).toBeInTheDocument();
    expect(screen.getByText('Canta')).toBeInTheDocument();
  });

  it('asks the server for the lobby QR instead of building one from a token', async () => {
    const mock = installFetchMock({
      [`GET ${DISPLAY}`]: () => jsonResponse(snapshot()),
      [`GET ${QR}`]: () => new Response('<svg data-testid="qr"></svg>', {
        status: 200, headers: { 'content-type': 'image/svg+xml' },
      }),
    });
    mount();

    await waitFor(() => expect(screen.getByTestId('party-stage-qr')).toBeInTheDocument());
    const call = mock.calls.find((c) => c.url.includes('join-qr'))!;
    expect(new Headers(call.init?.headers).get('X-Party-Display-Grant')).toBe(GRANT);
    // The display holds PIXELS. A party token never reaches it.
    expect(document.body.innerHTML).not.toMatch(/\/party\/[A-Za-z0-9_-]{20,}/);
  });

  it('fetches activity media with the grant and revokes the object URL', async () => {
    const revoke = vi.fn();
    const create = vi.fn(() => 'blob:activity-1');
    Object.defineProperty(URL, 'createObjectURL',
      { value: create, configurable: true, writable: true });
    Object.defineProperty(URL, 'revokeObjectURL',
      { value: revoke, configurable: true, writable: true });
    const mock = installFetchMock({
      [`GET ${DISPLAY}`]: () => jsonResponse(snapshot({
        status: 'live', phase: 'challenge_reveal', roundNumber: 1, roundId: 'r1',
        challenge: activity({ mediaUrl: '/api/party-display/challenges/c1/media' }),
      })),
      // A string body rather than a Blob: jsdom's Blob and undici's Response
      // do not interoperate, and the bytes are not what this test is about.
      'GET /api/party-display/challenges/c1/media': () =>
        new Response('image-bytes', { status: 200 }),
    });
    window.history.replaceState(null, '', `/party-display/stage#grant=${GRANT}`);
    const view = render(
      <I18nProvider>
        <MemoryRouter initialEntries={['/party-display/stage']}>
          <PartyDisplayStagePage />
        </MemoryRouter>
      </I18nProvider>,
    );

    await waitFor(() => {
      const call = mock.calls.find((c) => c.url.includes('/challenges/c1/media'));
      expect(call).toBeDefined();
      // An <img> cannot carry a header, which is why the bytes are fetched
      // here — and why the credential is not in the URL.
      expect(call!.url).not.toContain(GRANT);
    });

    // The bytes became an object URL — that is the whole adapter: an <img>
    // cannot carry a header, so the page fetches and hands the DOM a blob.
    await waitFor(() => expect(create).toHaveBeenCalled());

    // Unmounting must not leak it. An evening of activities would otherwise
    // leak one per round, on the device least able to afford it.
    view.unmount();
    await waitFor(() => expect(revoke).toHaveBeenCalledWith('blob:activity-1'));
  });

  it('beats every two seconds so the native shell can tell it is alive', async () => {
    const posted: string[] = [];
    vi.stubGlobal('ReactNativeWebView', { postMessage: (d: string) => posted.push(d) });
    installFetchMock({ [`GET ${DISPLAY}`]: () => jsonResponse(snapshot()) });
    mount();
    await screen.findByTestId('party-tv-stage');

    const before = posted.length;
    await act(async () => { await vi.advanceTimersByTimeAsync(4_100); });
    expect(posted.length).toBeGreaterThan(before);
    expect(JSON.parse(posted[0]).type).toBe('display-heartbeat');
    // The heartbeat measures THIS page, not the party server: it carries no
    // snapshot and no credential.
    expect(posted[0]).not.toContain(GRANT);
  });

  it('says the party is unavailable when the grant stops working', async () => {
    installFetchMock({ [`GET ${DISPLAY}`]: () => errorResponse(401) });
    mount();

    // 401 means the assignment moved, the party ended, or the TV was unpaired.
    // The page reports it and lets the shell decide; it does not retry forever
    // or invent a credential.
    await waitFor(() => expect(screen.getByTestId('party-tv-stage'))
      .toHaveAttribute('data-scene', 'lobby'));
    expect(screen.queryByTestId('party-stage-card')).not.toBeInTheDocument();
  });

  it('shows nothing at all without a grant', async () => {
    installFetchMock({ [`GET ${DISPLAY}`]: () => jsonResponse(snapshot()) });
    mount('');
    // No fragment, no credential, and deliberately no request: a display that
    // guessed would be a display that could be tricked.
    await screen.findByTestId('party-tv-stage');
    expect(screen.queryByTestId('party-stage-card')).not.toBeInTheDocument();
  });
});

describe('what the display page tells its native shell', () => {
  function bridge(): string[] {
    const posted: string[] = [];
    vi.stubGlobal('ReactNativeWebView', { postMessage: (d: string) => posted.push(d) });
    return posted;
  }
  const types = (posted: string[]) => posted.map((raw) => JSON.parse(raw).type as string);

  it('announces its bridge protocol with every heartbeat', async () => {
    const posted = bridge();
    installFetchMock({ [`GET ${DISPLAY}`]: () => jsonResponse(snapshot()) });
    mount();
    await screen.findByTestId('party-tv-stage');
    const beat = posted.map((raw) => JSON.parse(raw)).find((m) => m.type === 'display-heartbeat');
    expect(beat.protocol).toBe(2);
  });

  it('says once that the first real snapshot is on screen, and that a live scene is up', async () => {
    const posted = bridge();
    installFetchMock({ [`GET ${DISPLAY}`]: () => jsonResponse(snapshot()) });
    mount();
    await waitFor(() => expect(types(posted)).toContain('display-ready'));
    // More polls are more truth, not more "ready".
    await act(async () => { await vi.advanceTimersByTimeAsync(6_000); });
    expect(types(posted).filter((t) => t === 'display-ready')).toHaveLength(1);
    const presentation = posted.map((raw) => JSON.parse(raw))
      .filter((m) => m.type === 'display-presentation');
    expect(presentation.at(-1)).toEqual({ type: 'display-presentation', active: true });
    // Nothing the page tells the shell carries the credential.
    for (const raw of posted) expect(raw).not.toContain(GRANT);
  });

  it('reports a refused grant as its own event, and holds nothing live on screen', async () => {
    const posted = bridge();
    installFetchMock({ [`GET ${DISPLAY}`]: () => errorResponse(401) });
    mount();
    // Distinct from silence: the renderer is alive, the CAPABILITY is not, and
    // only the shell can mint another one.
    await waitFor(() => expect(types(posted)).toContain('display-auth-failed'));
    expect(types(posted)).not.toContain('display-ready');
    const presentation = posted.map((raw) => JSON.parse(raw))
      .filter((m) => m.type === 'display-presentation');
    expect(presentation.at(-1)?.active).toBe(false);
    for (const raw of posted) expect(raw).not.toContain(GRANT);
  });
});

describe('what the display page fetches besides the snapshot', () => {
  const svg = () => new Response('<svg viewBox="0 0 10 10" data-testid="qr"></svg>', {
    status: 200, headers: { 'content-type': 'image/svg+xml' },
  });

  it('retries the lobby code after a failure that was not an answer', async () => {
    let qrCalls = 0;
    installFetchMock({
      [`GET ${DISPLAY}`]: () => jsonResponse(snapshot()),
      [`GET ${QR}`]: () => {
        qrCalls += 1;
        if (qrCalls === 1) throw new TypeError('Failed to fetch');
        return qrCalls === 2 ? errorResponse(503) : svg();
      },
    });
    mount();
    await waitFor(() => expect(qrCalls).toBe(1));
    expect(screen.queryByTestId('party-stage-qr')).not.toBeInTheDocument();

    await act(async () => { await vi.advanceTimersByTimeAsync(1_100); });
    await waitFor(() => expect(qrCalls).toBe(2));
    await act(async () => { await vi.advanceTimersByTimeAsync(2_100); });
    await waitFor(() => expect(screen.getByTestId('party-stage-qr')).toBeInTheDocument());
    expect(qrCalls).toBe(3);
  });

  it('does not ask again when the server has answered', async () => {
    let qrCalls = 0;
    installFetchMock({
      [`GET ${DISPLAY}`]: () => jsonResponse(snapshot()),
      [`GET ${QR}`]: () => { qrCalls += 1; return errorResponse(404); },
    });
    mount();
    await waitFor(() => expect(qrCalls).toBe(1));
    await act(async () => { await vi.advanceTimersByTimeAsync(40_000); });
    expect(qrCalls).toBe(1);
  });

  it('stops retrying the code the moment the lobby is gone', async () => {
    let reads = 0;
    let qrCalls = 0;
    installFetchMock({
      [`GET ${DISPLAY}`]: () => {
        reads += 1;
        return jsonResponse(reads === 1 ? snapshot() : snapshot({
          status: 'live', phase: 'challenge_active', roundNumber: 1, roundId: 'r1',
          challenge: activity(),
        }));
      },
      [`GET ${QR}`]: () => { qrCalls += 1; return errorResponse(503); },
    });
    mount();
    await waitFor(() => expect(qrCalls).toBeGreaterThanOrEqual(1));
    await act(async () => { await vi.advanceTimersByTimeAsync(3_000); });
    await waitFor(() => expect(screen.getByTestId('party-tv-stage'))
      .toHaveAttribute('data-scene', 'active'));
    const settled = qrCalls;
    await act(async () => { await vi.advanceTimersByTimeAsync(90_000); });
    expect(qrCalls).toBe(settled);
  });

  it('reports a refused code to the shell instead of retrying it', async () => {
    const posted: string[] = [];
    vi.stubGlobal('ReactNativeWebView', { postMessage: (d: string) => posted.push(d) });
    let qrCalls = 0;
    installFetchMock({
      [`GET ${DISPLAY}`]: () => jsonResponse(snapshot()),
      [`GET ${QR}`]: () => { qrCalls += 1; return errorResponse(401); },
    });
    mount();
    await waitFor(() => expect(posted.some((raw) => raw.includes('display-auth-failed'))).toBe(true));
    await act(async () => { await vi.advanceTimersByTimeAsync(40_000); });
    expect(qrCalls).toBe(1);
  });

  it('retries the activity photograph after a failure that was not an answer', async () => {
    const create = vi.fn(() => 'blob:retried');
    Object.defineProperty(URL, 'createObjectURL', { value: create, configurable: true, writable: true });
    let mediaCalls = 0;
    installFetchMock({
      [`GET ${DISPLAY}`]: () => jsonResponse(snapshot({
        status: 'live', phase: 'challenge_active', roundNumber: 1, roundId: 'r1',
        challenge: activity({ mediaUrl: '/api/party-display/challenges/c1/media' }),
      })),
      'GET /api/party-display/challenges/c1/media': () => {
        mediaCalls += 1;
        return mediaCalls === 1 ? errorResponse(502) : new Response('image-bytes', { status: 200 });
      },
    });
    mount();
    await waitFor(() => expect(mediaCalls).toBe(1));
    expect(create).not.toHaveBeenCalled();
    await act(async () => { await vi.advanceTimersByTimeAsync(1_100); });
    await waitFor(() => expect(create).toHaveBeenCalledTimes(1));
    expect(mediaCalls).toBe(2);
  });

  it("a new activity takes the last one's photograph down and cancels its work", async () => {
    let n = 0;
    const create = vi.fn(() => `blob:${n++}`);
    const revoke = vi.fn();
    Object.defineProperty(URL, 'createObjectURL', { value: create, configurable: true, writable: true });
    Object.defineProperty(URL, 'revokeObjectURL', { value: revoke, configurable: true, writable: true });
    let reads = 0;
    let firstCalls = 0;
    let secondCalls = 0;
    installFetchMock({
      [`GET ${DISPLAY}`]: () => {
        reads += 1;
        return jsonResponse(reads === 1
          ? snapshot({
            status: 'live', phase: 'challenge_active', roundNumber: 1, roundId: 'r1',
            challenge: activity({ mediaUrl: '/api/party-display/challenges/c1/media' }),
          })
          : snapshot({
            status: 'live', phase: 'challenge_active', roundNumber: 2, roundId: 'r2',
            challenge: activity({ id: 'c2', title: 'Balla', mediaUrl: '/api/party-display/challenges/c2/media' }),
          }));
      },
      'GET /api/party-display/challenges/c1/media': () => { firstCalls += 1; return new Response('one', { status: 200 }); },
      'GET /api/party-display/challenges/c2/media': () => { secondCalls += 1; return errorResponse(503); },
    });
    mount();
    await waitFor(() => expect(create).toHaveBeenCalledTimes(1));

    // The host moved on. The old picture is revoked at once rather than left
    // on the new activity's card while the new one is still loading.
    await act(async () => { await vi.advanceTimersByTimeAsync(2_600); });
    await waitFor(() => expect(revoke).toHaveBeenCalledWith('blob:0'));
    expect(document.querySelector('img[src="blob:0"]')).toBeNull();

    // The new one keeps being asked for, on a backoff; the old one never again.
    await act(async () => { await vi.advanceTimersByTimeAsync(20_000); });
    expect(firstCalls).toBe(1);
    expect(secondCalls).toBeGreaterThan(1);
    expect(secondCalls).toBeLessThan(10);
  });

  it('reports a refused photograph to the shell', async () => {
    const posted: string[] = [];
    vi.stubGlobal('ReactNativeWebView', { postMessage: (d: string) => posted.push(d) });
    installFetchMock({
      [`GET ${DISPLAY}`]: () => jsonResponse(snapshot({
        status: 'live', phase: 'challenge_active', roundNumber: 1, roundId: 'r1',
        challenge: activity({ mediaUrl: '/api/party-display/challenges/c1/media' }),
      })),
      'GET /api/party-display/challenges/c1/media': () => errorResponse(401),
    });
    mount();
    await waitFor(() => expect(posted.some((raw) => raw.includes('display-auth-failed'))).toBe(true));
    // The photograph is fetched with the header, and the URL never carries it.
    for (const raw of posted) expect(raw).not.toContain(GRANT);
  });
});

describe('the public party television is unchanged', () => {
  it('still authorises with the party token and builds its own QR', async () => {
    const mock = installFetchMock({
      'GET /api/party/tok-1/game': () => jsonResponse(snapshot()),
    });
    render(
      <I18nProvider>
        <MemoryRouter initialEntries={['/party/tok-1/tv']}>
          <Routes>
            <Route path="/party/:token/tv" element={<PartyTvStagePage />} />
          </Routes>
        </MemoryRouter>
      </I18nProvider>,
    );
    // It reaches the GUEST surface, as it always has — the display route's
    // existence must not have changed the public one.
    await waitFor(() => expect(
      mock.calls.some((c) => c.url.includes('/api/party/tok-1/game'))).toBe(true));
    expect(mock.calls.some((c) => c.url.includes('/api/party-display/'))).toBe(false);
  });
});
