namespace NubArca.Api.Plates;

// Lifecycle of a domain PlateAnalysisJob (the owner-private ALPR analysis record
// for a single PlateImage). Distinct from the generic BackgroundJob status: the
// BackgroundJob is the queue mechanism; PlateAnalysisJob is the domain outcome.
// Stored lowercase on PlateAnalysisJob.Status (varchar(32)).
public static class PlateAnalysisJobStatuses
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Canceled = "canceled";

    public static bool IsActive(string? status) =>
        string.Equals(status, Queued, StringComparison.Ordinal)
        || string.Equals(status, Running, StringComparison.Ordinal);
}

// Stable, client-safe error codes persisted on a failed PlateAnalysisJob. NEVER
// a stack trace, model path, connection string, or blob/storage internal.
public static class PlateAnalysisErrorCodes
{
    public const string ModelNotConfigured = "model_not_configured";
    public const string ImageTooLarge = "image_too_large";
    public const string UnsupportedImage = "unsupported_image";
    public const string AnalysisFailed = "analysis_failed";
    public const string OcrFailed = "ocr_failed";
    public const string DetectorFailed = "detector_failed";
    public const string ImageNotFound = "image_not_found";

    // Slice 4 ONNX provider codes. Missing/incompatible model files are an
    // environment/config state — never a content failure and never a path leak.
    public const string AlprDisabled = "plate_alpr_disabled";
    public const string DetectorModelMissing = "plate_detector_model_missing";
    public const string OcrModelMissing = "plate_ocr_model_missing";
    public const string DetectorModelLoadFailed = "plate_detector_model_load_failed";
    public const string OcrModelLoadFailed = "plate_ocr_model_load_failed";
    public const string DetectorOutputUnsupported = "plate_detector_output_unsupported";
    public const string OcrOutputUnsupported = "plate_ocr_output_unsupported";
    public const string InferenceFailed = "plate_inference_failed";
}

// Product-facing analysis status surfaced in DTOs (derived from PlateImage.Status).
public static class PlateAnalysisProductStatus
{
    public const string NotStarted = "not_started";
    public const string Pending = "pending";
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Failed = "failed";

    public static string FromPlateImageStatus(string? plateImageStatus) => plateImageStatus switch
    {
        PlateImageStatuses.AnalysisPending => Pending,
        PlateImageStatuses.AnalysisRunning => Running,
        PlateImageStatuses.AnalysisCompleted => Completed,
        PlateImageStatuses.AnalysisFailed => Failed,
        _ => NotStarted,
    };
}
