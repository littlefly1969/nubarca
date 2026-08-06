import { api, ApiError } from './client';

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
  // The paired owner's UI language ("it" | "en") so the TV surface can localize
  // in the owner's language. A bare code — never owner identity.
  language: string;
}

export function startTvPairing(signal?: AbortSignal): Promise<TvPairingStarted> {
  return api<TvPairingStarted>('/api/tv/pairing/start', { method: 'POST', signal });
}

export function getTvPairingStatus(
  publicCode: string,
  pairingSecret: string,
  signal?: AbortSignal,
): Promise<TvPairingStatus> {
  return api<TvPairingStatus>(`/api/tv/pairing/${encodeURIComponent(publicCode)}/status`, {
    headers: { 'X-Tv-Pairing-Secret': pairingSecret },
    signal,
  });
}

// Atomic approval. An owner who does not yet have a Personal Area PIN MUST
// supply personalPin + personalPinConfirmation: the server commits the PIN and
// the approval together (400 pin_required / invalid_pin / pin_mismatch leave
// the pairing pending). An owner with an existing PIN omits them — supplied
// values are ignored server-side, an existing PIN is never replaced here.
export function approveTvPairing(
  publicCode: string,
  pairingSecret: string,
  personalPin?: string,
  personalPinConfirmation?: string,
): Promise<TvPairingStatus> {
  return api<TvPairingStatus>(`/api/tv/pairing/${encodeURIComponent(publicCode)}/approve`, {
    method: 'POST',
    json: { pairingSecret, personalPin, personalPinConfirmation },
  });
}

export function getTvSession(signal?: AbortSignal): Promise<TvSessionStatus> {
  return api<TvSessionStatus>('/api/tv/session', { signal });
}

export function heartbeatTvSession(signal?: AbortSignal): Promise<TvSessionStatus> {
  return api<TvSessionStatus>('/api/tv/session/heartbeat', { method: 'POST', signal });
}

export function revokeTvSession(): Promise<void> {
  return api<void>('/api/tv/session', { method: 'DELETE' });
}

// --- TV media browsing (ShowOnTv allowlist) ---

export interface TvAlbum {
  id: string;
  name: string;
  itemCount: number;
  coverThumbnailUrl: string | null;
  // Present when the owner enabled public party mode on this ShowOnTv album.
  // partyUrl is a RELATIVE public landing URL ("/party/{token}") the TV renders
  // as a QR; null/false when party mode is off. Never a token hash.
  partyEnabled: boolean;
  partyUrl: string | null;
  // Separate public UPLOAD landing URL ("/party/{uploadToken}/upload"), present
  // only when party AND guest upload are enabled. Rendered as a second QR.
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
  previewStripUrl: string | null;
}

export interface TvAlbumItems {
  id: string;
  name: string;
  items: TvAlbumItem[];
  partyEnabled: boolean;
  partyUrl: string | null;
  partyUploadUrl: string | null;
}

export function listTvAlbums(signal?: AbortSignal): Promise<TvAlbum[]> {
  return api<TvAlbum[]>('/api/tv/albums', { signal });
}

export function listTvAlbumItems(albumId: string, signal?: AbortSignal): Promise<TvAlbumItems> {
  return api<TvAlbumItems>(`/api/tv/albums/${encodeURIComponent(albumId)}/items`, { signal });
}

// --- TV active party face filter ---
// A guest's face search reaches the TV only after an EXPLICIT "show these
// photos on TV" activation on the party page. The TV polls this; when Active,
// it filters the grid/slideshow to the matching subset (same /api/tv media
// URLs, no names/scores/face identity data). activationVersion is the
// server-assigned activation order; activatedAt is the server update time;
// faceThumbnailUrl (relative /api/tv URL) serves the small detected-face
// indicator crop, null when none was stored. Active is false with empty items
// when nothing is activated.
export interface TvFaceSearchActive {
  active: boolean;
  searchId: string | null;
  activationVersion: number | null;
  activatedAt: string | null;
  faceThumbnailUrl: string | null;
  items: TvAlbumItem[];
}

