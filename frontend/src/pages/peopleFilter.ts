import type { Person } from '@nubarca/api-client';

// Narrowing an already-loaded list of people by name.
//
// The People tab loads every person in one call, so filtering is a decision
// about what to DRAW, not a query: no endpoint, no debounce, no request per
// keystroke, and nothing to get out of sync with the server.
//
// A person without a name never matches a non-empty query. They are real rows —
// an unnamed cluster the owner has not decided about yet — and matching them on
// an empty string would make every search return them.
export function filterPeopleByName(people: readonly Person[], query: string): Person[] {
  const needle = query.trim().toLowerCase();
  if (needle.length === 0) return [...people];
  return people.filter((p) => (p.name ?? '').toLowerCase().includes(needle));
}
