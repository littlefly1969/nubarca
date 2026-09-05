/* What the party hub knows and the print studio needs.
 *
 * The two pages are reached through DIFFERENT tokens — the hub holds a view
 * token, the studio a print token — so nothing in the URL can connect them, and
 * a guest who has just found their own photographs should not have to find them
 * again to print one.
 *
 * The ids travel through sessionStorage, which is per-tab and gone when the tab
 * closes. Nothing new leaves the browser: these are ids the guest's own search
 * already returned, and the studio only ever uses them by INTERSECTING with the
 * photographs its own token was served. That intersection is also what makes a
 * stale memo harmless — ids from another party match nothing here, so the
 * filter is simply not offered.
 */

const FACE_KEY = 'nubarca.party.faceFilter';
const HOME_KEY = 'nubarca.party.home';

/** Remember (or, with null, forget) the guest's current face-search result. */
export function rememberFaceFilter(itemIds: readonly string[] | null): void {
  try {
    if (itemIds === null || itemIds.length === 0) {
      window.sessionStorage.removeItem(FACE_KEY);
      return;
    }
    window.sessionStorage.setItem(FACE_KEY, JSON.stringify(itemIds));
  } catch {
    // Private browsing, a disabled store, a full quota: the studio just shows
    // every photograph, which is the behaviour without a search at all.
  }
}

export function recallFaceFilter(): string[] {
  try {
    const raw = window.sessionStorage.getItem(FACE_KEY);
    if (!raw) return [];
    const parsed: unknown = JSON.parse(raw);
    if (!Array.isArray(parsed)) return [];
    return parsed.filter((id): id is string => typeof id === 'string');
  } catch {
    return [];
  }
}

/**
 * Where the party hub lives, so the studio can offer a way back to it.
 *
 * The studio is reached on a PRINT token and can address nothing else: a print
 * capability that could also produce a view URL would be a wider capability
 * than the one that was granted. So the path is remembered by the page that
 * legitimately holds it, in the guest's own tab, alongside a token their
 * address bar is already showing. Opened cold — a bookmark, a second QR — there
 * is no memo, and the studio offers no back link rather than a broken one.
 */
export function rememberPartyHome(path: string): void {
  try {
    window.sessionStorage.setItem(HOME_KEY, path);
  } catch {
    // Same as above: without the memo the studio simply has no way back.
  }
}

export function recallPartyHome(): string | null {
  try {
    const path = window.sessionStorage.getItem(HOME_KEY);
    // Only an in-app party path is ever followed, so a tampered memo cannot
    // turn the back link into an off-site redirect.
    return path && /^\/party\/[^/]+$/.test(path) ? path : null;
  } catch {
    return null;
  }
}
