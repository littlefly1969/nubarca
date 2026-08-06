namespace NubArca.Api.Admin;

// Aggregate-only response for GET /api/admin/storage-stats. Every field is a
// number. The DTO deliberately omits every identifier (no GUIDs, no names,
// no paths, no tokens / hashes) so a leak of this body teaches the reader
// nothing about specific resources — only deployment-wide totals.
//
// Audience: MetadataAudience.AdminAggregate (see MetadataExposurePolicy).
// Per-file metadata, GPS, serials, raw metadata JSON, file paths, file ids,
// owner ids, tokens, and storage internals are out of scope for this DTO.
// If a future slice ever needs to surface metadata to admins, keep it
// aggregate-only (counts/buckets) — never per-file rows.
public sealed record StorageStatsResponse(
    UserStats Users,
    FolderStats Folders,
    FileStats Files,
    BlobStats Blobs,
    ImageStats Images,
    ShareLinkStats ShareLinks,
    AuditStats Audit,
    CleanupConfig Cleanup,
    // Slice 64 additions. Additive; every nested record carries aggregate
    // counters only — no ids, no names, no paths, no sensitive values.
    MediaStats Media,
    ExtractionStats Extraction,
    DerivativeStats Derivatives,
    UserMetadataStats UserMetadata,
    SensitiveAggregateStats SensitiveAggregates,
    // Slice 65 addition. Aggregate-only storage-quota posture; no per-user rows.
    QuotaStats Quota,
    // Slice 84 addition. Admin-only safe phase timings + cache info — no SQL
    // text, paths, or sensitive values. Lets an operator see which phase is
    // slow and whether the response came from the short-lived cache.
    StorageStatsDiagnostics Diagnostics,
    // Slice 96 addition. Physical PLACEMENT of derivative bytes relative to
    // the root the serving endpoints read from. The union-based blob counts
    // above stay clean when bytes sit in either root; this section is what
    // exposes the "row exists, bytes only in the original root, gallery
    // regenerates on every first view" state. Null when the physical scan
    // did not run. Counts only.
    DerivedReadinessStats? DerivedReadiness = null,
    // Slice 97 addition. LOGICAL reference integrity, distinct from the
    // physical checks above: BlobObject.ReferenceCount compared with the
    // actual owner rows (file_items + file_thumbnails). A leaked nonzero
    // refcount pins bytes forever (janitor-invisible); a zero refcount with
    // live owners risks the janitor deleting needed bytes. Null when the
    // on-demand integrity check did not run. Counts only.
    ReferenceIntegrityStats? ReferenceIntegrity = null,
    // Slice 99 addition. Explains WHY derivatives are missing: per size, how
    // many were never attempted vs failed (permanent / transient) vs skipped /
    // not-eligible, broken down by stable error code and detected format.
    // Turns the bare "ImagesMissingSmall = 85" above into an actionable
    // distribution after a backfill has attempted the missing files. Null when
    // the diagnostics service is unavailable. Aggregate counts only — no file
    // names, paths, ids, keys, or raw metadata.
    DerivativeDiagnosticsStats? DerivativeDiagnostics = null);

// Slice 99: per-size derivative-diagnostic distribution. NeverAttempted is
// derived (the size's missing count minus its recorded diagnostic rows), so
// the three top-level "missing" buckets partition cleanly into never-attempted
// vs the explicit failure/skip statuses. ByErrorCode / TopFormats are bounded,
// safe aggregates (stable codes + sniffed MIME types — never paths or names).
public sealed record DerivativeDiagnosticsStats(
    DerivativeDiagnosticSizeStats Small,
    DerivativeDiagnosticSizeStats Medium,
    DerivativeDiagnosticSizeStats Poster,
    DateTime? LastFailureAt);

public sealed record DerivativeDiagnosticSizeStats(
    int NeverAttempted,
    int Recorded,
    int FailedPermanent,
    int FailedTransient,
    int NotEligible,
    int Skipped,
    int Pending,
    int RetryableNow,
    DateTime? LastFailureAt,
    IReadOnlyList<DerivativeErrorCodeStat> ByErrorCode,
    IReadOnlyList<DerivativeFormatStat> TopFormats);

