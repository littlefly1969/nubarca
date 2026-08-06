import { api } from './client';

// Slice 94 — media-library (gallery membership) rules client. Safe shapes:
// the owner's own folder ids/names, rule fields, and counts — never paths,
// storage internals, or GPS coordinates.

export interface MediaLibraryRule {
  id: string;
  folderId: string;
  folderName: string;
  ruleType: 'include' | 'exclude' | string;
  appliesToPhotos: boolean;
  appliesToVideos: boolean;
  appliesToChildren: boolean;
  createdAt: string;
  updatedAt: string;
}

export interface MediaLibraryEffectiveKind {
  excluded: boolean;
  // "default" | "rule" (explicit on this folder) | "inherited" (ancestor).
  source: string;
  sourceFolderId: string | null;
  sourceFolderName: string | null;
}

export interface MediaLibraryEffective {
  folderId: string;
  photos: MediaLibraryEffectiveKind;
  videos: MediaLibraryEffectiveKind;
  rule: MediaLibraryRule | null;
}

export function getMediaLibraryEffective(
  folderId: string,
  signal?: AbortSignal,
): Promise<MediaLibraryEffective> {
  const params = new URLSearchParams({ folderId });
  return api<MediaLibraryEffective>(`/api/media-library/effective?${params.toString()}`, { signal });
}

export function putMediaLibraryRule(
  body: {
    folderId: string;
    ruleType: 'include' | 'exclude';
    appliesToPhotos: boolean;
    appliesToVideos: boolean;
    appliesToChildren: boolean;
  },
  signal?: AbortSignal,
): Promise<MediaLibraryRule> {
  return api<MediaLibraryRule>('/api/media-library/rules', { method: 'PUT', json: body, signal });
}

export function deleteMediaLibraryRule(ruleId: string, signal?: AbortSignal): Promise<void> {
  return api<void>(`/api/media-library/rules/${ruleId}`, { method: 'DELETE', signal });
}

// Slice 3 — per-file media-library exclusion. Owner-safe aggregate counts only
// (never which ids). `requested` is post-deduplication; changed + unchanged +
// notFoundOrNotOwned always sum to it.
export interface MediaLibraryBulkResult {
  requested: number;
  changed: number;
  unchanged: number;
  notFoundOrNotOwned: number;
}

// Active → Excluded: move the selected files OUT of the media library. They
// stay normal, browsable files; only the media surfaces suppress them.
export function excludeFromMediaLibrary(
  fileIds: string[],
  signal?: AbortSignal,
): Promise<MediaLibraryBulkResult> {
  return api<MediaLibraryBulkResult>('/api/media-library/exclude', {
    method: 'POST',
    json: { fileIds },
    signal,
  });
}

// Excluded → Active: restore the selected files to the media library.
export function restoreToMediaLibrary(
  fileIds: string[],
  signal?: AbortSignal,
): Promise<MediaLibraryBulkResult> {
  return api<MediaLibraryBulkResult>('/api/media-library/restore', {
    method: 'POST',
    json: { fileIds },
    signal,
  });
}
