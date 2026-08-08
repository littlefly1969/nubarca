// The permission keys and role keys, mirroring NubArca.Api.Access on the
// backend. They are a wire contract: the server rejects any key it does not
// recognise, so this file must stay in step with the server catalogue.
//
// The backend remains the authority on every decision. What these are for is
// UX — deciding which destinations to render and which controls to offer — so
// that a user is not shown a door that will answer 403 when they open it.

export const PERMISSIONS = {
  peopleAccess: 'people.access',
  semanticSearchAccess: 'semantic-search.access',
  laboratoryAccess: 'laboratory.access',
  laboratoryPlates: 'laboratory.plates',
  laboratoryAesthetics: 'laboratory.aesthetics',
  cloudFunctionsAccess: 'cloud-functions.access',
  privateVaultAccess: 'private-vault.access',
  tvManage: 'tv.manage',
  adminDashboard: 'admin.dashboard',
  adminUsersManage: 'admin.users.manage',
  adminImport: 'admin.import',
  adminJobsManage: 'admin.jobs.manage',
} as const;

export type PermissionKey = (typeof PERMISSIONS)[keyof typeof PERMISSIONS];

export const ROLES = {
  administrator: 'Administrator',
  member: 'Member',
  restricted: 'Restricted',
} as const;

export type RoleKey = (typeof ROLES)[keyof typeof ROLES];

// One permission as the admin Access editor shows it. `inheritedFromRole`,
// `override` and `effective` are kept apart on purpose: a flat list of
// effective keys cannot tell "the role grants this" from "somebody granted it
// to this person specifically", and an administrator editing access has to see
// the difference.
export interface AdminUserPermission {
  key: string;
  group: string;
  administrative: boolean;
  inheritedFromRole: boolean;
  override: 'grant' | 'deny' | null;
  effective: boolean;
}

export interface PermissionCatalogEntry {
  key: string;
  group: string;
  administrative: boolean;
}

export interface PermissionCatalog {
  roles: string[];
  permissions: PermissionCatalogEntry[];
  roleBaselines: Record<string, string[]>;
}
