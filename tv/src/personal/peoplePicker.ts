// Policy for the TV People picker — explicit paging, local name search and row
// geometry. Pure, node-testable, no React and no react-native import.
//
// WHY A POLICY MODULE AND NOT JUST A COMPONENT
// --------------------------------------------
// A physical Fire Stick gave the decisive failure mode: rows in a FlatList
// could receive focus and selection while their content was not painted (only
// a thin strip was visible). That makes any virtualized/scrolling solution the
// wrong primitive here, even if it behaves correctly in tests.
//
// The picker therefore mounts one small, explicit 2x4 page at a time. Every
// focusable person is an ordinary visible child; no native list viewport owns
// its geometry. Search and previous/next page controls keep a large library
// quick to navigate without mounting hundreds of focusables.

export interface PickerPerson {
  readonly id: string;
  readonly name: string | null;
  readonly faceCount: number;
}

export type PersonSelection = 'off' | 'include' | 'exclude';

// --- explicit pages ---------------------------------------------------------

/** A landscape TV uses width as well as height: two columns, four rows. */
export const PEOPLE_GRID_COLUMNS = 2;
export const PEOPLE_GRID_ROWS = 4;
export const PEOPLE_PAGE_SIZE = PEOPLE_GRID_COLUMNS * PEOPLE_GRID_ROWS;

function checkedPageSize(pageSize: number): number {
  if (!Number.isInteger(pageSize) || pageSize <= 0) {
    throw new RangeError('pageSize must be a positive integer');
  }
  return pageSize;
}

/** There is always one logical page, including the empty-search state. */
export function peoplePageCount(total: number, pageSize = PEOPLE_PAGE_SIZE): number {
  const size = checkedPageSize(pageSize);
  const safeTotal = Number.isFinite(total) ? Math.max(0, Math.trunc(total)) : 0;
  return Math.max(1, Math.ceil(safeTotal / size));
}

/** Keep a requested zero-based page inside the current result set. */
export function clampPeoplePage(
  page: number,
  total: number,
  pageSize = PEOPLE_PAGE_SIZE,
): number {
  const last = peoplePageCount(total, pageSize) - 1;
  const requested = Number.isFinite(page) ? Math.trunc(page) : 0;
  return Math.max(0, Math.min(requested, last));
}

/** The only people mounted in the chooser at a given moment. */
export function peoplePage<T>(
  people: readonly T[],
  page: number,
  pageSize = PEOPLE_PAGE_SIZE,
): T[] {
  const size = checkedPageSize(pageSize);
  const safePage = clampPeoplePage(page, people.length, size);
  const start = safePage * size;
  return people.slice(start, start + size);
}

/** Page containing a known person, or the first page when the id is absent. */
export function peoplePageForId<T extends { readonly id: string }>(
  people: readonly T[],
  personId: string,
  pageSize = PEOPLE_PAGE_SIZE,
): number {
  const size = checkedPageSize(pageSize);
  const index = people.findIndex((person) => person.id === personId);
  return index < 0 ? 0 : Math.floor(index / size);
}

/** Split one bounded page into native-focus-friendly horizontal rows. */
export function peopleGridRows<T>(
  people: readonly T[],
  columns = PEOPLE_GRID_COLUMNS,
): T[][] {
  const width = checkedPageSize(columns);
  const rows: T[][] = [];
  for (let start = 0; start < people.length; start += width) {
    rows.push(people.slice(start, start + width));
  }
  return rows;
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
