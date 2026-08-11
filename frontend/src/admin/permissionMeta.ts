import { PERMISSIONS, ROLES } from '@nubarca/api-client';
import type { MessageKey } from '../i18n';

// How the product TALKS about a permission or a role.
//
// The keys stay machine contracts and the server stays authoritative; what
// lives here is presentation — a label, a sentence explaining what the
// capability actually opens, and the group it belongs to. One map rather than a
// switch statement in each component, so adding a permission is one entry and
// cannot half-land in three places.
//
// Labels are message keys, not strings: Italian and English both have to be
// complete, and a raw machine key must never reach an operator's screen.

export interface PermissionPresentation {
  labelKey: MessageKey;
  descriptionKey: MessageKey;
}

const PERMISSION_META: Record<string, PermissionPresentation> = {
  [PERMISSIONS.peopleAccess]: {
    labelKey: 'perm.peopleAccess',
    descriptionKey: 'permDesc.peopleAccess',
  },
  [PERMISSIONS.peopleClusterRebuild]: {
    labelKey: 'perm.peopleClusterRebuild',
    descriptionKey: 'permDesc.peopleClusterRebuild',
  },
  [PERMISSIONS.semanticSearchAccess]: {
    labelKey: 'perm.semanticSearchAccess',
    descriptionKey: 'permDesc.semanticSearchAccess',
  },
  [PERMISSIONS.laboratoryAccess]: {
    labelKey: 'perm.laboratoryAccess',
    descriptionKey: 'permDesc.laboratoryAccess',
  },
  [PERMISSIONS.laboratoryPlates]: {
    labelKey: 'perm.laboratoryPlates',
    descriptionKey: 'permDesc.laboratoryPlates',
  },
  [PERMISSIONS.laboratoryAesthetics]: {
    labelKey: 'perm.laboratoryAesthetics',
    descriptionKey: 'permDesc.laboratoryAesthetics',
  },
  [PERMISSIONS.cloudFunctionsAccess]: {
    labelKey: 'perm.cloudFunctionsAccess',
    descriptionKey: 'permDesc.cloudFunctionsAccess',
  },
  [PERMISSIONS.privateVaultAccess]: {
    labelKey: 'perm.privateVaultAccess',
    descriptionKey: 'permDesc.privateVaultAccess',
  },
  [PERMISSIONS.tvManage]: {
    labelKey: 'perm.tvManage',
    descriptionKey: 'permDesc.tvManage',
  },
  [PERMISSIONS.castAccess]: {
    labelKey: 'perm.castAccess',
    descriptionKey: 'permDesc.castAccess',
  },
  [PERMISSIONS.adminDashboard]: {
    labelKey: 'perm.adminDashboard',
    descriptionKey: 'permDesc.adminDashboard',
  },
  [PERMISSIONS.adminUsersManage]: {
    labelKey: 'perm.adminUsersManage',
    descriptionKey: 'permDesc.adminUsersManage',
  },
  [PERMISSIONS.adminImport]: {
    labelKey: 'perm.adminImport',
    descriptionKey: 'permDesc.adminImport',
  },
  [PERMISSIONS.adminJobsManage]: {
    labelKey: 'perm.adminJobsManage',
    descriptionKey: 'permDesc.adminJobsManage',
  },
  [PERMISSIONS.adminRolesManage]: {
    labelKey: 'perm.adminRolesManage',
    descriptionKey: 'permDesc.adminRolesManage',
  },
};

// A key the running server knows about but this build does not is presented as
// "unknown", never as the raw string. An operator must never be asked to reason
// about `laboratory.plates`.
export function permissionLabelKey(key: string): MessageKey {
  return PERMISSION_META[key]?.labelKey ?? 'perm.unknown';
}

export function permissionDescriptionKey(key: string): MessageKey | null {
  return PERMISSION_META[key]?.descriptionKey ?? null;
}

export function permissionGroupLabelKey(group: string): MessageKey {
  return group === 'administration' ? 'adminUsers.groupAdministration' : 'adminUsers.groupFeatures';
}

// The three BUILT-IN roles have product names an operator recognises in their
// own language. A custom role is named by the operator, so its name is shown
// verbatim — translating somebody's own words would be wrong.
const BUILT_IN_ROLE_LABEL: Record<string, MessageKey> = {
  [ROLES.administrator]: 'roles.administrator',
  [ROLES.member]: 'roles.member',
  [ROLES.restricted]: 'roles.restricted',
};

const BUILT_IN_ROLE_DESCRIPTION: Record<string, MessageKey> = {
  [ROLES.administrator]: 'roles.administratorDescription',
  [ROLES.member]: 'roles.memberDescription',
  [ROLES.restricted]: 'roles.restrictedDescription',
};

export function builtInRoleLabelKey(roleKey: string): MessageKey | null {
  return BUILT_IN_ROLE_LABEL[roleKey] ?? null;
}

export function builtInRoleDescriptionKey(roleKey: string): MessageKey | null {
  return BUILT_IN_ROLE_DESCRIPTION[roleKey] ?? null;
}
