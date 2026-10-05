import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, render, screen } from '@testing-library/react';
import { MemoryRouter, useNavigate } from 'react-router';
import {
  PRODUCT_APP,
  PartyAppCanonical,
  PartyAppHead,
  partyAppBasename,
  partyAppForPath,
  partyAppNavigation,
  type PartyAppHeadLinks,
} from './partyHomeScreen';

// WHICH APP a page is, decided from its address alone — and the link a browser
// reads to install it BORN with that address. index.html carries no manifest
// link of its own: its bootstrap creates it. These tests run the real script
// from index.html, hold it to partyAppForPath's table, follow <PartyAppHead />
// through in-app navigation, and move an entry link to its party's own path.

const here = dirname(fileURLToPath(import.meta.url));
const INDEX_HTML = readFileSync(resolve(here, '../../index.html'), 'utf8');

function bootstrapSource(): string {
  const scripts = [...INDEX_HTML.matchAll(/<script>([\s\S]*?)<\/script>/g)].map((m) => m[1]);
  const party = scripts.find((s) => s.includes('Party app bootstrap'));
  if (!party) throw new Error('index.html no longer contains the party app bootstrap.');
  return party;
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

// Two parties' apps, as the server names them: /party/app/<32 hex>.
const APP_A = '/party/app/0123456789abcdef0123456789abcdef';
const APP_B = '/party/app/fedcba9876543210fedcba9876543210';

const PARTY = { manifestUrl: '/api/party/QrToken_-123/app-manifest', iconUrl: '/api/party/QrToken_-123/app-icon/192' };
const INVITE = { manifestUrl: '/api/party-invitations/InviteTok/app-manifest', iconUrl: '/api/party-invitations/InviteTok/app-icon/192' };
const NO_MANIFEST = { manifestUrl: null, iconUrl: PRODUCT_APP.iconUrl };

const TABLE: [string, PartyAppHeadLinks][] = [
  // The links guests hold.
  ['/party/QrToken_-123', PARTY],
  ['/party/QrToken_-123/', PARTY],
  ['/party/invite/InviteTok', INVITE],
  // The same pages under their party's own app.
  [`${APP_A}/party/QrToken_-123`, PARTY],
  [`${APP_A}/party/QrToken_-123/`, PARTY],
  [`${APP_A}/party/invite/InviteTok`, INVITE],
  // Another page of a party's app: no manifest — never another app's.
  [`${APP_A}/party/UploadTok/upload`, NO_MANIFEST],
  [`${APP_A}/party/crew/123`, NO_MANIFEST],
  [`${APP_A}/albums`, NO_MANIFEST],
  [`${APP_A}/`, NO_MANIFEST],
  [APP_A, NO_MANIFEST],
  // Not a party app's path: a key is exactly 32 lowercase hex.
  ['/party/app/0123456789ABCDEF0123456789ABCDEF/party/QrToken_-123', PRODUCT_APP],
  ['/party/app/0123456789abcdef0123456789abcde/party/QrToken_-123', PRODUCT_APP],
  ['/party/app/0123456789abcdef0123456789abcdef0/party/QrToken_-123', PRODUCT_APP],
  ['/party/app', PRODUCT_APP],
  // The party's other pages carry other capabilities' tokens.
  ['/party/UploadTok/upload', PRODUCT_APP],
  ['/party/PrintTok/print', PRODUCT_APP],
  ['/party/crew/123', PRODUCT_APP],
  ['/party/crew', PRODUCT_APP],
  ['/party/invite', PRODUCT_APP],
  ['/party-display/stage', PRODUCT_APP],
  ['/albums', PRODUCT_APP],
  ['/', PRODUCT_APP],
  ['/party/bad%20token', PRODUCT_APP],
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
    const bootstrap = INDEX_HTML.indexOf('Party app bootstrap');
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
    // …already pointing where it must…
    expect(href('manifest') ?? null).toBe(app.manifestUrl);
    expect(href('apple-touch-icon')).toBe(app.iconUrl);
    // …from the moment it exists: added once, never re-pointed afterwards.
    expect(records.filter((r) => r.type === 'attributes')).toEqual([]);
    expect(records.flatMap((r) => [...r.addedNodes])).toHaveLength(app.manifestUrl === null ? 1 : 2);
    // And it is partyAppForPath's answer, row for row.
    expect(partyAppForPath(pathname)).toEqual({ manifestUrl: href('manifest') ?? null, iconUrl: href('apple-touch-icon') });
  });

  it('knows a party app by its own path only', () => {
    expect(partyAppBasename(`${APP_A}/party/invite/InviteTok`)).toBe(APP_A);
    expect(partyAppBasename(APP_B)).toBe(APP_B);
    expect(partyAppBasename('/party/QrToken')).toBeUndefined();
    expect(partyAppBasename('/party/app/')).toBeUndefined();
    expect(partyAppBasename(`${APP_A}x/party/QrToken`)).toBeUndefined();
  });
});

