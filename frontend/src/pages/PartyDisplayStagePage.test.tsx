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
