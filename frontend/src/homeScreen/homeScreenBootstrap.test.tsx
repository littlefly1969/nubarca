import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, render, screen } from '@testing-library/react';
import { MemoryRouter, useNavigate } from 'react-router';
import {
  PRODUCT_APP,
  HomeScreenAppCanonical,
  HomeScreenAppHead,
  homeScreenAppBasename,
  homeScreenAppForPath,
  homeScreenAppNavigation,
  restartAlbumShareApp,
  useHomeScreenAppMismatch,
  type HomeScreenAppLinks,
} from './homeScreen';

// WHICH APP a page is, decided from its address alone — and the link a browser
// reads to install it BORN with that address. index.html carries no manifest
// link of its own: its bootstrap creates it. These tests run the real script
// from index.html, hold it to homeScreenAppForPath's table, follow
// <HomeScreenAppHead /> through in-app navigation, move an entry link to its
// app's own path, and hold an album's app to its own album.

const here = dirname(fileURLToPath(import.meta.url));
const INDEX_HTML = readFileSync(resolve(here, '../../index.html'), 'utf8');

function bootstrapSource(): string {
  const scripts = [...INDEX_HTML.matchAll(/<script>([\s\S]*?)<\/script>/g)].map((m) => m[1]);
  const app = scripts.find((s) => s.includes('Home-screen app bootstrap'));
  if (!app) throw new Error('index.html no longer contains the home-screen app bootstrap.');
  return app;
}

/** The head as index.html's own markup leaves it — scripts aside — then the bootstrap, at `pathname`. */
function bootAt(pathname: string): MutationRecord[] {
  document.head.innerHTML = '';
  window.history.replaceState(null, '', pathname);
  const observer = new MutationObserver(() => {});
  observer.observe(document.head, { childList: true, subtree: true, attributes: true });
  // eslint-disable-next-line @typescript-eslint/no-implied-eval
  new Function(bootstrapSource())();
  const records = observer.takeRecords();
  observer.disconnect();
  return records;
}

const links = (rel: string) => [...document.head.querySelectorAll(`link[rel="${rel}"]`)];
const href = (rel: string) => links(rel)[0]?.getAttribute('href');
const headApp = (): HomeScreenAppLinks => ({
  manifestUrl: href('manifest') ?? null,
  manifestCredentials: links('manifest')[0]?.getAttribute('crossorigin') === 'use-credentials',
  iconUrl: href('apple-touch-icon')!,
});

// Apps as the server names them: /party/app/<32 hex>, /album/app/<32 hex>.
const PARTY_APP = '/party/app/0123456789abcdef0123456789abcdef';
const ALBUM_A = '/album/app/1111111111111111aaaaaaaaaaaaaaaa';
const ALBUM_B = '/album/app/2222222222222222bbbbbbbbbbbbbbbb';
const ALBUM_TOKEN = 'AlbumLinkToken_-0123456789abcdefghijklmnopq';

const PARTY: HomeScreenAppLinks = {
  manifestUrl: '/api/party/QrToken_-123/app-manifest', manifestCredentials: false,
  iconUrl: '/api/party/QrToken_-123/app-icon/192',
};
const INVITE: HomeScreenAppLinks = {
  manifestUrl: '/api/party-invitations/InviteTok/app-manifest', manifestCredentials: false,
  iconUrl: '/api/party-invitations/InviteTok/app-icon/192',
};
const ALBUM: HomeScreenAppLinks = {
  manifestUrl: `/api/album-share/${ALBUM_TOKEN}/app-manifest`, manifestCredentials: true,
  iconUrl: `/api/album-share/${ALBUM_TOKEN}/app-icon/192`,
};
const NO_MANIFEST: HomeScreenAppLinks = { manifestUrl: null, manifestCredentials: false, iconUrl: PRODUCT_APP.iconUrl };

