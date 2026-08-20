// Policy for the TV People picker — virtualization, focus/viewport
// reconciliation, local name search and row geometry. Pure, node-testable, no
// React and no react-native import.
//
// WHY A POLICY MODULE AND NOT JUST A COMPONENT
// --------------------------------------------
// The picker previously did `people.map(...)` inside a ScrollView. With a real
// owner library that is hundreds of focusable rows mounted at once, on a Fire
// Stick, inside a panel that also wanted to scroll. Three separate problems —
// how many rows exist, who scrolls, and where the remote is — were tangled in
// one JSX expression, so none of them could be tested or reasoned about.
//
// THE FOCUS RULE, STATED ONCE
// ---------------------------
// The NATIVE focus engine decides which row is focused. JavaScript never
// decides UP/DOWN and never builds a nextFocusUp/nextFocusDown graph. What
// JavaScript may do is REACT: when a row reports `onFocus`, keep that row
// inside a comfortable band of the visible viewport. That is the difference
// between "focus follows the viewport" (correct) and "JS navigates" (a second
// authority that will eventually disagree with Android about which row is
// focused — the classic TV bug where the highlight and the selection differ).
//
// Because row height is fixed and `getItemLayout` is therefore exact, this
// reconciliation is arithmetic. It needs no timeout, no readiness probe and no
// debounce.

export interface PickerPerson {
  readonly id: string;
  readonly name: string | null;
  readonly faceCount: number;
}

export type PersonSelection = 'off' | 'include' | 'exclude';

// --- virtualization ----------------------------------------------------------

/** One person row's fixed height in dp. Fixed is what makes getItemLayout exact. */
export const PERSON_ROW_HEIGHT = 64;

/**
 * FlatList tuning. Bounded on purpose: the point of the rewrite is that a
 * 400-person library never mounts 400 focusables.
 *
 * `removeClippedSubviews` is FALSE and must stay false. On Android TV it
 * detaches off-screen views, and a detached view cannot hold focus — which
 * produces exactly the "focus vanishes when scrolling fast" defect this picker
 * is meant to end.
 */
export const PEOPLE_LIST_TUNING = {
  initialNumToRender: 12,
  maxToRenderPerBatch: 8,
  windowSize: 5,
  removeClippedSubviews: false,
} as const;

/** Exact geometry for FlatList.getItemLayout — no measurement, no async. */
export function personItemLayout(index: number): {
  length: number; offset: number; index: number;
} {
  return { length: PERSON_ROW_HEIGHT, offset: PERSON_ROW_HEIGHT * index, index };
}

/** How many whole rows fit in a viewport of `height` dp. At least one. */
export function visibleRowCount(height: number): number {
  return Math.max(1, Math.floor(height / PERSON_ROW_HEIGHT));
}

// --- focus / viewport reconciliation ----------------------------------------

export interface FocusViewport {
  /** Index the native engine just reported focused. */
  focusedIndex: number;
  /** First index currently visible, derived from scroll offset. */
  firstVisibleIndex: number;
  /** How many rows the viewport shows. */
  visibleCount: number;
  /** Total rows in the list. */
  total: number;
}

/**
 * Rows kept between the focused row and the viewport edge. Below this the row
 * is "approaching the boundary" and the list is scrolled to bring it back
 * toward the middle.
 */
export const FOCUS_SAFE_ROWS = 1;

export interface ScrollRequest {
  index: number;
  /** 0 = top, 0.5 = centred, 1 = bottom. */
  viewPosition: number;
}

/**
 * What (if anything) the list should do about a freshly focused row.
 *
 * Returns null when the row is already comfortably visible — which is the
 * common case, and the reason a held-down D-pad does not fight the scroller.
 * Otherwise it returns a deterministic target that recentres the row.
 */
export function reconcileFocusViewport(viewport: FocusViewport): ScrollRequest | null {
  const { focusedIndex, firstVisibleIndex, visibleCount, total } = viewport;
  if (total <= 0 || focusedIndex < 0) return null;
  // Everything fits: there is nothing to scroll.
  if (total <= visibleCount) return null;

  const lastVisibleIndex = firstVisibleIndex + visibleCount - 1;
  // Shrink the safe band rather than let it invert on a very short viewport.
  const margin = Math.min(FOCUS_SAFE_ROWS, Math.floor((visibleCount - 1) / 2));
  const bandTop = firstVisibleIndex + margin;
  const bandBottom = lastVisibleIndex - margin;

  // The first and last rows of the whole list have no room to be centred, so
  // reaching them is not a boundary violation.
  const atListTop = focusedIndex <= margin;
  const atListBottom = focusedIndex >= total - 1 - margin;
  const insideBand = focusedIndex >= bandTop && focusedIndex <= bandBottom;
  if (insideBand) return null;
  if (atListTop && focusedIndex >= firstVisibleIndex && focusedIndex <= lastVisibleIndex) return null;
  if (atListBottom && focusedIndex >= firstVisibleIndex && focusedIndex <= lastVisibleIndex) return null;

  return { index: focusedIndex, viewPosition: 0.5 };
}

// --- local name search -------------------------------------------------------

/**
 * Narrow the ALREADY-LOADED owner-scoped projection by display name.
 *
 * This is picker NAVIGATION, not a Media Workspace filter: it never touches
 * include/exclude, never reaches the backend, and is discarded when the picker
 * closes. Virtualization alone makes a 200-person list cheap to render but
 * still leaves person #87 twenty seconds of D-pad away.
 */
export function filterPeopleByName<T extends PickerPerson>(
  people: readonly T[],
  query: string,
  unnamedLabel = '',
): T[] {
  const needle = query.trim().toLocaleLowerCase();
  if (needle.length === 0) return [...people];
  return people.filter((person) => {
    const name = (person.name ?? unnamedLabel).toLocaleLowerCase();
    return name.includes(needle);
  });
}

/**
 * Where focus must land after a search narrows the list.
 *
 * Deterministic and total: the currently focused person when they survived,
 * otherwise the first remaining person, otherwise a named fallback control —
 * never "nowhere", which on a television is a dead remote.
 */
export function focusAfterSearch(
  visible: readonly PickerPerson[],
  focusedPersonId: string | null,
  fallback: string,
): string {
  if (focusedPersonId !== null && visible.some((p) => p.id === focusedPersonId)) {
    return focusedPersonId;
  }
  return visible.length > 0 ? visible[0].id : fallback;
}

// --- row geometry ------------------------------------------------------------

// A person row is NOT a generic filter row. The generic split gives the label
// about a third and the value two thirds, which is right for "Valutazione /
// Almeno 3 stelle" and wrong for "Maria Annunziata della Rovere / Off": the
// name — the only part that identifies the row — gets squeezed by a three-letter
// state.
//
// So the name takes the majority and the trailing meta is BOUNDED. The width
// allocated to the name must not depend on what the trailing text currently
// says, because otherwise the same name truncates at a different character in
// each of Off / Include / Exclude, and the row appears to change identity as
// the user cycles it.
export const PERSON_NAME_FLEX = 7;
export const PERSON_META_FLEX = 3;

/**
 * The trailing meta text: selection state plus a compact face count.
 *
 * The face count is deliberately NOT concatenated into the name. Doing that
 * made the count part of the truncatable string, so a long name could lose the
 * number entirely and a short one could push the name's ellipsis around.
 */
export function personMetaText(
  state: PersonSelection,
  faceCount: number,
  stateLabel: (state: PersonSelection) => string,
): string {
  const label = stateLabel(state);
  return faceCount > 0 ? `${label} · ${faceCount}` : label;
}
