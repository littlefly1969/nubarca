import { api } from './client';

// Mirrors NubArca.Api.Folders.FolderSummary on the backend. Deliberately
// omits OwnerUserId / ParentFolderId / DeletedAt — those are storage
// internals the API itself never returns.
export interface FolderSummary {
  id: string;
  name: string;
  createdAt: string;
}

// Mirrors NubArca.Api.Files.FileSummary. Same no-leak contract.
export interface FileSummary {
  id: string;
  name: string;
  mimeType: string;
  sizeBytes: number;
  createdAt: string;
  width?: number | null;
  height?: number | null;
}

// Mirrors NubArca.Api.Folders.FolderChildrenResponse. `folderId` is null
// at the root and non-null when listing a specific folder's contents.
//
// Files UI v2: `folders` is the full ordered set, present only on the first
// page (no cursor); `files` is one seek-paginated page. `nextCursor` is null at
// the end of the file list and `hasMore` mirrors it. Older callers that ignore
// the cursor fields still get the first page exactly as before.
export interface FolderChildrenResponse {
  folderId: string | null;
  folders: FolderSummary[];
  files: FileSummary[];
  nextCursor?: string | null;
  hasMore?: boolean;
}

// Sort options for the directory listing — mirrors the backend DirectorySort
// enum. Folders carry no size/type, so those sorts order folders by name.
export type DirectorySortField = 'name' | 'created' | 'size' | 'type';
export type SortDirection = 'asc' | 'desc';

export interface DirectoryListingParams {
  sort?: DirectorySortField;
  direction?: SortDirection;
  limit?: number;
  // Opaque cursor from a prior page's `nextCursor`. Bound to (sort, direction,
  // folder) server-side; a mismatch is a 400.
  cursor?: string | null;
}

function buildChildrenQuery(params?: DirectoryListingParams): string {
  if (params === undefined) return '';
  const search = new URLSearchParams();
  if (params.sort !== undefined) search.set('sort', params.sort);
  if (params.direction !== undefined) search.set('direction', params.direction);
  if (params.limit !== undefined) search.set('limit', String(params.limit));
  if (params.cursor !== undefined && params.cursor !== null) search.set('cursor', params.cursor);
  const qs = search.toString();
  return qs.length > 0 ? `?${qs}` : '';
}

// Files UI v2 listing entry point: sort + seek pagination in one call.
// `folderId === null` lists the user's root.
export function getDirectoryChildren(
  folderId: string | null,
  params: DirectoryListingParams,
  signal?: AbortSignal,
): Promise<FolderChildrenResponse> {
  const base = folderId === null
    ? '/api/folders/children'
    : `/api/folders/${folderId}/children`;
  return api<FolderChildrenResponse>(`${base}${buildChildrenQuery(params)}`, { signal });
}

// Thin first-page wrappers retained for callers that only need the folder set
// (e.g. the move destination picker). They hit the same endpoint with default
// (name asc) ordering.
export function getRootChildren(signal?: AbortSignal): Promise<FolderChildrenResponse> {
  return api<FolderChildrenResponse>('/api/folders/children', { signal });
}

export function getFolderChildren(
  folderId: string,
  signal?: AbortSignal,
): Promise<FolderChildrenResponse> {
  return api<FolderChildrenResponse>(`/api/folders/${folderId}/children`, { signal });
}

// Used as an <a href="..."> target. The backend serves the file with
// `Content-Disposition: attachment; filename=...`, so the browser always
// downloads instead of navigating — no need to fetch as a blob and assemble
// a synthetic anchor on the client.
export function downloadFileUrl(fileId: string): string {
  return `/api/files/${fileId}/content`;
}

// Uploads to POST /api/files (root) or POST /api/folders/{id}/files. The
// FormData field name "file" matches the backend's multipart binding. The
// browser sets the content-type with the multipart boundary; we never set
// it ourselves.
// Slice 76: `relativePath` (browser webkitRelativePath, e.g.
// "Holiday/2024/IMG_001.jpg") preserves the selected directory structure as
// logical folders. Omit it for a normal single-file upload. The backend
// validates/normalises the path and rejects traversal/absolute paths.
export function uploadRootFile(
  file: File,
  signal?: AbortSignal,
  relativePath?: string,
): Promise<FileSummary> {
  const form = new FormData();
  form.append('file', file, file.name);
  if (relativePath !== undefined && relativePath.length > 0) {
    form.append('relativePath', relativePath);
  }
  return api<FileSummary>('/api/files', { method: 'POST', formData: form, signal });
}

