import type { MessageKey } from '../i18n';
import type { Party, PartyStatus, PartySummary } from '@nubarca/api-client';

// How the product TALKS about a party, and the small amount of arithmetic its
// surfaces need. Pure data and pure functions, so the information architecture
// is testable without rendering anything — and so a raw `draft` never reaches a
// screen because somebody interpolated the wire value.

const STATUS_LABELS: Record<PartyStatus, MessageKey> = {
  draft: 'party.status.draft',
  published: 'party.status.published',
  live: 'party.status.live',
  ended: 'party.status.ended',
};

export function partyStatusLabelKey(status: string): MessageKey {
  return STATUS_LABELS[status as PartyStatus] ?? 'party.status.draft';
}

/**
 * The four steps of the evening, in order.
 *
 * A DESCRIPTION of where the party is, never a control: nothing here can be
 * clicked to change the state. The lifecycle moves through its own actions, one
 * per transition, so a timeline that also set the status would be a second way
 * to do the same thing with none of the version checking.
 */
export const PARTY_TIMELINE: readonly PartyStatus[] = ['draft', 'published', 'live', 'ended'];

export type TimelineStepState = 'done' | 'current' | 'upcoming';

export function timelineStepState(step: PartyStatus, status: string): TimelineStepState {
  const at = PARTY_TIMELINE.indexOf(status as PartyStatus);
  const here = PARTY_TIMELINE.indexOf(step);
  if (at < 0 || here < 0) return 'upcoming';
  if (here < at) return 'done';
  return here === at ? 'current' : 'upcoming';
}

/**
 * The ONE thing the host can do to the lifecycle from here, or null.
 *
 * One primary action per state, deliberately. `draft` has none: a party is
 * published by opening it to guests, which is the capability's own decision and
 * already performs the transition server-side — offering a second "publish"
 * button beside it would be two ways to reach one state. `ended` has none
 * either, because there is no re-open transition and a button that answered 400
 * would be worse than no button.
 */
export function partyPrimaryAction(
  status: string,
): { action: 'start-live' | 'end-live'; labelKey: MessageKey } | null {
  if (status === 'published') return { action: 'start-live', labelKey: 'party.action.startLive' };
  if (status === 'live') return { action: 'end-live', labelKey: 'party.action.endLive' };
  return null;
}

/** The party's `main` media source, or null while the host has not linked one. */
export function mainMediaSource(party: Party): { albumId: string; albumName: string } | null {
  const main = party.mediaSources.find((s) => s.role === 'main');
  return main ? { albumId: main.albumId, albumName: main.albumName } : null;
}

// Live first — it is happening now — then what is being prepared or announced,
// then what is over. It mirrors the server's own ordering exactly: the list
// arrives sorted and this is what a client-side re-sort has to agree with, so
// the two can never disagree about which party is at the top.
const STATUS_RANK: Record<PartyStatus, number> = { live: 0, published: 1, draft: 2, ended: 3 };

export function partySortKey(party: PartySummary): [number, string, string] {
  return [
    STATUS_RANK[party.status] ?? 3,
    party.eventStartsAt ?? party.updatedAt,
    party.id,
  ];
}

export function sortParties(parties: readonly PartySummary[]): PartySummary[] {
  return [...parties].sort((a, b) => {
    const [ra, da, ia] = partySortKey(a);
    const [rb, db, ib] = partySortKey(b);
    return ra - rb || da.localeCompare(db) || ia.localeCompare(ib);
  });
}