const TABLE: [string, HomeScreenAppLinks][] = [
  // The product.
  ['/', PRODUCT_APP],
  ['/albums', PRODUCT_APP],
  // The links guests hold.
  ['/party/QrToken_-123', PARTY],
  ['/party/QrToken_-123/', PARTY],
  ['/party/invite/InviteTok', INVITE],
  [`/album/${ALBUM_TOKEN}`, ALBUM],
  [`/album/${ALBUM_TOKEN}/`, ALBUM],
  // The same pages under their own app.
  [`${PARTY_APP}/party/QrToken_-123`, PARTY],
  [`${PARTY_APP}/party/QrToken_-123/`, PARTY],
  [`${PARTY_APP}/party/invite/InviteTok`, INVITE],
  [`${ALBUM_A}/open/${ALBUM_TOKEN}`, ALBUM],
  [`${ALBUM_A}/open/${ALBUM_TOKEN}/`, ALBUM],
  // Another page of an app: no manifest — never another app's.
  [`${PARTY_APP}/party/UploadTok/upload`, NO_MANIFEST],
  [`${PARTY_APP}/party/crew/123`, NO_MANIFEST],
  [`${PARTY_APP}/albums`, NO_MANIFEST],
  [`${PARTY_APP}/`, NO_MANIFEST],
  [PARTY_APP, NO_MANIFEST],
  [`${ALBUM_A}/`, NO_MANIFEST],
  [ALBUM_A, NO_MANIFEST],
  [`${ALBUM_A}/open`, NO_MANIFEST],
  [`${ALBUM_A}/open/${ALBUM_TOKEN}/extra`, NO_MANIFEST],
  // One kind's page inside the other kind's app is neither.
  [`${ALBUM_A}/party/QrToken_-123`, NO_MANIFEST],
  [`${ALBUM_A}/album/${ALBUM_TOKEN}`, NO_MANIFEST],
  [`${PARTY_APP}/open/${ALBUM_TOKEN}`, NO_MANIFEST],
  [`${PARTY_APP}/album/${ALBUM_TOKEN}`, NO_MANIFEST],
  // Not an app's path: a key is exactly 32 lowercase hex.
  ['/party/app/0123456789ABCDEF0123456789ABCDEF/party/QrToken_-123', PRODUCT_APP],
  ['/party/app/0123456789abcdef0123456789abcde/party/QrToken_-123', PRODUCT_APP],
  ['/party/app/0123456789abcdef0123456789abcdef0/party/QrToken_-123', PRODUCT_APP],
  [`/album/app/1111111111111111AAAAAAAAAAAAAAAA/open/${ALBUM_TOKEN}`, PRODUCT_APP],
  ['/party/app', PRODUCT_APP],
  ['/album/app', PRODUCT_APP],
  ['/album/app/', PRODUCT_APP],
  // The party's other pages carry other capabilities' tokens.
  ['/party/UploadTok/upload', PRODUCT_APP],
  ['/party/PrintTok/print', PRODUCT_APP],
  ['/party/crew/123', PRODUCT_APP],
  ['/party/crew', PRODUCT_APP],
  ['/party/invite', PRODUCT_APP],
  ['/party-display/stage', PRODUCT_APP],
  ['/party/bad%20token', PRODUCT_APP],
  [`/album/${ALBUM_TOKEN}/photos`, PRODUCT_APP],
  ['/album/bad%20token', PRODUCT_APP],
];

afterEach(() => {
  cleanup();
  vi.useRealTimers();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  window.history.replaceState(null, '', '/');
  document.head.innerHTML = '';
});

describe('index.html carries no app of its own', () => {
  it('has no static manifest or apple-touch-icon link for a browser to read first', () => {
    const markup = INDEX_HTML.replace(/<script>[\s\S]*?<\/script>/g, '');
    expect(markup).not.toMatch(/<link[^>]*rel="manifest"/);
    expect(markup).not.toMatch(/<link[^>]*rel="apple-touch-icon"/);
  });

  it('runs the bootstrap in the head, before the application bundle', () => {
    const bootstrap = INDEX_HTML.indexOf('Home-screen app bootstrap');
    expect(bootstrap).toBeGreaterThan(-1);
    expect(bootstrap).toBeLessThan(INDEX_HTML.indexOf('</head>'));
    expect(bootstrap).toBeLessThan(INDEX_HTML.indexOf('src="/src/main.tsx"'));
  });
});

