// Open-redirect regression coverage for the post-login destination.
//
// `returnTo` is the one navigation target in the app that is derived from the
// URL the visitor arrived on, so it is the sink that GHSA-wrjc-x8rr-h8h6
// (CVE-2026-53669, "open redirect via backslash in <Link> and useNavigate")
// would be exercised through. React Router 7.18.2 fixes the library-side
// handling; these tests lock in the application-side allow-list so a future
// refactor cannot silently reintroduce an external redirect.
//
// The rule under test: only a same-origin absolute path is honoured, i.e. it
// must start with a single "/" and must not start with "//".
import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router';
import { LoginPage } from './LoginPage';
import { AuthContext } from '../auth/AuthContext';
import { makeAuthValue } from '../test-utils';
import { I18nProvider } from '../i18n';

afterEach(cleanup);

function Landed() {
  const loc = useLocation();
  return <span data-testid="landed">{loc.pathname + loc.search}</span>;
}

// Renders an ALREADY authenticated LoginPage — that is the branch that
// consumes `returnTo` and issues <Navigate to={returnTo} replace />.
function landingFor(returnTo: unknown): string {
  render(
    <AuthContext.Provider
      value={makeAuthValue({
        status: 'authed',
        user: {
          id: 'user-1',
          email: 'dev@nubarca.local',
          displayName: 'Dev User',
          isAdmin: false,
          language: 'it',
        },
      })}
    >
      <I18nProvider>
        <MemoryRouter initialEntries={[{ pathname: '/login', state: { returnTo } }]}>
          <Routes>
            <Route path="/login" element={<LoginPage />} />
            <Route path="*" element={<Landed />} />
          </Routes>
        </MemoryRouter>
      </I18nProvider>
    </AuthContext.Provider>,
  );
  return screen.getByTestId('landed').textContent ?? '';
}

describe('LoginPage returnTo handling', () => {
  it('honours a same-origin absolute path', () => {
    expect(landingFor('/media')).toBe('/media');
  });

  it('preserves the query string of the requested destination', () => {
    expect(landingFor('/media?kind=image&q=dog')).toBe('/media?kind=image&q=dog');
  });

  it('falls back to / when there is no returnTo', () => {
    expect(landingFor(undefined)).toBe('/');
  });

  it('rejects a protocol-relative URL', () => {
    // "//evil.example" would navigate cross-origin in a real browser.
    expect(landingFor('//evil.example')).toBe('/');
  });

  it('rejects an absolute external URL', () => {
    expect(landingFor('https://evil.example/phish')).toBe('/');
  });

  it('rejects a javascript: target', () => {
    expect(landingFor('javascript:alert(1)')).toBe('/');
  });

  it('rejects a data: target', () => {
    expect(landingFor('data:text/html,<script>alert(1)</script>')).toBe('/');
  });

  it('rejects a non-string returnTo', () => {
    expect(landingFor({ href: '//evil.example' })).toBe('/');
  });

  // The GHSA-wrjc-x8rr-h8h6 / CVE-2026-53669 vector. "/\evil.example" passes a
  // naive startsWith('/') check, and a browser normalises "\" to "/", so a
  // vulnerable router could turn it into the protocol-relative
  // "//evil.example" and navigate cross-origin.
  //
  // Verified by running this file against both versions:
  //   react-router 7.15.1 -> "/\evil.example"  (backslash PRESERVED; a browser
  //                          normalises it to "//evil.example" => cross-origin)
  //   react-router 7.18.2 -> "/evil.example"   (normalised, stays same-origin)
  // So this is a genuine regression test: it FAILS on the vulnerable version.
  // The security property is "never escapes the origin", not "the string
  // disappears" — the resolved path is an ordinary in-app route that the App
  // catch-all then sends back to "/".
  it('keeps a backslash-prefixed target same-origin', () => {
    const landed = landingFor('/\\evil.example');
    expect(landed.startsWith('//')).toBe(false);
    expect(landed.startsWith('/')).toBe(true);
    expect(landed).not.toContain('\\');
  });

  it('keeps a backslash-escaped authority same-origin', () => {
    const landed = landingFor('\\\\evil.example');
    expect(landed.startsWith('//')).toBe(false);
    expect(landed.startsWith('/')).toBe(true);
    expect(landed).not.toContain('\\');
  });
});