export function uploadFileToFolder(
  folderId: string,
  file: File,
  signal?: AbortSignal,
  relativePath?: string,
): Promise<FileSummary> {
  const form = new FormData();
  form.append('file', file, file.name);
  if (relativePath !== undefined && relativePath.length > 0) {
    form.append('relativePath', relativePath);
  }
  return api<FileSummary>(`/api/folders/${folderId}/files`, {
    method: 'POST',
    formData: form,
    signal,
  });
}

// Creates a folder at the user's root. The backend already trims and
// validates the name (length, slashes, `.`/`..`), so the client just sends
// a trimmed string — both sides agree on whitespace handling and the
// backend remains authoritative.
export function createRootFolder(name: string, signal?: AbortSignal): Promise<FolderSummary> {
  return api<FolderSummary>('/api/folders', {
    method: 'POST',
    json: { name },
    signal,
  });
}

export function createFolderInFolder(
  parentFolderId: string,
  name: string,
  signal?: AbortSignal,
): Promise<FolderSummary> {
  return api<FolderSummary>(`/api/folders/${parentFolderId}/folders`, {
    method: 'POST',
    json: { name },
    signal,
  });
}

// Mutation calls. The server is authoritative for name validation; the
// client just `.trim()`s before sending so a row that's only whitespace
// short-circuits without a round-trip. Soft-delete is a normal DELETE —
// the item moves to Trash and can be restored from the existing TrashPage.

export function renameFile(
  fileId: string,
  name: string,
  signal?: AbortSignal,
): Promise<FileSummary> {
  return api<FileSummary>(`/api/files/${fileId}/rename`, {
    method: 'PATCH',
    json: { name },
    signal,
  });
}

export function renameFolder(
  folderId: string,
  name: string,
  signal?: AbortSignal,
): Promise<FolderSummary> {
  return api<FolderSummary>(`/api/folders/${folderId}/rename`, {
    method: 'PATCH',
    json: { name },
    signal,
  });
}

export function deleteFile(fileId: string, signal?: AbortSignal): Promise<void> {
  return api<void>(`/api/files/${fileId}`, { method: 'DELETE', signal });
}

// Slice 77: safe count preview for the delete-confirmation dialog.
export interface FolderDeletePreview {
  fileCount: number;
  folderCount: number;
}

export function getFolderDeletePreview(
  folderId: string,
  signal?: AbortSignal,
): Promise<FolderDeletePreview> {
  return api<FolderDeletePreview>(`/api/folders/${folderId}/delete-preview`, { signal });
}

// Slice 77: recursive delete result (counts only, no storage internals).
export interface RecursiveDeleteResult {
  deletedFileCount: number;
  deletedFolderCount: number;
}

export function deleteFolderRecursive(
  folderId: string,
  signal?: AbortSignal,
): Promise<RecursiveDeleteResult> {
  return api<RecursiveDeleteResult>(`/api/folders/${folderId}?recursive=true`, {
    method: 'DELETE',
    signal,
  });
}

export function deleteFolder(folderId: string, signal?: AbortSignal): Promise<void> {
  return api<void>(`/api/folders/${folderId}`, { method: 'DELETE', signal });
}

// Reparent a file or folder. `parentFolderId === null` moves the resource to
// the user's root. The backend enforces ownership, cycle prevention for
// folders, and sibling-name uniqueness — the client just forwards the chosen
// destination.
export function moveFile(
  fileId: string,
  parentFolderId: string | null,
  signal?: AbortSignal,
): Promise<FileSummary> {
  return api<FileSummary>(`/api/files/${fileId}/move`, {
    method: 'PATCH',
    json: { parentFolderId },
    signal,
  });
}

export function moveFolder(
  folderId: string,
  parentFolderId: string | null,
  signal?: AbortSignal,
): Promise<FolderSummary> {
  return api<FolderSummary>(`/api/folders/${folderId}/move`, {
    method: 'PATCH',
    json: { parentFolderId },
    signal,
  });
}