export function getTvActiveFaceSearch(
  albumId: string,
  signal?: AbortSignal,
): Promise<TvFaceSearchActive> {
  return api<TvFaceSearchActive>(
    `/api/tv/albums/${encodeURIComponent(albumId)}/face-search/active`,
    { signal },
  );
}

// TV exits face-filter mode. With searchId it DELETES that search (and its
// stored face crop) — row-scoped, so a stale call for an older search never
// removes a newer active filter; without searchId it only deactivates whatever
// is active. Idempotent either way.
export function clearTvActiveFaceSearch(
  albumId: string,
  searchId?: string,
  signal?: AbortSignal,
): Promise<void> {
  const suffix = searchId ? `?searchId=${encodeURIComponent(searchId)}` : '';
  return api<void>(
    `/api/tv/albums/${encodeURIComponent(albumId)}/face-search/active${suffix}`,
    { method: 'DELETE', signal },
  );
}

// --- TV Personal Area (limited TV session cookie) ---
// The Personal Area is a second local authorization step on top of the paired
// TV session: unlocking with the owner's 6-digit PIN returns an OPAQUE unlock
// grant that must accompany every personal call in the X-Tv-Personal-Unlock
// header. The grant is server-authoritative (bound to this session + owner,
// revocable, bounded lifetime) and must be kept ONLY in application memory —
// never localStorage/sessionStorage/cookies/URLs.

export const TV_PERSONAL_UNLOCK_HEADER = 'X-Tv-Personal-Unlock';

export interface TvPersonalUnlock {
  unlockToken: string;
  expiresAt: string;
}

export interface TvPersonalStatus {
  pinConfigured: boolean;
  unlocked: boolean;
}

export interface TvPersonalHome {
  displayName: string;
  galleryAvailable: boolean;
}

// 403 = wrong PIN (generic — deliberately indistinguishable from "no PIN
// configured"); 429 = progressive cooldown; 401 = TV session invalid/revoked.
export function unlockTvPersonal(pin: string, signal?: AbortSignal): Promise<TvPersonalUnlock> {
  return api<TvPersonalUnlock>('/api/tv/personal/unlock', { method: 'POST', json: { pin }, signal });
}

// Idempotent; revokes every live grant of this TV session server-side.
export function lockTvPersonal(signal?: AbortSignal): Promise<void> {
  return api<void>('/api/tv/personal/lock', { method: 'POST', signal });
}

export function getTvPersonalStatus(signal?: AbortSignal): Promise<TvPersonalStatus> {
  return api<TvPersonalStatus>('/api/tv/personal/status', { signal });
}

export function getTvPersonalHome(
  unlockToken: string,
  signal?: AbortSignal,
): Promise<TvPersonalHome> {
  return api<TvPersonalHome>('/api/tv/personal/home', {
    headers: { [TV_PERSONAL_UNLOCK_HEADER]: unlockToken },
    signal,
  });
}

// --- TV Personal Gallery (limited TV session cookie + unlock grant) ---
// Grant-gated projection of the owner image gallery: the same query semantics
// as the authenticated /api/images (shared backend parser), TV-safe DTOs, and
// derived-media URLs that themselves require the session cookie AND the grant
// header on every request. The grant is passed explicitly per call — it lives
// only in component state, never in storage or URLs.

export interface TvPersonalGalleryItem {
  id: string;
  name: string;
  mediaType: 'image';
  width: number | null;
  height: number | null;
  createdAt: string;
  thumbnailUrl: string;
  previewUrl: string;
  favorite: boolean;
  occurrenceCount: number;
}

