import { api } from './client';

// Mirrors NubArca.Api.Auth.CurrentUserResponse on the backend. The API never
// returns PasswordHash, a reset-token hash, the security version, or any
// storage internals.
//
// `effectivePermissions` is the RESOLVED answer — role baseline with the user's
// overrides already applied — sorted deterministically by the server. It drives
// navigation and feature visibility; the backend independently enforces every
// one of those permissions, so hiding a control is UX and never the gate.
//
// `isAdmin` is a computed compatibility value (role === 'Administrator') kept
// for the mobile client's model. Read `role` in new code.
export interface CurrentUser {
  id: string;
  email: string;
  displayName: string;
  firstName: string | null;
  lastName: string | null;
  isAdmin: boolean;
  role: string;
  effectivePermissions: string[];
  // Persisted UI language preference ("it" | "en"). Italian is the default.
  language: string;
  // IANA identifier ("Europe/Rome"), or null to follow the browser.
  timeZone: string | null;
  lastLoginAt: string | null;
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

// The caller's own profile. Role, permissions, disabled state and email are
// deliberately absent — they are not editable from here at any level, and the
// backend's request model has no field for them either.
//
// An omitted field is left unchanged; an empty string clears an optional one.
export interface UpdateMyProfileRequest {
  displayName?: string;
  firstName?: string;
  lastName?: string;
  language?: string;
  timeZone?: string;
}

export function updateMyProfile(body: UpdateMyProfileRequest): Promise<CurrentUser> {
  return api<CurrentUser>('/api/auth/me/profile', { method: 'PUT', json: body });
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

// ---------------------------------------------------------------- recovery

export interface PasswordRecoveryStatus {
  enabled: boolean;
}

// PUBLIC. Says only whether the operator has configured email recovery, so the
// forgot-password page can either offer the form or explain that an
// administrator must reset the password manually.
export function fetchPasswordRecoveryStatus(signal?: AbortSignal): Promise<PasswordRecoveryStatus> {
  return api<PasswordRecoveryStatus>('/api/auth/password-recovery/status', { signal });
}

// PUBLIC. Always resolves the same way for a known address, an unknown one, a
// disabled account and a failed delivery — the caller learns nothing about
// whether the account exists. A 429 (ApiError) means "asked too often", which
// is also true regardless of whether the address is real.
export function requestPasswordRecovery(email: string): Promise<void> {
  return api<void>('/api/auth/password-recovery/request', {
    method: 'POST',
    json: { email },
  });
}

// PUBLIC. The token travels in the JSON BODY, never in the URL: it arrives in
// the page's fragment, is held in component memory only, and is removed from
// the visible URL before this is called.
export function resetPasswordWithToken(token: string, newPassword: string): Promise<void> {
  return api<void>('/api/auth/password-recovery/reset', {
    method: 'POST',
    json: { token, newPassword },
  });
}
