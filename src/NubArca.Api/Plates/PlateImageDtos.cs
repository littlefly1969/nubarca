namespace NubArca.Api.Plates;

// Display-safe DTOs for the owner-private Plates surface. These deliberately
// expose NONE of: BlobObjectId, StorageKey, physical path, content hash/SHA,
// raw metadata JSON, model paths/internals, token/password hashes, OwnerUserId,
// the internal LogicalContainerKey, or PolygonJson. Media is reached only
// through the authenticated, owner-scoped URLs below (thumbnail/preview =
// derived; original = explicit authenticated endpoint).

public sealed record PlateImageListItem(
    Guid Id,
    string OriginalFileName,
    string ContentType,
    long SizeBytes,
    int? Width,
    int? Height,
    // Raw PlateImage lifecycle status (uploaded / analysis_pending / … ).
    string Status,
    // Product-facing analysis status (not_started / pending / running /
    // completed / failed) derived from Status.
    string AnalysisStatus,
    // Count of the latest accepted detections for this image.
    int PlatesCount,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    string ThumbnailUrl,
    string PreviewUrl);

public sealed record PlateImageDetail(
    Guid Id,
    string OriginalFileName,
    string ContentType,
    long SizeBytes,
    int? Width,
    int? Height,
    string Status,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    string PreviewUrl,
    string OriginalUrl,
    PlateAnalysisSummary AnalysisSummary,
    IReadOnlyList<PlateDetectionDto> Detections,
    // Safe, owner-only redaction summary. Carries NO face boxes/coordinates —
    // redaction is baked into the served media, so the UI never needs them.
    PlateRedactionInfo Redaction);

// Sanitized redaction summary for the owner. `Available` = server-side privacy
// redaction can be requested (feature enabled + a runnable detector).
// `FacesCount` is the number of face regions already detected+persisted for the
// current profile (0 until the owner first requests a redacted rendition —
// detail reads never run the detector). `ProfileKey` is a label only. No boxes,
// no identity, no internals.
public sealed record PlateRedactionInfo(
    bool Available,
    int FacesCount,
    string ProfileKey);

// Resolved source rendition bytes handed to the redaction pipeline (owner-scoped
// internal use only — never serialized). Bytes are a derived JPEG (thumbnail /
// preview) or the original; Width/Height are that rendition's pixel dimensions.
public sealed record PlateRedactionSource(byte[] Bytes, int Width, int Height);

// Result of resolving a redacted media rendition for serving: JPEG bytes + the
// fixed content type + the preserved pixel dimensions.
public sealed record PlateRedactedContent(byte[] Content, string ContentType, int Width, int Height);

// Sanitized analysis summary. FacesRedactedAvailable stays false until Slice 3
// implements server-side face redaction.
public sealed record PlateAnalysisSummary(
    int PlatesCount,
    bool FacesRedactedAvailable,
    string AnalysisStatus,
    Guid? LatestJobId,
    DateTime? LastAnalyzedAt);

// A normalized bounding box in [0..1] image-fraction space (matches FaceBoxDto).
public sealed record PlateBoxDto(double X, double Y, double Width, double Height);

// One detected plate exposed to the owner. No blob/model internals, no polygon.
public sealed record PlateDetectionDto(
    Guid Id,
    string Text,
    string NormalizedText,
    double Confidence,
    double PlateConfidence,
    double OcrConfidence,
    string? CountryHint,
    string? RegionHint,
    PlateBoxDto Bbox);

// Returned by POST analysis and GET analysis/latest. Job status is the domain
// PlateAnalysisJob status; ProfileKey is a label only.
public sealed record PlateAnalysisJobSummary(
    Guid Id,
    string Status,
    string AnalysisStatus,
    string ProfileKey,
    int PlatesCount,
    DateTime RequestedAt,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    DateTime? FailedAt,
    string? ErrorCode,
    DateTime? LastAnalyzedAt);

// Result of resolving derived (thumbnail/preview) bytes for serving: JPEG bytes
// plus the fixed content type. Original access uses PlateOriginalContent.
public sealed record PlateDerivativeContent(byte[] Content, string ContentType);

// Result of resolving the original image for serving. ContentType is the
// server-detected, allowlisted type; FileName is display-safe.
public sealed record PlateOriginalContent(Stream Content, string ContentType, string FileName);
