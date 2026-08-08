import { PERMISSIONS, ROLES, type CurrentUser, type PermissionKey } from '@nubarca/api-client';
import type { AuthState } from './AuthContext';

// The single place the UI asks "may this person do X".
//
// It exists so no component does raw array/string work on
// `user.effectivePermissions`. One helper means one place to change when the
// shape moves, one place to read when asking what a check means, and no
// component quietly inventing a different rule (`includes('admin')`, a role
// comparison, a stale copy of the catalogue).
//
// Everything here is UX. The backend authorizes every one of these permissions
// independently, so a wrong answer here hides or shows a control — it never
// grants access.

export { PERMISSIONS, ROLES };
export type { PermissionKey };

export function hasPermission(
  user: CurrentUser | null | undefined,
  permission: PermissionKey,
): boolean {
  return !!user && user.effectivePermissions.includes(permission);
}

// True when the user holds EVERY listed permission. The Laboratory sections
// need this: a section is reachable only with the shell permission as well,
// exactly as the server's composite policy requires.
export function hasAllPermissions(
  user: CurrentUser | null | undefined,
  permissions: readonly PermissionKey[],
): boolean {
  return !!user && permissions.every((p) => user.effectivePermissions.includes(p));
}

export function isAdministrator(user: CurrentUser | null | undefined): boolean {
  return user?.role === ROLES.administrator;
}

// Convenience for the many call sites that hold the auth state rather than a
// user, so a component never has to narrow the union itself just to ask.
export function currentUser(state: AuthState): CurrentUser | null {
  return state.status === 'authed' ? state.user : null;
}

export function statePermissions(state: AuthState): readonly string[] {
  return state.status === 'authed' ? state.user.effectivePermissions : [];
}
