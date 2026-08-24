import { describe, expect, it } from 'vitest';
import { PERMISSIONS } from '@nubarca/api-client';
import { ALL_PERMISSIONS, MEMBER_PERMISSIONS } from '../../test-utils';
import { buildNavGroups } from './navModel';

const RESTRICTED: readonly string[] = [];

function allRoutes(permissions: readonly string[]): string[] {
  return buildNavGroups({ permissions }).flatMap((g) => g.items.map((i) => i.to));
}

describe('primary navigation model', () => {
  // The migration promise, in navigation form: a Member — which is what every
  // pre-role non-admin account became — sees exactly the destinations they saw
  // before roles existed.
  it('keeps every normal-user destination for a Member', () => {
    expect(allRoutes(MEMBER_PERMISSIONS)).toEqual([
      '/', '/media', '/albums', '/people',
      '/lab', '/shares', '/cloud-functions', '/private', '/trash',
    ]);
  });

  // An album is an album. Albums the user owns and albums other people have
  // shared with them are ONE destination, because putting somebody else's album
  // in a different part of the product made it read as a different feature. The
  // collection is stated on every card and addressed by /albums?scope=shared —
  // never by a second primary entry.
  it('offers ONE albums destination, not a second one for shared albums', () => {
    const routes = allRoutes(MEMBER_PERMISSIONS);
    expect(routes).toContain('/albums');
    expect(routes).not.toContain('/shared-albums');
    const albums = buildNavGroups({ permissions: MEMBER_PERMISSIONS })
      .flatMap((g) => g.items).find((i) => i.to === '/albums')!;
    expect(albums.labelKey).toBe('nav.albums');
    // No `end`: /albums/:albumId keeps the entry active.
    expect(albums.end).toBeUndefined();
  });

  // UX-02: Plates and Aesthetics are sections of one Laboratory workspace,
  // not two primary destinations. The entry has no `end`, so it stays active
  // for every /lab/* child route.
  it('offers ONE Laboratory entry, not separate Plates and Aesthetics', () => {
    const routes = allRoutes(MEMBER_PERMISSIONS);
    expect(routes).toContain('/lab');
    expect(routes).not.toContain('/plates');
    expect(routes).not.toContain('/lab/aesthetics');
    const lab = buildNavGroups({ permissions: MEMBER_PERMISSIONS })
      .flatMap((g) => g.items).find((i) => i.to === '/lab')!;
    expect(lab.end).toBeUndefined();
    expect(lab.labelKey).toBe('nav.laboratory');
  });

  it('excludes Upload and TV Devices — they are Cloud Functions tools now', () => {
    const routes = allRoutes(ALL_PERMISSIONS);
    expect(routes).not.toContain('/upload');
    expect(routes).not.toContain('/tv-devices');
  });

  it('omits the administration group entirely for a Member', () => {
    const groups = buildNavGroups({ permissions: MEMBER_PERMISSIONS });
    expect(groups.map((g) => g.id)).toEqual(['main', 'more']);
    expect(allRoutes(MEMBER_PERMISSIONS).some((r) => r.startsWith('/admin'))).toBe(false);
  });

  it('adds a separate administration group for an Administrator', () => {
    const groups = buildNavGroups({ permissions: ALL_PERMISSIONS });
    expect(groups.map((g) => g.id)).toEqual(['main', 'more', 'admin']);
    const admin = groups.find((g) => g.id === 'admin')!;
    expect(admin.items.map((i) => i.to)).toEqual([
      '/admin', '/admin/users', '/admin/roles', '/admin/import', '/admin/jobs',
    ]);
  });

  it('shows Roles only to somebody who may edit them', () => {
    // Managing users and editing roles are different authorities: a user
    // manager assigns roles and can never change what one contains.
    expect(allRoutes([PERMISSIONS.adminUsersManage])).toContain('/admin/users');
    expect(allRoutes([PERMISSIONS.adminUsersManage])).not.toContain('/admin/roles');
    expect(allRoutes([PERMISSIONS.adminRolesManage])).toContain('/admin/roles');
  });

  it('matches / and /admin exactly so their children do not light them up', () => {
    const items = buildNavGroups({ permissions: ALL_PERMISSIONS }).flatMap((g) => g.items);
    expect(items.find((i) => i.to === '/')?.end).toBe(true);
    expect(items.find((i) => i.to === '/admin')?.end).toBe(true);
    expect(items.find((i) => i.to === '/media')?.end).toBeUndefined();
  });

  it('gives every entry an icon and a label key', () => {
    for (const item of buildNavGroups({ permissions: ALL_PERMISSIONS }).flatMap((g) => g.items)) {
      expect(item.icon).toBeTruthy();
      expect(item.labelKey).toBeTruthy();
    }
  });

  // ---------------------------------------------------------- permissions

  it('leaves the core personal cloud untouched for a Restricted user', () => {
    // Files, media, albums, shares and trash are not gated at all — which is
    // why Restricted needs no permission to keep them.
    expect(allRoutes(RESTRICTED)).toEqual([
      '/', '/media', '/albums', '/shares', '/trash',
    ]);
  });

  it('omits People without people.access', () => {
    expect(allRoutes(RESTRICTED)).not.toContain('/people');
    expect(allRoutes([PERMISSIONS.peopleAccess])).toContain('/people');
  });

  it('omits the Laboratory without laboratory.access', () => {
    expect(allRoutes(RESTRICTED)).not.toContain('/lab');
  });

  it('shows the Laboratory for a user holding the shell and only Plates', () => {
    // Which sections appear INSIDE it is the Laboratory page's own decision;
    // the nav entry needs only the shell permission.
    const routes = allRoutes([PERMISSIONS.laboratoryAccess, PERMISSIONS.laboratoryPlates]);
    expect(routes).toContain('/lab');
  });

  it('omits Cloud Functions and the Private Vault without their permissions', () => {
    expect(allRoutes(RESTRICTED)).not.toContain('/cloud-functions');
    expect(allRoutes(RESTRICTED)).not.toContain('/private');
    expect(allRoutes([PERMISSIONS.cloudFunctionsAccess])).toContain('/cloud-functions');
    expect(allRoutes([PERMISSIONS.privateVaultAccess])).toContain('/private');
  });

  it('shows only the administration destinations the user actually holds', () => {
    // Holding one administrative permission must not advertise the others —
    // the same separation the four admin APIs enforce server-side.
    const groups = buildNavGroups({ permissions: [PERMISSIONS.adminJobsManage] });
    const admin = groups.find((g) => g.id === 'admin')!;
    expect(admin.items.map((i) => i.to)).toEqual(['/admin/jobs']);
  });

  it('declares a permission on every gated destination and none on the core ones', () => {
    const items = buildNavGroups({ permissions: ALL_PERMISSIONS }).flatMap((g) => g.items);
    const gated = Object.fromEntries(items.map((i) => [i.to, i.permission]));

    expect(gated['/people']).toBe(PERMISSIONS.peopleAccess);
    expect(gated['/lab']).toBe(PERMISSIONS.laboratoryAccess);
    expect(gated['/cloud-functions']).toBe(PERMISSIONS.cloudFunctionsAccess);
    expect(gated['/private']).toBe(PERMISSIONS.privateVaultAccess);
    expect(gated['/admin']).toBe(PERMISSIONS.adminDashboard);
    expect(gated['/admin/users']).toBe(PERMISSIONS.adminUsersManage);
    expect(gated['/admin/import']).toBe(PERMISSIONS.adminImport);
    expect(gated['/admin/jobs']).toBe(PERMISSIONS.adminJobsManage);

    for (const core of ['/', '/media', '/albums', '/shared-albums', '/shares', '/trash']) {
      expect(gated[core]).toBeUndefined();
    }
  });
});
