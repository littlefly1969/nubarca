// When a workspace screen should reload on focus.
//
// THE BUG THIS ANSWERS, reported from a physical device: opening a photo and
// coming back dropped the reader far up the library, and past a certain point
// it always landed in the same place.
//
// The cause is that a refresh REPLACES the accumulator with page one. A screen
// that refreshed on every focus therefore threw away every page the user had
// scrolled through, and the list could never be longer than one page after a
// round trip to the viewer. "The same place" was the end of page one.
//
// So a focus refresh is for a list that has nothing yet. A list that already
// has content keeps it, and stays fresh by three other means that all still
// work: pull-to-refresh, the explicit refresh every mutation already performs,
// and the new query generation a filter change produces.
//
// The trade is stated rather than hidden: an item added on ANOTHER screen —
// by the sync engine, say — will not appear in an already-loaded list until
// the user pulls to refresh. Silently discarding their scroll position on
// every return is the worse of the two.

export function shouldRefreshOnFocus(input: {
  /** Items already accumulated. */
  itemCount: number;
  /** True once a mutation elsewhere has marked this list stale. */
  stale: boolean;
}): boolean {
  if (input.stale) return true;
  return input.itemCount === 0;
}
