// Mobile TRANSPORT for the People catalogue, as the media filter needs it.
//
// READ-ONLY on purpose (§13-§16). This module can list people and nothing
// else: no create, no rename, no delete, no face assignment, no merge or
// split, no suggestion review. Face management is a separate future slice, and
// keeping its verbs out of the filter's transport is what stops the picker
// growing management affordances by accident.
//
// The response is narrowed to PersonSummary at the boundary, so the broad
// owner-private Person record never reaches the picker at all — its type
// simply has no room for a face box, a cluster or a confidence.

import { apiGet } from './client.ts';
import type { PersonSummary } from '@nubarca/contracts';
import { PEOPLE_LIST_PATH, comparePersonSummaries, toPersonSummary } from '@nubarca/contracts';

export type { PersonSummary } from '@nubarca/contracts';

/** The owner-private record as it arrives. Narrowed immediately below. */
interface PersonWireItem {
  personId: string;
  name: string | null;
  faceCount: number;
  representative: { faceId: string } | null;
}

/**
 * The people this owner may filter by, in the picker's stable order.
 *
 * Sorting here rather than in the component means the order cannot drift
 * between the two clients, and cannot depend on the server's insertion order.
 */
export async function listPeopleForFilter(signal?: AbortSignal): Promise<PersonSummary[]> {
  const people = await apiGet<PersonWireItem[]>(PEOPLE_LIST_PATH, signal);
  return people.map(toPersonSummary).sort(comparePersonSummaries);
}
