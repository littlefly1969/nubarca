// Unified album-card model tests: owned and shared normalize into ONE
// presentation shape without ever confusing the two authorities.

import assert from 'node:assert/strict';
import { test } from 'node:test';
import {
  cardFromOwned,
  cardFromShared,
  buildUnifiedCards,
  filterCards,
  type UnifiedAlbumCard,
} from './albumCardModel.ts';
import type { AlbumSummary } from '../api/albums.ts';
import type { SharedAlbumSummary } from '../api/sharedAlbums.ts';

const ownedAlbum: AlbumSummary = {
  id: 'own-1',
  name: 'Vacanze',
  description: null,
  itemCount: 12,
  showOnTv: false,
  createdAt: '',
  updatedAt: '',
  photoCount: 10,
  videoCount: 2,
  excludedCount: 0,
  coverItems: [],
};

const sharedAlbum: SharedAlbumSummary = {
  albumId: 'shr-1',
  name: 'Festa comune',
  description: 'album di prova',
  ownerDisplayName: 'Maria Rossi',
  role: 'contributor',
  allowOriginalDownload: true,
  itemCount: 7,
  sharedAt: '',
  coverItems: [
    { fileItemId: 'c1', kind: 'image', thumbnailUrl: '/api/shared-albums/shr-1/media/c1/thumbnail' },
    { fileItemId: 'c2', kind: 'video', thumbnailUrl: '/api/shared-albums/shr-1/media/c2/poster' },
  ],
};

test('an OWNED card keeps its identity, counts, and empty owner fields', () => {
  const c = cardFromOwned(ownedAlbum);
  assert.equal(c.key, 'owned:own-1');
  assert.equal(c.origin, 'owned');
  assert.equal(c.name, 'Vacanze');
  assert.equal(c.itemCount, 12);
  assert.equal(c.photoCount, 10);
  assert.equal(c.videoCount, 2);
  assert.equal(c.ownerDisplayName, '');
  assert.equal(c.role, '');
});

test('a SHARED card carries owner + role and NEVER invents per-kind counts', () => {
  const c = cardFromShared(sharedAlbum);
  assert.equal(c.key, 'shared:shr-1');
  assert.equal(c.origin, 'shared');
  assert.equal(c.ownerDisplayName, 'Maria Rossi');
  assert.equal(c.role, 'contributor');
  assert.equal(c.itemCount, 7);
  // The summary has no per-kind counts by contract — the card must not guess.
  assert.equal(c.photoCount, 0);
  assert.equal(c.videoCount, 0);
});

test('buildUnifiedCards lists shared first and namespaces the keys', () => {
  const cards = buildUnifiedCards([ownedAlbum], [sharedAlbum]);
  assert.deepEqual(
    cards.map((c) => c.origin),
    ['shared', 'owned'],
  );
  assert.ok(new Set(cards.map((c) => c.key)).size === cards.length);
});

test('filters slice the unified list by origin', () => {
  const cards: UnifiedAlbumCard[] = buildUnifiedCards(
    [ownedAlbum],
    [sharedAlbum],
    );
  assert.equal(filterCards(cards, 'all').length, 2);
  assert.deepEqual(
    filterCards(cards, 'mine').map((c) => c.albumId),
    ['own-1'],
  );
  assert.deepEqual(
    filterCards(cards, 'shared').map((c) => c.albumId),
    ['shr-1'],
  );
});
