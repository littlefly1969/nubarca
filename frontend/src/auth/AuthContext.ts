import { createContext } from 'react';
import type { CurrentUser } from '@nubarca/api-client';

// Three states only:
//   * `status: 'loading'`  — the initial /api/auth/me probe is in flight.
//   * `status: 'anon'`     — confirmed unauthenticated (probe returned 401).
//   * `status: 'authed'`   — confirmed authenticated; `user` is set.
export type AuthState =
  | { status: 'loading' }
  | { status: 'anon' }
  | { status: 'authed'; user: CurrentUser };

export interface AuthContextValue {
  state: AuthState;
  login(email: string, password: string): Promise<void>;
  logout(): Promise<void>;
  // Called when an authenticated API call comes back 401 (e.g., the user was
  // disabled or deleted mid-session by the backend). Flips state to `anon`
  // without touching the backend — the cookie is already invalid.
  invalidateAuth(): void;
  // Replaces the current user in state after a server-side profile change
  // (e.g. updating the UI language preference) — no re-fetch needed.
  updateUser(user: CurrentUser): void;
}

export const AuthContext = createContext<AuthContextValue | null>(null);
