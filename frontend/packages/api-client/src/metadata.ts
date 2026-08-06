import { api } from './client';

// Mirrors NubArca.Api.Metadata.FileMetadataResponse and its nested records.
// Curated, safe fields only — the backend never returns raw embedded metadata,
// GPS coordinates, serial numbers, StorageKey, physical paths, SHA-256, or
// BlobObjectId. `hasGps` is a boolean presence flag with no coordinates.

export interface EmbeddedImageMetadata {
  dateTaken: string | null;
  dateTakenSource: string | null;
  orientation: number | null;
  cameraMake: string | null;
  cameraModel: string | null;
  lensModel: string | null;
  iso: number | null;
  aperture: number | null;
  exposureTime: string | null;
  focalLength: number | null;
  colorSpace: string | null;
  hasGps: boolean;
}

// Curated, safe subset of probed video metadata (ffprobe). Present only when
// the video probe completed. Dimensions come from the Blob block's width/height.
export interface VideoMetadata {
  durationSeconds: number | null;
  videoCodec: string | null;
  audioCodec: string | null;
  frameRate: number | null;
  videoBitrate: number | null;
  hasAudio: boolean;
  audioChannels: number | null;
  audioSampleRate: number | null;
  rotation: number | null;
}

export interface BlobDerivedMetadata {
  mediaCategory: string;
  detectedContentType: string | null;
  detectedFormat: string | null;
  width: number | null;
  height: number | null;
  pixelCount: number | null;
  thumbnailStatus: string;
  extractionStatus: string;
  embedded: EmbeddedImageMetadata | null;
  video: VideoMetadata | null;
}

export interface UserMetadataView {
  title: string | null;
  description: string | null;
  tags: string[];
  rating: number | null;
  favorite: boolean;
  dateTakenOverride: string | null;
  locationOverride: string | null;
}

// Resolved metadata layer (slice 56). DateTaken precedence: user override →
// embedded DateTaken → upload time. DisplayName: user title → file name.
// Location: user override only (embedded GPS coordinates stay internal).
export interface EffectiveMetadata {
  displayName: string;
  dateTaken: string;
  dateTakenSource: 'user' | 'embedded' | 'uploaded';
  location: string | null;
}

export interface FileMetadata {
  id: string;
  name: string;
  mimeType: string;
  sizeBytes: number;
  createdAt: string;
  updatedAt: string | null;
  blob: BlobDerivedMetadata;
  user: UserMetadataView;
  effective: EffectiveMetadata;
}

// Editable user-metadata fields (slice 56). Mirrors
// NubArca.Api.Metadata.UpdateFileMetadataRequest. Full-replace semantics:
// every field listed here is set to the value provided, and omitting one is
// equivalent to clearing it. The frontend always loads the current document
// first and sends every field on save, so an edit never silently clears
// neighbouring fields.
export interface UpdateFileMetadataRequest {
  title?: string | null;
  description?: string | null;
  tags?: string[] | null;
  rating?: number | null;
  favorite?: boolean | null;
  dateTakenOverride?: string | null;
  locationOverride?: string | null;
}

// Owner-scoped; 404 for missing/foreign/soft-deleted files.
export function getFileMetadata(fileId: string, signal?: AbortSignal): Promise<FileMetadata> {
  return api<FileMetadata>(`/api/files/${fileId}/metadata`, { signal });
}

// Owner-scoped edit of user metadata only. Never mutates blob bytes or
// blob-derived metadata. 400 on validation failure, 404 on missing/foreign,
// 401 unauthenticated. Returns the recomputed effective metadata.
export function updateFileMetadata(
  fileId: string,
  patch: UpdateFileMetadataRequest,
  signal?: AbortSignal,
): Promise<FileMetadata> {
  return api<FileMetadata>(`/api/files/${fileId}/metadata`, {
    method: 'PATCH',
    json: patch,
    signal,
  });
}

// Slice 58: strong metadata mutation. Strips embedded metadata
// (EXIF/IPTC/XMP/ICC/PNG text chunks) from the file by re-encoding the bytes
// into a NEW blob. The original blob is never modified — other files sharing
// it remain unchanged. User metadata is preserved. 404 missing/foreign,
// 415 non-image / unsupported format, 401 unauthenticated.
export function stripFileMetadata(
  fileId: string,
  signal?: AbortSignal,
): Promise<FileMetadata> {
  return api<FileMetadata>(`/api/files/${fileId}/metadata/strip-embedded`, {
    method: 'POST',
    signal,
  });
}

// Slice 66: strong metadata mutation. Bakes the user's DateTaken override
// into the image bytes (JPEG EXIF) by creating/reusing a NEW blob. The
// original blob and other files sharing it are unchanged; user metadata is
// preserved. 400 when no DateTaken override is set, 404 missing/foreign,
// 415 unsupported format, 401 unauthenticated.
export function writeFileDateTaken(
  fileId: string,
  signal?: AbortSignal,
): Promise<FileMetadata> {
  return api<FileMetadata>(`/api/files/${fileId}/metadata/write-datetaken`, {
    method: 'POST',
    signal,
  });
}

// Slice 66: relative URL for the privacy-safe (metadata-stripped) download.
// Streamed by the backend on the fly; the FileItem is never mutated. Used as
// an <a href> target so the browser's cookie is sent same-origin.
export function privacySafeDownloadUrl(fileId: string): string {
  return `/api/files/${fileId}/content/privacy-safe`;
}

// Relative URL for downloading the IMMUTABLE ORIGINAL bytes.
//
// GET /api/files/{id}/content opens the file's own content-addressed
// BlobObject and streams it verbatim as an attachment carrying the original
// file name. It is deliberately NOT any of the derived artifacts:
//   * /thumbnail        → small derivative (grid)
//   * /preview          → medium derivative (viewer)
//   * /poster           → video poster frame
//   * /content/privacy-safe → re-encoded, metadata-stripped copy
//
// Named helper (rather than an inline template string at each call site) so the
// "original, not a derivative" decision is stated in exactly one place and can
// be asserted by a regression test. Used as an <a href> target so the browser's
// same-origin cookie authenticates it; the backend re-checks ownership and
// audits every download.
export function originalDownloadUrl(fileId: string): string {
  return `/api/files/${fileId}/content`;
}