export interface TvPersonalGalleryPage {
  items: TvPersonalGalleryItem[];
  nextCursor: string | null;
  hasMore: boolean;
  // Server-authoritative count of items matching the CURRENT filtered query
  // (not the number loaded so far). Stable across the pages of one query; the
  // viewer counter denominator. Present on every page with the same value. On a
  // semantic query this is the REDUCED semantic result total (<= Top-K).
  totalCount: number;
  // Slice 100: present when a semantic query is applied. semanticStatus:
  // 'ok' | 'unavailable' (engine/profile not ready) | 'indexing' (many filtered
  // items not yet embedded). Absent/false → a normal filtered page.
  semanticActive?: boolean;
  semanticTopK?: number;
  semanticStatus?: 'ok' | 'unavailable' | 'indexing' | null;
}

export type TvGallerySortField = 'created' | 'name' | 'size' | 'datetaken';
export type TvGallerySortDirection = 'asc' | 'desc';

export interface TvPersonalGalleryQuery {
  q?: string;
  sort?: TvGallerySortField;
  direction?: TvGallerySortDirection;
  limit?: number;
  cursor?: string;
  favorite?: boolean;
  minRating?: number;
  hasGps?: boolean;
  dateTakenFrom?: string; // ISO-8601 UTC
  dateTakenTo?: string;
  collapseDuplicates?: boolean;
  includePeople?: string[];
  excludePeople?: string[];
  includePeopleMode?: 'all' | 'any';
  // Slice 100: optional VISUAL semantic residual + server-clamped Top-K. When set
  // the gallery is physical-filter-first + semantic-ranked.
  semanticQuery?: string;
  semanticTopK?: number;
}

// --- Natural-language command interpretation (LOCAL; no cloud) ---
// The client sends the typed command + the CURRENT filter state; the server
// returns a validated PROPOSED draft the user must explicitly apply. The command
// is never persisted, never in a URL, and never in browser storage.

export interface TvCurrentFilterState {
  peopleInclude: string[];
  peopleExclude: string[];
  peopleMatch: 'all' | 'any';
  favorite: boolean | null;
  minRating: number | null;
  hasGps: boolean | null;
  dateTakenFrom: string | null;
  dateTakenTo: string | null;
  collapseDuplicates: boolean | null;
  sort: string | null;
  sortDirection: string | null;
  metadataSearch: string | null;
  semanticQuery: string | null;
}

export interface TvInterpretDraft {
  version: number;
  operation: 'replace' | 'refine' | 'clear';
  peopleInclude: string[];
  peopleExclude: string[];
  peopleMatch: 'all' | 'any';
  favorite: boolean | null;
  minRating: number | null;
  hasGps: boolean | null;
  dateTakenFrom: string | null;
  dateTakenTo: string | null;
  collapseDuplicates: boolean | null;
  sort: string | null;
  sortDirection: string | null;
  metadataSearch: string | null;
  semanticQuery: string | null;
  semanticQueryEnglish: string | null;
  semanticTopK: number;
}

export interface TvResolvedPerson {
  text: string;
  mode: 'include' | 'exclude';
  personId: string;
  name: string | null;
}

export interface TvPersonAmbiguity {
  text: string;
  mode: 'include' | 'exclude';
  candidates: { personId: string; name: string | null; faceCount: number }[];
}

export interface TvInterpretResponse {
  draft: TvInterpretDraft;
  resolvedPeople: TvResolvedPerson[];
  ambiguities: TvPersonAmbiguity[];
  warnings: string[];
  requiresClarification: boolean;
}

export interface TvInterpretRequest {
  command: string;
  locale: string;
  timeZone: string;
  currentDate?: string;
  currentFilters: TvCurrentFilterState;
}

// Distinct, non-technical error kinds so the UI can pick the right message
// WITHOUT ever surfacing raw model output. 'auth' = session/grant problem.
export type TvInterpretErrorKind =
  | 'unsupported'
  | 'busy'
  | 'timeout'
  | 'unavailable'
  | 'failed'
  | 'auth';

export class TvInterpretError extends Error {
  constructor(public readonly kind: TvInterpretErrorKind) {
    super(kind);
    this.name = 'TvInterpretError';
  }
}

