// Owner authentication flows. Mirrors frontend/packages/api-client auth.ts
// contracts (POST /api/auth/login, GET /api/auth/me, POST /api/auth/logout).
//
// Login sequence and its failure discipline:
//   1. drop any stale cookie BEFORE the request so old credentials never ride
//      along with a new login;
//   2. POST /api/auth/login — a 401 here is "wrong credentials", NOT a dead
//      session, so it must not trigger the global unauthorized handler;
//   3. ensure the captured cookie is durably stored BEFORE reporting success,
//      so a reported login survives an immediate app kill;
//   4. persist the base URL for prefill.

import { apiGet, apiPost, configureBaseUrl } from './client.ts';
import { clearPersistedSession, ownerSession, persistBaseUrl } from './session.ts';

export interface CurrentUser {
  id: string;
  email: string;
  displayName: string;
  isAdmin: boolean;
  // Persisted UI language preference ("it" | "en"). Italian is the default.
  language: string;
}

export async function login(
  baseUrl: string,
  email: string,
  password: string,
): Promise<CurrentUser> {
  configureBaseUrl(baseUrl);
  ownerSession.clear();
  try {
    const me = await apiPost<CurrentUser>(
      '/api/auth/login',
      { email, password },
      { allow401: true },
    );
    await ownerSession.ensure();
    await persistBaseUrl(baseUrl.replace(/\/$/, ''));
    return me;
  } catch (err) {
    // A failed login leaves no usable session behind.
    await clearPersistedSession();
    throw err;
  }
}

export function fetchCurrentUser(): Promise<CurrentUser> {
  return apiGet<CurrentUser>('/api/auth/me');
}

// The LOCAL-FIRST logout lives in ../signOut.ts (kept free of SecureStore so
// node --test can exercise the ordering). The provider wires it with its own
// teardown callback.

