import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';
import { afterEach, describe, expect, it } from 'vitest';
import { act, cleanup, render } from '@testing-library/react';
import { MemoryRouter, useNavigate } from 'react-router';
import { PRODUCT_APP, PartyAppHead, partyAppForPath } from './partyHomeScreen';

// WHICH APP a page is, decided from its address alone — and the link a browser
// reads to install it BORN with that address. index.html carries no manifest
// link of its own: its bootstrap creates it. These tests run the real script
// from index.html, hold it to partyAppForPath's table, and follow
// <PartyAppHead /> through in-app navigation.

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

const TABLE: [string, ReturnType<typeof partyAppForPath>][] = [
  ['/party/QrToken_-123', { manifestUrl: '/api/party/QrToken_-123/app-manifest', iconUrl: '/api/party/QrToken_-123/app-icon/192' }],
  ['/party/QrToken_-123/', { manifestUrl: '/api/party/QrToken_-123/app-manifest', iconUrl: '/api/party/QrToken_-123/app-icon/192' }],
  ['/party/invite/InviteTok', { manifestUrl: '/api/party-invitations/InviteTok/app-manifest', iconUrl: '/api/party-invitations/InviteTok/app-icon/192' }],
  // The party's other pages carry other capabilities' tokens.
  ['/party/UploadTok/upload', null],
  ['/party/PrintTok/print', null],
  ['/party/crew/123', null],
  ['/party/crew', null],
  ['/party/invite', null],
  ['/party-display/stage', null],
  ['/albums', null],
  ['/', null],
  ['/party/bad%20token', null],
];

afterEach(() => {
  cleanup();
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
  it.each(TABLE)('%s', (pathname, expected) => {
    const records = bootAt(pathname);
    const app = expected ?? PRODUCT_APP;

    // Exactly one of each…
    expect(links('manifest')).toHaveLength(1);
    expect(links('apple-touch-icon')).toHaveLength(1);
    // …already pointing where it must…
    expect(href('manifest')).toBe(app.manifestUrl);
    expect(href('apple-touch-icon')).toBe(app.iconUrl);
    // …from the moment it exists: added once, never re-pointed afterwards.
    expect(records.filter((r) => r.type === 'attributes')).toEqual([]);
    expect(records.flatMap((r) => [...r.addedNodes])).toHaveLength(2);
    // And it is partyAppForPath's answer, row for row.
    expect(partyAppForPath(pathname) ?? PRODUCT_APP).toEqual({ manifestUrl: href('manifest'), iconUrl: href('apple-touch-icon') });
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
});