export async function interpretTvPersonalGalleryCommand(
  unlockToken: string,
  request: TvInterpretRequest,
  signal?: AbortSignal,
): Promise<TvInterpretResponse> {
  try {
    return await api<TvInterpretResponse>('/api/tv/personal/gallery/interpret-command', {
      method: 'POST',
      json: request,
      headers: grantHeaders(unlockToken),
      signal,
    });
  } catch (err) {
    const status = (err as { status?: number }).status;
    throw new TvInterpretError(mapInterpretStatus(status));
  }
}

function mapInterpretStatus(status: number | undefined): TvInterpretErrorKind {
  switch (status) {
    case 401:
    case 403:
      return 'auth';
    case 422:
      return 'unsupported';
    case 429:
      return 'busy';
    case 503:
      return 'unavailable';
    case 504:
      return 'timeout';
    default:
      return 'failed';
  }
}

export interface TvPersonalPerson {
  id: string;
  name: string | null;
  faceCount: number;
}

export interface TvPersonalAlbum {
  id: string;
  name: string;
  itemCount: number;
}

export interface TvPersonalAlbumAddResult {
  requested: number;
  succeeded: number;
  skipped: number;
}

export interface TvPersonalGalleryBulkResult {
  requested: number;
  succeeded: number;
  skipped: number;
  succeededItemIds: string[];
  failures: { itemId: string; reason: string }[];
}

export interface TvPersonalMediaInfo {
  id: string;
  name: string;
  sizeBytes: number;
  width: number | null;
  height: number | null;
  dateTaken: string;
  dateTakenSource: 'user' | 'embedded' | 'uploaded';
  cameraMake: string | null;
  cameraModel: string | null;
  lensModel: string | null;
  iso: number | null;
  aperture: number | null;
  exposureTime: string | null;
  focalLength: number | null;
  hasGps: boolean;
  title: string | null;
  description: string | null;
  tags: string[];
  rating: number | null;
  favorite: boolean;
  location: string | null;
}

function grantHeaders(unlockToken: string): Record<string, string> {
  return { [TV_PERSONAL_UNLOCK_HEADER]: unlockToken };
}

export function listTvPersonalGallery(
  unlockToken: string,
  query: TvPersonalGalleryQuery = {},
  signal?: AbortSignal,
): Promise<TvPersonalGalleryPage> {
  const params = new URLSearchParams();
  if (query.q !== undefined && query.q.length > 0) params.set('q', query.q);
  if (query.sort !== undefined) params.set('sort', query.sort);
  if (query.direction !== undefined) params.set('direction', query.direction);
  if (query.limit !== undefined) params.set('limit', String(query.limit));
  if (query.cursor !== undefined && query.cursor.length > 0) params.set('cursor', query.cursor);
  if (query.favorite !== undefined) params.set('favorite', String(query.favorite));
  if (query.minRating !== undefined) params.set('minRating', String(query.minRating));
  if (query.hasGps !== undefined) params.set('hasGps', String(query.hasGps));
  if (query.dateTakenFrom !== undefined && query.dateTakenFrom.length > 0) {
    params.set('dateTakenFrom', query.dateTakenFrom);
  }
  if (query.dateTakenTo !== undefined && query.dateTakenTo.length > 0) {
    params.set('dateTakenTo', query.dateTakenTo);
  }
  if (query.collapseDuplicates === true) params.set('collapseDuplicates', 'true');
  if (query.includePeople !== undefined && query.includePeople.length > 0) {
    params.set('includePeople', query.includePeople.join(','));
    if (query.includePeopleMode !== undefined) {
      params.set('includePeopleMode', query.includePeopleMode);
    }
  }
  if (query.excludePeople !== undefined && query.excludePeople.length > 0) {
    params.set('excludePeople', query.excludePeople.join(','));
  }
  if (query.semanticQuery !== undefined && query.semanticQuery.length > 0) {
    params.set('semanticQuery', query.semanticQuery);
    if (query.semanticTopK !== undefined) params.set('semanticTopK', String(query.semanticTopK));
  }
  const qs = params.toString();
  const path = qs.length === 0 ? '/api/tv/personal/gallery' : `/api/tv/personal/gallery?${qs}`;
  return api<TvPersonalGalleryPage>(path, { headers: grantHeaders(unlockToken), signal });
}

