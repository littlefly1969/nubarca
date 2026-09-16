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
