import { api, ApiError } from './client';

// --- Owner-side party settings (normal user auth) ---

export interface AlbumPartyStatus {
  albumId: string;
  showOnTv: boolean;
  partyMode: boolean;
  // Relative public landing URL ("/party/{token}") while party mode is active,
  // else null. Never a token hash. The frontend prepends its own origin for QR.
  partyUrl: string | null;
  // Whether anonymous guest UPLOAD is currently allowed, and the relative public
  // upload landing URL ("/party/{uploadToken}/upload") when it is (separate
  // token from partyUrl). Both null/false when party or upload is off.
  uploadEnabled: boolean;
  uploadUrl: string | null;
  // When true, new guest uploads wait for owner approval before appearing on the
  // public party page / TV. Default false (immediate visibility).
  requireUploadApproval: boolean;
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
): Promise<AlbumPartyStatus> {
  const json: Record<string, boolean> = { enabled };
  if (uploadEnabled !== undefined) json.uploadEnabled = uploadEnabled;
  if (requireUploadApproval !== undefined) json.requireUploadApproval = requireUploadApproval;
  return api<AlbumPartyStatus>(`/api/albums/${albumId}/party-settings`, {
    method: 'PATCH',
    json,
    signal,
  });
}

// --- Owner-side party upload moderation (normal user auth) ---

export type PartyUploadStatus = 'approved' | 'pending' | 'hidden' | 'rejected' | 'removed_from_album';

export interface PartyUploadItem {
  fileItemId: string;
  name: string;
  mediaType: 'image' | 'video';
  status: PartyUploadStatus;
  // Owner-auth thumbnail path ("/api/files/{id}/thumbnail"). Never a storage key.
  thumbnailUrl: string;
  uploadedAt: string;
  moderatedAt: string | null;
}

export interface PartyUploadList {
  albumId: string;
  requireUploadApproval: boolean;
  items: PartyUploadItem[];
}

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

// --- Public party landing (anonymous, token-scoped) ---

export interface PartyAlbum {
  albumName: string;
  itemCount: number;
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

// --- Public party UPLOAD (anonymous, upload-token scoped) ---

export interface PartyUploadResult {
  accepted: number;
  rejected: number;
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
