using NubArca.Api.Domain;

namespace NubArca.Api.Plates;

// Pure mapping helpers from analysis entities to display-safe DTOs. Centralised
// so the read-side (PlateImageService) and the analysis service produce
// identical, sanitized shapes (no blob/model internals, no PolygonJson).
internal static class PlateAnalysisMapping
{
    public static PlateDetectionDto ToDto(PlateDetection d) => new(
        d.Id,
        d.Text,
        d.NormalizedText,
        Math.Round(d.CombinedConfidence, 3),
        Math.Round(d.PlateConfidence, 3),
        Math.Round(d.OcrConfidence, 3),
        d.CountryHint,
        d.RegionHint,
        new PlateBoxDto(
            Math.Round(d.BoundingBoxX, 6),
            Math.Round(d.BoundingBoxY, 6),
            Math.Round(d.BoundingBoxWidth, 6),
            Math.Round(d.BoundingBoxHeight, 6)));

    public static PlateAnalysisSummary ToSummary(
        string plateImageStatus, int platesCount, Guid? latestJobId, DateTime? lastAnalyzedAt) => new(
        PlatesCount: platesCount,
        FacesRedactedAvailable: false,
        AnalysisStatus: PlateAnalysisProductStatus.FromPlateImageStatus(plateImageStatus),
        LatestJobId: latestJobId,
        LastAnalyzedAt: lastAnalyzedAt);

    public static PlateAnalysisJobSummary ToJobSummary(
        PlateAnalysisJob job, string plateImageStatus, int platesCount, DateTime? lastAnalyzedAt) => new(
        Id: job.Id,
        Status: job.Status,
        AnalysisStatus: PlateAnalysisProductStatus.FromPlateImageStatus(plateImageStatus),
        ProfileKey: job.ProfileKey,
        PlatesCount: platesCount,
        RequestedAt: job.RequestedAt,
        StartedAt: job.StartedAt,
        CompletedAt: job.CompletedAt,
        FailedAt: job.FailedAt,
        ErrorCode: job.ErrorCode,
        LastAnalyzedAt: lastAnalyzedAt);
}
