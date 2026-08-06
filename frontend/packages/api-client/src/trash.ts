import { api } from './client';

// Mirrors NubArca.Api.Folders.FolderTrashSummary on the backend. The
// `parentFolderId` is part of the user's own logical tree, not a storage
// secret, so it is exposed by design — a trash UI uses it for "originally
// located in …" hints. OwnerUserId / BlobObjectId / StorageKey are never
// returned by the API.
export interface FolderTrashSummary {
  id: string;
  name: string;
  parentFolderId: string | null;
  createdAt: string;
  updatedAt: string | null;
  deletedAt: string;
}

export interface FileTrashSummary {
  id: string;
  name: string;
  mimeType: string;
  sizeBytes: number;
  parentFolderId: string | null;
  createdAt: string;
  updatedAt: string | null;
  deletedAt: string;
  width?: number | null;
  height?: number | null;
}

export interface TrashResponse {
  folders: FolderTrashSummary[];
  files: FileTrashSummary[];
}

export interface EmptyTrashFailure {
  id: string;
  type: 'file' | 'folder';
  reason: string;
}

export interface EmptyTrashResult {
  deletedFiles: number;
  deletedFolders: number;
  conflicts: number;
  errors: number;
  failures: EmptyTrashFailure[];
}

export function getTrash(signal?: AbortSignal): Promise<TrashResponse> {
  return api<TrashResponse>('/api/trash', { signal });
}

export function restoreFile(fileId: string, signal?: AbortSignal): Promise<unknown> {
  return api<unknown>(`/api/files/${fileId}/restore`, { method: 'POST', signal });
}

export function restoreFolder(folderId: string, signal?: AbortSignal): Promise<unknown> {
  return api<unknown>(`/api/folders/${folderId}/restore`, { method: 'POST', signal });
}

export function permanentDeleteFile(fileId: string, signal?: AbortSignal): Promise<void> {
  return api<void>(`/api/trash/files/${fileId}`, { method: 'DELETE', signal });
}

export function permanentDeleteFolder(folderId: string, signal?: AbortSignal): Promise<void> {
  return api<void>(`/api/trash/folders/${folderId}`, { method: 'DELETE', signal });
}

export function emptyTrash(signal?: AbortSignal): Promise<EmptyTrashResult> {
  return api<EmptyTrashResult>('/api/trash', { method: 'DELETE', signal });
}
