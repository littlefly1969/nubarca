using NubArca.Api.Domain;

namespace NubArca.Api.Metadata;

// Normalized, typed output of embedded image metadata extraction, plus the
// internal raw structured document. Produced by IEmbeddedMetadataExtractor and
// mapped onto a BlobMetadata row by the upload path.
//
// Sensitivity note: GpsLatitude/GpsLongitude/GpsAltitude and the *SerialNumber
// fields are SENSITIVE. They may be persisted internally (exhaustive
// extraction is the goal) but must NEVER be surfaced through a normal DTO —
// that gate lives in the metadata service / response shaping, and the privacy
// slice (57) governs any opt-in exposure.
public sealed record ImageMetadataExtractionResult
{
    // One of MetadataStatuses.{Completed,Skipped,Failed} (Skipped doubles as
    // "unsupported" — the format could not be parsed).
    public required string Status { get; init; }

    // One of MetadataErrorCodes.* or null.
    public string? ErrorCode { get; init; }

    // Bumped when extraction logic changes so a future backfill can find rows
    // produced by an older extractor.
    public int Version { get; init; }

    // Internal structured document (JSON object keyed by directory → tag →
    // description). Internal only — never serialized to a normal DTO.
    public string? RawMetadataJson { get; init; }

    // --- date/time ---
    public DateTime? DateTaken { get; init; }            // normalized, stored as UTC-nominal
    public string? DateTakenSource { get; init; }        // which EXIF tag it came from
    public string? DateTakenOffset { get; init; }        // tz offset string if present

    // --- orientation ---
    public int? Orientation { get; init; }               // EXIF 1..8

    // --- camera / device ---
    public string? CameraMake { get; init; }
    public string? CameraModel { get; init; }
    public string? LensMake { get; init; }
    public string? LensModel { get; init; }
    public string? Software { get; init; }               // sensitive-ish: not in DTO
    public string? BodySerialNumber { get; init; }       // SENSITIVE: not in DTO
    public string? LensSerialNumber { get; init; }       // SENSITIVE: not in DTO

    // --- capture settings ---
    public int? IsoSpeed { get; init; }
    public double? FNumber { get; init; }                // aperture
    public string? ExposureTime { get; init; }           // e.g. "1/250 sec"
    public double? FocalLength { get; init; }            // mm
    public int? FocalLength35mm { get; init; }
    public string? ExposureBias { get; init; }
    public string? ExposureProgram { get; init; }
    public string? MeteringMode { get; init; }
    public string? Flash { get; init; }
    public string? WhiteBalance { get; init; }

    // --- color / image ---
    public string? ColorSpace { get; init; }
    public bool HasIccProfile { get; init; }
    public string? IccProfileName { get; init; }

    // --- gps (SENSITIVE: stored internally, never in a normal DTO) ---
    public double? GpsLatitude { get; init; }
    public double? GpsLongitude { get; init; }
    public double? GpsAltitude { get; init; }

    public static ImageMetadataExtractionResult ForStatus(string status, string? errorCode, int version)
        => new() { Status = status, ErrorCode = errorCode, Version = version };
}