describe('the bootstrap creates the app the address names', () => {
  it.each(TABLE)('%s', (pathname, app) => {
    const records = bootAt(pathname);

    // Exactly one icon, and one manifest unless the page must have none…
    expect(links('apple-touch-icon')).toHaveLength(1);
    expect(links('manifest')).toHaveLength(app.manifestUrl === null ? 0 : 1);
    // …already pointing where it must, asked for with the cookie when it is
    // an album's…
    expect(headApp()).toEqual(app);
    // …from the moment it exists: added once, never re-pointed afterwards.
    expect(records.filter((r) => r.type === 'attributes')).toEqual([]);
    expect(records.flatMap((r) => [...r.addedNodes])).toHaveLength(app.manifestUrl === null ? 1 : 2);
    // And it is homeScreenAppForPath's answer, row for row.
    expect(homeScreenAppForPath(pathname)).toEqual(headApp());
  });

  it('knows an app by its own path only', () => {
    expect(homeScreenAppBasename(`${PARTY_APP}/party/invite/InviteTok`)).toBe(PARTY_APP);
    expect(homeScreenAppBasename(`${ALBUM_A}/open/${ALBUM_TOKEN}`)).toBe(ALBUM_A);
    expect(homeScreenAppBasename(ALBUM_B)).toBe(ALBUM_B);
    expect(homeScreenAppBasename('/party/QrToken')).toBeUndefined();
    expect(homeScreenAppBasename(`/album/${ALBUM_TOKEN}`)).toBeUndefined();
    expect(homeScreenAppBasename('/party/app/')).toBeUndefined();
    expect(homeScreenAppBasename(`${PARTY_APP}x/party/QrToken`)).toBeUndefined();
    expect(homeScreenAppBasename(`/photos/app/${'a'.repeat(32)}`)).toBeUndefined();
  });
});

describe('<HomeScreenAppHead />', () => {
  function Navigator({ to }: { to: { current: ((path: string) => void) | null } }) {
    const navigate = useNavigate();
    to.current = navigate;
    return null;
  }

  it('follows in-app navigation, from the address alone, keeping one link of each', () => {
    bootAt('/party/invite/InviteTok');
    const navigate: { current: ((path: string) => void) | null } = { current: null };
    render(
      <MemoryRouter initialEntries={['/party/invite/InviteTok']}>
        <HomeScreenAppHead />
        <Navigator to={navigate} />
      </MemoryRouter>,
    );
    expect(href('manifest')).toBe('/api/party-invitations/InviteTok/app-manifest');

    // "Entra nel Party": the invitation leads into the party's own page.
    act(() => navigate.current!('/party/QrToken'));
    expect(href('manifest')).toBe('/api/party/QrToken/app-manifest');
    expect(href('apple-touch-icon')).toBe('/api/party/QrToken/app-icon/192');

    act(() => navigate.current!(`/album/${ALBUM_TOKEN}`));
    expect(headApp()).toEqual(ALBUM);

    act(() => navigate.current!('/albums'));
    expect(headApp()).toEqual(PRODUCT_APP);

    act(() => navigate.current!('/party/invite/Other'));
    expect(href('manifest')).toBe('/api/party-invitations/Other/app-manifest');
    expect(links('manifest')).toHaveLength(1);
    expect(links('apple-touch-icon')).toHaveLength(1);
  });

  it('creates the links once if a page somehow has none, never twice', () => {
    document.head.innerHTML = '';
    const { rerender } = render(
      <MemoryRouter initialEntries={['/party/QrToken']}>
        <HomeScreenAppHead />
      </MemoryRouter>,
    );
    rerender(
      <MemoryRouter initialEntries={['/party/QrToken']}>
        <HomeScreenAppHead />
      </MemoryRouter>,
    );
    expect(links('manifest')).toHaveLength(1);
    expect(href('manifest')).toBe('/api/party/QrToken/app-manifest');
  });

  it.each([
    ['a party', PARTY_APP, '/party/invite/InviteTok', ['/party/QrToken', '/party/UploadTok/upload']],
    ['an album', ALBUM_A, `/open/${ALBUM_TOKEN}`, ['/albums', `/open/${ALBUM_TOKEN}`]],
  ])("leaves %s app's document as the bootstrap made it, wherever the app goes", (_label, base, entry, moves) => {
    bootAt(`${base}${entry}`);
    const before = headApp();
    const navigate: { current: ((path: string) => void) | null } = { current: null };
    const observer = new MutationObserver(() => {});
    observer.observe(document.head, { childList: true, subtree: true, attributes: true });
    render(
      <MemoryRouter basename={base} initialEntries={[`${base}${entry}`]}>
        <HomeScreenAppHead basename={base} />
        <Navigator to={navigate} />
      </MemoryRouter>,
    );

    for (const move of moves) act(() => navigate.current!(move));
    expect(observer.takeRecords()).toEqual([]);
    observer.disconnect();
    expect(headApp()).toEqual(before);
  });
});

