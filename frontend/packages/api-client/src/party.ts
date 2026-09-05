import { api, ApiError } from './client';
import type {
  AlbumPartyStatus,
  PartyMessageAction,
  PartyMessageList,
  PartyUploadList,
} from '@nubarca/contracts';

// Web TRANSPORT for Party. The DTOs, the validation RANGES and the message
// transition matrix are canonical in @nubarca/contracts, shared with the phone
// (§33, §34, §36) — the transition rules in particular used to live inlined in
// this app's JSX, which is not somewhere a second client can read them.
// Everything is re-exported under its existing name, so every web call site is
// unchanged.

export type {
  AlbumPartyStatus,
  PartyGameSettings,
  PartyMessage,
  PartyMessageAction,
  PartyMessageList,
  PartyMessageModeration,
  PartyMessageStatus,
  PartySlideshowSettings,
  PartyUploadItem,
  PartyUploadList,
  PartyUploadStatus,
} from '@nubarca/contracts';
export {
  DESTRUCTIVE_PARTY_MESSAGE_ACTIONS,
  PARTY_GAME_RANGES,
  PARTY_SLIDESHOW_RANGES,
  clampToRange,
  invalidGameFields,
  invalidSlideshowFields,
  isPartyMessageActionAllowed,
  partyGuestUrl,
  partyMessageActions,
} from '@nubarca/contracts';


// --- Owner-side party settings (normal user auth) ---

export function setPartyGameSettings(
  albumId: string,
  settings: {
    gameEnabled: boolean; minChallengeIntervalSeconds: number;
    maxChallengeIntervalSeconds: number; votesPerGuest: number;
    maxChallengesPerSession: number | null;
  },
  signal?: AbortSignal,
): Promise<AlbumPartyStatus> {
  return api<AlbumPartyStatus>(`/api/albums/${albumId}/party-game-settings`, {
    method: 'PATCH', json: settings, signal,
  });
}

// Saves ONLY the four numeric settings. Deliberately a different endpoint from
// setAlbumPartyMode so saving them cannot rotate a token, toggle party/upload,
// or change approval mode as a side effect.
export function setPartySlideshowSettings(
  albumId: string,
  settings: {
    photoSlideSeconds?: number;
    maxVideoSlideSeconds?: number;
    maxPhotoUploadsPerParticipant?: number;
    maxVideoUploadsPerParticipant?: number;
  },
  signal?: AbortSignal,
): Promise<AlbumPartyStatus> {
  return api<AlbumPartyStatus>(`/api/albums/${albumId}/party-slideshow-settings`, {
    method: 'PATCH',
    json: settings,
    signal,
  });
}

export function getAlbumPartySettings(
  albumId: string,
  signal?: AbortSignal,
): Promise<AlbumPartyStatus> {
  return api<AlbumPartyStatus>(`/api/albums/${albumId}/party-settings`, { signal });
}

export function setAlbumPartyMode(
  albumId: string,
  enabled: boolean,
  uploadEnabled?: boolean,
  requireUploadApproval?: boolean,
  signal?: AbortSignal,
  requireMessageApproval?: boolean,
): Promise<AlbumPartyStatus> {
  const json: Record<string, boolean> = { enabled };
  if (uploadEnabled !== undefined) json.uploadEnabled = uploadEnabled;
  if (requireUploadApproval !== undefined) json.requireUploadApproval = requireUploadApproval;
  if (requireMessageApproval !== undefined) json.requireMessageApproval = requireMessageApproval;
  return api<AlbumPartyStatus>(`/api/albums/${albumId}/party-settings`, {
    method: 'PATCH',
    json,
    signal,
  });
}

// --- Owner-side party upload moderation (normal user auth) ---

export function listPartyUploads(
  albumId: string,
  signal?: AbortSignal,
): Promise<PartyUploadList> {
  return api<PartyUploadList>(`/api/albums/${albumId}/party-uploads`, { signal });
}

// Hide a previously-visible guest upload, approve a pending one, or reject a
// pending one — each removes/adds it from the public party + TV surfaces on the
// next poll. 204 No Content; the caller refreshes the list.
export function moderatePartyUpload(
  albumId: string,
  fileItemId: string,
  action: 'hide' | 'approve' | 'reject' | 'restore',
  signal?: AbortSignal,
): Promise<void> {
  return api<void>(
    `/api/albums/${albumId}/party-uploads/${fileItemId}/${action}`,
    { method: 'POST', signal },
  );
}

// --- Owner-side party PRINT settings (normal user auth) ---

