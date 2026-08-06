import { api } from './client';

// Mirrors NubArca.Api.Auth.CurrentUserResponse on the backend. Adding fields
// here when the backend grows the response is the only thing the client needs
// to do — the API never returns PasswordHash, DisabledAt, or any storage
// internals. `isAdmin` (slice 47) lets the UI decide whether to render the
// Admin nav; the backend remains the authoritative gate via the `Admin`
// policy on `/api/admin/*`.
export interface CurrentUser {
  id: string;
  email: string;
  displayName: string;
  isAdmin: boolean;
  // Persisted UI language preference ("it" | "en"). Italian is the default.
  language: string;
}

export function loginRequest(email: string, password: string): Promise<CurrentUser> {
  return api<CurrentUser>('/api/auth/login', {
    method: 'POST',
    json: { email, password },
  });
}

export function logoutRequest(): Promise<void> {
  return api<void>('/api/auth/logout', { method: 'POST' });
}

export function fetchCurrentUser(signal?: AbortSignal): Promise<CurrentUser> {
  return api<CurrentUser>('/api/auth/me', { signal });
}

// Updates ONLY the caller's own UI language preference (cookie session). The
// backend rejects unsupported codes (400) and returns the refreshed user.
export function updateMyLanguage(language: string): Promise<CurrentUser> {
  return api<CurrentUser>('/api/auth/me/language', {
    method: 'PUT',
    json: { language },
  });
}

// Self-service password change. Requires the caller's CURRENT password;
// the backend rejects with 400 on a wrong current password or a new
// password that fails the minimum policy, and 409 when the account has no
// password set yet (an admin must set the first one).
export function changeMyPassword(currentPassword: string, newPassword: string): Promise<void> {
  return api<void>('/api/auth/me/password', {
    method: 'POST',
    json: { currentPassword, newPassword },
  });
}
