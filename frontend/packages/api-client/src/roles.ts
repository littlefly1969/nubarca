import { api } from './client';

// A role, mirroring NubArca.Api.Access.RoleDto.
//
// `permissions` is the WHOLE set the role carries. That is deliberate: a role
// preview is rendered from role data alone, never reconstructed from a user's
// cached detail — which is exactly what used to make a role change display the
// previous role's permissions.
export interface Role {
  key: string;
  name: string;
  description: string | null;
  isSystem: boolean;
  isAdministrator: boolean;
  userCount: number;
  permissions: string[];
  version: number;
}

export interface ListRolesResponse {
  roles: Role[];
}

export function listRoles(signal?: AbortSignal): Promise<ListRolesResponse> {
  return api<ListRolesResponse>('/api/admin/roles', { signal });
}

export function getRole(roleKey: string, signal?: AbortSignal): Promise<Role> {
  return api<Role>(`/api/admin/roles/${encodeURIComponent(roleKey)}`, { signal });
}

export interface CreateRoleRequest {
  name: string;
  description?: string | null;
  permissions: string[];
}

export function createRole(body: CreateRoleRequest): Promise<Role> {
  return api<Role>('/api/admin/roles', { method: 'POST', json: body });
}

// Name, description and the FULL permission set in one deliberate save. Sending
// a request per checkbox would leave a role half-edited and live for every user
// assigned to it. `version` is the optimistic-concurrency token: a stale one is
// answered with 409 rather than silently overwriting somebody else's edit.
export interface UpdateRoleRequest {
  name: string;
  description?: string | null;
  permissions: string[];
  version: number;
}

export function updateRole(roleKey: string, body: UpdateRoleRequest): Promise<Role> {
  return api<Role>(`/api/admin/roles/${encodeURIComponent(roleKey)}`, {
    method: 'PUT',
    json: body,
  });
}

// 409 when the role is a system role, or when users are still assigned to it.
// Nothing here cascade-deletes accounts or reassigns them.
export function deleteRole(roleKey: string): Promise<void> {
  return api<void>(`/api/admin/roles/${encodeURIComponent(roleKey)}`, { method: 'DELETE' });
}
