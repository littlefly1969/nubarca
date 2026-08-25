// Session provider: the ONE authentication authority.
//
// Wires the SecureStore-backed OwnerSessionCookieStore into the API seam once
// at module load, owns the cold-start restore → validate flow, exposes
// login/signOut, and reacts to a mid-session 401 by tearing the session down.
//
// Authenticated subtrees are keyed by user id (see app/_layout.tsx), so a
// different signed-in user remounts every screen — selection and in-memory
// media state can never leak across accounts.

import React, {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useRef,
  useState,
} from 'react';
import { ownerSession, getStoredBaseUrl } from '../api/session';
import { setSessionCookieSource } from '../api/sessionAccess';
import { configureBaseUrl, setUnauthorizedHandler } from '../api/client';
import { shouldDropPersistedSession } from './sessionRecovery';
import {
  login as apiLogin,
  fetchCurrentUser,
  type CurrentUser,
} from '../api/auth';
import { notifyServerLogout } from '../api/signOut';
import { clearImageCache } from '../media/imageLoader';
import { useI18n } from '../i18n';
import { toLanguage } from '../i18n';

// Production wiring: every client/image/video request reads the cookie
// through this seam. Done here, not in module scope of client.ts, to keep
// those modules importable by plain node --test.
setSessionCookieSource({
  get current() {
    return ownerSession.current;
  },
  snapshot() {
    return ownerSession.snapshot();
  },
  captureIfCurrent(setCookie, generation) {
    return ownerSession.captureIfCurrent(setCookie, generation);
  },
});

type SessionState =
  | { phase: 'restoring' }
  | { phase: 'unauthed'; expired: boolean }
  | { phase: 'authed'; user: CurrentUser };

interface SessionContextValue {
  status: SessionState['phase'];
  expired: boolean;
  user: CurrentUser | null;
  login: (baseUrl: string, email: string, password: string) => Promise<void>;
  logout: () => void;
}

const SessionContext = createContext<SessionContextValue | null>(null);

export function SessionProvider({
  children,
}: {
  children: React.ReactNode;
}): React.JSX.Element {
  const { setLanguage } = useI18n();
  const [state, setState] = useState<SessionState>({ phase: 'restoring' });
  const stateRef = useRef(state);
  stateRef.current = state;

  const adoptLanguage = useCallback(
    (user: CurrentUser) => {
      const lang = toLanguage(user.language);
      if (lang) setLanguage(lang);
    },
    [setLanguage],
  );

  // LOCAL-FIRST + DURABLE-TRACKED teardown (acceptance BLOCKER):
  //   1. capture the pre-teardown cookie (last chance);
  //   2. START the SecureStore removal — its promise is TRACKED, not awaited
  //      here, so a slow disk can never hold the UI hostage;
  //   3. wipe the image cache and flip the UI to unauthed synchronously (the
  //      VIEWER remounts empty through its identity key — see app/_layout.tsx);
  //   4. afterwards, settle the durable removal and fire ONE best-effort
  //      server notification riding the captured cookie. A hanging/failing
  //      network can neither resurrect the session nor block anything.
  const teardownToUnauthed = useCallback((expired: boolean) => {
    const cookie = ownerSession.current ?? undefined;
    const durableClear = ownerSession.clear();
    clearImageCache();
    setState({ phase: 'unauthed', expired });
    void (async () => {
      await durableClear.catch(() => undefined);
      if (cookie !== undefined) await notifyServerLogout(cookie);
    })();
  }, []);

  const handleUnauthorized = useCallback(() => {
    // Fires when an AUTHENTICATED request comes back 401: the session died.
    if (stateRef.current.phase !== 'authed') return;
    teardownToUnauthed(true);
  }, [teardownToUnauthed]);

  useEffect(() => {
    setUnauthorizedHandler(handleUnauthorized);
    return () => {
      setUnauthorizedHandler(null);
    };
  }, [handleUnauthorized]);

  // Cold start: restore the persisted cookie and re-validate it.
  useEffect(() => {
    let cancelled = false;
    void (async () => {
      try {
        const restored = await ownerSession.restore();
        if (!restored) {
          if (!cancelled) setState({ phase: 'unauthed', expired: false });
          return;
        }
        const savedBase = await getStoredBaseUrl();
        if (savedBase) configureBaseUrl(savedBase);
        if (!ownerSession.current) {
          if (!cancelled) setState({ phase: 'unauthed', expired: false });
          return;
        }
        const user = await fetchCurrentUser();
        if (!cancelled) {
          adoptLanguage(user);
          setState({ phase: 'authed', user });
        }
      } catch (err) {
        // A dead cookie (401/403 from the server) drops the persisted session;
        // an unreachable server must NOT — an airplane-mode cold start would
        // otherwise sign the user out permanently.
        if (shouldDropPersistedSession(err)) await ownerSession.clear();
        clearImageCache();
        const expired = (err as { status?: number }).status === 401;
        if (!cancelled) setState({ phase: 'unauthed', expired });
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [adoptLanguage]);

  const login = useCallback(
    async (baseUrl: string, email: string, password: string) => {
      const user = await apiLogin(baseUrl, email, password);
      adoptLanguage(user);
      setState({ phase: 'authed', user });
    },
    [adoptLanguage],
  );

  const logout = useCallback(() => {
    teardownToUnauthed(false);
  }, [teardownToUnauthed]);

  const value = useMemo<SessionContextValue>(
    () => ({
      status: state.phase,
      expired: state.phase === 'unauthed' ? state.expired : false,
      user: state.phase === 'authed' ? state.user : null,
      login,
      logout,
    }),
    [state, login, logout],
  );

  return <SessionContext.Provider value={value}>{children}</SessionContext.Provider>;
}

export function useSession(): SessionContextValue {
  const ctx = useContext(SessionContext);
  if (ctx === null) throw new Error('useSession must be used within SessionProvider');
  return ctx;
}
