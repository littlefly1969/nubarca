import { api } from './client';

// Mirrors NubArca.Api.Admin.StorageStatsResponse on the backend. The
// types are aggregate counters only — there are no ids, names, paths, or
// tokens anywhere in this shape, matching the no-leak contract for
// `GET /api/admin/storage-stats`.

export interface UserStats {
  total: number;
  active: number;
  disabled: number;
}

export interface FolderStats {
  total: number;
  active: number;
  softDeleted: number;
}

export interface FileStats {
  total: number;
  active: number;
  softDeleted: number;
  logicalBytesTotal: number;
  logicalBytesIncludingTrash: number;
}

export interface BlobStats {
  total: number;
  zeroReference: number;
  zeroReferenceBeyondGrace: number;
  physicalBytesTotal: number;
  // Slice 85b: physical integrity cross-check. -1 / absent = not computed
  // (the expensive scan is opt-in via the integrity-check button).
  physicalBlobCount?: number;
  missingPhysicalBlobCount?: number;
  unreferencedPhysicalBlobCount?: number;
}

export interface ImageStats {
  imageFilesCount: number;
  filesWithDimensionsCount: number;
  thumbnailCount: number;
  thumbnailBlobBytes: number;
}

export interface ShareLinkStats {
  total: number;
  active: number;
  revoked: number;
  expired: number;
  exhausted: number;
}

export interface AuditStats {
  total: number;
}

export interface SweeperConfig {
  enabled: boolean;
  intervalMinutes: number;
  graceMinutes: number;
}

export interface CleanupConfig {
  fileItemSweeper: SweeperConfig;
  blobJanitor: SweeperConfig;
}

// Slice 64 additive blocks. Every nested record carries aggregate counters
// only — no ids, no names, no paths, no sensitive values.
export interface MediaStats {
  imagesCount: number;
  videosCount: number;
  audioCount: number;
  documentsCount: number;
  otherCount: number;
}

export interface ExtractionStats {
  pending: number;
  completed: number;
  skipped: number;
  failed: number;
  currentVersion: number;
  atCurrentVersion: number;
  belowCurrentVersion: number;
  unsupportedFormatErrors: number;
  ioErrors: number;
  unexpectedErrors: number;
  rawTruncatedErrors: number;
}

export interface DerivativeStats {
  smallThumbnailCount: number;
  mediumPreviewCount: number;
  videoPosterCount: number;
  imagesMissingSmall: number;
  imagesMissingMedium: number;
  videosMissingPoster: number;
}

export interface UserMetadataStats {
  totalRows: number;
  withTitle: number;
  withDescription: number;
  withTags: number;
  withRating: number;
  favorites: number;
  withDateTakenOverride: number;
  withLocationOverride: number;
}

export interface SensitiveAggregateStats {
  // Presence counts; coordinates / serials / raw documents are never
  // exposed by this DTO.
  blobsWithGps: number;
  blobsWithRawDocument: number;
  blobsWithBodySerial: number;
  blobsWithLensSerial: number;
  metadataUpdates: number;
  metadataStripEvents: number;
}

export interface StorageStats {
  users: UserStats;
  folders: FolderStats;
  files: FileStats;
  blobs: BlobStats;
  images: ImageStats;
  shareLinks: ShareLinkStats;
  audit: AuditStats;
  cleanup: CleanupConfig;
  media: MediaStats;
  extraction: ExtractionStats;
  derivatives: DerivativeStats;
  userMetadata: UserMetadataStats;
  sensitiveAggregates: SensitiveAggregateStats;
  diagnostics?: StorageStatsDiagnostics;
  // Slice 96: physical placement of derivative bytes vs the derived root the
  // serving endpoints read from. Present only when the physical scan ran.
  derivedReadiness?: DerivedReadinessStats | null;
  // Slice 97: logical refcount integrity (BlobObject.ReferenceCount vs real
  // owner rows). Present only when the on-demand integrity check ran.
  referenceIntegrity?: ReferenceIntegrityStats | null;
  // Slice 99: explains the "images missing small/medium" / "videos missing
  // poster" counts above — per size, how many were never attempted vs failed
  // (permanent/transient) vs skipped/not-eligible, with a code/format
  // breakdown. Present once the diagnostics service is available.
  derivativeDiagnostics?: DerivativeDiagnosticsStats | null;
}

