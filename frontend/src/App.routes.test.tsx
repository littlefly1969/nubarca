// Route-table regression coverage against the REAL <App /> router.
//
// The other routing tests build their own <MemoryRouter> harness, which proves
// component behaviour but not that App.tsx actually wires the paths that way.
// These tests mount the real component tree so the actual <Route> table — the
// public/protected split and the catch-all — is what gets exercised.
//
// Everything here runs in the anonymous auth state on purpose: ProtectedRoute
// short-circuits before any protected page renders, so the route table can be
// asserted without standing up ~30 pages' worth of API mocks.
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import { App } from './App';
import { installFetchMock, errorResponse } from './test-utils';

function goto(path: string) {
  window.history.replaceState({}, '', path);
}

beforeEach(() => {
  // 401 on every call: the auth probe resolves to `anon`, and any public page
  // that does fetch on mount degrades instead of throwing.
  installFetchMock({ '* ': () => errorResponse(401) });
});

afterEach(() => {
  cleanup();
  goto('/');
});

// The login form is the reliable "we ended up on /login" signal.
async function expectLanded(path: string) {
  goto(path);
  render(<App />);
  await waitFor(() => {
    expect(window.location.pathname).toBe('/login');
  });
}

describe('App route table (anonymous)', () => {
  it('sends a protected route to /login', async () => {
    await expectLanded('/media');
  });

  it('sends the root route to /login', async () => {
    await expectLanded('/');
  });

  it('sends a protected route with a route parameter to /login', async () => {
    await expectLanded('/albums/album-42');
  });

  it('sends an unknown route through the catch-all and on to /login', async () => {
    // The catch-all is <Navigate to="/" replace />, and "/" is protected — so
    // an anonymous visitor to an unknown URL lands on /login. This asserts the
    // real behaviour: NubArca has no standalone 404 page.
    await expectLanded('/definitely-not-a-route');
  });

  // UX-02: the Laboratory subtree and the preserved /plates deep link are all
  // protected, so an anonymous visitor lands on /login rather than seeing an
  // unguarded workspace or a dead route.
  it('protects the whole Laboratory subtree', async () => {
    for (const path of ['/lab', '/lab/plates', '/lab/aesthetics']) {
      await expectLanded(path);
      cleanup();
    }
  });

  it('still resolves the old /plates deep link, protected', async () => {
    await expectLanded('/plates');
  });

  // SHARE-ALBUM-01: a live share is an AUTHENTICATED surface. Unlike /party/…,
  // which is a public token link, /shared-albums resolves nothing without a
  // session — a pasted URL is not a capability.
  it('protects the shared-album routes, list and deep link alike', async () => {
    for (const path of ['/shared-albums', '/shared-albums/album-42']) {
      await expectLanded(path);
      cleanup();
    }
  });

  it('sends a deep unknown route to /login too', async () => {
    await expectLanded('/albums/album-42/nope/deeper');
  });

  it('renders /login itself without redirecting', async () => {
    goto('/login');
    render(<App />);
    await waitFor(() => {
      expect(screen.getByLabelText('Email')).toBeInTheDocument();
    });
    expect(window.location.pathname).toBe('/login');
  });

  it('keeps /tv public — no redirect to /login', async () => {
    goto('/tv');
    render(<App />);
    // Give the auth probe time to settle to `anon`; the path must not move.
    await waitFor(() => {
      expect(window.location.pathname).toBe('/tv');
    });
  });

  it('keeps the public party landing route public', async () => {
    goto('/party/token-abc');
    render(<App />);
    await waitFor(() => {
      expect(window.location.pathname).toBe('/party/token-abc');
    });
  });

  it('keeps the public party upload route public', async () => {
    goto('/party/token-abc/upload');
    render(<App />);
    await waitFor(() => {
      expect(window.location.pathname).toBe('/party/token-abc/upload');
    });
  });

  it('keeps the public beauty-lab upload route public', async () => {
    goto('/beauty-lab-upload/token-xyz');
    render(<App />);
    await waitFor(() => {
      expect(window.location.pathname).toBe('/beauty-lab-upload/token-xyz');
    });
  });

  it('protects /tv/pair even though /tv is public', async () => {
    await expectLanded('/tv/pair');
  });

  it('preserves the query string of the requested destination through the redirect', async () => {
    goto('/media?kind=image&q=dog');
    render(<App />);
    await waitFor(() => {
      expect(window.location.pathname).toBe('/login');
    });
    // returnTo travels in history state, not the URL — assert it survived.
    const state = window.history.state as { usr?: { returnTo?: string } } | null;
    expect(state?.usr?.returnTo).toBe('/media?kind=image&q=dog');
  });
});
