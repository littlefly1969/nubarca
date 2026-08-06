namespace NubArca.Api.Storage;

// Slice 78: governs the Kestrel request-body limit and ASP.NET Core
// FormOptions.MultipartBodyLengthLimit for all upload endpoints. Separate from
// Storage:MaxUploadBytes (which guards the in-stream app-layer ceiling during
// blob writing) because the Kestrel/IIS limits must be set at startup before
// the request body is read.
//
// Both default to 10 GiB — large enough for long video clips while keeping
// the server safe from unbounded uploads. Operators can lower them via .env
// (Uploads__MaxFileSizeBytes / Uploads__MaxRequestBodySizeBytes) without
// rebuilding.
public class UploadOptions
{
    public const string SectionName = "Uploads";

    // Maps to FormOptions.MultipartBodyLengthLimit (per-part limit).
    // Default 10 GiB.
    public long MaxFileSizeBytes { get; set; } = 10L * 1024 * 1024 * 1024;

    // Maps to KestrelServerLimits.MaxRequestBodySize (whole request limit).
    // Default 10 GiB. Set to 0 for unlimited (not recommended for public hosts).
    public long MaxRequestBodySizeBytes { get; set; } = 10L * 1024 * 1024 * 1024;
}
