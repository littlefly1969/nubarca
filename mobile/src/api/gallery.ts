import { apiGet, probe } from './client';

// Minimal, temporary duplication of the gallery-listing endpoints needed for
// the read-only mobile gallery slice. These mirror the contracts in
// frontend/packages/api-client (folders.ts) but are duplicated here because
// that package is wired for the Vite/frontend toolchain and is not yet
// Metro-compatible. Keep this file to the SMALLEST set of read-only endpoints.
//
// No storage internals are exposed by these contracts: no StorageKey, physical
// path, BlobObjectId, SHA, raw metadata, or vectors — only logical FileItem /
// folder ids, names, and display-safe fields.

export interface FolderSummary {
  id: string;
  name: string;
  createdAt: string;
}

export interface FileSummary {
  id: string;
  name: string;
  mimeType: string;
  sizeBytes: number;
  createdAt: string;
  width?: number | null;
  height?: number | null;
}

export interface FolderChildrenResponse {
  folderId: string | null;
  folders: FolderSummary[];
  files: FileSummary[];
  nextCursor?: string | null;
  hasMore?: boolean;
}

// GET /api/folders/children (root) or /api/folders/{id}/children.
// `folderId === null` lists the owner's root. `cursor` pages the file list;
// `folders` is the full set returned on the first (cursorless) page.
export function getDirectoryChildren(
  folderId: string | null,
  opts: { limit?: number; cursor?: string | null } = {},
): Promise<FolderChildrenResponse> {
  const base =
    folderId === null
      ? '/api/folders/children'
      : `/api/folders/${folderId}/children`;
  const params = new URLSearchParams();
  if (opts.limit !== undefined) params.set('limit', String(opts.limit));
  if (opts.cursor !== undefined && opts.cursor !== null) {
    params.set('cursor', opts.cursor);
  }
  const qs = params.toString();
  return apiGet<FolderChildrenResponse>(`${base}${qs ? `?${qs}` : ''}`);
}

export function isImage(file: FileSummary): boolean {
  return file.mimeType.startsWith('image/');
}

// --- All-photos view (GET /api/images) -------------------------------------
// Owner-private, cursor-paginated list of ALL the owner's images regardless of
// folder — the same endpoint the web gallery's main view uses. Returns
// display-safe DTOs only (logical FileItem id + name + a thumbnail URL); no
// StorageKey, BlobObjectId, SHA, raw metadata, or vectors.

export interface ImageItem {
  id: string;
  name: string;
  mimeType: string;
  createdAt: string;
}

export interface ImageListResponse {
  items: ImageItem[];
  nextCursor: string | null;
  hasMore: boolean;
}

// Newest-first by capture date is the natural "all photos" ordering; the
// backend falls back to upload date when DateTaken is absent.
export function listImages(
  opts: { limit?: number; cursor?: string | null } = {},
): Promise<ImageListResponse> {
  const params = new URLSearchParams();
  params.set('sort', 'datetaken');
  params.set('direction', 'desc');
  if (opts.limit !== undefined) params.set('limit', String(opts.limit));
  if (opts.cursor !== undefined && opts.cursor !== null) {
    params.set('cursor', opts.cursor);
  }
  return apiGet<ImageListResponse>(`/api/images?${params.toString()}`);
}

// Small derivative for list/grid (never the original).
export function smallThumbnailPath(fileId: string): string {
  return `/api/files/${fileId}/thumbnail?size=small`;
}

// Medium preview for the full-screen viewer (never the original full-res).
export function mediumPreviewPath(fileId: string): string {
  return `/api/files/${fileId}/preview`;
}

// Diagnostic: probe one file's small thumbnail with the authenticated fetch and
// translate the HTTP status into a human-readable verdict. Distinguishes image
// auth failure from a missing thumbnail from a missing cookie from an endpoint
// error — the four things a blank grid could mean.
export async function diagnoseThumbnail(
  fileId: string,
): Promise<{ status: number; verdict: string }> {
  const { status, cookieSent } = await probe(smallThumbnailPath(fileId));
  let verdict: string;
  if (!cookieSent) {
    verdict = 'No session cookie is being sent — sign in again.';
  } else if (status === 200) {
    verdict = 'OK (200): thumbnail auth works. Cookie is forwarded correctly.';
  } else if (status === 401) {
    verdict = '401 Unauthorized: cookie not accepted for image requests.';
  } else if (status === 403) {
    verdict = '403 Forbidden: authenticated but not authorized for this file.';
  } else if (status === 404) {
    verdict = '404 Not Found: this file has no small thumbnail derivative.';
  } else if (status === -1) {
    verdict = 'Network error: could not reach the server.';
  } else {
    verdict = `Unexpected status ${status}.`;
  }
  return { status, verdict };
}
