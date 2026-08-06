import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { AuthProvider } from './AuthProvider';
import { useAuth } from './useAuth';
import { I18nProvider } from '../i18n';
import { installFetchMock, jsonResponse, errorResponse } from '../test-utils';

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

// Tiny test rig that mounts AuthProvider and renders the resolved auth state
// + a button that triggers `login(...)`. Avoids pulling in router or any
// other component, so the test exercises the provider in isolation.
function AuthProbe() {
  const { state, login } = useAuth();
  return (
    <div>
      <p data-testid="status">{state.status}</p>
      {state.status === 'authed' && (
        <p data-testid="display-name">{state.user.displayName}</p>
      )}
      <button
        type="button"
        onClick={() => {
          void login('dev@nubarca.local', 'password').catch(() => {});
        }}
      >
        Sign in
      </button>
    </div>
  );
}

describe('AuthProvider', () => {
  it('resolves to anon when /api/auth/me returns 401 on mount', async () => {
    installFetchMock({
      'GET /api/auth/me': () => errorResponse(401),
    });

    render(
      <I18nProvider>
        <AuthProvider>
          <AuthProbe />
        </AuthProvider>
      </I18nProvider>,
    );

    await waitFor(() => {
      expect(screen.getByTestId('status')).toHaveTextContent('anon');
    });
  });

  it('flips to authed after a successful login() call with credentials', async () => {
    const mock = installFetchMock({
      'GET /api/auth/me': () => errorResponse(401),
      'POST /api/auth/login': (req) => {
        // The api client always passes credentials: 'include'; that lives on
        // the second argument of fetch, captured in `init`.
        expect(req.init?.credentials).toBe('include');
        return jsonResponse({
          id: 'user-1',
          email: 'dev@nubarca.local',
          displayName: 'Dev User',
          isAdmin: false,
          language: 'it',
        });
      },
    });

    render(
      <I18nProvider>
        <AuthProvider>
          <AuthProbe />
        </AuthProvider>
      </I18nProvider>,
    );

    await waitFor(() => {
      expect(screen.getByTestId('status')).toHaveTextContent('anon');
    });

    const user = userEvent.setup();
    await act(async () => {
      await user.click(screen.getByRole('button', { name: 'Sign in' }));
    });

    await waitFor(() => {
      expect(screen.getByTestId('status')).toHaveTextContent('authed');
    });
    expect(screen.getByTestId('display-name')).toHaveTextContent('Dev User');

    // POST body should carry email + password as JSON, no extra fields.
    const loginCall = mock.calls.find((c) => c.url === '/api/auth/login');
    expect(loginCall).toBeDefined();
    expect(loginCall!.method).toBe('POST');
    expect(JSON.parse(loginCall!.body ?? '{}')).toEqual({
      email: 'dev@nubarca.local',
      password: 'password',
    });
  });
});
