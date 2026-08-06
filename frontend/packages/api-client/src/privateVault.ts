import { api, ApiError } from './client';

// Owner-private Private Vault (v0). Exclusion-first: vault content is kept out
// of every normal flow server-side. Access requires a password unlock that
// returns a SHORT-LIVED token — held ONLY in page memory (never localStorage /
// sessionStorage / URL). All DTOs are sanitized: no BlobObjectId, StorageKey,
// SHA, path, password hash, or token hash.

export interface PrivateVaultStatus {
  // Whether a vault password has been configured for this account. Reveals
  // NOTHING about content (no counts, no empty/non-empty signal).
  configured: boolean;
  displayName: string;
  encryptionMode: string;
}

export interface PrivateVaultUnlockResult {
  // Raw unlock token — keep in memory only; expires quickly.
  token: string;
  expiresAt: string;
}

export interface VaultFolder {
  id: string;
  name: string;
}

export type VaultMediaKind = 'image' | 'video' | 'other';

export interface VaultFile {
  id: string;
  // Original filename (kept for the details panel / tooltip).
  name: string;
  // User title override, if any.
  title: string | null;
  // What the grid shows: `title ?? name`.
  displayName: string;
  mediaKind: VaultMediaKind;
  mimeType: string;
  sizeBytes: number;
  createdAt: string;
  width: number | null;
  height: number | null;
  // Whether a small grid thumbnail / video poster derivative already exists.
  // The grid uses these to avoid a doomed fetch (no generation happens either
  // way — a missing derivative just renders the neutral placeholder).
  thumbnailAvailable: boolean;
  posterAvailable: boolean;
}

// Sanitized read-only detail for the viewer / info panel. Mirrors the backend
// VaultMediaInfo: NO storage path, blob id, hash, embedding, or face data.
export interface VaultMediaInfo {
  id: string;
  name: string;
  title: string | null;
  displayName: string;
  mediaKind: VaultMediaKind;
  mimeType: string;
  sizeBytes: number;
  width: number | null;
  height: number | null;
  createdAt: string;
  takenAt: string | null;
  description: string | null;
  tags: string[];
  rating: number | null;
  favorite: boolean;
  location: string | null;
  thumbnailAvailable: boolean;
  previewAvailable: boolean;
  posterAvailable: boolean;
}

export interface VaultListing {
  folderId: string | null;
  folders: VaultFolder[];
  files: VaultFile[];
}

export interface VaultMoveResult {
  movedFiles: number;
  movedFolders: number;
}

const vaultHeader = (token: string): Record<string, string> => ({ 'X-Vault-Token': token });

export function getVaultStatus(signal?: AbortSignal): Promise<PrivateVaultStatus> {
  return api<PrivateVaultStatus>('/api/private-vault', { signal });
}

// Creates the vault the first time. 409 if already configured, 400 if the
// password is too short.
export function setupVault(password: string, signal?: AbortSignal): Promise<{ configured: boolean }> {
  return api<{ configured: boolean }>('/api/private-vault/setup', {
    method: 'POST',
    json: { password },
    signal,
  });
}

// 401 (generic) on wrong password OR missing vault — indistinguishable.
export function unlockVault(password: string, signal?: AbortSignal): Promise<PrivateVaultUnlockResult> {
  return api<PrivateVaultUnlockResult>('/api/private-vault/unlock', {
    method: 'POST',
    json: { password },
    signal,
  });
}

export function lockVault(token: string, signal?: AbortSignal): Promise<void> {
  return api<void>('/api/private-vault/lock', {
    method: 'POST',
    headers: vaultHeader(token),
    signal,
  });
}

export function listVaultRoot(token: string, signal?: AbortSignal): Promise<VaultListing> {
  return api<VaultListing>('/api/private-vault/root', { headers: vaultHeader(token), signal });
}

export function listVaultFolder(
  token: string,
  folderId: string,
  signal?: AbortSignal,
): Promise<VaultListing> {
  return api<VaultListing>(`/api/private-vault/folders/${encodeURIComponent(folderId)}`, {
    headers: vaultHeader(token),
    signal,
  });
}

