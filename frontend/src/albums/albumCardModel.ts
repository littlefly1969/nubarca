import type { AlbumCoverItem, AlbumRole, AlbumSummary, SharedAlbumSummary } from '@nubarca/api-client';

// ONE presentation model for an album card, whoever owns the album.
//
// This is a PRESENTATION boundary and nothing more. `AlbumSummary` and
// `SharedAlbumSummary` stay two different shapes in the API layer, because they
// come from two different authorities: one is the caller's own album, the other
// is a revocable grant on somebody else's. Normalising them HERE is what lets
// one grid render both without the API layer ever pretending that "my album" and
// "an album shared with me" are the same object.
//
// Two consequences are deliberate. `ownerKind` is not derived from anything
// optional being absent — it is stated by whichever constructor built the card,
// so a shared album can never drift into looking owned because a field was
// null. And `recentAt` carries `recentKind` beside it: an owned album's most
// recent fact is when it was UPDATED, a shared one's is when it was SHARED with
// you. Sorting them together is fine; labelling them as the same thing would be
// a lie, so the card says which one it is showing.

export type AlbumOwnerKind = 'self' | 'shared';

// The album collection the user is looking at. `all` is the default: the point
// of the slice is that an album is an album.
export type AlbumCollectionScope = 'all' | 'mine' | 'shared';

export const ALBUM_COLLECTION_SCOPES: readonly AlbumCollectionScope[] = ['all', 'mine', 'shared'] as const;

export type AlbumSortKey = 'recent' | 'name' | 'count';

export interface AlbumCardModel {
  // Stable React key. Prefixed by ownership because an owned album and a shared
  // one are addressed through different routes, and an id alone would collide
  // if a user ever held both views of one album.
  key: string;
  id: string;
  // Where the card navigates. Owner and recipient resolve to DIFFERENT routes
  // backed by different authority — the unified experience stops at the door.
  href: string;
  name: string;
  description: string | null;
  coverItems: AlbumCoverItem[];
  itemCount: number;
  // `null` means "this source does not carry that count", never zero.
  photoCount: number | null;
  videoCount: number | null;
  excludedCount: number | null;
  ownerKind: AlbumOwnerKind;
  // Present only for a shared album: whose album this is.
  ownerDisplayName: string | null;
  // Present only for a shared album: what this membership may do.
  role: AlbumRole | null;
  showOnTv: boolean;
  // Presentation-normalised recency + what it actually means.
  recentAt: string;
  recentKind: 'updated' | 'shared';
}

export function ownedAlbumCard(album: AlbumSummary): AlbumCardModel {
  return {
    key: `mine:${album.id}`,
    id: album.id,
    href: `/albums/${album.id}`,
    name: album.name,
    description: album.description,
    coverItems: album.coverItems,
    // The per-kind counts are the ACTIVE ones; `itemCount` is raw membership
    // and includes excluded members, so the card counts what it can show.
    itemCount: album.photoCount + album.videoCount,
    photoCount: album.photoCount,
    videoCount: album.videoCount,
    excludedCount: album.excludedCount,
    ownerKind: 'self',
    ownerDisplayName: null,
    role: null,
    showOnTv: album.showOnTv,
    recentAt: album.updatedAt,
    recentKind: 'updated',
  };
}

export function sharedAlbumCard(album: SharedAlbumSummary): AlbumCardModel {
  return {
    key: `shared:${album.albumId}`,
    id: album.albumId,
    href: `/shared-albums/${album.albumId}`,
    name: album.name,
    description: album.description,
    coverItems: album.coverItems,
    itemCount: album.itemCount,
    // The recipient's summary carries no per-kind split, and inventing one from
    // the cover mosaic would be a number the server never said.
    photoCount: null,
    videoCount: null,
    excludedCount: null,
    ownerKind: 'shared',
    ownerDisplayName: album.ownerDisplayName,
    role: album.role,
    // Show-on-TV is an owner publication setting. A recipient does not have one,
    // and a recipient's card must never suggest they do.
    showOnTv: false,
    recentAt: album.sharedAt,
    recentKind: 'shared',
  };
}

export interface AlbumCardSelection {
  cards: readonly AlbumCardModel[];
  scope: AlbumCollectionScope;
  query: string;
  sort: AlbumSortKey;
}

/**
 * Scope filter + name search + sort, as one pure function.
 *
 * Name search runs across BOTH collections, because the user searching for
 * "Wedding" does not know or care whose album it is — that is the whole point of
 * one destination.
 */
export function selectAlbumCards({ cards, scope, query, sort }: AlbumCardSelection): AlbumCardModel[] {
  const needle = query.trim().toLowerCase();
  const visible = cards.filter((card) => {
    if (scope === 'mine' && card.ownerKind !== 'self') return false;
    if (scope === 'shared' && card.ownerKind !== 'shared') return false;
    if (needle.length > 0 && !card.name.toLowerCase().includes(needle)) return false;
    return true;
  });

  const sorted = [...visible];
  sorted.sort((a, b) => {
    if (sort === 'name') return a.name.localeCompare(b.name);
    if (sort === 'count') return b.itemCount - a.itemCount;
    // Recency across two different facts. Ties break by name so the order is
    // total — an owned album and a shared one can carry the same instant.
    const byRecent = b.recentAt.localeCompare(a.recentAt);
    return byRecent !== 0 ? byRecent : a.name.localeCompare(b.name);
  });
  return sorted;
}

export function countByOwnerKind(cards: readonly AlbumCardModel[]): { all: number; mine: number; shared: number } {
  let mine = 0;
  for (const card of cards) {
    if (card.ownerKind === 'self') mine += 1;
  }
  return { all: cards.length, mine, shared: cards.length - mine };
}

// Whether `scope` is a value this page understands, for reading it back off the
// URL. An unknown one degrades to "all" rather than an empty grid.
export function parseAlbumScope(value: string | null): AlbumCollectionScope {
  return value === 'mine' || value === 'shared' ? value : 'all';
}
