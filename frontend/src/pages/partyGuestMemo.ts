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

/*
 * The guest's own picks, for printing — kept PER PARTY.
 *
 * An anonymous guest has no account to hold a selection, so it lives exactly as
 * long as the session that granted it: this tab's sessionStorage, ids only —
 * never a token, never a URL. One tab can visit two parties, and the same
 * photograph can be in both, so filtering by what a party serves is not enough:
 * the picks are stored under a key derived from the party's PRINT token, the
 * one capability both the hub (its print link) and the studio (its own route)
 * hold. The key is a one-way digest of it, so the token itself is never
 * written down, and nothing of party A is shown, printed or forgotten by B.
 */
const PICKS_PREFIX = 'nubarca.party.picks.';
// Written by an earlier build without a party scope; never read, only removed.
const LEGACY_PICKS_KEY = 'nubarca.party.picks';
const MAX_PICKS = 200;

// cyrb53 twice with different seeds: 106 bits of a stable, synchronous digest.
// It only has to tell parties apart and not reveal the token; a print token is
// already high-entropy and is in the guest's own address bar anyway.
function digest(text: string): string {
  const half = (seed: number) => {
    let h1 = 0xdeadbeef ^ seed;
    let h2 = 0x41c6ce57 ^ seed;
    for (let i = 0; i < text.length; i++) {
      const ch = text.charCodeAt(i);
      h1 = Math.imul(h1 ^ ch, 2654435761);
      h2 = Math.imul(h2 ^ ch, 1597334677);
    }
    h1 = Math.imul(h1 ^ (h1 >>> 16), 2246822507) ^ Math.imul(h2 ^ (h2 >>> 13), 3266489909);
    h2 = Math.imul(h2 ^ (h2 >>> 16), 2246822507) ^ Math.imul(h1 ^ (h1 >>> 13), 3266489909);
    return (4294967296 * (2097151 & h2) + (h1 >>> 0)).toString(36);
  };
  return `${half(1)}${half(2)}`;
}

/** The scope of one party's picks, from its print token (never stored itself). */
export function partyPicksScope(printToken: string): string {
  return digest(printToken);
}

/** The print token in a hub's print link (`/party/{token}/print`), or null. */
export function printTokenOf(printUrl: string | null | undefined): string | null {
  const match = printUrl ? /^\/party\/([^/?#]+)\/print$/.exec(printUrl) : null;
  return match ? match[1] : null;
}

export function rememberPartyPicks(scope: string, itemIds: readonly string[]): void {
  try {
    window.sessionStorage.removeItem(LEGACY_PICKS_KEY);
    if (itemIds.length === 0) {
      window.sessionStorage.removeItem(PICKS_PREFIX + scope);
      return;
    }
    window.sessionStorage.setItem(PICKS_PREFIX + scope, JSON.stringify(itemIds.slice(0, MAX_PICKS)));
  } catch {
    // Without storage the picks simply last as long as the page.
  }
}

export function recallPartyPicks(scope: string): string[] {
  try {
    const raw = window.sessionStorage.getItem(PICKS_PREFIX + scope);
    if (!raw) return [];
    const parsed: unknown = JSON.parse(raw);
    if (!Array.isArray(parsed)) return [];
    return parsed.filter((id): id is string => typeof id === 'string').slice(0, MAX_PICKS);
  } catch {
    return [];
  }
}

export function forgetPartyPicks(scope: string): void {
  rememberPartyPicks(scope, []);
}
