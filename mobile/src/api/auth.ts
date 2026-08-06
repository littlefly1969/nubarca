import { apiGet, apiPost } from './client';

export interface CurrentUser {
  id: string;
  email: string;
  displayName: string;
  isAdmin: boolean;
  // Persisted UI language preference ("it" | "en"). Italian is the default.
  language: string;
}

export function loginRequest(
  email: string,
  password: string,
): Promise<CurrentUser> {
  return apiPost<CurrentUser>('/api/auth/login', { email, password });
}

export function fetchCurrentUser(): Promise<CurrentUser> {
  return apiGet<CurrentUser>('/api/auth/me');
}

export function logoutRequest(): Promise<void> {
  return apiPost<void>('/api/auth/logout');
}
