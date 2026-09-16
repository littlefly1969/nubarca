import { describe, expect, it } from 'vitest';
import type { PartySummary } from '@nubarca/api-client';
import { partyStatusLabelKey, sortParties } from './partyModel';
import { PARTY_STATUSES } from './workspace/partyWorkspaceModel';

// The party's information architecture, testable without rendering anything.

const summary = (over: Partial<PartySummary>): PartySummary => ({
  id: 'p1', title: 'Festa', status: 'draft',
  eventStartsAt: null, liveStartedAt: null, liveEndedAt: null,
  updatedAt: '2027-01-01T00:00:00Z', mainAlbumId: null, mainAlbumName: null,
  ...over,
});

describe('party status', () => {
  it('every status has a product label, and an unknown one never leaks raw', () => {
    for (const status of PARTY_STATUSES) {
      expect(partyStatusLabelKey(status)).toBe(`party.status.${status}`);
    }
    // A wire value the client does not know must still produce a message key,
    // never the raw string on a screen.
    expect(partyStatusLabelKey('something-new')).toMatch(/^party\.status\./);
  });
});

describe('ordering', () => {
  it('puts what is happening first and what is over last', () => {
    const order = sortParties([
      summary({ id: 'ended', status: 'ended' }),
      summary({ id: 'draft', status: 'draft' }),
      summary({ id: 'live', status: 'live' }),
      summary({ id: 'published', status: 'published' }),
    ]).map((p) => p.id);

    expect(order).toEqual(['live', 'published', 'draft', 'ended']);
  });

  it('is deterministic when two parties share a status and a date', () => {
    // Without a stable tie-break the list would reshuffle between two reads of
    // the same data, which is exactly what a client-side re-sort must not do.
    const parties = [
      summary({ id: 'b', eventStartsAt: '2027-06-12T18:00:00Z' }),
      summary({ id: 'a', eventStartsAt: '2027-06-12T18:00:00Z' }),
    ];
    expect(sortParties(parties).map((p) => p.id)).toEqual(['a', 'b']);
    expect(sortParties([...parties].reverse()).map((p) => p.id)).toEqual(['a', 'b']);
  });

  it('falls back to the last update for a party with no date', () => {
    const order = sortParties([
      summary({ id: 'later', eventStartsAt: null, updatedAt: '2027-02-01T00:00:00Z' }),
      summary({ id: 'earlier', eventStartsAt: null, updatedAt: '2027-01-01T00:00:00Z' }),
    ]).map((p) => p.id);

    expect(order).toEqual(['earlier', 'later']);
  });
});
