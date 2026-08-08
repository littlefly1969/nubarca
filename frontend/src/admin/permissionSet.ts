import type { PermissionCatalogEntry } from '@nubarca/api-client';

// The dependency rule, as a pure function.
//
// `laboratory.plates` and `laboratory.aesthetics` are meaningless without
// `laboratory.access` — the server refuses to store a role carrying a section
// without its shell, and the endpoint policy would refuse the request anyway.
// So the editor makes the broken state unreachable rather than reporting it
// afterwards: turning a section ON turns the shell on with it, and turning the
// shell OFF takes its sections with it.
//
// Kept out of the component so the behaviour can be asserted directly, and so
// the two places that edit a permission set cannot disagree about it.

export function togglePermission(
  current: readonly string[],
  key: string,
  enabled: boolean,
  catalog: readonly PermissionCatalogEntry[],
): string[] {
  const next = new Set(current);
  const entry = catalog.find((p) => p.key === key);

  if (enabled) {
    next.add(key);
    // Enable the parent in the SAME change, so the operator never sees a moment
    // where the section is ticked and grants nothing.
    if (entry?.parent) {
      next.add(entry.parent);
    }
  } else {
    next.delete(key);
    for (const child of catalog.filter((p) => p.parent === key)) {
      next.delete(child.key);
    }
  }

  return catalog
    .filter((p) => next.has(p.key))
    .map((p) => p.key)
    .sort();
}

// Two permission sets are the same set, regardless of order. Used for the
// editor's dirty state: reordering is not an edit.
export function samePermissions(a: readonly string[], b: readonly string[]): boolean {
  if (a.length !== b.length) return false;
  const left = [...a].sort();
  const right = [...b].sort();
  return left.every((key, i) => key === right[i]);
}

// The catalogue grouped for display, parents before their sections, and with
// the entries a role may never carry (Administrator-only) removed.
export interface PermissionGroup {
  group: string;
  entries: PermissionCatalogEntry[];
}

export function groupAssignablePermissions(
  catalog: readonly PermissionCatalogEntry[],
): PermissionGroup[] {
  const groups: PermissionGroup[] = [];
  for (const entry of catalog) {
    if (!entry.assignable) continue;
    let bucket = groups.find((g) => g.group === entry.group);
    if (!bucket) {
      bucket = { group: entry.group, entries: [] };
      groups.push(bucket);
    }
    bucket.entries.push(entry);
  }
  return groups;
}

// Every permission grouped, including the ones no role may be given. A PREVIEW
// shows the whole picture — an operator reading what the Administrator role
// contains should see role management in it.
export function groupAllPermissions(
  catalog: readonly PermissionCatalogEntry[],
): PermissionGroup[] {
  const groups: PermissionGroup[] = [];
  for (const entry of catalog) {
    let bucket = groups.find((g) => g.group === entry.group);
    if (!bucket) {
      bucket = { group: entry.group, entries: [] };
      groups.push(bucket);
    }
    bucket.entries.push(entry);
  }
  return groups;
}
