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

// What this television is FOR. Pairing answered who it is and never changes;
// this is ordinary server-side state beside it, so the owner can move this TV
// between the general NubArca experience and one specific party without another
// PIN and without walking back to the television.
//
// `albumId`/`albumName` are present only for a party assignment.
// `partyAvailable` is false when the party named here has since been revoked or
// switched off — an honest "that party is over" rather than a silent fall back
// to the general experience. No party link id and no token ever cross.
//
// `presentation` is the server's answer to "which surface, right now": the
// party's native slideshow, its game on the canonical web stage, or
// unavailable. A PROJECTION of the party's state — never a game phase, which
// this app does not know and must not learn. Absent on a server that predates
// it (see lib/assignmentView for how that is read). `assignmentKey` is an
// opaque identity of "this TV, this party link" that changes whenever the
// party does; no endpoint accepts it.
export interface TvDisplayAssignment {
  kind: 'general' | 'party';
  albumId: string | null;
  albumName: string | null;
  partyAvailable: boolean;
  presentation?: 'general' | 'slideshow' | 'game' | 'unavailable';
  assignmentKey?: string | null;
}

export interface TvSessionStatus {
  status: 'active';
  expiresAt: string;
  lastSeenAt: string;
  // The paired owner's UI language ("it" | "en") so the TV app localizes in the
  // owner's language. A bare code — never owner identity.
  language: string;
  assignment: TvDisplayAssignment;
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

// Party slideshow timing for THIS album's active party link, or null when party
// mode is off. Arrives with the album items so the TV never calls an owner API
// to learn how the owner configured the wall.
export interface TvPartySlideshow {
  photoSeconds: number;
  maxVideoSeconds: number;
}

export interface TvAlbumItems {
  id: string;
  name: string;
  items: TvAlbumItem[];
  partyEnabled: boolean;
  partyUrl: string | null;
  partyUploadUrl: string | null;
  partySlideshow: TvPartySlideshow | null;
}

export function startTvPairing(signal?: AbortSignal): Promise<TvPairingStarted> {
  return tvPost<TvPairingStarted>('/api/tv/pairing/start', undefined, undefined, signal);
}

export function getTvPairingStatus(
  publicCode: string,
  pairingSecret: string,
  signal?: AbortSignal,
): Promise<TvPairingStatus> {
  // The pairing secret travels in a header, never the URL (matches the web flow).
  return tvGet<TvPairingStatus>(`/api/tv/pairing/${encodeURIComponent(publicCode)}/status`, {
    'X-Tv-Pairing-Secret': pairingSecret,
  }, signal);
}

export function getTvSession(signal?: AbortSignal): Promise<TvSessionStatus> {
  return tvGet<TvSessionStatus>('/api/tv/session', undefined, signal);
}

// The same answer as getTvSession, and the one read that also stamps the
// session's LastSeenAt. The control plane reads briskly and beats rarely: the
// read is free, the beat is a write (lib/assignmentView SESSION_HEARTBEAT_MS).
export function heartbeatTvSession(signal?: AbortSignal): Promise<TvSessionStatus> {
  return tvPost<TvSessionStatus>('/api/tv/session/heartbeat', undefined, undefined, signal);
}

// The television's permission to SHOW the party it is assigned to.
//
// Authenticated by the TV session cookie alone, and it takes no arguments on
// purpose: the party comes from the device's own assignment, resolved
// server-side. There is nothing here for a client to name, which is what makes
// showing somebody else's party impossible rather than merely refused.
//
// The raw grant is returned once and is never persisted — not in AsyncStorage,
// not anywhere. It lives in memory, goes into a URL fragment, and is replaced
// by minting again. `expiresInSeconds` is the lifetime as a server-measured
// duration, which is what renewal is scheduled from (lib/partyDisplayGrant).
export interface TvPartyDisplayGrant {
  grant: string;
  expiresAt: string;
  expiresInSeconds?: number;
}

export function mintPartyDisplayGrant(signal?: AbortSignal): Promise<TvPartyDisplayGrant> {
  return tvPost<TvPartyDisplayGrant>('/api/tv/party-display/grant', undefined, undefined, signal);
}

export function listTvAlbums(): Promise<TvAlbum[]> {
  return tvGet<TvAlbum[]>('/api/tv/albums');
}

export function listTvAlbumItems(albumId: string): Promise<TvAlbumItems> {
  return tvGet<TvAlbumItems>(`/api/tv/albums/${encodeURIComponent(albumId)}/items`);
}

// A guest's written greeting, projected to the TV. A SEPARATE feed from the
// media carousel — TvAlbumItem stays `image | video`, so this type is additive
// and an older APK that never calls listTvPartyMessages keeps working exactly
// as it did.
//
// `text` is PLAIN TEXT the server has already normalised to one line: no
// markup, no Markdown, no interpreted URIs, and nothing to render but a string.
// `displayName` is null when the guest signed nothing. No moderation state
// reaches here — only messages the party may show are sent at all.
export interface TvPartyMessage {
  id: string;
  displayName: string | null;
  text: string;
  createdAt: string;
  isHero: boolean;
  heroPromotedAt: string | null;
}

export interface TvPartyMessages {
  messages: TvPartyMessage[];
}

// The live message feed for the paired album's CURRENT party. Recomputed
// server-side on every call, so hiding a message, revoking the party or turning
// the album off TV empties or removes it within one poll — there is no local
// cache that could keep a withdrawn greeting on screen.
export function listTvPartyMessages(albumId: string): Promise<TvPartyMessages> {
  return tvGet<TvPartyMessages>(
    `/api/tv/albums/${encodeURIComponent(albumId)}/party-messages`,
  );
}

export interface TvPartyChallenge {
  id: string; title: string; body: string; kind: 'dare' | 'penalty' | 'guess' | 'custom';
  mediaUrl: string | null;
}
export interface TvPartyPlayback {
  mode: 'media' | 'challenge_hold';
  activeChallenge: TvPartyChallenge | null;
  nextChallengeAt: string | null;
  completedCount: number;
}
export function getTvPartyPlayback(albumId: string): Promise<TvPartyPlayback> {
  return tvGet<TvPartyPlayback>(`/api/tv/albums/${encodeURIComponent(albumId)}/party-playback`);
}
export function advanceTvPartyBoundary(albumId: string): Promise<TvPartyPlayback> {
  return tvPost<TvPartyPlayback>(`/api/tv/albums/${encodeURIComponent(albumId)}/party-playback/boundary`);
}
export function completeTvPartyChallenge(albumId: string): Promise<TvPartyPlayback> {
  return tvPost<TvPartyPlayback>(`/api/tv/albums/${encodeURIComponent(albumId)}/party-playback/next`);
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