/** One product's own switch, its own budget, and its own usage. */
export interface PartyPrintProductSettings {
  enabled: boolean;
  maxPrints: number;
  /** Prints already accepted into the queue. History: never reset. */
  used: number;
  remaining: number;
  /** What ONE guest may take. 0 means no per-guest limit. */
  perGuest: number;
}

export interface PartyPrintSettings {
  enabled: boolean;
  printStationId: string | null;
  printerDeviceId: string | null;
  // Photo and strip are NEVER summed: they cost different things and the host
  // set them separately.
  photo: PartyPrintProductSettings;
  strip: PartyPrintProductSettings;
  footerText: string | null;
  footerMaxLength: number;
  minBudget: number;
  maxBudget: number;
}

/** Every field optional: an omitted one keeps its value rather than clearing it. */
export interface PartyPrintSettingsPatch {
  enabled?: boolean;
  printStationId?: string;
  printerDeviceId?: string;
  photoEnabled?: boolean;
  photoMaxPrints?: number;
  photoPrintsPerGuest?: number;
  stripEnabled?: boolean;
  stripMaxPrints?: number;
  stripPrintsPerGuest?: number;
  footerText?: string;
}

export function getPartyPrintSettings(
  albumId: string, signal?: AbortSignal,
): Promise<PartyPrintSettings> {
  return api<PartyPrintSettings>(`/api/albums/${albumId}/party-print-settings`, { signal });
}

// A separate endpoint from party-settings on purpose: saving a print budget
// must not be able to rotate a token, flip party mode, or change moderation.
export function setPartyPrintSettings(
  albumId: string, patch: PartyPrintSettingsPatch, signal?: AbortSignal,
): Promise<PartyPrintSettings> {
  return api<PartyPrintSettings>(`/api/albums/${albumId}/party-print-settings`, {
    method: 'PATCH', json: patch, signal,
  });
}

// --- Public party landing (anonymous, token-scoped) ---

export interface PartyAlbum {
  albumName: string;
  itemCount: number;
  coverUrl: string | null;
  contributionUrl: string | null;
  gameEnabled: boolean;
  // Non-null ONLY while printing would actually work: configured, enabled, on a
  // live station whose printer does 10x15, with budget left in at least one
  // product. Null is how the guest hub knows there is no print card to show.
  printUrl: string | null;
}

// --- Party print studio (anonymous, print-token scoped) ---

export type PartyPrintProduct = 'photo' | 'strip4';
export type PartyPrintTheme = 'pure' | 'midnight' | 'event';

export interface PartyPrintFormat {
  type: PartyPrintProduct;
  enabled: boolean;
  /** This product's OWN remaining count. The two are never summed. */
  remaining: number;
  requiredPhotos: number;
}

/** A choosable photograph: safe derived URLs only, never an original. */
export interface PartyPrintPhoto {
  id: string;
  thumbnailUrl: string;
  previewUrl: string;
}

export interface PartyPrintManifest {
  partyName: string;
  footerText: string | null;
  formats: PartyPrintFormat[];
  photos: PartyPrintPhoto[];
}

/** A crop, normalised to the auto-oriented source so the server reads it the same. */
export interface PartyPrintSlot {
  itemId: string;
  cropX: number;
  cropY: number;
  cropWidth: number;
  cropHeight: number;
}

export interface PartyPrintAccepted {
  jobId: string;
  publicSequence: number;
  product: PartyPrintProduct;
  remainingForProduct: number;
  /** Sheets ahead of this one on the printer. Zero means it is next. */
  queueAhead: number;
}

/** The pipeline's states, reduced to what a guest can act on. */
export type PartyPrintState =
  | 'preparing' | 'queued' | 'printing' | 'completed' | 'failed' | 'unknown';

export interface PartyPrintStatus {
  jobId: string;
  state: PartyPrintState;
  publicSequence: number;
  product: PartyPrintProduct;
}

export function getPartyPrintManifest(
  printToken: string, signal?: AbortSignal,
): Promise<PartyPrintManifest> {
  return api<PartyPrintManifest>(
    `/api/party/${encodeURIComponent(printToken)}/print`, { signal });
}

/**
 * Submit a composition.
 *
 * `idempotencyKey` is minted by the caller and REUSED for retries of the same
 * submission: printing has a physical effect, so a double tap or a replayed
 * request must return the first job rather than start a second sheet.
 */
export function submitPartyPrint(
  printToken: string,
  body: {
    product: PartyPrintProduct;
    theme: PartyPrintTheme;
    slots: PartyPrintSlot[];
  },
  idempotencyKey: string,
  signal?: AbortSignal,
): Promise<PartyPrintAccepted> {
  return api<PartyPrintAccepted>(
    `/api/party/${encodeURIComponent(printToken)}/print`,
    { method: 'POST', json: body, headers: { 'Idempotency-Key': idempotencyKey }, signal });
}