export function listTvPersonalPeople(
  unlockToken: string,
  signal?: AbortSignal,
): Promise<TvPersonalPerson[]> {
  return api<TvPersonalPerson[]>('/api/tv/personal/gallery/people', {
    headers: grantHeaders(unlockToken),
    signal,
  });
}

export function listTvPersonalAlbums(
  unlockToken: string,
  signal?: AbortSignal,
): Promise<TvPersonalAlbum[]> {
  return api<TvPersonalAlbum[]>('/api/tv/personal/gallery/albums', {
    headers: grantHeaders(unlockToken),
    signal,
  });
}

export function addTvPersonalItemsToAlbum(
  unlockToken: string,
  albumId: string,
  fileItemIds: string[],
): Promise<TvPersonalAlbumAddResult> {
  return api<TvPersonalAlbumAddResult>(
    `/api/tv/personal/gallery/albums/${encodeURIComponent(albumId)}/items`,
    { method: 'POST', json: { fileItemIds }, headers: grantHeaders(unlockToken) },
  );
}

export type TvPersonalGalleryDestination = 'beauty-lab' | 'plates';

export function addTvPersonalItemsToDestination(
  unlockToken: string,
  destination: TvPersonalGalleryDestination,
  fileItemIds: string[],
): Promise<TvPersonalGalleryBulkResult> {
  return api<TvPersonalGalleryBulkResult>(
    `/api/tv/personal/gallery/add-to-${destination}`,
    { method: 'POST', json: { fileItemIds }, headers: grantHeaders(unlockToken) },
  );
}

export function trashTvPersonalGalleryItems(
  unlockToken: string,
  fileItemIds: string[],
): Promise<TvPersonalGalleryBulkResult> {
  return api<TvPersonalGalleryBulkResult>(
    '/api/tv/personal/gallery/trash',
    { method: 'POST', json: { fileItemIds }, headers: grantHeaders(unlockToken) },
  );
}

export function getTvPersonalMediaInfo(
  unlockToken: string,
  fileId: string,
  signal?: AbortSignal,
): Promise<TvPersonalMediaInfo> {
  return api<TvPersonalMediaInfo>(
    `/api/tv/personal/media/${encodeURIComponent(fileId)}/info`,
    { headers: grantHeaders(unlockToken), signal },
  );
}

export function setTvPersonalFavorite(
  unlockToken: string,
  fileId: string,
  favorite: boolean,
): Promise<{ id: string; favorite: boolean }> {
  return api<{ id: string; favorite: boolean }>(
    `/api/tv/personal/media/${encodeURIComponent(fileId)}/favorite`,
    { method: 'PUT', json: { favorite }, headers: grantHeaders(unlockToken) },
  );
}

// The <img> tag cannot send the grant header, so /tv fetches personal media
// bytes explicitly (browser fetch with the header + session cookie) and renders
// object URLs. Returns the blob URL; the caller owns revocation.
export async function fetchTvPersonalMediaObjectUrl(
  unlockToken: string,
  mediaUrl: string,
  signal?: AbortSignal,
): Promise<string> {
  if (!mediaUrl.startsWith('/api/tv/personal/media/')) {
    throw new Error('Refusing non-personal TV media URL');
  }
  const response = await fetch(mediaUrl, {
    headers: grantHeaders(unlockToken),
    credentials: 'include',
    signal,
  });
  if (!response.ok) {
    throw new ApiError(response.status, `GET ${mediaUrl} → ${response.status}`, null);
  }
  return URL.createObjectURL(await response.blob());
}

