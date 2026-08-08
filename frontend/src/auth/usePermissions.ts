import { useMemo } from 'react';
import type { PermissionKey } from '@nubarca/api-client';
import { useAuth } from './useAuth';
import { currentUser, hasAllPermissions, hasPermission, isAdministrator } from './permissions';

export interface PermissionsApi {
  has(permission: PermissionKey): boolean;
  hasAll(permissions: readonly PermissionKey[]): boolean;
  isAdministrator: boolean;
  // The raw list, for the one place that genuinely needs it: building the
  // navigation model. Components ask `has(...)` instead.
  all: readonly string[];
}

// The hook form of the permission helpers, so a component asks
// `perms.has(PERMISSIONS.peopleAccess)` rather than reaching into auth state
// and doing its own array work.
export function usePermissions(): PermissionsApi {
  const { state } = useAuth();
  const user = currentUser(state);

  return useMemo(() => ({
    has: (permission: PermissionKey) => hasPermission(user, permission),
    hasAll: (permissions: readonly PermissionKey[]) => hasAllPermissions(user, permissions),
    isAdministrator: isAdministrator(user),
    all: user?.effectivePermissions ?? [],
  }), [user]);
}