export function vaultMoveIn(
  token: string,
  items: { fileIds?: string[]; folderIds?: string[] },
  signal?: AbortSignal,
): Promise<VaultMoveResult> {
  return api<VaultMoveResult>('/api/private-vault/move-in', {
    method: 'POST',
    headers: vaultHeader(token),
    json: { fileIds: items.fileIds ?? [], folderIds: items.folderIds ?? [] },
    signal,
  });
}

export function vaultMoveOut(
  token: string,
  items: { fileIds?: string[]; folderIds?: string[] },
  signal?: AbortSignal,
): Promise<VaultMoveResult> {
  return api<VaultMoveResult>('/api/private-vault/move-out', {
    method: 'POST',
    headers: vaultHeader(token),
    json: { fileIds: items.fileIds ?? [], folderIds: items.folderIds ?? [] },
    signal,
  });
}

// ── Derived-media bytes (slice 4) ────────────────────────────────────────────
// The vault media endpoints serve DERIVED artifacts only (small/medium
// thumbnail, medium preview, video poster) — never originals. An <img src>
// cannot carry the X-Vault-Token header, so the browser fetches the bytes
// explicitly (with the header + session cookie) and the caller renders an
// object URL. The token travels ONLY in the header — never the URL/query.

// Only JPEG derivatives are served; reject anything else defensively.
const ALLOWED_MEDIA_TYPE_PREFIX = 'image/';
// Prudent cap so a misbehaving/oversized response can never balloon memory. A
// medium preview JPEG is well under this.
const MAX_MEDIA_BYTES = 30 * 1024 * 1024;

async function fetchVaultBlob(token: string, path: string, signal?: AbortSignal): Promise<Blob> {
  // Never log the token or a token-bearing string; the token lives only in the
  // header, so `path` here is already token-free.
  const response = await fetch(path, {
    headers: vaultHeader(token),
    credentials: 'include',
    signal,
  });
  if (response.status === 401) {
    // Surfaced distinctly so the page can treat it as "vault token expired" and
    // fall back to the unlock form (never a global session teardown).
    throw new ApiError(401, `GET ${path} → 401`, null);
  }
  if (!response.ok) {
    throw new ApiError(response.status, `GET ${path} → ${response.status}`, null);
  }
  const contentType = response.headers.get('content-type') ?? '';
  if (!contentType.startsWith(ALLOWED_MEDIA_TYPE_PREFIX)) {
    throw new ApiError(response.status, `GET ${path} → unexpected content-type`, null);
  }
  const declared = Number(response.headers.get('content-length'));
  if (Number.isFinite(declared) && declared > MAX_MEDIA_BYTES) {
    throw new ApiError(response.status, `GET ${path} → too large`, null);
  }
  const blob = await response.blob();
  if (blob.size > MAX_MEDIA_BYTES) {
    throw new ApiError(response.status, `GET ${path} → too large`, null);
  }
  return blob;
}

export function fetchVaultThumbnail(
  token: string,
  fileId: string,
  size: 'small' | 'medium',
  signal?: AbortSignal,
): Promise<Blob> {
  return fetchVaultBlob(
    token,
    `/api/private-vault/media/${encodeURIComponent(fileId)}/thumbnail?size=${size}`,
    signal,
  );
}

export function fetchVaultPreview(token: string, fileId: string, signal?: AbortSignal): Promise<Blob> {
  return fetchVaultBlob(
    token,
    `/api/private-vault/media/${encodeURIComponent(fileId)}/preview`,
    signal,
  );
}

export function fetchVaultPoster(token: string, fileId: string, signal?: AbortSignal): Promise<Blob> {
  return fetchVaultBlob(
    token,
    `/api/private-vault/media/${encodeURIComponent(fileId)}/poster`,
    signal,
  );
}

export function getVaultMediaInfo(
  token: string,
  fileId: string,
  signal?: AbortSignal,
): Promise<VaultMediaInfo> {
  return api<VaultMediaInfo>(
    `/api/private-vault/media/${encodeURIComponent(fileId)}/info`,
    { headers: vaultHeader(token), signal },
  );
}