// --- TV "Beauty Lab" (Laboratorio bellezza) — grant-gated projection of the
// owner-private Aesthetics Lab. Every call carries the personal unlock grant and
// hits the SAME application services the web lab uses, so behaviour is identical.
// DTOs are the display-safe Aesthetics Lab shapes (no blob id / storage / SHA /
// raw model output); media is derived-only and fetched as object URLs below.

export type {
  AestheticLabItem as TvBeautyLabItem,
  AestheticLabItemDetail as TvBeautyLabItemDetail,
  AestheticLabPage as TvBeautyLabPage,
  AestheticRun as TvBeautyLabRun,
  AestheticMetric as TvBeautyLabMetric,
  AestheticAnalysisBatchResult as TvBeautyLabAnalysisResult,
} from './aestheticsLab';
import type {
  AestheticLabItemDetail,
  AestheticLabPage,
  AestheticRun,
  AestheticAnalysisBatchResult,
} from './aestheticsLab';

// Short-lived QR mobile-upload session the TV mints, polls, and revokes. The
// uploadUrl (raw token in a path segment) is returned ONCE, for the QR render.
export interface TvBeautyLabUploadSession {
  id: string;
  uploadUrl: string;
  expiresAt: string;
  maxFiles: number;
  maxTotalBytes: number;
  accepted: number;
  rejected: number;
  status: string;
}

export interface TvBeautyLabUploadSessionStatus {
  id: string;
  expiresAt: string;
  maxFiles: number;
  maxTotalBytes: number;
  accepted: number;
  rejected: number;
  status: string;
}

export function listTvBeautyLabItems(
  unlockToken: string,
  cursor?: string | null,
  limit?: number,
  signal?: AbortSignal,
): Promise<AestheticLabPage> {
  const params = new URLSearchParams();
  if (cursor) params.set('cursor', cursor);
  if (limit) params.set('limit', String(limit));
  const qs = params.toString();
  return api<AestheticLabPage>(
    `/api/tv/personal/aesthetics/items${qs ? `?${qs}` : ''}`,
    { headers: grantHeaders(unlockToken), signal },
  );
}

export function getTvBeautyLabItem(
  unlockToken: string,
  id: string,
  signal?: AbortSignal,
): Promise<AestheticLabItemDetail> {
  return api<AestheticLabItemDetail>(
    `/api/tv/personal/aesthetics/items/${encodeURIComponent(id)}`,
    { headers: grantHeaders(unlockToken), signal },
  );
}

export function requestTvBeautyLabAnalysis(
  unlockToken: string,
  itemIds: string[],
  capabilities?: string[],
  signal?: AbortSignal,
): Promise<AestheticAnalysisBatchResult> {
  return api<AestheticAnalysisBatchResult>(
    '/api/tv/personal/aesthetics/analyses',
    {
      method: 'POST',
      json: { itemIds, capabilities: capabilities ?? null },
      headers: grantHeaders(unlockToken),
      signal,
    },
  );
}

export function cancelTvBeautyLabRun(
  unlockToken: string,
  runId: string,
  signal?: AbortSignal,
): Promise<void> {
  return api<void>(
    `/api/tv/personal/aesthetics/runs/${encodeURIComponent(runId)}/cancel`,
    { method: 'POST', headers: grantHeaders(unlockToken), signal },
  );
}

export function retryTvBeautyLabRun(
  unlockToken: string,
  runId: string,
  signal?: AbortSignal,
): Promise<AestheticRun> {
  return api<AestheticRun>(
    `/api/tv/personal/aesthetics/runs/${encodeURIComponent(runId)}/retry`,
    { method: 'POST', headers: grantHeaders(unlockToken), signal },
  );
}

export function removeTvBeautyLabItem(
  unlockToken: string,
  id: string,
  signal?: AbortSignal,
): Promise<void> {
  return api<void>(
    `/api/tv/personal/aesthetics/items/${encodeURIComponent(id)}`,
    { method: 'DELETE', headers: grantHeaders(unlockToken), signal },
  );
}