// Slice 99: aggregate-only. NeverAttempted is missing − recorded; the rest are
// direct status counts. byErrorCode / topFormats are bounded safe aggregates
// (stable codes + sniffed MIME types — never paths, names, ids, or keys).
export interface DerivativeErrorCodeStat {
  code: string;
  count: number;
}

export interface DerivativeFormatStat {
  detectedContentType: string;
  count: number;
}

export interface DerivativeDiagnosticSizeStats {
  neverAttempted: number;
  recorded: number;
  failedPermanent: number;
  failedTransient: number;
  notEligible: number;
  skipped: number;
  pending: number;
  retryableNow: number;
  lastFailureAt?: string | null;
  byErrorCode: DerivativeErrorCodeStat[];
  topFormats: DerivativeFormatStat[];
}

export interface DerivativeDiagnosticsStats {
  small: DerivativeDiagnosticSizeStats;
  medium: DerivativeDiagnosticSizeStats;
  poster: DerivativeDiagnosticSizeStats;
  lastFailureAt?: string | null;
}

// Slice 97: mismatches mean the derived accounting drifted from the owner
// tables (crash between refcount commit and owner commit). Fix with
// `storage blobs repair-references`.
export interface ReferenceIntegrityStats {
  totalBlobs: number;
  refcountMismatchCount: number;
  orphanedNonzeroRefcountCount: number;
  zeroRefWithRealReferencesCount: number;
}

// Slice 96: the union-based integrity counts treat bytes in either root as
// present; this section reports whether derivative bytes are in the DERIVED
// root specifically (onlyInOriginalRoot > 0 = the gallery would silently
// regenerate on first view; fixable with `media derivatives repair-bytes`).
export interface DerivedReadinessSizeStats {
  checked: number;
  presentInDerivedRoot: number;
  onlyInOriginalRoot: number;
  missingFromBoth: number;
}

export interface DerivedReadinessStats {
  thumbnailRowsTotal: number;
  presentInDerivedRoot: number;
  onlyInOriginalRoot: number;
  missingFromBoth: number;
  splitRoots: boolean;
  small: DerivedReadinessSizeStats;
  medium: DerivedReadinessSizeStats;
  poster: DerivedReadinessSizeStats;
}

// Slice 84: admin-only safe phase timings + cache info. Optional so older
// responses/mocks stay valid.
export interface StorageStatsDiagnostics {
  totalMillis: number;
  coreMillis: number;
  physicalScanMillis: number;
  derivativeScanMillis: number;
  metadataAggregateMillis: number;
  cached: boolean;
  computedAt: string;
  ageSeconds: number;
  physicalScanIncluded: boolean;
}

// Admin-only on the backend; the frontend hides the link for non-admin
// users but the server is still the source of truth (401 unauth / 403 not
// admin / 200 admin). `refresh` bypasses the short-lived server cache;
// `includePhysicalScan` runs the expensive blob-store filesystem walk
// (off for fast dashboard loads, on for the on-demand integrity check).
export function getStorageStats(
  refresh = false,
  includePhysicalScan = false,
  signal?: AbortSignal,
): Promise<StorageStats> {
  const params = new URLSearchParams();
  if (refresh) params.set('refresh', 'true');
  // physical defaults to true server-side, so always send the explicit value.
  params.set('physical', includePhysicalScan ? 'true' : 'false');
  return api<StorageStats>(`/api/admin/storage-stats?${params.toString()}`, { signal });
}

export interface AdminMediumPreviewJob {
  jobId: string;
  status: string;
  progressCurrent: number | null;
  progressTotal: number | null;
  progressMessage: string | null;
  createdAt: string;
  updatedAt: string;
  completedAt: string | null;
}

export interface AdminMediumPreviewStatus {
  mediumPreviewMaxEdge: number;
  job: AdminMediumPreviewJob | null;
}

export interface AdminMediumPreviewRebuildResponse {
  jobId: string;
  status: string;
  mediumPreviewMaxEdge: number;
}

export function getMediumPreviewStatus(signal?: AbortSignal): Promise<AdminMediumPreviewStatus> {
  return api<AdminMediumPreviewStatus>('/api/admin/media/previews/medium/status', { signal });
}

export function rebuildMediumPreviews(signal?: AbortSignal): Promise<AdminMediumPreviewRebuildResponse> {
  return api<AdminMediumPreviewRebuildResponse>(
    '/api/admin/media/previews/medium/rebuild',
    { method: 'POST', signal },
  );
}
