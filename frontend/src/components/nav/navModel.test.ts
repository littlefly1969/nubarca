import { describe, expect, it } from 'vitest';
import { buildNavGroups } from './navModel';

function allRoutes(isAdmin: boolean): string[] {
  return buildNavGroups({ isAdmin }).flatMap((g) => g.items.map((i) => i.to));
}

describe('primary navigation model', () => {
  it('keeps the normal-user destinations', () => {
    const routes = allRoutes(false);
    expect(routes).toEqual([
      '/', '/media', '/albums', '/shared-albums', '/people',
      '/lab', '/shares', '/cloud-functions', '/private', '/trash',
    ]);
  });

  // SHARE-ALBUM-01: albums other people own are their OWN destination, next to
  // — never merged into — the user's own /albums. Mixing them would make "whose
  // album is this" a matter of reading a badge.
  it('keeps albums shared with the user separate from their own albums', () => {
    const routes = allRoutes(false);
    expect(routes).toContain('/albums');
    expect(routes).toContain('/shared-albums');
    const shared = buildNavGroups({ isAdmin: false })
      .flatMap((g) => g.items).find((i) => i.to === '/shared-albums')!;
    expect(shared.labelKey).toBe('nav.sharedAlbums');
    // No `end`: /shared-albums/:albumId keeps the entry active.
    expect(shared.end).toBeUndefined();
  });

  // UX-02: Plates and Aesthetics are sections of one Laboratory workspace,
  // not two primary destinations. The entry has no `end`, so it stays active
  // for every /lab/* child route.
  it('offers ONE Laboratory entry, not separate Plates and Aesthetics', () => {
    const routes = allRoutes(false);
    expect(routes).toContain('/lab');
    expect(routes).not.toContain('/plates');
    expect(routes).not.toContain('/lab/aesthetics');
    const lab = buildNavGroups({ isAdmin: false })
      .flatMap((g) => g.items).find((i) => i.to === '/lab')!;
    expect(lab.end).toBeUndefined();
    expect(lab.labelKey).toBe('nav.laboratory');
  });

  it('excludes Upload and TV Devices — they are Cloud Functions tools now', () => {
    const routes = allRoutes(true);
    expect(routes).not.toContain('/upload');
    expect(routes).not.toContain('/tv-devices');
  });

  it('keeps Private Vault as a primary destination', () => {
    expect(allRoutes(false)).toContain('/private');
  });

  it('keeps Cloud Functions as a primary destination', () => {
    expect(allRoutes(false)).toContain('/cloud-functions');
  });

  it('omits the administration group entirely for a normal user', () => {
    const groups = buildNavGroups({ isAdmin: false });
    expect(groups.map((g) => g.id)).toEqual(['main', 'more']);
    expect(allRoutes(false).some((r) => r.startsWith('/admin'))).toBe(false);
  });

  it('adds a separate administration group for an admin', () => {
    const groups = buildNavGroups({ isAdmin: true });
    expect(groups.map((g) => g.id)).toEqual(['main', 'more', 'admin']);
    const admin = groups.find((g) => g.id === 'admin')!;
    expect(admin.items.map((i) => i.to)).toEqual([
      '/admin', '/admin/import', '/admin/jobs', '/admin/users',
    ]);
  });

  it('matches / and /admin exactly so their children do not light them up', () => {
    const items = buildNavGroups({ isAdmin: true }).flatMap((g) => g.items);
    expect(items.find((i) => i.to === '/')?.end).toBe(true);
    expect(items.find((i) => i.to === '/admin')?.end).toBe(true);
    expect(items.find((i) => i.to === '/media')?.end).toBeUndefined();
  });

  it('gives every entry an icon and a label key', () => {
    for (const item of buildNavGroups({ isAdmin: true }).flatMap((g) => g.items)) {
      expect(item.icon).toBeTruthy();
      expect(item.labelKey).toBeTruthy();
    }
  });
});
