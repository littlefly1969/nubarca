// Unified album-card model: owned and shared albums normalized into ONE
// presentation shape, the way the Albums tab renders them. Normalization is
// PRESENTATION ONLY — the underlying APIs and their authority stay separate
// (owned: /api/albums; shared: /api/shared-albums with grant-resolved access).
//
// Pure + node --test-able so a card can never render "shared" as "mine".

import type { AlbumSummary } from '../api/albums.ts';
import type { SharedAlbumSummary } from '../api/sharedAlbums.ts';

export type AlbumCardOrigin = 'owned' | 'shared';
export type AlbumFilter = 'all' | 'mine' | 'shared';

export interface UnifiedAlbumCard {
  // Namespaced key for FlatList across two id spaces.
  key: string;
  origin: AlbumCardOrigin;
  albumId: string;
  name: string;
  description: string | null;
  itemCount: number;
  photoCount: number;
  videoCount: number;
  // Shared-only presentation fields. Empty string when owned.
  ownerDisplayName: string;
  role: 'viewer' | 'contributor' | 'editor' | '';
}

export function cardFromOwned(album: AlbumSummary): UnifiedAlbumCard {
  return {
    key: `owned:${album.id}`,
    origin: 'owned',
    albumId: album.id,
    name: album.name,
    description: album.description,
    itemCount: album.itemCount,
    photoCount: album.photoCount,
    videoCount: album.videoCount,
    ownerDisplayName: '',
    role: '',
  };
}

// The shared summary carries NO per-kind counts by contract; the cover mosaic
// is bounded and deriving numbers from it would lie. A shared card shows ONE
// honest total.
export function cardFromShared(album: SharedAlbumSummary): UnifiedAlbumCard {
  return {
    key: `shared:${album.albumId}`,
    origin: 'shared',
    albumId: album.albumId,
    name: album.name,
    description: album.description,
    itemCount: album.itemCount,
    photoCount: 0,
    videoCount: 0,
    ownerDisplayName: album.ownerDisplayName,
    role: album.role,
  };
}

export function buildUnifiedCards(
  owned: AlbumSummary[],
  shared: SharedAlbumSummary[],
): UnifiedAlbumCard[] {
  // Shared first (the fresher social context), owned in the server's order.
  return [...shared.map(cardFromShared), ...owned.map(cardFromOwned)];
}

export function filterCards(
  cards: UnifiedAlbumCard[],
  filter: AlbumFilter,
): UnifiedAlbumCard[] {
  if (filter === 'all') return cards;
  if (filter === 'mine') return cards.filter((c) => c.origin === 'owned');
  return cards.filter((c) => c.origin === 'shared');
}