export function getPartyPrintStatus(
  printToken: string, jobId: string, signal?: AbortSignal,
): Promise<PartyPrintStatus> {
  return api<PartyPrintStatus>(
    `/api/party/${encodeURIComponent(printToken)}/print/${encodeURIComponent(jobId)}`,
    { signal });
}

export interface PartyItem {
  id: string;
  mediaType: 'image' | 'video';
  thumbnailUrl: string;
  previewUrl: string;
  // Present for images (metadata-stripped medium download); null for videos.
  downloadUrl: string | null;
}

export interface PartyItems {
  albumName: string;
  items: PartyItem[];
}

export function getPartyAlbum(token: string, signal?: AbortSignal): Promise<PartyAlbum> {
  return api<PartyAlbum>(`/api/party/${encodeURIComponent(token)}`, { signal });
}

export function getPartyItems(token: string, signal?: AbortSignal): Promise<PartyItems> {
  return api<PartyItems>(`/api/party/${encodeURIComponent(token)}/items`, { signal });
}

export type PartyChallengeKind = 'dare' | 'penalty' | 'guess' | 'custom';
export interface PartyChallenge {
  id: string;
  title: string;
  body: string;
  kind: PartyChallengeKind;
  mediaFileItemId: string | null;
  mediaUrl: string | null;
  isEnabled: boolean;
  sortOrder: number;
  voteCount: number;
  createdAt: string;
  updatedAt: string;
}
export interface PartyChallengeList { albumId: string; items: PartyChallenge[]; }
export interface PartyChallengeWrite {
  title: string; body: string; kind: PartyChallengeKind;
  mediaFileItemId: string | null; isEnabled: boolean;
}
export function listPartyChallenges(albumId: string, signal?: AbortSignal): Promise<PartyChallengeList> {
  return api<PartyChallengeList>(`/api/albums/${albumId}/party-challenges`, { signal });
}
export function createPartyChallenge(albumId: string, value: PartyChallengeWrite): Promise<PartyChallenge> {
  return api<PartyChallenge>(`/api/albums/${albumId}/party-challenges`, { method: 'POST', json: value });
}
export function updatePartyChallenge(albumId: string, id: string, value: PartyChallengeWrite): Promise<PartyChallenge> {
  return api<PartyChallenge>(`/api/albums/${albumId}/party-challenges/${id}`, { method: 'PUT', json: value });
}
export function deletePartyChallenge(albumId: string, id: string): Promise<void> {
  return api<void>(`/api/albums/${albumId}/party-challenges/${id}`, { method: 'DELETE' });
}
export function reorderPartyChallenges(albumId: string, challengeIds: string[]): Promise<void> {
  return api<void>(`/api/albums/${albumId}/party-challenges/order`, {
    method: 'PUT', json: { challengeIds },
  });
}

export interface PartyGuestChallenge {
  id: string; title: string; body: string; kind: PartyChallengeKind;
  mediaUrl: string | null; voted: boolean;
}
export interface PartyGuestChallenges {
  albumName: string; votesPerGuest: number; votesUsed: number; votesRemaining: number;
  items: PartyGuestChallenge[];
}
export interface PartyVoteResult { voted: boolean; votesUsed: number; votesRemaining: number; }
export function listPartyGuestChallenges(token: string, signal?: AbortSignal): Promise<PartyGuestChallenges> {
  return api<PartyGuestChallenges>(`/api/party/${encodeURIComponent(token)}/challenges`, { signal });
}
export function setPartyChallengeVote(token: string, id: string, voted: boolean): Promise<PartyVoteResult> {
  return api<PartyVoteResult>(
    `/api/party/${encodeURIComponent(token)}/challenges/${encodeURIComponent(id)}/vote`,
    { method: voted ? 'PUT' : 'DELETE' },
  );
}

// --- Public party UPLOAD (anonymous, upload-token scoped) ---

export interface PartyUploadResult {
  // Total accepted, kept for compatibility with the pre-video contract.
  accepted: number;
  rejected: number;
  // Per-kind breakdown and quota state. Optional so an older server response
  // still parses; the page treats a missing field as "not reported".
  acceptedPhotos?: number;
  acceptedVideos?: number;
  quotaRejectedPhotos?: number;
  quotaRejectedVideos?: number;
  // null = that kind is unlimited (never 0-means-unlimited on the wire).
  remainingPhotos?: number | null;
  remainingVideos?: number | null;
}

