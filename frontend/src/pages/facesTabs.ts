// The Faces (Volti) tab contract, as a pure unit.
//
// The selected tab used to be component state, which meant: a refresh dropped
// it, a tab could not be linked, Back/Forward ignored it, and opening a named
// person and coming back landed on the default tab instead of the one you left.
// The URL owns it now — /people?tab=people — and this module is the only place
// that decides what a query string means.
//
// Nothing here renames a route, an endpoint, a DTO or a table. `/people` and
// `Person` stay exactly what they are; only what the user READS changed, and
// that lives in the locale files.

export const FACES_TABS = [
  'suggested',
  'people',
  'unassigned',
  'review',
  'videoFaces',
  'ignored',
  'settings',
] as const;

export type FacesTab = (typeof FACES_TABS)[number];

/** Where an absent or unusable `?tab=` lands. */
export const DEFAULT_FACES_TAB: FacesTab = 'suggested';

/** The named-cluster tab: the return target from a person detail. */
export const NAMED_PEOPLE_TAB: FacesTab = 'people';

/** Settings exposes admin-only Face AI thresholds. */
const ADMIN_ONLY: ReadonlySet<FacesTab> = new Set<FacesTab>(['settings']);

/**
 * The tab a query value selects.
 *
 * Anything unrecognised, absent, or not permitted for this user resolves to
 * the default rather than erroring — a hand-edited or stale URL should show
 * the page, not break it. The admin gate here is UX: the backend gates the
 * Face AI settings endpoints itself.
 */
export function resolveFacesTab(raw: string | null | undefined, isAdmin: boolean): FacesTab {
  const candidate = FACES_TABS.find((t) => t === raw);
  if (candidate === undefined) return DEFAULT_FACES_TAB;
  if (ADMIN_ONLY.has(candidate) && !isAdmin) return DEFAULT_FACES_TAB;
  return candidate;
}

/** The canonical location for a tab. */
export function facesTabPath(tab: FacesTab): string {
  return `/people?tab=${tab}`;
}

/** The return target when a person detail was opened without one. */
export const FACES_FALLBACK_RETURN = facesTabPath(NAMED_PEOPLE_TAB);

/**
 * Router state carried from a Faces tab into a person detail, so the visible
 * Back action returns to the EXACT tab that opened it.
 *
 * A person URL can also be opened directly, in a new tab, or from a bookmark,
 * where there is no state and no useful history entry — which is why the Back
 * action is a link to a resolved location rather than `navigate(-1)`.
 */
export interface FacesReturnState {
  facesReturn?: string;
}

/** The return location for a person detail: the caller's tab, or the fallback. */
export function resolveFacesReturn(state: unknown): string {
  const candidate = (state as FacesReturnState | null)?.facesReturn;
  // Same-origin, in-app paths only: never follow an absolute or
  // protocol-relative URL out of the application.
  if (typeof candidate === 'string'
    && candidate.startsWith('/people')
    && !candidate.startsWith('//')) {
    return candidate;
  }
  return FACES_FALLBACK_RETURN;
}
