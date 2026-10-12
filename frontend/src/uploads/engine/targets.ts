// WHERE an upload goes. The engine moves bytes the same way whatever the
// destination; a target only says which address receives them and what the
// destination accepts. Adding a destination is adding a case here, never a
// second upload loop.

export interface FolderUploadTarget {
  kind: 'folder';
  // null = the root of the owner's files.
  folderId: string | null;
  // What the tray calls the destination ("Home", "Holiday 2026").
  label: string;
}

export interface AlbumUploadTarget {
  kind: 'album';
  albumId: string;
  // The album's name, as the tray shows it.
  label: string;
}

export type UploadTarget = FolderUploadTarget | AlbumUploadTarget;

/** Stable identity of a destination: same key, same place. */
export function targetKey(target: UploadTarget): string {
  return target.kind === 'album' ? `album:${target.albumId}` : `folder:${target.folderId ?? 'root'}`;
}

// The extensions a photo or video may arrive with when the browser names no
// type (HEIC from a Windows share, RAW from a card reader). Mirrors the
// server, which has the last word.
const MEDIA_EXTENSIONS = new Set([
  'jpg', 'jpeg', 'png', 'gif', 'webp', 'bmp', 'tif', 'tiff', 'heic', 'heif', 'avif', 'dng', 'svg',
  'mp4', 'm4v', 'mov', 'webm', 'mkv', 'avi', '3gp', 'mpg', 'mpeg', 'wmv',
]);

export function isMediaFile(file: File): boolean {
  if (file.type.startsWith('image/') || file.type.startsWith('video/')) return true;
  const dot = file.name.lastIndexOf('.');
  return dot > 0 && MEDIA_EXTENSIONS.has(file.name.slice(dot + 1).toLowerCase());
}

/** Whether a destination takes this file at all: an album holds only photos and videos. */
export function accepts(target: UploadTarget, file: File): boolean {
  return target.kind === 'folder' || isMediaFile(file);
}

export interface TargetEndpoints {
  // One multipart request carrying the whole file.
  multipartUrl: string;
  multipartFields(relativePath: string): Record<string, string>;
  // The resumable completion: the same ingest, fed from the assembled chunks.
  completeBody(relativePath: string): Record<string, unknown>;
}

export function endpointsFor(target: UploadTarget): TargetEndpoints {
  if (target.kind === 'album') {
    // The server files an album upload under "Album/<name>" itself, renames
    // on a clash and links bytes the uploader already has.
    const albumId = target.albumId;
    return {
      multipartUrl: `/api/albums/${encodeURIComponent(albumId)}/uploads`,
      multipartFields: () => ({}),
      completeBody: () => ({ albumId }),
    };
  }
  const folderId = target.folderId;
  return {
    multipartUrl: folderId === null ? '/api/files' : `/api/folders/${encodeURIComponent(folderId)}/files`,
    multipartFields: (relativePath) => ({
      // A taken name becomes "name (2).ext" instead of bouncing the file.
      conflict: 'rename',
      ...(relativePath.length > 0 ? { relativePath } : {}),
    }),
    completeBody: (relativePath) => ({
      parentFolderId: folderId,
      conflict: 'rename',
      ...(relativePath.length > 0 ? { relativePath } : {}),
    }),
  };
}