// What this guest may still upload on this link. Created or reused server-side;
// the participant identity itself lives in an HttpOnly cookie this code cannot
// read, which is the point — a quota the client could see is a quota the client
// could edit.
export interface PartyUploadSession {
  maxPhotos: number | null;
  maxVideos: number | null;
  usedPhotos: number;
  usedVideos: number;
  remainingPhotos: number | null;
  remainingVideos: number | null;
}

// Idempotent. Safe to call on every page load: it mints a session the first
// time and reuses it afterwards.
export function startPartyUploadSession(
  uploadToken: string,
  signal?: AbortSignal,
): Promise<PartyUploadSession> {
  return api<PartyUploadSession>(
    `/api/party/${encodeURIComponent(uploadToken)}/upload-session`,
    { method: 'POST', signal },
  );
}

// Declared types the party upload endpoint will consider. The SERVER decides
// what a file really is after ingest; this only keeps the picker and the
// obviously-pointless-upload check honest.
export const PARTY_VIDEO_TYPES = ['video/mp4', 'video/webm', 'video/quicktime'] as const;

export type PartyMediaKind = 'photo' | 'video' | 'unsupported';

// Best-effort client classification from the browser-reported type. UX only:
// it decides which counter a file is charged against locally and which files
// are obviously over quota, never whether the upload is allowed.
export function classifyPartyFile(file: File): PartyMediaKind {
  const type = (file.type || '').toLowerCase();
  if (type.startsWith('image/')) return 'photo';
  if ((PARTY_VIDEO_TYPES as readonly string[]).includes(type)) return 'video';
  return 'unsupported';
}

// Uploads one or more image files to a party album using the separate upload
// token. No auth; the multipart body is sent as-is (the browser sets the
// boundary). Safe count DTO back — no ids or storage internals.
export function uploadToParty(
  uploadToken: string,
  files: File[],
  signal?: AbortSignal,
): Promise<PartyUploadResult> {
  const form = new FormData();
  for (const file of files) {
    form.append('file', file, file.name);
  }
  return api<PartyUploadResult>(`/api/party/${encodeURIComponent(uploadToken)}/upload`, {
    method: 'POST',
    formData: form,
    signal,
  });
}

// Same public upload as uploadToParty, but via XMLHttpRequest so the UI can show
// real BYTE progress (fetch cannot report upload progress). `onProgress` gets a
// 0..1 fraction of bytes sent; it reaches 1 while the server is still processing
// (moderation / derivatives), so the caller should show a "processing" state
// after that until this promise resolves. Same-origin + credentials to match the
// fetch client; errors surface as ApiError so callers keep one error type.
export function uploadToPartyWithProgress(
  uploadToken: string,
  files: File[],
  onProgress?: (fraction: number) => void,
  signal?: AbortSignal,
): Promise<PartyUploadResult> {
  const form = new FormData();
  for (const file of files) {
    form.append('file', file, file.name);
  }
  const url = `/api/party/${encodeURIComponent(uploadToken)}/upload`;
  return new Promise<PartyUploadResult>((resolve, reject) => {
    const xhr = new XMLHttpRequest();
    xhr.open('POST', url);
    xhr.withCredentials = true;
    if (xhr.upload && onProgress) {
      xhr.upload.onprogress = (e) => {
        if (e.lengthComputable && e.total > 0) onProgress(Math.min(1, e.loaded / e.total));
      };
    }
    xhr.onload = () => {
      let parsed: unknown = null;
      const text = xhr.responseText;
      if (text && text.length > 0) {
        try { parsed = JSON.parse(text); } catch { parsed = text; }
      }
      if (xhr.status >= 200 && xhr.status < 300) {
        resolve(parsed as PartyUploadResult);
      } else {
        reject(new ApiError(xhr.status, `Request failed: POST ${url} → ${xhr.status}`, parsed));
      }
    };
    xhr.onerror = () => reject(new ApiError(0, `Request failed: POST ${url}`, null));
    xhr.onabort = () => reject(new DOMException('Aborted', 'AbortError'));
    if (signal) {
      if (signal.aborted) { xhr.abort(); return; }
      signal.addEventListener('abort', () => xhr.abort(), { once: true });
    }
    xhr.send(form);
  });
}

// --- Public party FACE SEARCH ("find your face", anonymous, view-token scoped) ---

// Safe machine status the UI maps to localized copy. The selfie is processed in
// memory server-side and never stored; no similarity score / face id / person id
// / vector is ever returned.
export type PartyFaceSearchStatus = 'ready' | 'no_face' | 'invalid_image' | 'unavailable';

