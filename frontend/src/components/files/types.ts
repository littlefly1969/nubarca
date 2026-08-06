// Shared types + tiny pure helpers for the Files UI v2. Kept dependency-free so
// they can be unit-tested in isolation and imported by every files/* component
// without circular imports.
import type { FileSummary, FolderSummary } from '@nubarca/api-client';
import { looksLikeVideo } from '../VideoModal';

export type { DirectorySortField, SortDirection } from '@nubarca/api-client';

export type ViewMode = 'grid' | 'list';

// A directory entry is either a folder or a file. Folders always sort before
// files in the listing (the backend returns them as a separate ordered set).
export type Entry =
  | { kind: 'folder'; id: string; folder: FolderSummary }
  | { kind: 'file'; id: string; file: FileSummary };

export type MediaKind = 'image' | 'video' | null;

// Selection keys namespace folder/file ids so a folder and a file that happen
// to share an id (they never do, but defence-in-depth) can't collide.
export function folderKey(id: string): string {
  return `folder:${id}`;
}

export function fileKey(id: string): string {
  return `file:${id}`;
}

export function entryKey(entry: Entry): string {
  return entry.kind === 'folder' ? folderKey(entry.id) : fileKey(entry.id);
}

// Builds the flat, render-ordered entry list (folders first, then files) used
// for rendering, range selection, and viewer indexing.
export function toEntries(
  folders: readonly FolderSummary[],
  files: readonly FileSummary[],
): Entry[] {
  const out: Entry[] = [];
  for (const folder of folders) out.push({ kind: 'folder', id: folder.id, folder });
  for (const file of files) out.push({ kind: 'file', id: file.id, file });
  return out;
}

export function isImage(mimeType: string): boolean {
  return mimeType.toLowerCase().startsWith('image/');
}

// Classifies a file as viewable media. Images are detected by MIME; videos by
// MIME OR the name heuristic (the backend /video endpoint is the real gate).
export function mediaKindOf(file: FileSummary): MediaKind {
  if (isImage(file.mimeType)) return 'image';
  if (file.mimeType.toLowerCase().startsWith('video/') || looksLikeVideo(file.name)) {
    return 'video';
  }
  return null;
}

// Small thumbnail for grid/list (never the original, never the medium preview).
export function smallThumbnailUrl(fileId: string): string {
  return `/api/files/${fileId}/thumbnail?size=small`;
}

// Medium preview for lightbox/viewer (never the original full-res bytes).
export function mediumPreviewUrl(fileId: string): string {
  return `/api/files/${fileId}/preview`;
}

export function videoPosterUrl(fileId: string): string {
  return `/api/files/${fileId}/poster`;
}

export function downloadUrl(fileId: string): string {
  return `/api/files/${fileId}/content`;
}

// A short, human label for a file's type, derived from its MIME. Never exposes
// any storage internals — MIME is already part of every listing DTO.
export function typeLabel(mimeType: string): string {
  const mime = mimeType.toLowerCase();
  if (mime === 'application/pdf') return 'PDF';
  const slash = mime.indexOf('/');
  if (slash === -1) return mime || 'file';
  const major = mime.slice(0, slash);
  const minor = mime.slice(slash + 1);
  if (major === 'image' || major === 'video' || major === 'audio' || major === 'text') {
    return `${major.charAt(0).toUpperCase()}${major.slice(1)}`;
  }
  return minor.split(/[.+]/).pop()?.toUpperCase() ?? major;
}