describe('<HomeScreenAppCanonical />', () => {
  function manifestAnswering(body: Record<string, unknown>, status = 200) {
    const fetchMock = vi.fn(async (_url: string, _init?: RequestInit) => new Response(JSON.stringify(body), { status }));
    vi.stubGlobal('fetch', fetchMock);
    return fetchMock;
  }

  function MismatchProbe() {
    return <p>{useHomeScreenAppMismatch() ? 'mismatch' : 'the page'}</p>;
  }

  function mountAt(path: string) {
    window.history.replaceState(null, '', path);
    const replace = vi.spyOn(homeScreenAppNavigation, 'replace').mockImplementation(() => {});
    render(
      <HomeScreenAppCanonical>
        <MismatchProbe />
      </HomeScreenAppCanonical>,
    );
    return replace;
  }

  describe('an entry link moves under its app', () => {
    it.each([
      ['/party/QrToken_-123', '/api/party/QrToken_-123/app-manifest', PARTY_APP, `${PARTY_APP}/party/QrToken_-123`],
      ['/party/QrToken_-123/?lang=it#photos', '/api/party/QrToken_-123/app-manifest', PARTY_APP, `${PARTY_APP}/party/QrToken_-123/?lang=it#photos`],
      ['/party/invite/InviteTok', '/api/party-invitations/InviteTok/app-manifest', PARTY_APP, `${PARTY_APP}/party/invite/InviteTok`],
      [`/album/${ALBUM_TOKEN}`, `/api/album-share/${ALBUM_TOKEN}/app-manifest`, ALBUM_A, `${ALBUM_A}/open/${ALBUM_TOKEN}`],
      [`/album/${ALBUM_TOKEN}/?x=1#top`, `/api/album-share/${ALBUM_TOKEN}/app-manifest`, ALBUM_A, `${ALBUM_A}/open/${ALBUM_TOKEN}?x=1#top`],
    ])('%s → its app, before anything is drawn', async (path, manifestUrl, app, canonical) => {
      const fetchMock = manifestAnswering({ id: app, scope: `${app}/` });
      const replace = mountAt(path);
      expect(screen.queryByText('the page')).toBeNull();

      await vi.waitFor(() => expect(replace).toHaveBeenCalledWith(canonical));
      expect(fetchMock).toHaveBeenCalledTimes(1);
      // Same-origin, so an album's device cookie rides along.
      expect(fetchMock.mock.calls[0]).toEqual([manifestUrl, expect.objectContaining({ credentials: 'same-origin' })]);
      expect(screen.queryByText('the page')).toBeNull();
    });

    it.each([
      ['the shared scope every party used to claim', '/party/QrToken', '/party/'],
      ['the whole of /album/', `/album/${ALBUM_TOKEN}`, '/album/'],
      ['the product', '/party/QrToken', '/'],
      ['no scope', '/party/QrToken', undefined],
      ['a key that is not one', '/party/QrToken', '/party/app/not-a-key/'],
      ['another origin', '/party/QrToken', `https://elsewhere.example${PARTY_APP}/`],
      ["an album's app, for a party's link", '/party/QrToken', `${ALBUM_A}/`],
      ["a party's app, for an album's link", `/album/${ALBUM_TOKEN}`, `${PARTY_APP}/`],
    ])('never moves to %s: the page stays', async (_label, path, scope) => {
      manifestAnswering({ scope });
      const replace = mountAt(path);

      expect(await screen.findByText('the page')).toBeTruthy();
      expect(replace).not.toHaveBeenCalled();
    });

    it.each([
      ['there is no party behind the link', '/party/Unknown', 404],
      ['the link was revoked', `/album/${ALBUM_TOKEN}`, 404],
      ['the album asks for its second factor first', `/album/${ALBUM_TOKEN}`, 401],
    ])('stays where it is when %s', async (_label, path, status) => {
      manifestAnswering({ error: 'x' }, status);
      const replace = mountAt(path);

      expect(await screen.findByText('the page')).toBeTruthy();
      expect(replace).not.toHaveBeenCalled();
    });

    it('stays where it is when the server does not answer in time', async () => {
      vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
      vi.stubGlobal('fetch', vi.fn((_url: string, init: RequestInit) => new Promise((_resolve, reject) => {
        init.signal!.addEventListener('abort', () => reject(new DOMException('aborted', 'AbortError')));
      })));
      const replace = mountAt(`/album/${ALBUM_TOKEN}`);
      expect(screen.queryByText('the page')).toBeNull();

      await act(async () => { await vi.advanceTimersByTimeAsync(3000); });

      expect(screen.getByText('the page')).toBeTruthy();
      expect(replace).not.toHaveBeenCalled();
    });
  });

  describe("an album's app is its own album's", () => {
    it('draws the album when the key is the one its token opens', async () => {
      const fetchMock = manifestAnswering({ id: ALBUM_A, scope: `${ALBUM_A}/` });
      const replace = mountAt(`${ALBUM_A}/open/${ALBUM_TOKEN}`);
      expect(screen.queryByText('the page')).toBeNull();

      expect(await screen.findByText('the page')).toBeTruthy();
      expect(fetchMock.mock.calls[0][0]).toBe(`/api/album-share/${ALBUM_TOKEN}/app-manifest`);
      expect(replace).not.toHaveBeenCalled();
    });

    it("says a made-up address — album A's key, album B's token — is not available, and moves nowhere", async () => {
      manifestAnswering({ id: ALBUM_B, scope: `${ALBUM_B}/` });
      const replace = mountAt(`${ALBUM_A}/open/${ALBUM_TOKEN}`);

      expect(await screen.findByText('mismatch')).toBeTruthy();
      expect(screen.queryByText('the page')).toBeNull();
      expect(replace).not.toHaveBeenCalled();
    });

    it.each([401, 404])('leaves a %s for the page to say', async (status) => {
      manifestAnswering({ error: 'x' }, status);
      mountAt(`${ALBUM_A}/open/${ALBUM_TOKEN}`);
      expect(await screen.findByText('the page')).toBeTruthy();
    });
  });

  it.each([
    ["a page already inside a party's app", `${PARTY_APP}/party/QrToken`],
    ["another page of an album's app", `${ALBUM_A}/`],
    ['any page that is not an entry link', '/party/UploadTok/upload'],
    ['the product', '/albums'],
  ])('draws %s at once, asking nothing', (_label, path) => {
    const fetchMock = manifestAnswering({ scope: `${PARTY_APP}/` });
    const replace = mountAt(path);

    expect(screen.getByText('the page')).toBeTruthy();
    expect(fetchMock).not.toHaveBeenCalled();
    expect(replace).not.toHaveBeenCalled();
  });
});

describe('restartAlbumShareApp', () => {
  it.each([
    [`/album/${ALBUM_TOKEN}`, true],
    [`${ALBUM_A}/open/${ALBUM_TOKEN}`, true],
    ['/party/QrToken', false],
    ['/albums', false],
    [`${ALBUM_A}/`, false],
  ])('at %s starts the page again: %s', (path, restarts) => {
    window.history.replaceState(null, '', path);
    const reload = vi.spyOn(homeScreenAppNavigation, 'reload').mockImplementation(() => {});

    expect(restartAlbumShareApp()).toBe(restarts);
    expect(reload).toHaveBeenCalledTimes(restarts ? 1 : 0);
  });
});