export interface PartyFaceSearchResponse {
  status: PartyFaceSearchStatus;
  // Present only for a ready search (so the guest/TV can re-fetch it).
  searchId: string | null;
  resultCount: number;
  // Party-safe media items (same metadata-stripped derived URLs as the grid).
  items: PartyItem[];
}

// Upload one selfie and search THIS party album for matching photos. The server
// returns the safe DTO both on success (200) and on the capability-unavailable
// (503) / invalid-image (400) paths, so we normalise the ApiError body back to a
// PartyFaceSearchResponse the UI can render as a localized state.
export async function partyFaceSearch(
  token: string,
  file: File,
  signal?: AbortSignal,
): Promise<PartyFaceSearchResponse> {
  const form = new FormData();
  form.append('file', file, file.name);
  try {
    return await api<PartyFaceSearchResponse>(
      `/api/party/${encodeURIComponent(token)}/face-search`,
      { method: 'POST', formData: form, signal },
    );
  } catch (err) {
    if (
      err instanceof ApiError
      && err.body
      && typeof err.body === 'object'
      && 'status' in (err.body as Record<string, unknown>)
    ) {
      return err.body as PartyFaceSearchResponse;
    }
    throw err;
  }
}

// Explicitly activate a completed face search as the paired TV's face filter
// ("Show these photos on TV"). Completing a search never touches the TV by
// itself. The server enforces ordering: 409 {error:"no_matches"} for an empty
// search, 409 {error:"stale_search"} when a newer search is already active,
// 404 for an unknown/expired search.
export interface PartyFaceSearchActivation {
  searchId: string;
  // Server-assigned monotonic activation order (opaque counter).
  activationVersion: number;
}

export function activatePartyFaceSearchTv(
  token: string,
  searchId: string,
  signal?: AbortSignal,
): Promise<PartyFaceSearchActivation> {
  return api<PartyFaceSearchActivation>(
    `/api/party/${encodeURIComponent(token)}/face-search/${encodeURIComponent(searchId)}/activate-tv`,
    { method: 'POST', signal },
  );
}

// Cancel/delete a face search (session + stored face crop server-side). If this
// search is the active TV filter, deleting it also deactivates the TV.
// Idempotent (204 even when already gone); row-scoped, so cancelling an older
// search never removes a newer active TV filter.
export function deletePartyFaceSearch(
  token: string,
  searchId: string,
  signal?: AbortSignal,
): Promise<void> {
  return api<void>(
    `/api/party/${encodeURIComponent(token)}/face-search/${encodeURIComponent(searchId)}`,
    { method: 'DELETE', signal },
  );
}

// Re-fetch a stored face search's currently-visible matches (rank order). Throws
// ApiError(404) once the search expires or the party is disabled.
export function getPartyFaceSearch(
  token: string,
  searchId: string,
  signal?: AbortSignal,
): Promise<PartyFaceSearchResponse> {
  return api<PartyFaceSearchResponse>(
    `/api/party/${encodeURIComponent(token)}/face-search/${encodeURIComponent(searchId)}`,
    { signal },
  );
}

// --- Party guest MESSAGES ---
//
// A text-only channel beside the photo/video stream. Nothing here touches the
// media contract: a message is never a PartyItem and never a TV album item.

export function listPartyMessages(
  albumId: string,
  signal?: AbortSignal,
): Promise<PartyMessageList> {
  return api<PartyMessageList>(`/api/albums/${albumId}/party-messages`, { signal });
}

// Owner or delegate. 204 No Content; the caller refreshes the list. Promoting
// a message that is not currently visible is a 400 — the UI only offers Hero on
// live messages, so this is the backstop rather than an expected path.
export function moderatePartyMessage(
  albumId: string,
  messageId: string,
  action: PartyMessageAction,
  signal?: AbortSignal,
): Promise<void> {
  return api<void>(
    `/api/albums/${albumId}/party-messages/${messageId}/${action}`,
    { method: 'POST', signal },
  );
}

// --- Public guest message submission (anonymous, upload-token scoped) ---

export interface PartyMessageSubmission {
  id: string;
  // 'pending' when the host reads greetings before they go up, else 'visible'.
  status: 'visible' | 'pending';
  createdAt: string;
}

// The UPLOAD token, not the view token: writing is contributing, and the same
// switch that closes photo uploads closes this.
export function submitPartyMessage(
  uploadToken: string,
  message: { displayName?: string | null; text: string },
  signal?: AbortSignal,
): Promise<PartyMessageSubmission> {
  return api<PartyMessageSubmission>(
    `/api/party/${encodeURIComponent(uploadToken)}/messages`,
    { method: 'POST', json: message, signal },
  );
}
