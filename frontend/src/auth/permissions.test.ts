import { describe, expect, it } from 'vitest';
import { PERMISSIONS, ROLES, type CurrentUser } from '@nubarca/api-client';
import { hasAllPermissions, hasPermission, isAdministrator } from './permissions';

function user(role: string, permissions: string[]): CurrentUser {
  return {
    id: 'user-1',
    email: 'dev@nubarca.local',
    displayName: 'Dev',
    firstName: null,
    lastName: null,
    isAdmin: role === ROLES.administrator,
    role,
    effectivePermissions: permissions,
    language: 'it',
    timeZone: null,
    lastLoginAt: null,
  };
}

describe('hasPermission', () => {
  it('answers from the effective list', () => {
    const member = user(ROLES.member, [PERMISSIONS.peopleAccess]);
    expect(hasPermission(member, PERMISSIONS.peopleAccess)).toBe(true);
    expect(hasPermission(member, PERMISSIONS.adminUsersManage)).toBe(false);
  });

  it('is false for an absent user rather than throwing', () => {
    // Every call site holds auth state that can be loading or anonymous; a
    // helper that threw there would push the narrowing back into components.
    expect(hasPermission(null, PERMISSIONS.peopleAccess)).toBe(false);
    expect(hasPermission(undefined, PERMISSIONS.peopleAccess)).toBe(false);
  });

  it('does not match on a prefix or a substring', () => {
    // A hand-rolled `includes('admin')` would say yes to all of these. This is
    // why there is one helper instead of ad-hoc string work per component.
    const jobsOnly = user(ROLES.restricted, [PERMISSIONS.adminJobsManage]);
    expect(hasPermission(jobsOnly, PERMISSIONS.adminUsersManage)).toBe(false);
    expect(hasPermission(jobsOnly, PERMISSIONS.adminDashboard)).toBe(false);
    expect(hasPermission(jobsOnly, PERMISSIONS.adminJobsManage)).toBe(true);
  });

  it('reads the resolved list, so a denied role permission is simply absent', () => {
    // The server applies overrides before answering; the client never
    // re-derives them, which is why a deny needs no special case here.
    const denied = user(ROLES.member, [PERMISSIONS.privateVaultAccess]);
    expect(hasPermission(denied, PERMISSIONS.peopleAccess)).toBe(false);
  });
});

describe('hasAllPermissions', () => {
  it('requires every listed permission', () => {
    const partial = user(ROLES.restricted, [PERMISSIONS.laboratoryPlates]);
    expect(hasAllPermissions(partial, [PERMISSIONS.laboratoryPlates])).toBe(true);
    // A Laboratory section needs the shell too — the same composite the server
    // authorizes, so the UI cannot offer a door the API will refuse.
    expect(hasAllPermissions(partial, [
      PERMISSIONS.laboratoryAccess, PERMISSIONS.laboratoryPlates,
    ])).toBe(false);

    const full = user(ROLES.restricted, [
      PERMISSIONS.laboratoryAccess, PERMISSIONS.laboratoryPlates,
    ]);
    expect(hasAllPermissions(full, [
      PERMISSIONS.laboratoryAccess, PERMISSIONS.laboratoryPlates,
    ])).toBe(true);
  });

  it('is vacuously true for an empty requirement on a signed-in user', () => {
    expect(hasAllPermissions(user(ROLES.restricted, []), [])).toBe(true);
  });
});

describe('isAdministrator', () => {
  it('reads the role, not the permission list', () => {
    // A user granted every permission individually is still not an
    // Administrator: the role is what the safety guards key on.
    const everything = user(ROLES.member, Object.values(PERMISSIONS));
    expect(isAdministrator(everything)).toBe(false);
    expect(isAdministrator(user(ROLES.administrator, Object.values(PERMISSIONS)))).toBe(true);
    expect(isAdministrator(null)).toBe(false);
  });
});