describe('<PartyAppHead />', () => {
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
        <PartyAppHead />
        <Navigator to={navigate} />
      </MemoryRouter>,
    );
    expect(href('manifest')).toBe('/api/party-invitations/InviteTok/app-manifest');

    // "Entra nel Party": the invitation leads into the party's own page.
    act(() => navigate.current!('/party/QrToken'));
    expect(href('manifest')).toBe('/api/party/QrToken/app-manifest');
    expect(href('apple-touch-icon')).toBe('/api/party/QrToken/app-icon/192');

    act(() => navigate.current!('/albums'));
    expect(href('manifest')).toBe(PRODUCT_APP.manifestUrl);
    expect(href('apple-touch-icon')).toBe(PRODUCT_APP.iconUrl);

    act(() => navigate.current!('/party/invite/Other'));
    expect(href('manifest')).toBe('/api/party-invitations/Other/app-manifest');
    expect(links('manifest')).toHaveLength(1);
    expect(links('apple-touch-icon')).toHaveLength(1);
  });

  it('creates the links once if a page somehow has none, never twice', () => {
    document.head.innerHTML = '';
    const { rerender } = render(
      <MemoryRouter initialEntries={['/party/QrToken']}>
        <PartyAppHead />
      </MemoryRouter>,
    );
    rerender(
      <MemoryRouter initialEntries={['/party/QrToken']}>
        <PartyAppHead />
      </MemoryRouter>,
    );
    expect(links('manifest')).toHaveLength(1);
    expect(href('manifest')).toBe('/api/party/QrToken/app-manifest');
  });

  it("leaves a party app's document as the bootstrap made it, wherever the app goes", () => {
    bootAt(`${APP_A}/party/invite/InviteTok`);
    const navigate: { current: ((path: string) => void) | null } = { current: null };
    const observer = new MutationObserver(() => {});
    observer.observe(document.head, { childList: true, subtree: true, attributes: true });
    render(
      <MemoryRouter basename={APP_A} initialEntries={[`${APP_A}/party/invite/InviteTok`]}>
        <PartyAppHead basename={APP_A} />
        <Navigator to={navigate} />
      </MemoryRouter>,
    );

    // Into the party, and on to its upload page: one app, one install, the
    // start_url it was installed with.
    act(() => navigate.current!('/party/QrToken'));
    act(() => navigate.current!('/party/UploadTok/upload'));
    expect(observer.takeRecords()).toEqual([]);
    observer.disconnect();
    expect(href('manifest')).toBe('/api/party-invitations/InviteTok/app-manifest');
  });
});

describe('<PartyAppCanonical />: an entry link moves under its party app', () => {
  function manifestAnswering(scope: unknown, status = 200) {
    const fetchMock = vi.fn(async () => new Response(JSON.stringify({ scope }), { status }));
    vi.stubGlobal('fetch', fetchMock);
    return fetchMock;
  }

  function mountAt(path: string, basename?: string) {
    window.history.replaceState(null, '', path);
    const replace = vi.spyOn(partyAppNavigation, 'replace').mockImplementation(() => {});
    render(
      <PartyAppCanonical basename={basename}>
        <p>the page</p>
      </PartyAppCanonical>,
    );
    return replace;
  }

  it.each([
    ['/party/QrToken_-123', '/api/party/QrToken_-123/app-manifest', `${APP_A}/party/QrToken_-123`],
    ['/party/QrToken_-123/?lang=it#photos', '/api/party/QrToken_-123/app-manifest', `${APP_A}/party/QrToken_-123/?lang=it#photos`],
    ['/party/invite/InviteTok', '/api/party-invitations/InviteTok/app-manifest', `${APP_A}/party/invite/InviteTok`],
  ])('%s → its party app, before anything is drawn', async (path, manifestUrl, canonical) => {
    const fetchMock = manifestAnswering(`${APP_A}/`);
    const replace = mountAt(path);
    expect(screen.queryByText('the page')).toBeNull();

    await vi.waitFor(() => expect(replace).toHaveBeenCalledWith(canonical));
    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(fetchMock.mock.calls[0]).toEqual([manifestUrl, expect.anything()]);
    expect(screen.queryByText('the page')).toBeNull();
  });

  it.each([
    ['the shared scope every party used to claim', '/party/'],
    ['the product', '/'],
    ['no scope', undefined],
    ['a key that is not one', '/party/app/not-a-key/'],
    ['another origin', `https://elsewhere.example${APP_A}/`],
  ])('never moves to %s: the page stays', async (_label, scope) => {
    manifestAnswering(scope);
    const replace = mountAt('/party/QrToken');

    expect(await screen.findByText('the page')).toBeTruthy();
    expect(replace).not.toHaveBeenCalled();
  });

  it('stays where it is when there is no party behind the link', async () => {
    manifestAnswering(`${APP_A}/`, 404);
    const replace = mountAt('/party/Unknown');

    expect(await screen.findByText('the page')).toBeTruthy();
    expect(replace).not.toHaveBeenCalled();
  });

  it('stays where it is when the server does not answer in time', async () => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
    vi.stubGlobal('fetch', vi.fn((_url: string, init: RequestInit) => new Promise((_resolve, reject) => {
      init.signal!.addEventListener('abort', () => reject(new DOMException('aborted', 'AbortError')));
    })));
    const replace = mountAt('/party/QrToken');
    expect(screen.queryByText('the page')).toBeNull();

    await act(async () => { await vi.advanceTimersByTimeAsync(3000); });

    expect(screen.getByText('the page')).toBeTruthy();
    expect(replace).not.toHaveBeenCalled();
  });

  it.each([
    ['a page already inside its party app', `${APP_A}/party/QrToken`, APP_A],
    ['any page that is not an entry link', '/party/UploadTok/upload', undefined],
    ['the product', '/albums', undefined],
  ])('draws %s at once, asking nothing', (_label, path, basename) => {
    const fetchMock = manifestAnswering(`${APP_A}/`);
    const replace = mountAt(path, basename);

    expect(screen.getByText('the page')).toBeTruthy();
    expect(fetchMock).not.toHaveBeenCalled();
    expect(replace).not.toHaveBeenCalled();
  });
});