export function createTvBeautyLabUploadSession(
  unlockToken: string,
  signal?: AbortSignal,
): Promise<TvBeautyLabUploadSession> {
  return api<TvBeautyLabUploadSession>(
    '/api/tv/personal/aesthetics/upload-sessions',
    { method: 'POST', headers: grantHeaders(unlockToken), signal },
  );
}

export function getTvBeautyLabUploadSession(
  unlockToken: string,
  id: string,
  signal?: AbortSignal,
): Promise<TvBeautyLabUploadSessionStatus> {
  return api<TvBeautyLabUploadSessionStatus>(
    `/api/tv/personal/aesthetics/upload-sessions/${encodeURIComponent(id)}`,
    { headers: grantHeaders(unlockToken), signal },
  );
}

export function revokeTvBeautyLabUploadSession(
  unlockToken: string,
  id: string,
  signal?: AbortSignal,
): Promise<void> {
  return api<void>(
    `/api/tv/personal/aesthetics/upload-sessions/${encodeURIComponent(id)}/revoke`,
    { method: 'POST', headers: grantHeaders(unlockToken), signal },
  );
}

// The <img> tag can't send the grant header, so /tv fetches Beauty Lab derived
// media explicitly and renders object URLs (caller owns revocation). Restricted
// to the Beauty Lab media namespace — never an original endpoint.
export async function fetchTvBeautyLabMediaObjectUrl(
  unlockToken: string,
  mediaUrl: string,
  signal?: AbortSignal,
): Promise<string> {
  if (!mediaUrl.startsWith('/api/tv/personal/aesthetics/items/')) {
    throw new Error('Refusing non-Beauty-Lab TV media URL');
  }
  const response = await fetch(mediaUrl, {
    headers: grantHeaders(unlockToken),
    credentials: 'include',
    signal,
  });
  if (!response.ok) {
    throw new ApiError(response.status, `GET ${mediaUrl} → ${response.status}`, null);
  }
  return URL.createObjectURL(await response.blob());
}

// --- TV Personal Area PIN management (normal user auth, owner-side) ---
// Deliberately NOT under /api/tv: the TV session can never call these. The PIN
// is created exactly once from the authenticated pairing/approval flow and is
// never returned by any endpoint.

export interface TvPersonalPinStatus {
  configured: boolean;
  // Last time the PIN was set or changed; null when unconfigured. Never the
  // hash, generation, attempt counters, or grant details.
  updatedAt: string | null;
}

export function getTvPersonalPinStatus(signal?: AbortSignal): Promise<TvPersonalPinStatus> {
  return api<TvPersonalPinStatus>('/api/tv-personal/pin', { signal });
}

// Owner-authenticated set/change/reset (no old PIN — the owner session IS the
// authorization). Changing the PIN revokes every outstanding unlock grant of
// this owner: all TVs must re-enter the new PIN; Party is unaffected.
// 400 invalid_pin / pin_mismatch.
export function setTvPersonalPin(pin: string, confirmPin: string): Promise<TvPersonalPinStatus> {
  return api<TvPersonalPinStatus>('/api/tv-personal/pin', {
    method: 'POST',
    json: { pin, confirmPin },
  });
}

// --- Owner-side TV device/session management (normal user auth) ---
// These are OWNER endpoints, deliberately NOT under /api/tv, so the limited TV
// session cannot reach them. Safe DTO only — no token/hash/secret.

export interface TvDevice {
  id: string;
  deviceLabel: string | null;
  userAgent: string | null;
  status: 'active' | 'expired' | 'revoked';
  createdAt: string;
  lastSeenAt: string;
  expiresAt: string;
  revokedAt: string | null;
}

export function listTvDevices(signal?: AbortSignal): Promise<TvDevice[]> {
  return api<TvDevice[]>('/api/tv-devices', { signal });
}

export function revokeTvDevice(sessionId: string, signal?: AbortSignal): Promise<void> {
  return api<void>(`/api/tv-devices/${encodeURIComponent(sessionId)}`, {
    method: 'DELETE',
    signal,
  });
}
