import type { MessageKey } from '../../i18n';
import type { IconName } from '../icons/Icon';

// The single source of truth for the authenticated primary navigation.
//
// Pure data so the information architecture is testable on its own: a test can
// assert that Upload and TV Devices are absent, that Private is present, and
// that the administration group only materializes for an admin — without
// rendering the shell.
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
  { to: '/people', labelKey: 'nav.people', icon: 'people' },
];

const MORE: NavItem[] = [
  // ONE Laboratory entry. Plates and Aesthetics are sections inside it now,
  // reached by /lab/plates and /lab/aesthetics; without `end` this link stays
  // active for every /lab/* child.
  { to: '/lab', labelKey: 'nav.laboratory', icon: 'aesthetics' },
  { to: '/shares', labelKey: 'nav.shares', icon: 'shares' },
  { to: '/cloud-functions', labelKey: 'nav.cloudFunctions', icon: 'functions' },
  { to: '/private', labelKey: 'nav.private', icon: 'private' },
  { to: '/trash', labelKey: 'nav.trash', icon: 'trash' },
];

// Admin entries stay exactly as they were — this slice only moves them into
// their own visually separated group. The backend gates /api/admin/* itself;
// hiding the group is UX, not security.
const ADMIN: NavItem[] = [
  { to: '/admin', labelKey: 'nav.admin', icon: 'admin', end: true },
  { to: '/admin/import', labelKey: 'nav.import', icon: 'import' },
  { to: '/admin/jobs', labelKey: 'nav.jobs', icon: 'jobs' },
  { to: '/admin/users', labelKey: 'nav.users', icon: 'users' },
];

export function buildNavGroups({ isAdmin }: { isAdmin: boolean }): NavGroup[] {
  const groups: NavGroup[] = [
    { id: 'main', titleKey: 'nav.groupMain', items: MAIN },
    { id: 'more', titleKey: 'nav.groupMore', items: MORE },
  ];
  if (isAdmin) {
    groups.push({ id: 'admin', titleKey: 'nav.groupAdmin', items: ADMIN });
  }
  return groups;
}
