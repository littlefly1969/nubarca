import { describe, expect, it } from 'vitest';
import type { AlbumSummary, SharedAlbumSummary } from '@nubarca/api-client';
import {
  countByOwnerKind,
  ownedAlbumCard,
  parseAlbumScope,
  selectAlbumCards,
  sharedAlbumCard,
} from './albumCardModel';

// The presentation boundary between two authorities. What these tests defend:
// one grid can render both collections WITHOUT the model ever losing track of
// which is which, or stating a number the server did not.

function owned(over: Partial<AlbumSummary> = {}): AlbumSummary {
  return {
    id: 'a1', name: 'Alpha', description: 'first', itemCount: 4, showOnTv: false,
    createdAt: '2025-01-01T00:00:00Z', updatedAt: '2025-01-01T00:00:00Z',
    photoCount: 2, videoCount: 1, excludedCount: 1, coverItems: [],
    ...over,
  };
}

function shared(over: Partial<SharedAlbumSummary> = {}): SharedAlbumSummary {
  return {
    albumId: 's1', name: 'Wedding', description: null, ownerDisplayName: 'Marco',
    role: 'viewer', allowOriginalDownload: false, itemCount: 83,
    sharedAt: '2025-03-01T00:00:00Z', coverItems: [],
    ...over,
  };
}

describe('album card model', () => {
  it('keeps ownership explicit rather than inferring it from a missing field', () => {
    expect(ownedAlbumCard(owned()).ownerKind).toBe('self');
    expect(sharedAlbumCard(shared()).ownerKind).toBe('shared');
    // A shared album with an owner display name that happens to be empty is
    // still a shared album.
    expect(sharedAlbumCard(shared({ ownerDisplayName: '' })).ownerKind).toBe('shared');
  });

  it('routes each collection to its own authority', () => {
    expect(ownedAlbumCard(owned()).href).toBe('/albums/a1');
    expect(sharedAlbumCard(shared()).href).toBe('/shared-albums/s1');
  });

  it('counts the ACTIVE members of an owned album, not raw membership', () => {
    // `itemCount` on the owner's summary includes excluded members; the card
    // counts what it can actually show.
    const card = ownedAlbumCard(owned({ itemCount: 9, photoCount: 2, videoCount: 1 }));
    expect(card.itemCount).toBe(3);
    expect(card.excludedCount).toBe(1);
  });

  it('leaves a shared album’s per-kind counts ABSENT rather than zero', () => {
    const card = sharedAlbumCard(shared());
    expect(card.itemCount).toBe(83);
    expect(card.photoCount).toBeNull();
    expect(card.videoCount).toBeNull();
    expect(card.excludedCount).toBeNull();
  });

  it('never gives a recipient an owner publication flag', () => {
    // Show-on-TV is the owner's; a recipient has no such setting and the card
    // must not suggest one.
    expect(sharedAlbumCard(shared()).showOnTv).toBe(false);
    expect(ownedAlbumCard(owned({ showOnTv: true })).showOnTv).toBe(true);
  });

  it('labels WHICH recency it is showing instead of conflating two facts', () => {
    expect(ownedAlbumCard(owned()).recentKind).toBe('updated');
    expect(ownedAlbumCard(owned()).recentAt).toBe('2025-01-01T00:00:00Z');
    expect(sharedAlbumCard(shared()).recentKind).toBe('shared');
    expect(sharedAlbumCard(shared()).recentAt).toBe('2025-03-01T00:00:00Z');
  });

  it('keys the two collections apart', () => {
    // The same album id could in principle reach both lists; the key must not
    // collide.
    expect(ownedAlbumCard(owned({ id: 'x' })).key)
      .not.toBe(sharedAlbumCard(shared({ albumId: 'x' })).key);
  });
});

describe('album card selection', () => {
  const cards = [
    ownedAlbumCard(owned({ id: 'a1', name: 'Alpha', updatedAt: '2025-01-01T00:00:00Z' })),
    ownedAlbumCard(owned({ id: 'a2', name: 'Zulu', updatedAt: '2025-08-01T00:00:00Z', photoCount: 9, videoCount: 0 })),
    sharedAlbumCard(shared({ albumId: 's1', name: 'Wedding', sharedAt: '2025-06-01T00:00:00Z' })),
  ];

  it('filters by collection', () => {
    const names = (scope: 'all' | 'mine' | 'shared') =>
      selectAlbumCards({ cards, scope, query: '', sort: 'name' }).map((c) => c.name);
    expect(names('all')).toEqual(['Alpha', 'Wedding', 'Zulu']);
    expect(names('mine')).toEqual(['Alpha', 'Zulu']);
    expect(names('shared')).toEqual(['Wedding']);
  });

  it('searches by name across both collections', () => {
    const found = selectAlbumCards({ cards, scope: 'all', query: 'WED', sort: 'name' });
    expect(found.map((c) => c.name)).toEqual(['Wedding']);
  });

  it('sorts by recency across two different kinds of recent', () => {
    const order = selectAlbumCards({ cards, scope: 'all', query: '', sort: 'recent' })
      .map((c) => c.name);
    expect(order).toEqual(['Zulu', 'Wedding', 'Alpha']);
  });

  it('sorts by item count', () => {
    const order = selectAlbumCards({ cards, scope: 'all', query: '', sort: 'count' })
      .map((c) => c.name);
    expect(order).toEqual(['Wedding', 'Zulu', 'Alpha']);
  });

  it('breaks a recency tie by name so the order is total', () => {
    const tied = [
      ownedAlbumCard(owned({ id: 'a', name: 'Beta', updatedAt: '2025-01-01T00:00:00Z' })),
      sharedAlbumCard(shared({ albumId: 'b', name: 'Alpha', sharedAt: '2025-01-01T00:00:00Z' })),
    ];
    expect(selectAlbumCards({ cards: tied, scope: 'all', query: '', sort: 'recent' })
      .map((c) => c.name)).toEqual(['Alpha', 'Beta']);
  });

  it('counts each collection', () => {
    expect(countByOwnerKind(cards)).toEqual({ all: 3, mine: 2, shared: 1 });
  });
});

describe('album scope parsing', () => {
  it('reads the collection off the URL and degrades an unknown one to all', () => {
    expect(parseAlbumScope('shared')).toBe('shared');
    expect(parseAlbumScope('mine')).toBe('mine');
    expect(parseAlbumScope(null)).toBe('all');
    expect(parseAlbumScope('everyone-elses')).toBe('all');
  });
});
