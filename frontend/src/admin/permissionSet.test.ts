import { describe, expect, it } from 'vitest';
import { PERMISSIONS, type PermissionCatalogEntry } from '@nubarca/api-client';
import {
  groupAssignablePermissions,
  samePermissions,
  togglePermission,
} from './permissionSet';

const CATALOG: PermissionCatalogEntry[] = [
  { key: PERMISSIONS.peopleAccess, group: 'features', administrative: false, parent: null, assignable: true },
  { key: PERMISSIONS.laboratoryAccess, group: 'features', administrative: false, parent: null, assignable: true },
  {
    key: PERMISSIONS.laboratoryPlates,
    group: 'features',
    administrative: false,
    parent: PERMISSIONS.laboratoryAccess,
    assignable: true,
  },
  {
    key: PERMISSIONS.laboratoryAesthetics,
    group: 'features',
    administrative: false,
    parent: PERMISSIONS.laboratoryAccess,
    assignable: true,
  },
  { key: PERMISSIONS.adminJobsManage, group: 'administration', administrative: true, parent: null, assignable: true },
  { key: PERMISSIONS.adminRolesManage, group: 'administration', administrative: true, parent: null, assignable: false },
];

describe('togglePermission', () => {
  it('enabling a Laboratory section enables the Laboratory itself', () => {
    // The section alone opens nothing — the composite endpoint policy requires
    // both — so the editor never lets the operator reach that state.
    const next = togglePermission([], PERMISSIONS.laboratoryPlates, true, CATALOG);

    expect(next).toContain(PERMISSIONS.laboratoryAccess);
    expect(next).toContain(PERMISSIONS.laboratoryPlates);
  });

  it('disabling the Laboratory takes its sections with it', () => {
    const current = [
      PERMISSIONS.laboratoryAccess,
      PERMISSIONS.laboratoryPlates,
      PERMISSIONS.laboratoryAesthetics,
      PERMISSIONS.peopleAccess,
    ];

    const next = togglePermission(current, PERMISSIONS.laboratoryAccess, false, CATALOG);

    expect(next).toEqual([PERMISSIONS.peopleAccess]);
  });

  it('disabling one section leaves the other and the shell alone', () => {
    const current = [
      PERMISSIONS.laboratoryAccess,
      PERMISSIONS.laboratoryPlates,
      PERMISSIONS.laboratoryAesthetics,
    ];

    const next = togglePermission(current, PERMISSIONS.laboratoryPlates, false, CATALOG);

    expect(next).toContain(PERMISSIONS.laboratoryAccess);
    expect(next).toContain(PERMISSIONS.laboratoryAesthetics);
    expect(next).not.toContain(PERMISSIONS.laboratoryPlates);
  });

  it('is idempotent for an independent permission', () => {
    const once = togglePermission([], PERMISSIONS.peopleAccess, true, CATALOG);
    const twice = togglePermission(once, PERMISSIONS.peopleAccess, true, CATALOG);

    expect(twice).toEqual([PERMISSIONS.peopleAccess]);
  });
});

describe('samePermissions', () => {
  it('ignores order', () => {
    expect(samePermissions(['b', 'a'], ['a', 'b'])).toBe(true);
  });

  it('notices a difference', () => {
    expect(samePermissions(['a'], ['a', 'b'])).toBe(false);
    expect(samePermissions(['a'], ['b'])).toBe(false);
  });
});

describe('groupAssignablePermissions', () => {
  it('omits what no role may be given', () => {
    // admin.roles.manage is Administrator-only server-side. Offering it as a
    // checkbox would present a setting the server always refuses.
    const groups = groupAssignablePermissions(CATALOG);
    const keys = groups.flatMap((g) => g.entries.map((e) => e.key));

    expect(keys).not.toContain(PERMISSIONS.adminRolesManage);
    expect(keys).toContain(PERMISSIONS.adminJobsManage);
  });

  it('keeps features and administration apart, in catalogue order', () => {
    const groups = groupAssignablePermissions(CATALOG);

    expect(groups.map((g) => g.group)).toEqual(['features', 'administration']);
    expect(groups[0].entries[0].key).toBe(PERMISSIONS.peopleAccess);
  });
});
