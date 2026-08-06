namespace NubArca.Api.Plates;

// Closed set of PlateImage lifecycle statuses. Stored as a lowercase string on
// PlateImage.Status (varchar(32)) so the enum can grow without a migration.
//
// This slice functionally uses ONLY `Uploaded`. The Analysis* values describe
// the intended future ALPR/OCR worker pipeline (slice 2) and are defined now so
// the model stays stable, but NO AI/analysis job is started in this slice.
public static class PlateImageStatuses
{
    public const string Uploaded = "uploaded";
    public const string AnalysisPending = "analysis_pending";
    public const string AnalysisRunning = "analysis_running";
    public const string AnalysisCompleted = "analysis_completed";
    public const string AnalysisFailed = "analysis_failed";
    public const string Deleted = "deleted";

    // The analysis status surfaced in the (safe) detail DTO before any analysis
    // pipeline exists. Kept separate from the row Status because it is a
    // product-facing summary code, not a lifecycle value.
    public const string AnalysisNotStarted = "not_started";
}
