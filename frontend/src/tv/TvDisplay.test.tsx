import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import { I18nProvider } from '../i18n';
import { emptyResponse, errorResponse, jsonResponse, type InstalledFetchMock } from '../test-utils';
import { TvDisplay } from './TvDisplay';
import { DisplayPlatformContext } from './platform/displayPlatform';
import { createFakePlatform, type FakePlatform } from './testing/fakePlatform';
import { installTvMock, MEDIA_PLAYBACK, TV_SESSION } from './testing/tvMock';
import { CONTROL_POLL_MS } from './semantics/assignmentView';
import { HERO_DURATION_MS, HERO_EVERY_N_BOUNDARIES } from './semantics/partyMessages';
import { VIDEO_PREPARING_GRACE_MS } from './semantics/partySlideshow';
import { clearTvDiagnostics, readTvDiagnostics } from './diagnostics';

/**
 * A BROWSER RUNNING /tv IS A NUBARCA DISPLAY.
 *
 * These drive the whole display — pairing, admission, the control plane, the
 * assigned party, resume, the network and the wake lock — against a mocked
 * server and a fake platform, on fake timers. Each describe block is one of the
 * guarantees the display makes; the native app makes the same ones, and the
 * rules behind them are held to the app's by semantics/nativeParity.test.ts.
 */

vi.mock('qrcode', () => ({
  default: { toString: vi.fn(async () => '<svg data-testid="generated-qr"></svg>') },
}));

let platform: FakePlatform;
const play = vi.fn(() => Promise.resolve());

beforeEach(() => {
  vi.useFakeTimers();
  platform = createFakePlatform();
  clearTvDiagnostics();
  play.mockImplementation(() => Promise.resolve());
  Object.defineProperty(HTMLMediaElement.prototype, 'play', { configurable: true, value: play });
  Object.defineProperty(HTMLMediaElement.prototype, 'pause', { configurable: true, value: vi.fn() });
  Object.defineProperty(HTMLMediaElement.prototype, 'load', { configurable: true, value: vi.fn() });
});

