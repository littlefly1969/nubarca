import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';
import { afterEach, describe, expect, it } from 'vitest';
import { act, cleanup, render } from '@testing-library/react';
import { MemoryRouter, useNavigate } from 'react-router';
import { PRODUCT_APP, PartyAppHead, partyAppForPath } from './partyHomeScreen';

// WHICH APP a page is, decided from its address alone: by the bootstrap in
// index.html before any manifest is read, and by <PartyAppHead /> on in-app
// navigation. The two are held to one table here — the script is extracted
// from the real index.html and run against the same addresses.

const here = dirname(fileURLToPath(import.meta.url));
const INDEX_HTML = resolve(here, '../../index.html');

function bootstrapSource(): string {
  const html = readFileSync(INDEX_HTML, 'utf8');
  const scripts = [...html.matchAll(/<script>([\s\S]*?)<\/script>/g)].map((m) => m[1]);
  const party = scripts.find((s) => s.includes('Party app bootstrap'));
  if (!party) throw new Error('index.html no longer contains the party app bootstrap.');
  return party;
}

function productHead(): void {
  document.head.innerHTML = `
    <link rel="apple-touch-icon" href="${PRODUCT_APP.iconUrl}">
    <link rel="manifest" href="${PRODUCT_APP.manifestUrl}">`;
}

function headAfterBootstrapAt(pathname: string) {
  productHead();
  window.history.replaceState(null, '', pathname);
  // eslint-disable-next-line @typescript-eslint/no-implied-eval
  new Function(bootstrapSource())();
  return {
    manifestUrl: document.head.querySelector('link[rel="manifest"]')!.getAttribute('href'),
    iconUrl: document.head.querySelector('link[rel="apple-touch-icon"]')!.getAttribute('href'),
  };
}

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

describe('the party app chosen from the address', () => {
  it.each(TABLE)('%s', (pathname, expected) => {
    expect(partyAppForPath(pathname)).toEqual(expected);
  });

  it.each(TABLE)('index.html bootstrap agrees at %s', (pathname, expected) => {
    expect(headAfterBootstrapAt(pathname)).toEqual(expected ?? PRODUCT_APP);
  });

  it('is in place before React runs: the bootstrap is an inline script in the head', () => {
    const html = readFileSync(INDEX_HTML, 'utf8');
    const bootstrap = html.indexOf('Party app bootstrap');
    expect(bootstrap).toBeGreaterThan(html.indexOf('<link rel="manifest"'));
    expect(bootstrap).toBeLessThan(html.indexOf('</head>'));
    expect(bootstrap).toBeLessThan(html.indexOf('src="/src/main.tsx"'));
  });
});

describe('<PartyAppHead />', () => {
  function Navigator({ to }: { to: { current: ((path: string) => void) | null } }) {
    const navigate = useNavigate();
    to.current = navigate;
    return null;
  }

  it('follows in-app navigation into and out of a party, from the address alone', () => {
    productHead();
    const navigate: { current: ((path: string) => void) | null } = { current: null };
    render(
      <MemoryRouter initialEntries={['/party/invite/InviteTok']}>
        <PartyAppHead />
        <Navigator to={navigate} />
      </MemoryRouter>,
    );
    const manifest = () => document.head.querySelector('link[rel="manifest"]')!.getAttribute('href');
    expect(manifest()).toBe('/api/party-invitations/InviteTok/app-manifest');

    // "Entra nel Party": the invitation leads into the party's own page.
    act(() => navigate.current!('/party/QrToken'));
    expect(manifest()).toBe('/api/party/QrToken/app-manifest');

    act(() => navigate.current!('/albums'));
    expect(manifest()).toBe(PRODUCT_APP.manifestUrl);
  });
});
