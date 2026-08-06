import { useCallback, useEffect, useMemo, useState } from 'react';
import type { ReactNode } from 'react';
import {
  fetchCurrentUser,
  loginRequest,
  logoutRequest,
  type CurrentUser,
} from '@nubarca/api-client';
import { ApiError } from '@nubarca/api-client';
import { AuthContext, type AuthState } from './AuthContext';
import { useI18n, toLanguage } from '../i18n';

interface AuthProviderProps {
  children: ReactNode;
}

export function AuthProvider({ children }: AuthProviderProps) {
  const [state, setState] = useState<AuthState>({ status: 'loading' });
  const { setLanguage, t } = useI18n();

  // Adopt the authenticated user's persisted language whenever the session
  // resolves (login / mount probe / profile update). The server value is the
  // source of truth for signed-in users; unsupported values are ignored (the
  // provider keeps its current/default language). We do NOT persist to
  // localStorage here — the server row is the durable store for this user.
  const adoptUserLanguage = useCallback((user: CurrentUser) => {
    const lang = toLanguage(user.language);
    if (lang) setLanguage(lang, { persistLocal: false });
  }, [setLanguage]);

  // On mount: probe /api/auth/me. 200 → we have a valid cookie; 401 → not
  // signed in. Any other shape (network error, server-side outage) also lands
  // on the unauthenticated branch so the user lands on the login screen
  // rather than an indefinite spinner.
  useEffect(() => {
    const controller = new AbortController();
    fetchCurrentUser(controller.signal)
      .then((user: CurrentUser) => {
        setState({ status: 'authed', user });
        adoptUserLanguage(user);
      })
      .catch((err: unknown) => {
        if (err instanceof DOMException && err.name === 'AbortError') {
          return;
        }
        setState({ status: 'anon' });
      });
    return () => controller.abort();
  }, [adoptUserLanguage]);

  const login = useCallback(async (email: string, password: string) => {
    try {
      const user = await loginRequest(email, password);
      setState({ status: 'authed', user });
      adoptUserLanguage(user);
    } catch (err) {
      // Normalise: the backend returns 401 for every failure mode (bad
      // password, unknown email, disabled user) by design. UI shows a single
      // generic message — never leak which case it was.
      if (err instanceof ApiError && err.status === 401) {
        throw new Error(t('login.invalidCredentials'));
      }
      throw new Error(t('login.unavailable'));
    }
  }, [adoptUserLanguage, t]);

  const logout = useCallback(async () => {
    try {
      await logoutRequest();
    } catch {
      // The cookie may already be invalid (e.g., user disabled mid-session).
      // Either way we want to land on the login screen.
    }
    setState({ status: 'anon' });
  }, []);

  const invalidateAuth = useCallback(() => {
    setState({ status: 'anon' });
  }, []);

  const updateUser = useCallback((user: CurrentUser) => {
    setState({ status: 'authed', user });
    adoptUserLanguage(user);
  }, [adoptUserLanguage]);

  const value = useMemo(
    () => ({ state, login, logout, invalidateAuth, updateUser }),
    [state, login, logout, invalidateAuth, updateUser],
  );

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}