afterEach(() => {
  cleanup();
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

function mount() {
  return render(
    <I18nProvider>
      <MemoryRouter>
        <DisplayPlatformContext.Provider value={platform}>
          <TvDisplay />
        </DisplayPlatformContext.Provider>
      </MemoryRouter>
    </I18nProvider>,
  );
}

async function advance(ms: number) {
  await act(async () => { await vi.advanceTimersByTimeAsync(ms); });
}

async function settle() {
  for (let i = 0; i < 8; i += 1) await advance(1);
}

const flow = () => screen.getByTestId('tv-display').getAttribute('data-flow');
const calls = (mock: InstalledFetchMock, method: string, path: string) =>
  mock.calls.filter((c) => c.method === method && c.url.split('?')[0] === path).length;

type Presentation = 'slideshow' | 'game' | 'unavailable';
const party = (key: string, albumId: string, presentation: Presentation) => ({
  kind: 'party', albumId, albumName: `Festa ${albumId}`,
  partyAvailable: presentation !== 'unavailable', presentation, assignmentKey: key,
});
const GENERAL = TV_SESSION.assignment;
const session = (assignment: unknown) => jsonResponse({ ...TV_SESSION, assignment });

const photo = (id: string) => ({
  id, name: `${id}.jpg`, mediaType: 'image', width: 1600, height: 900,
  thumbnailUrl: `/api/tv/media/${id}/thumbnail`, previewUrl: `/api/tv/media/${id}/preview`,
  posterUrl: null, videoUrl: null, previewStripUrl: null,
});
const video = (id: string) => ({
  ...photo(id), mediaType: 'video',
  posterUrl: `/api/tv/media/${id}/poster`, videoUrl: `/api/tv/media/${id}/video`,
});
const albumItems = (id: string, items: unknown[], timing = { photoSeconds: 5, maxVideoSeconds: 20 }) => ({
  id, name: `Festa ${id}`, items, partyEnabled: true, partyUrl: '/party/tok', partyUploadUrl: null,
  partySlideshow: timing,
});
const greeting = (id: string, text: string, over: Record<string, unknown> = {}) => ({
  id, displayName: 'Anna', text, createdAt: '2027-06-12T20:00:00Z', isHero: false, heroPromotedAt: null, ...over,
});
const GRANT = {
  grant: 'display-grant-0123456789abcdef', expiresAt: '2099-01-01T00:00:00Z', expiresInSeconds: 3600,
};
const LOBBY = {
  albumName: 'Festa', status: 'lobby', phase: 'lobby', version: 0, roundNumber: 0, totalChallenges: 3,
  phaseEndsAt: null, challenge: null, roundId: null, voting: null, myVote: null,
};
const PAIRING = {
  publicCode: 'NEWCODE1', pairingSecret: 'a'.repeat(43),
  approvalUrl: `https://nubarca.test/tv/pair?code=NEWCODE1#secret=${'a'.repeat(43)}`,
  expiresAt: '2026-07-05T12:10:00Z',
};
const progressive = () => new Response('x', { status: 206, headers: { 'content-type': 'video/mp4' } });
const gameHandlers = {
  'POST /api/tv/party-display/grant': () => jsonResponse(GRANT),
  'GET /api/party-display/game': () => jsonResponse(LOBBY),
  'GET /api/party-display/join-qr': () => new Response('<svg></svg>', { status: 200 }),
};

// ---------------------------------------------------------------------------

describe('pairing survives everything but a real answer', () => {
  it('an unpaired browser meets pairing', async () => {
    installTvMock({
      'GET /api/tv/session': () => errorResponse(401),
      'POST /api/tv/pairing/start': () => jsonResponse(PAIRING),
    });
    mount();
    await settle();
    expect(screen.getByTestId('tv-pairing-code')).toHaveTextContent('NEWCODE1');
    expect(screen.queryByTestId('tv-revoked')).not.toBeInTheDocument();
  });

  it('a completed pairing admits the display without a reload', async () => {
    let paired = false;
    installTvMock({
      'GET /api/tv/session': () => (paired ? session(GENERAL) : errorResponse(401)),
      'POST /api/tv/pairing/start': () => jsonResponse(PAIRING),
      'GET /api/tv/pairing/NEWCODE1/status': () => {
        paired = true;
        return jsonResponse({ status: 'paired', expiresAt: PAIRING.expiresAt });
      },
    });
    mount();
    await settle();
    await advance(600);
    await settle();
    expect(screen.getByTestId('tv-mode-party')).toBeInTheDocument();
  });

  it('a reload with a valid session never shows pairing', async () => {
    const mock = installTvMock({ 'GET /api/tv/session': () => session(GENERAL) });
    mount();
    await settle();
    expect(screen.getByTestId('tv-mode-party')).toBeInTheDocument();
    cleanup();
    mount();
    await settle();
    expect(screen.getByTestId('tv-mode-party')).toBeInTheDocument();
    expect(calls(mock, 'POST', '/api/tv/pairing/start')).toBe(0);
  });

  it('keeps its pairing through a boot without network, and retries until the server answers', async () => {
    let failures = 3;
    const mock = installTvMock({
      'GET /api/tv/session': () => {
        if (failures > 0) {
          failures -= 1;
          throw new TypeError('Failed to fetch');
        }
        return session(GENERAL);
      },
    });
    mount();
    await settle();
    // Not pairing: a mini-PC that powers on before its network must not unpair itself.
    expect(screen.getByTestId('tv-connecting')).toHaveTextContent('Riconnessione…');
    expect(calls(mock, 'POST', '/api/tv/pairing/start')).toBe(0);
    await advance(2_000 + 4_000 + 8_000);
    await settle();
    expect(screen.getByTestId('tv-mode-party')).toBeInTheDocument();
    expect(calls(mock, 'POST', '/api/tv/pairing/start')).toBe(0);
  });

  it('only a 401 unpairs a display that is running', async () => {
    let answer: 'ok' | '500' | '401' = 'ok';
    installTvMock({
      'GET /api/tv/session': () => (answer === 'ok' ? session(GENERAL)
        : answer === '500' ? errorResponse(500) : errorResponse(401)),
      'POST /api/tv/pairing/start': () => jsonResponse(PAIRING),
    });
    mount();
    await settle();
    expect(flow()).toBe('mode');

    answer = '500';
    await advance(CONTROL_POLL_MS);
    await settle();
    expect(flow()).toBe('mode');
    expect(screen.getByTestId('tv-reconnecting')).toBeInTheDocument();

    answer = '401';
    await advance(30_000);
    await settle();
    expect(flow()).toBe('pairing');
    expect(screen.getByTestId('tv-revoked')).toHaveTextContent('Questa sessione TV è stata revocata.');
  });
});

describe('the first answer already carries the assignment', () => {
  function watchModeSelector() {
    const seen = { mode: false };
    const observer = new MutationObserver(() => {
      if (document.querySelector('[data-testid="tv-mode-party"]')) seen.mode = true;
    });
    observer.observe(document.body, { childList: true, subtree: true });
    return { seen, stop: () => observer.disconnect() };
  }

  it('a general display opens on the mode selector', async () => {
    installTvMock({ 'GET /api/tv/session': () => session(GENERAL) });
    mount();
    await settle();
    expect(flow()).toBe('mode');
  });

  it('a display assigned to a party starts IN its slideshow, never on the mode selector', async () => {
    const watch = watchModeSelector();
    installTvMock({
      'GET /api/tv/session': () => session(party('kA', 'a1', 'slideshow')),
      'GET /api/tv/albums/a1/items': () => jsonResponse(albumItems('a1', [photo('p1'), photo('p2')])),
    });
    mount();
    await settle();
    watch.stop();
    expect(screen.getByTestId('tv-party-slideshow')).toBeInTheDocument();
    expect(screen.getByAltText('p1.jpg')).toHaveAttribute('src', '/api/tv/media/p1/preview');
    expect(watch.seen.mode).toBe(false);
  });

  it('a display assigned to a game starts on the game stage, from its own grant', async () => {
    const watch = watchModeSelector();
    const mock = installTvMock({
      'GET /api/tv/session': () => session(party('kA', 'a1', 'game')),
      ...gameHandlers,
    });
    mount();
    await settle();
    watch.stop();
    expect(screen.getByTestId('party-tv-stage')).toBeInTheDocument();
    expect(watch.seen.mode).toBe(false);
    // The grant came from the TV session alone: no body, no party token.
    const mint = mock.calls.find((c) => c.url === '/api/tv/party-display/grant')!;
    expect(mint.body).toBeNull();
    const read = mock.calls.find((c) => c.url === '/api/party-display/game')!;
    expect(new Headers(read.init?.headers).get('X-Party-Display-Grant')).toBe(GRANT.grant);
    expect(mock.calls.some((c) => /\/api\/party\/[^/]+\//.test(c.url))).toBe(false);
  });

  it('a party that cannot be shown fails closed', async () => {
    installTvMock({ 'GET /api/tv/session': () => session(party('kA', 'a1', 'unavailable')) });
    mount();
    await settle();
    expect(screen.getByTestId('tv-party-unavailable')).toHaveTextContent('Questo Party non è disponibile');
    expect(screen.queryByTestId('tv-mode-party')).not.toBeInTheDocument();
  });
});

describe('the owner’s assignment takes the screen, live', () => {
  it('follows general → slideshow → game → slideshow → another party → unavailable → general', async () => {
    let assignment: unknown = GENERAL;
    const mock = installTvMock({
      'GET /api/tv/session': () => session(assignment),
      'GET /api/tv/albums/a1/items': () => jsonResponse(albumItems('a1', [photo('p1')])),
      'GET /api/tv/albums/a2/items': () => jsonResponse(albumItems('a2', [photo('q1')])),
      ...gameHandlers,
    });
    mount();
    await settle();
    expect(flow()).toBe('mode');

    const next = async (value: unknown) => {
      assignment = value;
      await advance(CONTROL_POLL_MS);
      await settle();
    };
    await next(party('kA', 'a1', 'slideshow'));
    expect(screen.getByAltText('p1.jpg')).toBeInTheDocument();
    await next(party('kA', 'a1', 'game'));
    expect(screen.getByTestId('party-tv-stage')).toBeInTheDocument();
    expect(screen.queryByTestId('tv-party-slideshow')).not.toBeInTheDocument();
    await next(party('kA', 'a1', 'slideshow'));
    expect(screen.getByAltText('p1.jpg')).toBeInTheDocument();
    expect(screen.queryByTestId('party-tv-stage')).not.toBeInTheDocument();
    await next(party('kB', 'a2', 'slideshow'));
    expect(screen.getByAltText('q1.jpg')).toBeInTheDocument();
    expect(screen.queryByAltText('p1.jpg')).not.toBeInTheDocument();
    await next(party('kB', 'a2', 'unavailable'));
    expect(screen.getByTestId('tv-party-unavailable')).toBeInTheDocument();
    await next(GENERAL);
    expect(flow()).toBe('mode');
    expect(calls(mock, 'POST', '/api/tv/pairing/start')).toBe(0);
  });

  it('takes the screen from a Personal Area and locks it on the way', async () => {
    let assignment: unknown = GENERAL;
    let locks = 0;
    installTvMock({
      'GET /api/tv/session': () => session(assignment),
      'POST /api/tv/personal/unlock': () => jsonResponse({ unlockToken: 'grant-1', expiresAt: '2099-01-01T00:00:00Z' }),
      'GET /api/tv/personal/home': () => jsonResponse({ displayName: 'Owner', galleryAvailable: true }),
      'POST /api/tv/personal/lock': () => { locks += 1; return emptyResponse(204); },
      'GET /api/tv/albums/a1/items': () => jsonResponse(albumItems('a1', [photo('p1')])),
    });
    mount();
    await settle();
    fireEvent.click(screen.getByTestId('tv-mode-personal'));
    await settle();
    const entry = screen.getByTestId('tv-code-entry');
    for (const key of ['ArrowUp', 'ArrowRight', 'ArrowDown', 'ArrowLeft', 'Enter', 'ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight']) {
      fireEvent.keyDown(entry, { key });
    }
    await settle();
    expect(screen.getByTestId('tv-personal-home')).toBeInTheDocument();

    assignment = party('kA', 'a1', 'slideshow');
    await advance(CONTROL_POLL_MS);
    await settle();
    expect(screen.queryByTestId('tv-personal-home')).not.toBeInTheDocument();
    expect(screen.getByAltText('p1.jpg')).toBeInTheDocument();
    expect(locks).toBe(1);
    expect(readTvDiagnostics().some((e) => e.event === 'tv.personal.preempted')).toBe(true);
  });

  it('BACK on an assigned party never leaves it; it only leaves fullscreen', async () => {
    installTvMock({
      'GET /api/tv/session': () => session(party('kA', 'a1', 'slideshow')),
      'GET /api/tv/albums/a1/items': () => jsonResponse(albumItems('a1', [photo('p1')])),
    });
    mount();
    await settle();
    fireEvent.click(screen.getByTestId('tv-fullscreen'));
    await settle();
    expect(platform.fullscreen).toBe(true);
    fireEvent.keyDown(window, { key: 'Backspace' });
    await settle();
    fireEvent.keyDown(window, { key: 'Backspace' });
    await settle();
    expect(platform.fullscreen).toBe(false);
    expect(flow()).toBe('partySlideshow');
    expect(screen.getByAltText('p1.jpg')).toBeInTheDocument();
  });
});

describe('an old answer can never roll the screen back', () => {
  it('reads the control plane one request at a time; a resume waits and then reads again', async () => {
    let inFlight = 0;
    let maxInFlight = 0;
    const pending: Array<(r: Response) => void> = [];
    let hold = false;
    let assignment: unknown = party('kA', 'a1', 'slideshow');
    installTvMock({
      'GET /api/tv/session': () => {
        const answer = assignment;
        if (!hold) return session(answer);
        inFlight += 1;
        maxInFlight = Math.max(maxInFlight, inFlight);
        return new Promise<Response>((resolve) => {
          pending.push((r) => { inFlight -= 1; resolve(r); });
        }).then(() => session(answer));
      },
      'GET /api/tv/albums/a1/items': () => jsonResponse(albumItems('a1', [photo('p1')])),
      ...gameHandlers,
    });
    mount();
    await settle();
    expect(flow()).toBe('partySlideshow');

    // Request A starts and hangs; it will say "slideshow".
    hold = true;
    await advance(CONTROL_POLL_MS);
    // The server moves on to the game, and the display resumes — asking again.
    assignment = party('kA', 'a1', 'game');
    platform.emit('visible');
    await settle();
    expect(pending.length).toBe(1);
    // A answers late with its stale "slideshow"; the read waiting behind it
    // runs at once and has the last word.
    hold = false;
    pending[0](new Response());
    await settle();
    expect(maxInFlight).toBe(1);
    expect(flow()).toBe('partyGame');
  });
});

describe('the assigned party plays like the app', () => {
  it('holds each photo for the party’s own time', async () => {
    installTvMock({
      'GET /api/tv/session': () => session(party('kA', 'a1', 'slideshow')),
      'GET /api/tv/albums/a1/items': () => jsonResponse(albumItems('a1', [photo('p1'), photo('p2')], { photoSeconds: 5, maxVideoSeconds: 20 })),
    });
    mount();
    await settle();
    expect(screen.getByAltText('p1.jpg')).toBeInTheDocument();
    await advance(4_900);
    expect(screen.getByAltText('p1.jpg')).toBeInTheDocument();
    await advance(200);
    await settle();
    expect(screen.getByAltText('p2.jpg')).toBeInTheDocument();
  });

  it('a challenge holds the wall until the room moves on', async () => {
    let held = false;
    const challenge = { id: 'c1', title: 'Canta!', body: 'Una strofa a squarciagola.', kind: 'dare', mediaUrl: null };
    const hold = { mode: 'challenge_hold', activeChallenge: challenge, nextChallengeAt: null, completedCount: 0 };
    installTvMock({
      'GET /api/tv/session': () => session(party('kA', 'a1', 'slideshow')),
      'GET /api/tv/albums/a1/items': () => jsonResponse(albumItems('a1', [photo('p1'), photo('p2'), photo('p3')])),
      'POST /api/tv/albums/a1/party-playback/boundary': () => { held = true; return jsonResponse(hold); },
      'GET /api/tv/albums/a1/party-playback': () => jsonResponse(held ? hold : MEDIA_PLAYBACK),
      'POST /api/tv/albums/a1/party-playback/next': () => { held = false; return jsonResponse(MEDIA_PLAYBACK); },
    });
    mount();
    await settle();
    await advance(5_000);
    await settle();
    expect(screen.getByTestId('tv-party-challenge')).toHaveTextContent('Canta!');
    // Nothing advances invisibly behind it.
    await advance(15_000);
    await settle();
    expect(screen.getByAltText('p1.jpg')).toBeInTheDocument();

    fireEvent.keyDown(window, { key: 'ArrowRight' });
    await settle();
    expect(screen.queryByTestId('tv-party-challenge')).not.toBeInTheDocument();
    expect(screen.getByAltText('p2.jpg')).toBeInTheDocument();
  });

  it('plays the real video and moves on when it ends', async () => {
    installTvMock({
      'GET /api/tv/session': () => session(party('kA', 'a1', 'slideshow')),
      'GET /api/tv/albums/a1/items': () => jsonResponse(albumItems('a1', [video('v1'), photo('p2')])),
      'GET /api/tv/media/v1/video': progressive,
    });
    mount();
    await settle();
    const element = screen.getByTestId('tv-wall-video-element') as HTMLVideoElement;
    expect(element.getAttribute('src')).toBe('/api/tv/media/v1/video');
    expect(element.getAttribute('poster')).toBe('/api/tv/media/v1/poster');
    expect(element.controls).toBe(false);
    expect(play).toHaveBeenCalled();
    fireEvent.loadedData(element);
    fireEvent.ended(element);
    await settle();
    expect(screen.getByAltText('p2.jpg')).toBeInTheDocument();
  });

  it('cuts a long video at the party’s cap, on media time, exactly once', async () => {
    const boundaries: string[] = [];
    installTvMock({
      'GET /api/tv/session': () => session(party('kA', 'a1', 'slideshow')),
      'GET /api/tv/albums/a1/items': () => jsonResponse(albumItems('a1', [video('v1'), photo('p2'), photo('p3')], { photoSeconds: 5, maxVideoSeconds: 8 })),
      'GET /api/tv/media/v1/video': progressive,
      'POST /api/tv/albums/a1/party-playback/boundary': () => { boundaries.push('b'); return jsonResponse(MEDIA_PLAYBACK); },
    });
    mount();
    await settle();
    const element = screen.getByTestId('tv-wall-video-element') as HTMLVideoElement;
    fireEvent.loadedData(element);
    // Wall-clock time passing while the clip is paused spends nothing.
    await advance(30_000);
    expect(screen.getByTestId('tv-wall-video-element')).toBe(element);
    Object.defineProperty(element, 'currentTime', { configurable: true, value: 8.2 });
    fireEvent.timeUpdate(element);
    fireEvent.timeUpdate(element);
    fireEvent.ended(element);
    await settle();
    expect(screen.getByAltText('p2.jpg')).toBeInTheDocument();
    expect(boundaries).toHaveLength(1);
  });

  it('a video that cannot play never stops the wall', async () => {
    installTvMock({
      'GET /api/tv/session': () => session(party('kA', 'a1', 'slideshow')),
      'GET /api/tv/albums/a1/items': () => jsonResponse(albumItems('a1', [video('v1'), photo('p2')])),
      'GET /api/tv/media/v1/video': () => errorResponse(404),
    });
    mount();
    await settle();
    expect(screen.getByTestId('tv-wall-video')).toHaveAttribute('data-state', 'error');
    await advance(VIDEO_PREPARING_GRACE_MS);
    await settle();
    expect(screen.getByAltText('p2.jpg')).toBeInTheDocument();
    expect(readTvDiagnostics().some((e) => e.event === 'tv.video.error')).toBe(true);
  });

  it('a browser that refuses sound gets the video muted, not a still frame', async () => {
    play.mockImplementationOnce(() => Promise.reject(new DOMException('no gesture', 'NotAllowedError')));
    installTvMock({
      'GET /api/tv/session': () => session(party('kA', 'a1', 'slideshow')),
      'GET /api/tv/albums/a1/items': () => jsonResponse(albumItems('a1', [video('v1')])),
      'GET /api/tv/media/v1/video': progressive,
    });
    mount();
    await settle();
    const element = screen.getByTestId('tv-wall-video-element') as HTMLVideoElement;
    expect(element.muted).toBe(true);
    expect(play).toHaveBeenCalledTimes(2);
    expect(screen.getByTestId('tv-sound-hint')).toBeInTheDocument();
    fireEvent.keyDown(window, { key: 'Enter' });
    await settle();
    expect(element.muted).toBe(false);
  });

  it('a guest upload arrives without moving what is on screen', async () => {
    let items = [photo('p1'), photo('p2')];
    installTvMock({
      'GET /api/tv/session': () => session(party('kA', 'a1', 'slideshow')),
      'GET /api/tv/albums/a1/items': () => jsonResponse(albumItems('a1', items, { photoSeconds: 60, maxVideoSeconds: 20 })),
    });
    mount();
    await settle();
    fireEvent.keyDown(window, { key: 'ArrowRight' });
    await settle();
    expect(screen.getByAltText('p2.jpg')).toBeInTheDocument();
    items = [photo('p1'), photo('p2'), photo('p3')];
    await advance(15_000);
    await settle();
    expect(screen.getByAltText('p2.jpg')).toBeInTheDocument();
    expect(screen.getByTestId('tv-viewer-counter')).toHaveTextContent('2 / 3');
  });
});

describe('greetings, as the app shows them', () => {
  it('runs the band, and slips a Hero in on the tenth boundary for its full time', async () => {
    installTvMock({
      'GET /api/tv/session': () => session(party('kA', 'a1', 'slideshow')),
      'GET /api/tv/albums/a1/items': () => jsonResponse(albumItems('a1', [photo('p1'), photo('p2'), photo('p3')], { photoSeconds: 3, maxVideoSeconds: 20 })),
      'GET /api/tv/albums/a1/party-messages': () => jsonResponse({
        messages: [
          greeting('m1', 'Auguri Marta!'),
          greeting('h1', 'Buon compleanno!', { isHero: true, heroPromotedAt: '2027-06-12T20:05:00Z' }),
        ],
      }),
    });
    mount();
    await settle();
    expect(screen.getByTestId('tv-party-ribbon')).toHaveTextContent('Auguri Marta!');

    for (let i = 1; i < HERO_EVERY_N_BOUNDARIES; i += 1) {
      await advance(3_000);
      await settle();
      expect(screen.queryByTestId('tv-party-hero')).not.toBeInTheDocument();
    }
    const before = document.querySelector('.tv-viewer-media')?.getAttribute('alt');
    await advance(3_000);
    await settle();
    expect(screen.getByTestId('tv-party-hero')).toHaveTextContent('Buon compleanno!');
    // The band steps aside, and the photograph under the card does not move.
    expect(screen.queryByTestId('tv-party-ribbon')).not.toBeInTheDocument();
    await advance(HERO_DURATION_MS - 100);
    expect(screen.getByTestId('tv-party-hero')).toBeInTheDocument();
    expect(document.querySelector('.tv-viewer-media')?.getAttribute('alt')).toBe(before);
    await advance(200);
    await settle();
    expect(screen.queryByTestId('tv-party-hero')).not.toBeInTheDocument();
    expect(document.querySelector('.tv-viewer-media')?.getAttribute('alt')).not.toBe(before);
  });

  it('a greeting arriving or withdrawn moves neither the photograph nor the band', async () => {
    let feed = [greeting('m1', 'Primo'), greeting('m2', 'Secondo'), greeting('m3', 'Terzo')];
    installTvMock({
      'GET /api/tv/session': () => session(party('kA', 'a1', 'slideshow')),
      'GET /api/tv/albums/a1/items': () => jsonResponse(albumItems('a1', [photo('p1'), photo('p2')], { photoSeconds: 60, maxVideoSeconds: 20 })),
      'GET /api/tv/albums/a1/party-messages': () => jsonResponse({ messages: feed }),
    });
    mount();
    await settle();
    fireEvent.keyDown(window, { key: 'ArrowRight' });
    await settle();
    await advance(7_000);
    await settle();
    expect(screen.getByTestId('tv-party-ribbon')).toHaveTextContent('Secondo');

    // The host hides the FIRST greeting while the second is being read, and a
    // new one arrives at the end (the feed is oldest first).
    feed = [greeting('m2', 'Secondo'), greeting('m3', 'Terzo'), greeting('m4', 'Quarto')];
    await advance(5_000);
    await settle();
    expect(screen.getByAltText('p2.jpg')).toBeInTheDocument();
    expect(screen.getByTestId('tv-party-ribbon')).toHaveTextContent('Secondo');
  });
});

describe('a guest’s face search reaches the screen', () => {
  it('narrows, is replaced, is cleared — and never follows the display to another party', async () => {
    let assignment: unknown = party('kA', 'a1', 'slideshow');
    let search: { id: string; items: unknown[] } | null = null;
    installTvMock({
      'GET /api/tv/session': () => session(assignment),
      'GET /api/tv/albums/a1/items': () => jsonResponse(albumItems('a1', [photo('p1'), photo('p2'), photo('p3')], { photoSeconds: 60, maxVideoSeconds: 20 })),
      'GET /api/tv/albums/a2/items': () => jsonResponse(albumItems('a2', [photo('q1')])),
      'GET /api/tv/albums/a1/face-search/active': () => jsonResponse(search
        ? { active: true, searchId: search.id, activationVersion: 1, activatedAt: '2027-06-12T20:00:00Z', faceThumbnailUrl: null, items: search.items }
        : { active: false, searchId: null, activationVersion: null, activatedAt: null, faceThumbnailUrl: null, items: [] }),
    });
    mount();
    await settle();

    search = { id: 's1', items: [photo('p3')] };
    await advance(6_000);
    await settle();
    expect(screen.getByTestId('tv-face-viewer')).toBeInTheDocument();
    expect(screen.getByAltText('p3.jpg')).toBeInTheDocument();

    search = { id: 's2', items: [photo('p2')] };
    await advance(6_000);
    await settle();
    expect(screen.getByAltText('p2.jpg')).toBeInTheDocument();

    search = null;
    await advance(6_000);
    await settle();
    expect(screen.queryByTestId('tv-face-viewer')).not.toBeInTheDocument();
    expect(screen.getByTestId('tv-viewer-counter')).toHaveTextContent('/ 3');

    search = { id: 's3', items: [photo('p1')] };
    await advance(6_000);
    await settle();
    expect(screen.getByTestId('tv-face-viewer')).toBeInTheDocument();
    assignment = party('kB', 'a2', 'slideshow');
    await advance(CONTROL_POLL_MS);
    await settle();
    expect(screen.getByAltText('q1.jpg')).toBeInTheDocument();
    expect(screen.queryByTestId('tv-face-viewer')).not.toBeInTheDocument();
  });
});

describe('coming back from standby', () => {
  it('a page shown again holds the screen again and asks the server at once', async () => {
    const mock = installTvMock({
      'GET /api/tv/session': () => session(party('kA', 'a1', 'slideshow')),
      'GET /api/tv/albums/a1/items': () => jsonResponse(albumItems('a1', [photo('p1')])),
    });
    mount();
    await settle();
    expect(platform.wakeLocksHeld).toBe(1);
    platform.hide();
    await settle();
    expect(platform.wakeLocksHeld).toBe(0);
    await advance(60_000);

    const before = calls(mock, 'GET', '/api/tv/session') + calls(mock, 'POST', '/api/tv/session/heartbeat');
    const itemsBefore = calls(mock, 'GET', '/api/tv/albums/a1/items');
    platform.show();
    await settle();
    expect(platform.wakeLocksHeld).toBe(1);
    expect(calls(mock, 'GET', '/api/tv/session') + calls(mock, 'POST', '/api/tv/session/heartbeat')).toBeGreaterThan(before);
    expect(calls(mock, 'GET', '/api/tv/albums/a1/items')).toBeGreaterThan(itemsBefore);
    expect(screen.getByAltText('p1.jpg')).toBeInTheDocument();
  });

  it('notices a sleep the page never saw from the clock, and re-reads everything', async () => {
    let assignment: unknown = party('kA', 'a1', 'slideshow');
    const mock = installTvMock({
      'GET /api/tv/session': () => session(assignment),
      'GET /api/tv/albums/a1/items': () => jsonResponse(albumItems('a1', [photo('p1')])),
      ...gameHandlers,
    });
    mount();
    await settle();
    // The machine slept; the server moved on while it did.
    assignment = party('kA', 'a1', 'game');
    const reads = calls(mock, 'GET', '/api/tv/session') + calls(mock, 'POST', '/api/tv/session/heartbeat');
    platform.clockOffset += 10 * 60_000;
    await advance(1_000);
    await settle();
    expect(calls(mock, 'GET', '/api/tv/session') + calls(mock, 'POST', '/api/tv/session/heartbeat')).toBe(reads + 1);
    expect(flow()).toBe('partyGame');
    expect(readTvDiagnostics().some((e) => e.event === 'tv.resume' && e.detail?.reason === 'discontinuity')).toBe(true);
  });
});

describe('the network comes and goes', () => {
  it('keeps the party on screen through failures, retries, and recovers', async () => {
    let mode: 'ok' | 'down' | 'reject' = 'ok';
    installTvMock({
      'GET /api/tv/session': () => {
        if (mode === 'reject') throw new TypeError('Failed to fetch');
        return mode === 'down' ? errorResponse(503) : session(party('kA', 'a1', 'slideshow'));
      },
      'GET /api/tv/albums/a1/items': () => {
        if (mode !== 'ok') throw new TypeError('Failed to fetch');
        return jsonResponse(albumItems('a1', [photo('p1')], { photoSeconds: 60, maxVideoSeconds: 20 }));
      },
    });
    mount();
    await settle();
    mode = 'down';
    await advance(CONTROL_POLL_MS);
    await settle();
    mode = 'reject';
    await advance(30_000);
    await settle();
    expect(flow()).toBe('partySlideshow');
    expect(screen.getByAltText('p1.jpg')).toBeInTheDocument();
    expect(screen.getByTestId('tv-reconnecting')).toBeInTheDocument();

    mode = 'ok';
    platform.emit('online');
    await settle();
    expect(screen.queryByTestId('tv-reconnecting')).not.toBeInTheDocument();
    expect(readTvDiagnostics().map((e) => e.event)).toEqual(
      expect.arrayContaining(['tv.network.offline', 'tv.network.restored']));
  });

  it('a request that never answers is abandoned and asked again', async () => {
    let hang = false;
    const mock = installTvMock({
      // Hangs until the display gives up on it, as a half-open socket does.
      'GET /api/tv/session': (req) => (hang
        ? new Promise<Response>((_, reject) => {
          req.init?.signal?.addEventListener('abort', () => reject(new DOMException('aborted', 'AbortError')));
        })
        : session(GENERAL)),
    });
    mount();
    await settle();
    hang = true;
    await advance(CONTROL_POLL_MS);
    const first = calls(mock, 'GET', '/api/tv/session') + calls(mock, 'POST', '/api/tv/session/heartbeat');
    await advance(12_000 + CONTROL_POLL_MS + 100);
    expect(calls(mock, 'GET', '/api/tv/session') + calls(mock, 'POST', '/api/tv/session/heartbeat')).toBeGreaterThan(first);
    expect(flow()).toBe('mode');
  });

  it('a revoked display stops its media and returns to pairing', async () => {
    let revoked = false;
    installTvMock({
      'GET /api/tv/session': () => (revoked ? errorResponse(401) : session(party('kA', 'a1', 'slideshow'))),
      'GET /api/tv/albums/a1/items': () => (revoked ? errorResponse(401)
        : jsonResponse(albumItems('a1', [video('v1')]))),
      'GET /api/tv/media/v1/video': progressive,
      'POST /api/tv/pairing/start': () => jsonResponse(PAIRING),
    });
    mount();
    await settle();
    expect(screen.getByTestId('tv-wall-video-element')).toBeInTheDocument();
    revoked = true;
    await advance(CONTROL_POLL_MS);
    await settle();
    expect(flow()).toBe('pairing');
    expect(screen.queryByTestId('tv-wall-video-element')).not.toBeInTheDocument();
    expect(screen.getByTestId('tv-revoked')).toBeInTheDocument();
    expect(screen.getByTestId('tv-pairing-code')).toHaveTextContent('NEWCODE1');
  });
});

describe('nothing the display waits on can hold it for ever', () => {
  /**
   * A request that never answers until the display gives up on it. `onAbort`
   * runs at the moment of the abort, so "in flight" is counted exactly: an
   * aborted request is dead the instant its signal fires.
   */
  function hanging(req: { init?: RequestInit }, log?: string[], onAbort?: () => void) {
    return new Promise<Response>((_, reject) => {
      req.init?.signal?.addEventListener('abort', () => {
        log?.push('abandoned');
        onAbort?.();
        reject(new DOMException('aborted', 'AbortError'));
      });
    });
  }

  it('an assigned slideshow whose first load never answers asks again and appears', async () => {
    const log: string[] = [];
    let first = true;
    installTvMock({
      'GET /api/tv/session': () => session(party('kA', 'a1', 'slideshow')),
      'GET /api/tv/albums/a1/items': (req) => {
        if (first) {
          first = false;
          log.push('hung');
          return hanging(req, log);
        }
        log.push('read');
        return jsonResponse(albumItems('a1', [photo('p1')]));
      },
    });
    mount();
    await settle();
    expect(screen.getByTestId('tv-party-slideshow-waiting')).toBeInTheDocument();
    await advance(12_000);
    await settle();
    await advance(2_000);
    await settle();
    expect(log.slice(0, 3)).toEqual(['hung', 'abandoned', 'read']);
    expect(screen.getByAltText('p1.jpg')).toBeInTheDocument();
  });

  function mintHandlers(state: { hangFirst: boolean; inFlight: number; max: number; mints: number }) {
    return {
      'POST /api/tv/party-display/grant': (req: { init?: RequestInit }) => {
        state.mints += 1;
        state.inFlight += 1;
        state.max = Math.max(state.max, state.inFlight);
        if (state.hangFirst) {
          state.hangFirst = false;
          return hanging(req, undefined, () => { state.inFlight -= 1; });
        }
        state.inFlight -= 1;
        return jsonResponse(GRANT);
      },
      'GET /api/party-display/game': () => jsonResponse(LOBBY),
      'GET /api/party-display/join-qr': () => new Response('<svg></svg>', { status: 200 }),
    };
  }

  it('a game grant mint that never answers times out and the next one takes the screen', async () => {
    const state = { hangFirst: true, inFlight: 0, max: 0, mints: 0 };
    installTvMock({
      'GET /api/tv/session': () => session(party('kA', 'a1', 'game')),
      ...mintHandlers(state),
    });
    mount();
    await settle();
    expect(screen.getByTestId('tv-party-game-cover')).toBeInTheDocument();
    await advance(12_000);
    await settle();
    await advance(2_000);
    await settle();
    expect(screen.getByTestId('party-tv-stage')).toBeInTheDocument();
    expect(state.mints).toBe(2);
    expect(state.max).toBe(1);
  });

  it('a resume recovers from a mint that is still hanging from before the sleep', async () => {
    const state = { hangFirst: true, inFlight: 0, max: 0, mints: 0 };
    installTvMock({
      'GET /api/tv/session': () => session(party('kA', 'a1', 'game')),
      ...mintHandlers(state),
    });
    mount();
    await settle();
    await advance(3_000);
    // Long before the deadline: the display comes back and asks again now.
    platform.emit('visible');
    await settle();
    expect(screen.getByTestId('party-tv-stage')).toBeInTheDocument();
    expect(state.mints).toBe(2);
    expect(state.max).toBe(1);
  });

  it('a party boundary that never answers cannot freeze the wall, and advances exactly once', async () => {
    let posts = 0;
    installTvMock({
      'GET /api/tv/session': () => session(party('kA', 'a1', 'slideshow')),
      'GET /api/tv/albums/a1/items': () => jsonResponse(albumItems('a1', [photo('p1'), photo('p2'), photo('p3')])),
      'POST /api/tv/albums/a1/party-playback/boundary': (req) => {
        posts += 1;
        return posts === 1 ? hanging(req) : jsonResponse(MEDIA_PLAYBACK);
      },
    });
    mount();
    await settle();
    await advance(5_000);
    await settle();
    // The photograph's time is up and the server is silent: the wall waits…
    expect(screen.getByAltText('p1.jpg')).toBeInTheDocument();
    await advance(8_000);
    await settle();
    // …then carries on, by ONE photograph, not two.
    expect(screen.getByAltText('p2.jpg')).toBeInTheDocument();
    expect(screen.getByTestId('tv-viewer-counter')).toHaveTextContent('2 / 3');
    expect(posts).toBe(1);
    // And keeps rotating with a server that answers again.
    await advance(5_000);
    await settle();
    expect(screen.getByAltText('p3.jpg')).toBeInTheDocument();
    expect(posts).toBe(2);
  });

  it('a video’s end, cap and a hung boundary are still one advance', async () => {
    let posts = 0;
    installTvMock({
      'GET /api/tv/session': () => session(party('kA', 'a1', 'slideshow')),
      'GET /api/tv/albums/a1/items': () => jsonResponse(albumItems('a1', [video('v1'), photo('p2'), photo('p3')], { photoSeconds: 30, maxVideoSeconds: 8 })),
      'GET /api/tv/media/v1/video': progressive,
      'POST /api/tv/albums/a1/party-playback/boundary': (req) => {
        posts += 1;
        return hanging(req);
      },
    });
    mount();
    await settle();
    const element = screen.getByTestId('tv-wall-video-element') as HTMLVideoElement;
    fireEvent.loadedData(element);
    Object.defineProperty(element, 'currentTime', { configurable: true, value: 8.5 });
    fireEvent.timeUpdate(element);
    fireEvent.ended(element);
    fireEvent.ended(element);
    await settle();
    expect(posts).toBe(1);
    await advance(8_000);
    await settle();
    expect(screen.getByAltText('p2.jpg')).toBeInTheDocument();
    expect(screen.getByTestId('tv-viewer-counter')).toHaveTextContent('2 / 3');
    expect(posts).toBe(1);
  });
});

describe('holding the screen awake', () => {
  it('asks while paired, lets go when hidden or unpaired, and survives a refusal', async () => {
    installTvMock({ 'GET /api/tv/session': () => session(GENERAL) });
    const view = mount();
    await settle();
    expect(platform.wakeLockRequests).toBe(1);
    expect(platform.wakeLocksHeld).toBe(1);

    platform.hide();
    await settle();
    expect(platform.wakeLocksHeld).toBe(0);
    platform.show();
    await settle();
    expect(platform.wakeLocksHeld).toBe(1);

    view.unmount();
    await settle();
    expect(platform.wakeLocksHeld).toBe(0);
  });

  it('a browser that refuses the lock still runs the display', async () => {
    platform.wakeLockMode = 'reject';
    installTvMock({
      'GET /api/tv/session': () => session(party('kA', 'a1', 'slideshow')),
      'GET /api/tv/albums/a1/items': () => jsonResponse(albumItems('a1', [photo('p1')])),
    });
    mount();
    await settle();
    expect(screen.getByAltText('p1.jpg')).toBeInTheDocument();
    expect(readTvDiagnostics().some((e) => e.event === 'tv.wakelock.failed')).toBe(true);
  });
});

describe('diagnostics say what happened and nothing more', () => {
  it('never records a grant, a URL or a greeting', async () => {
    installTvMock({
      'GET /api/tv/session': () => session(party('kA', 'a1', 'game')),
      ...gameHandlers,
    });
    mount();
    await settle();
    const log = JSON.stringify(readTvDiagnostics());
    expect(log).not.toContain(GRANT.grant);
    expect(log).not.toContain('/api/');
    expect(log).not.toContain('kA');
  });
});
