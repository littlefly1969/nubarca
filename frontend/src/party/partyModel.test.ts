import { describe, expect, it } from 'vitest';
import type { PartySummary } from '@nubarca/api-client';
import {
  PARTY_TIMELINE,
  partyPrimaryAction,
  partyStatusLabelKey,
  sortParties,
  timelineStepState,
} from './partyModel';

// The party's information architecture, testable without rendering anything.

const summary = (over: Partial<PartySummary>): PartySummary => ({
  id: 'p1', title: 'Festa', status: 'draft',
  eventStartsAt: null, liveStartedAt: null, liveEndedAt: null,
  updatedAt: '2027-01-01T00:00:00Z', mainAlbumId: null, mainAlbumName: null,
  ...over,
});

describe('party status', () => {
  it('every status has a product label, and an unknown one never leaks raw', () => {
    for (const status of PARTY_TIMELINE) {
      expect(partyStatusLabelKey(status)).toBe(`party.status.${status}`);
    }
    // A wire value the client does not know must still produce a message key,
    // never the raw string on a screen.
    expect(partyStatusLabelKey('something-new')).toMatch(/^party\.status\./);
  });
});

describe('the lifecycle timeline', () => {
  it('describes where the evening is, in order', () => {
    expect(PARTY_TIMELINE).toEqual(['draft', 'published', 'live', 'ended']);

    expect(timelineStepState('draft', 'live')).toBe('done');
    expect(timelineStepState('published', 'live')).toBe('done');
    expect(timelineStepState('live', 'live')).toBe('current');
    expect(timelineStepState('ended', 'live')).toBe('upcoming');
  });

  it('offers exactly one action per state, and none where there is no move', () => {
    // Draft has none: a party is published by opening it to guests, and a
    // second button reaching the same state would be two ways to do one thing.
    expect(partyPrimaryAction('draft')).toBeNull();
    expect(partyPrimaryAction('published')?.action).toBe('start-live');
    expect(partyPrimaryAction('live')?.action).toBe('end-live');
    // Ended has none: there is no re-open transition, and a button that
    // answered 400 would be worse than no button.
    expect(partyPrimaryAction('ended')).toBeNull();
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
