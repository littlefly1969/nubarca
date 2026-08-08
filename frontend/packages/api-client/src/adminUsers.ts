import { api } from './client';
import type { PermissionCatalog } from './permissions';

// Mirrors NubArca.Api.Users.AdminUserDto. Never includes PasswordHash, the
// security version, raw auth claims, token hashes or storage internals —
// `hasPassword` is the only credential signal.
export interface AdminUser {
  id: string;
  email: string;
  displayName: string;
  firstName: string | null;
  lastName: string | null;
  role: string;
  disabledAt: string | null;
  createdAt: string;
  hasPassword: boolean;
  language: string;
  timeZone: string | null;
  lastLoginAt: string | null;
  passwordChangedAt: string | null;
}

// The admin detail view: the user alone. Deliberately carries NO permission
// list — permissions belong to the ROLE and are read from the role catalogue.
// A user-shaped permission list is precisely what used to go stale the moment
// the role changed.
export interface AdminUserDetail {
  user: AdminUser;
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
  role?: string;
  disabled?: boolean;
  language?: string;
  firstName?: string;
  lastName?: string;
  timeZone?: string;
}

export function createAdminUser(body: CreateAdminUserRequest): Promise<AdminUser> {
  return api<AdminUser>('/api/admin/users', { method: 'POST', json: body });
}

export function getAdminUser(userId: string, signal?: AbortSignal): Promise<AdminUserDetail> {
  return api<AdminUserDetail>(`/api/admin/users/${userId}`, { signal });
}

// Profile fields only. Role, permissions, disabled state and email each have
// their own guarded endpoint.
export interface UpdateAdminUserRequest {
  displayName?: string;
  firstName?: string;
  lastName?: string;
  language?: string;
  timeZone?: string;
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

// Sends the ordinary recovery email to this user. 409 when the installation has
// no mail configured, or when the account is disabled.
export function sendAdminUserPasswordResetEmail(userId: string): Promise<void> {
  return api<void>(`/api/admin/users/${userId}/password-reset-email`, { method: 'POST' });
}

export function setAdminUserRole(userId: string, role: string): Promise<AdminUser> {
  return api<AdminUser>(`/api/admin/users/${userId}/role`, {
    method: 'PUT',
    json: { role },
  });
}

export function setAdminUserDisabled(userId: string, disabled: boolean): Promise<AdminUser> {
  return api<AdminUser>(`/api/admin/users/${userId}/disabled`, {
    method: 'PUT',
    json: { disabled },
  });
}

// The catalogue itself, so the editor renders labels and grouping from the
// server's list rather than a copy the frontend has to keep in step.
export function fetchPermissionCatalog(signal?: AbortSignal): Promise<PermissionCatalog> {
  return api<PermissionCatalog>('/api/admin/permissions', { signal });
}
