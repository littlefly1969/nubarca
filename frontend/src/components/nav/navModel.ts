import { PERMISSIONS, type PermissionKey } from '@nubarca/api-client';
import type { MessageKey } from '../../i18n';
import type { IconName } from '../icons/Icon';

// The single source of truth for the authenticated primary navigation.
//
// Pure data so the information architecture is testable on its own: a test can
// assert that Upload and TV Devices are absent, that Private is present, and
// that a Restricted user sees neither People nor the Laboratory — without
// rendering the shell.
//
// Each restricted destination DECLARES the permission it needs, and the
// generator filters on the current user's effective permissions. Desktop rail
// and mobile drawer both consume this one model: a second, form-factor-specific
// navigation model would be two places for the answer to drift.
//
// Hiding a destination is UX. The backend independently refuses every gated API,
// and a direct navigation still meets a route guard — see PermissionRoute.
//
// Upload and TV Devices deliberately do NOT appear here: they are Cloud
// Functions tools now. Their old routes still resolve (they redirect to the
// matching tool), so existing bookmarks keep working — they are just not
// primary destinations any more.

export interface NavItem {
  to: string;
  labelKey: MessageKey;
  icon: IconName;
  // `end` matches the route exactly (needed for '/' and '/admin', which are
  // prefixes of their children).
  end?: boolean;
  // Omitted for a destination every authenticated user reaches — files, media,
  // albums, shares, trash. Those are the core personal cloud and are not gated
  // at all, which is why the Restricted role needs no permission to keep them.
  permission?: PermissionKey;
}

export type NavGroupId = 'main' | 'more' | 'admin';

export interface NavGroup {
  id: NavGroupId;
  titleKey: MessageKey;
  items: NavItem[];
}

const MAIN: NavItem[] = [
  { to: '/', labelKey: 'nav.files', icon: 'files', end: true },
  { to: '/media', labelKey: 'mediaLib.title', icon: 'library' },
  { to: '/albums', labelKey: 'nav.albums', icon: 'albums' },
  // SHARE-ALBUM-01: albums other people own and have shared with this user. A
  // primary destination rather than a tab inside /albums, so somebody else's
  // content is never mixed into the list of the user's own albums.
  { to: '/shared-albums', labelKey: 'nav.sharedAlbums', icon: 'shared-albums' },
  { to: '/people', labelKey: 'nav.people', icon: 'people', permission: PERMISSIONS.peopleAccess },
];

const MORE: NavItem[] = [
  // ONE Laboratory entry. Plates and Aesthetics are sections inside it now,
  // reached by /lab/plates and /lab/aesthetics; without `end` this link stays
  // active for every /lab/* child. The entry needs only the SHELL permission:
  // which sections appear inside is the Laboratory page's own decision, so a
  // user with Plates but not Aesthetics still sees the Laboratory.
  {
    to: '/lab',
    labelKey: 'nav.laboratory',
    icon: 'aesthetics',
    permission: PERMISSIONS.laboratoryAccess,
  },
  { to: '/shares', labelKey: 'nav.shares', icon: 'shares' },
  {
    to: '/cloud-functions',
    labelKey: 'nav.cloudFunctions',
    icon: 'functions',
    permission: PERMISSIONS.cloudFunctionsAccess,
  },
  {
    to: '/private',
    labelKey: 'nav.private',
    icon: 'private',
    permission: PERMISSIONS.privateVaultAccess,
  },
  { to: '/trash', labelKey: 'nav.trash', icon: 'trash' },
];

// Each administration destination carries its OWN permission, so holding one
// never reveals another — the same separation the four admin APIs enforce.
const ADMIN: NavItem[] = [
  {
    to: '/admin',
    labelKey: 'nav.admin',
    icon: 'admin',
    end: true,
    permission: PERMISSIONS.adminDashboard,
  },
  {
    to: '/admin/users',
    labelKey: 'nav.users',
    icon: 'users',
    permission: PERMISSIONS.adminUsersManage,
  },
  // Roles are a first-class destination, and a SEPARATE authority: a user
  // manager runs accounts, while editing what a role may do is Administrator
  // work. Managing users without seeing this entry is the intended shape.
  {
    to: '/admin/roles',
    labelKey: 'nav.roles',
    icon: 'roles',
    permission: PERMISSIONS.adminRolesManage,
  },
  { to: '/admin/import', labelKey: 'nav.import', icon: 'import', permission: PERMISSIONS.adminImport },
  { to: '/admin/jobs', labelKey: 'nav.jobs', icon: 'jobs', permission: PERMISSIONS.adminJobsManage },
];

export interface NavModelInput {
  // The caller's effective permissions, straight from `/api/auth/me`.
  permissions: readonly string[];
}

export function buildNavGroups({ permissions }: NavModelInput): NavGroup[] {
  const allowed = (item: NavItem) =>
    item.permission === undefined || permissions.includes(item.permission);

  const groups: NavGroup[] = [
    { id: 'main', titleKey: 'nav.groupMain', items: MAIN.filter(allowed) },
    { id: 'more', titleKey: 'nav.groupMore', items: MORE.filter(allowed) },
  ];

  // The Administration group materialises only when at least one of its
  // destinations survives, so a user with no administrative permission sees no
  // empty heading.
  const adminItems = ADMIN.filter(allowed);
  if (adminItems.length > 0) {
    groups.push({ id: 'admin', titleKey: 'nav.groupAdmin', items: adminItems });
  }
  return groups;
}