public sealed record DerivativeErrorCodeStat(string Code, int Count);

public sealed record DerivativeFormatStat(string DetectedContentType, int Count);

// Slice 97: see `storage blobs audit-references` — same computation.
public sealed record ReferenceIntegrityStats(
    int TotalBlobs,
    int RefcountMismatchCount,
    int OrphanedNonzeroRefcountCount,
    int ZeroRefWithRealReferencesCount);

// Slice 96: derived-readiness buckets. Checked = FileThumbnail rows examined;
// the three placement buckets partition it. OnlyInOriginalRoot > 0 means
// `media derivatives repair-bytes` can fix placement with a pure byte copy
// (no decode); MissingFromBoth requires regeneration.
public sealed record DerivedReadinessSizeStats(
    int Checked,
    int PresentInDerivedRoot,
    int OnlyInOriginalRoot,
    int MissingFromBoth);

public sealed record DerivedReadinessStats(
    int ThumbnailRowsTotal,
    int PresentInDerivedRoot,
    int OnlyInOriginalRoot,
    int MissingFromBoth,
    // Whether Storage:DerivedRootPath points at a distinct root. When false
    // the two roots coincide and OnlyInOriginalRoot is structurally 0.
    bool SplitRoots,
    DerivedReadinessSizeStats Small,
    DerivedReadinessSizeStats Medium,
    DerivedReadinessSizeStats Poster);

public sealed record StorageStatsDiagnostics(
    long TotalMillis,
    long CoreMillis,
    long PhysicalScanMillis,
    long DerivativeScanMillis,
    long MetadataAggregateMillis,
    bool Cached,
    DateTime ComputedAt,
    int AgeSeconds,
    // Slice 85b: whether the expensive physical blob-store scan ran for this
    // response. When false, the physical/missing/unreferenced blob counts are
    // "not computed" (-1) and the UI offers an on-demand integrity check.
    bool PhysicalScanIncluded);

public sealed record UserStats(
    int Total,
    int Active,
    int Disabled);

public sealed record FolderStats(
    int Total,
    int Active,
    int SoftDeleted);

public sealed record FileStats(
    int Total,
    int Active,
    int SoftDeleted,
    // Sum of FileItem.SizeBytes restricted to DeletedAt == null. This is the
    // user-visible "how much do I store" number.
    long LogicalBytesTotal,
    // Sum across every FileItem row, including soft-deleted ones that the
    // sweeper has not yet purged.
    long LogicalBytesIncludingTrash);

public sealed record BlobStats(
    int Total,
    int ZeroReference,
    // Subset of ZeroReference whose purge-eligibility timestamp is beyond the
    // configured BlobJanitor grace.
    // Useful for "do I have stuck blobs the janitor can't purge yet?"
    int ZeroReferenceBeyondGrace,
    // Sum of every BlobObject.SizeBytes — the on-disk footprint regardless of
    // whether each blob is currently referenced by any FileItem row.
    long PhysicalBytesTotal,
    // Slice 78: admin storage diagnostics — physical-file cross-check.
    // All counts only; no SHA, BlobObjectId, StorageKey, or physical paths.
    // ActiveFileItemCount: non-deleted FileItem rows with at least one owner.
    int ActiveFileItemCount,
    // UniqueReferencedBlobCount: distinct BlobObjects referenced by at least
    // one active FileItem. Dedup means this may be < ActiveFileItemCount.
    int UniqueReferencedBlobCount,
    // PhysicalBlobCount: objects found on disk in the storage root(s).
    // -1 when IBlobStorage is not available (e.g. test harness).
    int PhysicalBlobCount,
    // MissingPhysicalBlobCount: BlobObject rows whose on-disk file is absent.
    // > 0 indicates data loss; operator should restore from backup.
    int MissingPhysicalBlobCount,
    // UnreferencedPhysicalBlobCount: on-disk objects with no BlobObject row.
    // Normally cleared by BlobJanitor; > 0 after crashes or manual edits.
    int UnreferencedPhysicalBlobCount);

