import { api } from './client';

// Mirrors NubArca.Api.Users.AdminUserDto on the backend. Never includes
// PasswordHash, raw auth claims, token hashes, or storage internals —
// `hasPassword` is the only signal about credential state.
export interface AdminUser {
  id: string;
  email: string;
  displayName: string;
  isAdmin: boolean;
  disabledAt: string | null;
  createdAt: string;
  hasPassword: boolean;
  language: string;
}

export interface ListAdminUsersResponse {
  items: AdminUser[];
  total: number;
  limit: number;
  offset: number;
}

export interface ListAdminUsersParams {
  q?: string;
  includeDisabled?: boolean;
  limit?: number;
  offset?: number;
}

export function listAdminUsers(
  params: ListAdminUsersParams = {},
  signal?: AbortSignal,
): Promise<ListAdminUsersResponse> {
  const search = new URLSearchParams();
  if (params.q) search.set('q', params.q);
  if (params.includeDisabled) search.set('includeDisabled', 'true');
  if (params.limit) search.set('limit', String(params.limit));
  if (params.offset) search.set('offset', String(params.offset));
  const qs = search.toString();
  return api<ListAdminUsersResponse>(`/api/admin/users${qs ? `?${qs}` : ''}`, { signal });
}

export interface CreateAdminUserRequest {
  email: string;
  displayName: string;
  password: string;
  isAdmin?: boolean;
  disabled?: boolean;
  language?: string;
}

export function createAdminUser(body: CreateAdminUserRequest): Promise<AdminUser> {
  return api<AdminUser>('/api/admin/users', { method: 'POST', json: body });
}

export function getAdminUser(userId: string, signal?: AbortSignal): Promise<AdminUser> {
  return api<AdminUser>(`/api/admin/users/${userId}`, { signal });
}

export interface UpdateAdminUserRequest {
  displayName?: string;
  language?: string;
}

export function updateAdminUser(userId: string, body: UpdateAdminUserRequest): Promise<AdminUser> {
  return api<AdminUser>(`/api/admin/users/${userId}`, { method: 'PUT', json: body });
}

export function resetAdminUserPassword(userId: string, password: string): Promise<void> {
  return api<void>(`/api/admin/users/${userId}/password`, {
    method: 'POST',
    json: { password },
  });
}

export function setAdminUserAdmin(userId: string, isAdmin: boolean): Promise<AdminUser> {
  return api<AdminUser>(`/api/admin/users/${userId}/admin`, {
    method: 'PUT',
    json: { isAdmin },
  });
}

export function setAdminUserDisabled(userId: string, disabled: boolean): Promise<AdminUser> {
  return api<AdminUser>(`/api/admin/users/${userId}/disabled`, {
    method: 'PUT',
    json: { disabled },
  });
}
