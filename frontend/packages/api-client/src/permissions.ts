// The permission keys and built-in role keys, mirroring NubArca.Api.Access on
// the backend. They are a wire contract: the server rejects any key it does not
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
  // Delegating short-lived external playback of a video the user can already
  // play (Google Cast). It never widens which files are reachable.
  castAccess: 'cast.access',
  adminDashboard: 'admin.dashboard',
  adminUsersManage: 'admin.users.manage',
  adminImport: 'admin.import',
  adminJobsManage: 'admin.jobs.manage',
  // Editing the roles themselves. Administrator-only server-side: it can never
  // be put on another role, so holding it means holding the Administrator role.
  adminRolesManage: 'admin.roles.manage',
} as const;

export type PermissionKey = (typeof PERMISSIONS)[keyof typeof PERMISSIONS];

// The three BUILT-IN role keys. Roles are rows now and an installation may have
// any number of custom ones, so this is not the list of roles — it is the list
// of keys the product itself names.
export const ROLES = {
  administrator: 'Administrator',
  member: 'Member',
  restricted: 'Restricted',
} as const;

export type BuiltInRoleKey = (typeof ROLES)[keyof typeof ROLES];

// One permission as the server describes it. `parent` names a permission this
// one is meaningless without (a Laboratory section needs the Laboratory shell);
// `assignable` is false for a capability only the Administrator role may carry,
// which is why the role editor never offers it as a checkbox.
export interface PermissionCatalogEntry {
  key: string;
  group: string;
  administrative: boolean;
  parent: string | null;
  assignable: boolean;
}

export interface PermissionCatalog {
  permissions: PermissionCatalogEntry[];
}