public sealed record ImageStats(
    // Active FileItem rows whose MimeType starts with "image/".
    int ImageFilesCount,
    // Active FileItem rows that successfully had dimensions detected.
    int FilesWithDimensionsCount,
    // FileThumbnail rows (one per file per size — currently only "small").
    int ThumbnailCount,
    // Sum of BlobObject.SizeBytes for blobs that back at least one thumbnail.
    long ThumbnailBlobBytes);

public sealed record ShareLinkStats(
    int Total,
    int Active,
    int Revoked,
    int Expired,
    int Exhausted);

public sealed record AuditStats(
    int Total);

public sealed record CleanupConfig(
    SweeperConfig FileItemSweeper,
    SweeperConfig BlobJanitor);

public sealed record SweeperConfig(
    bool Enabled,
    int IntervalMinutes,
    int GraceMinutes);

// Slice 64: counts by SERVER-DETECTED media category (BlobMetadata.MediaCategory),
// restricted to active FileItem rows. Sums across the five enumerated values
// approach Files.Active. `Other` includes the "unknown" fallback bucket that
// pre-metadata-model blobs occupy.
public sealed record MediaStats(
    int ImagesCount,
    int VideosCount,
    int AudioCount,
    int DocumentsCount,
    int OtherCount);

// Slice 64: BlobMetadata.ExtractionStatus + ExtractionErrorCode + ExtractionVersion
// distribution. One BlobMetadata row per blob — counts are blob-level, not
// file-level, so deduped uploads aren't double-counted. Error-code buckets
// only consider rows with a non-null code (typically Failed / Skipped).
public sealed record ExtractionStats(
    int Pending,
    int Completed,
    int Skipped,
    int Failed,
    int CurrentVersion,
    int AtCurrentVersion,
    int BelowCurrentVersion,
    int UnsupportedFormatErrors,
    int IoErrors,
    int UnexpectedErrors,
    int RawTruncatedErrors);

// Slice 64: derivative counts by Size. Missing counts are how many active
// owner-scoped FileItems of the matching media category don't yet have a
// FileThumbnail row of that Size — the same buckets the slice-63 prewarm
// CLI targets.
public sealed record DerivativeStats(
    int SmallThumbnailCount,
    int MediumPreviewCount,
    int VideoPosterCount,
    int ImagesMissingSmall,
    int ImagesMissingMedium,
    int VideosMissingPoster);

// Slice 64: aggregate counts over FileItemUserMetadata. Total rows + how
// many of each rich field are populated. No titles / descriptions / tag
// content / location strings ever leave this DTO — only counts.
public sealed record UserMetadataStats(
    int TotalRows,
    int WithTitle,
    int WithDescription,
    int WithTags,
    int WithRating,
    int Favorites,
    int WithDateTakenOverride,
    int WithLocationOverride);

// Slice 64: aggregate privacy posture. The point is to let an operator see
// how much sensitive metadata sits in the internal stores WITHOUT exposing
// any of it. GPS coordinates / serials / raw documents are NEVER read into
// the DTO — only presence counts.
public sealed record SensitiveAggregateStats(
    int BlobsWithGps,
    int BlobsWithRawDocument,
    int BlobsWithBodySerial,
    int BlobsWithLensSerial,
    int MetadataUpdates,
    int MetadataStripEvents);

// Slice 65: aggregate storage-quota posture. DefaultQuotaBytes is null when
// no per-user quota is configured (unlimited). UsersOverQuota counts users
// whose logical bytes already exceed the configured quota — 0 when unlimited.
// TotalLogicalBytes is the sum of every FileItem.SizeBytes (active + trash);
// no per-user rows, no names, no paths.
public sealed record QuotaStats(
    long? DefaultQuotaBytes,
    int UsersOverQuota,
    long TotalLogicalBytes);
