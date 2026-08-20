// The cursor-paging TOTAL contract, and the one place that understands its
// sentinel. Pure, node-testable, no React.
//
// THE CONTRACT
// ------------
// The backend answers a cursor page with:
//
//     first page       TotalCount = the exact total
//     later pages      TotalCount = -1
//
// -1 does NOT mean "zero" and does not mean "unknown". It means UNCHANGED:
// "keep the total you were already given". That is deliberate — computing the
// real total is a global COUNT costing 160-320 ms, and paying it on every page
// to re-derive a number that cannot have changed would be worse than the
// sentinel.
//
// THE BUG THIS EXISTS TO KILL
// ---------------------------
// PersonalLibraryScreen.loadMore() merged pages with a plain
// `totalCount: page.totalCount`, so page two overwrote a perfectly good 137
// with -1 and the UI rendered "7 / -1". The count was never wrong on the
// server; the accumulator threw it away.
//
// WHY IT LIVES HERE AND NOT IN A COMPONENT
// ----------------------------------------
// A transport sentinel that reaches presentation is a bug waiting to be
// reprinted in the next component that renders a count. Nothing outside this
// module is allowed to know what -1 means: screens merge through
// `mergePagedTotal` and render through `formatPosition`/`formatTotal`, both of
// which only ever emit valid, non-negative numbers.

/** The transport value meaning "unchanged — keep the total you have". */
export const TOTAL_UNCHANGED = -1;

/** True for any value that must not be treated as a real total. */
export function isUnknownTotal(value: number | null | undefined): boolean {
  return value === null || value === undefined
    || !Number.isFinite(value) || value < 0;
}

/**
 * Fold one page's reported total into the accumulated one.
 *
 * A real total always wins, including a legitimately smaller one (the first
 * page of a NEW query). The sentinel — and anything else that is not a usable
 * total — preserves what we already had.
 */
export function mergePagedTotal(
  accumulated: number | null,
  reported: number | null | undefined,
): number | null {
  if (isUnknownTotal(reported)) return accumulated;
  return reported as number;
}

/**
 * The denominator to display, or null when there is nothing honest to show.
 *
 * `loaded` is the fallback: before the first page resolves, or if a defensive
 * path ever hands us a sentinel we never merged, the number of items actually
 * in hand is true even when the server total is not known.
 */
export function displayTotal(
  accumulated: number | null,
  loaded: number,
): number {
  return isUnknownTotal(accumulated) ? Math.max(0, loaded) : (accumulated as number);
}

/**
 * "7 / 137" — or plain "7" when no valid denominator exists.
 *
 * Never "7 / -1", and never "7 / 0" for a list that plainly has items: an
 * absent denominator is dropped entirely rather than printed as a lie.
 * `position` is ZERO-BASED; the returned string is one-based, because that is
 * what a person counting photos means.
 */
export function formatPosition(position: number, total: number | null): string {
  const oneBased = Math.max(1, Math.floor(position) + 1);
  if (isUnknownTotal(total)) return String(oneBased);
  const denominator = total as number;
  // A denominator smaller than the position would be a worse lie than none.
  if (denominator < oneBased) return String(oneBased);
  return `${oneBased} / ${denominator}`;
}

/** A standalone count for a badge/summary. Never negative, never a sentinel. */
export function formatTotal(total: number | null, loaded: number): number {
  return displayTotal(total, loaded);
}
