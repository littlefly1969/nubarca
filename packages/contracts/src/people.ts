// People, as the media FILTER needs them (§13, §14, §16).
//
// This is a READ-ONLY view for choosing who to filter by. It is deliberately
// the narrow half of the People domain: editorial management — creating,
// renaming, merging, splitting, assigning faces, reviewing suggestions — is a
// separate future concern and must not leak in here.
//
// So a PersonSummary knows nothing about clustering, face sessions, merge or
// split candidates, recognition confidence or AI training state. It carries an
// identity, a label, a size and one authorized avatar, and nothing else. That
// boundary is what lets the management slice change a person's name, avatar or
// cluster composition later without touching a single saved filter.
//
// IDENTITY IS THE personId, always. A display name is not identity: it can
// change, it can be null, and two people can share one. Filtering by name
// would silently retarget a saved query the moment somebody corrects a label.

export interface PersonSummary {
  /** Stable identity. The ONLY thing a filter may key on. */
  personId: string;
  /** Display label; null when the person has not been named yet. */
  name: string | null;
  /** How many faces are attached — used for ordering and for "unnamed" hints. */
  faceCount: number;
  /**
   * The face whose crop represents this person, or null when there is none.
   * An id, not a URL, so the client builds an authorized path with its own
   * origin and transport.
   */
  representativeFaceId: string | null;
}

export const PEOPLE_LIST_PATH = '/api/people';

/** The authorized avatar crop for a representative face. */
export function personAvatarPath(faceId: string): string {
  return `/api/people/faces/${encodeURIComponent(faceId)}/preview`;
}

/**
 * Narrow the full owner-private Person record down to what a filter picker may
 * see. Written as a projection so a client cannot accidentally hand the picker
 * the broad record: the picker's type simply has no room for the rest.
 */
export function toPersonSummary(person: {
  personId: string;
  name: string | null;
  faceCount: number;
  representative: { faceId: string } | null;
}): PersonSummary {
  return {
    personId: person.personId,
    name: person.name,
    faceCount: person.faceCount,
    representativeFaceId: person.representative?.faceId ?? null,
  };
}

/**
 * Filter-picker ordering: named people first (alphabetically), then unnamed
 * ones by size. A picker whose order depends on the server's insertion order
 * moves under the user's finger between sessions.
 */
export function comparePersonSummaries(a: PersonSummary, b: PersonSummary): number {
  const aNamed = a.name !== null && a.name.length > 0;
  const bNamed = b.name !== null && b.name.length > 0;
  if (aNamed !== bNamed) return aNamed ? -1 : 1;
  if (aNamed && bNamed) return (a.name ?? '').localeCompare(b.name ?? '');
  return b.faceCount - a.faceCount;
}

/** Case-insensitive display-name search for the picker's search field. */
export function matchesPersonQuery(person: PersonSummary, query: string): boolean {
  const needle = query.trim().toLowerCase();
  if (needle.length === 0) return true;
  return (person.name ?? '').toLowerCase().includes(needle);
}
