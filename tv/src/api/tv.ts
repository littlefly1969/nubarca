// TV-only endpoints. Mirrors the backend TV DTOs. NONE of the normal owner APIs
// are referenced here — the TV app is limited to /api/tv/* by design and by the
// assertTvPath guard in client.ts.

import { tvDelete, tvGet, tvPost } from './client';

export interface TvPairingStarted {
  publicCode: string;
  pairingSecret: string;
  approvalUrl: string;
  expiresAt: string;
}

export interface TvPairingStatus {
  status: 'pending' | 'approved' | 'paired' | 'expired';
  expiresAt: string;
}

export interface TvSessionStatus {
  status: 'active';
  expiresAt: string;
  lastSeenAt: string;
  // The paired owner's UI language ("it" | "en") so the TV app localizes in the
  // owner's language. A bare code — never owner identity.
  language: string;
}

export interface TvAlbum {
  id: string;
  name: string;
  itemCount: number;
  coverThumbnailUrl: string | null;
  // Public party mode: partyUrl is a RELATIVE landing URL ("/party/{token}")
  // rendered as a QR; null/false when off. Never a token hash. partyUploadUrl is
  // the separate upload-QR landing when guest upload is on.
  partyEnabled: boolean;
  partyUrl: string | null;
  partyUploadUrl: string | null;
}

export interface TvAlbumItem {
  id: string;
  name: string;
  mediaType: 'image' | 'video';
  // Display (rotation-aware for video) pixel dimensions, so the grid lays out
  // proportional tiles from the DTO alone — never by loading a thumbnail/poster.
  // Null when the source blob has not been probed.
  width: number | null;
  height: number | null;
  thumbnailUrl: string;
  previewUrl: string;
  posterUrl: string | null;
  videoUrl: string | null;
}

export interface TvAlbumItems {
  id: string;
  name: string;
  items: TvAlbumItem[];
  partyEnabled: boolean;
  partyUrl: string | null;
  partyUploadUrl: string | null;
}

export function startTvPairing(): Promise<TvPairingStarted> {
  return tvPost<TvPairingStarted>('/api/tv/pairing/start');
}

export function getTvPairingStatus(
  publicCode: string,
  pairingSecret: string,
): Promise<TvPairingStatus> {
  // The pairing secret travels in a header, never the URL (matches the web flow).
  return tvGet<TvPairingStatus>(`/api/tv/pairing/${encodeURIComponent(publicCode)}/status`, {
    'X-Tv-Pairing-Secret': pairingSecret,
  });
}

export function getTvSession(): Promise<TvSessionStatus> {
  return tvGet<TvSessionStatus>('/api/tv/session');
}

export function listTvAlbums(): Promise<TvAlbum[]> {
  return tvGet<TvAlbum[]>('/api/tv/albums');
}

export function listTvAlbumItems(albumId: string): Promise<TvAlbumItems> {
  return tvGet<TvAlbumItems>(`/api/tv/albums/${encodeURIComponent(albumId)}/items`);
}

// Active party face filter for the paired album. A guest's face search reaches
// the TV only after the guest EXPLICITLY presses "Show these photos on TV" on
// the public party page; the TV polls this and filters the grid/slideshow to
// the matching subset. activationVersion is the server-assigned activation
// order; activatedAt is the server update time; faceThumbnailUrl is a relative
// /api/tv URL for the small detected-face indicator crop (null when none). No
// names/scores/face identity data — just the matching media subset.
export interface TvFaceSearchActive {
  active: boolean;
  searchId: string | null;
  activationVersion: number | null;
  activatedAt: string | null;
  faceThumbnailUrl: string | null;
  items: TvAlbumItem[];
}

export function getTvActiveFaceSearch(albumId: string): Promise<TvFaceSearchActive> {
  return tvGet<TvFaceSearchActive>(
    `/api/tv/albums/${encodeURIComponent(albumId)}/face-search/active`,
  );
}

// TV exits face-filter mode (BACK / "show all photos"). With searchId it
// DELETES that search (and its stored face crop) — row-scoped, so a stale call
// for an older search never removes a newer active filter; without searchId it
// only deactivates whatever is active. Idempotent either way.
export function clearTvActiveFaceSearch(albumId: string, searchId?: string): Promise<void> {
  const suffix = searchId ? `?searchId=${encodeURIComponent(searchId)}` : '';
  return tvDelete<void>(
    `/api/tv/albums/${encodeURIComponent(albumId)}/face-search/active${suffix}`,
  );
}
