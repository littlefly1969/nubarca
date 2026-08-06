// Routing regression coverage for the authentication guard.
//
// These lock in the navigation contract that the React Router 7.18.2 security
// update must not change: an anonymous visitor is sent to /login, the route
// they originally asked for survives the round trip, and an in-flight auth
// probe never flashes a redirect.
import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router';
import { AuthContext } from './AuthContext';
import { ProtectedRoute } from './ProtectedRoute';
import { makeAuthValue } from '../test-utils';
import type { AuthState } from './AuthContext';

afterEach(cleanup);

// Renders the location AND the navigation state, so a test can assert that
// `returnTo` was captured verbatim rather than merely that a redirect happened.
function LoginProbe() {
  const loc = useLocation();
  const state = loc.state as { returnTo?: unknown } | null;
  return (
    <div>
      <span data-testid="path">{loc.pathname}</span>
      <span data-testid="return-to">{String(state?.returnTo ?? '')}</span>
    </div>
  );
}

function renderAt(entry: string, state: AuthState) {
  render(
    <AuthContext.Provider value={makeAuthValue(state)}>
      <MemoryRouter initialEntries={[entry]}>
        <Routes>
          <Route path="/login" element={<LoginProbe />} />
          <Route
            path="/albums/:albumId"
            element={
              <ProtectedRoute>
                <div data-testid="protected">album page</div>
              </ProtectedRoute>
            }
          />
          <Route
            path="/media"
            element={
              <ProtectedRoute>
                <div data-testid="protected">media page</div>
              </ProtectedRoute>
            }
          />
        </Routes>
      </MemoryRouter>
    </AuthContext.Provider>,
  );
}

describe('ProtectedRoute', () => {
  it('redirects an anonymous visitor to /login', () => {
    renderAt('/media', { status: 'anon' });

    expect(screen.getByTestId('path').textContent).toBe('/login');
    expect(screen.queryByTestId('protected')).toBeNull();
  });

  it('preserves the originally requested path, query string and hash as returnTo', () => {
    renderAt('/media?kind=image&q=dog#top', { status: 'anon' });

    expect(screen.getByTestId('path').textContent).toBe('/login');
    expect(screen.getByTestId('return-to').textContent).toBe('/media?kind=image&q=dog#top');
  });

  it('captures a route parameter in returnTo', () => {
    renderAt('/albums/album-42', { status: 'anon' });

    expect(screen.getByTestId('return-to').textContent).toBe('/albums/album-42');
  });

  it('renders the protected route for an authenticated user', () => {
    renderAt('/media', {
      status: 'authed',
      user: {
        id: 'user-1',
        email: 'dev@nubarca.local',
        displayName: 'Dev User',
        isAdmin: false,
        language: 'it',
      },
    });

    expect(screen.getByTestId('protected').textContent).toBe('media page');
  });

  it('shows the loading screen instead of redirecting while the auth probe is in flight', () => {
    // Regression guard: redirecting on `loading` would visually log the user
    // out on every page reload.
    renderAt('/media', { status: 'loading' });

    expect(screen.queryByTestId('path')).toBeNull();
    expect(screen.queryByTestId('protected')).toBeNull();
    expect(screen.getByText('Loading…')).toBeInTheDocument();
  });
});
