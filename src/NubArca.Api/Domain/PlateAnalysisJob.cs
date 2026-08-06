using NubArca.Api.Plates;

namespace NubArca.Api.Domain;

// Owner-private ALPR analysis record for one PlateImage. Created (Queued) when
// the owner requests analysis; a background job (JobTypes.PlatesAnalyze) drives
// it to Completed/Failed on the worker. This is domain metadata attached to a
// PlateImage only — it never touches Files/Gallery/People/Party/TV/Vault and
// creates no People/Face identity artifacts.
public class PlateAnalysisJob
{
    public Guid Id { get; set; }
    public Guid OwnerUserId { get; set; }
    public Guid PlateImageId { get; set; }

    // PlateAnalysisJobStatuses (queued/running/completed/failed/canceled).
    public string Status { get; set; } = PlateAnalysisJobStatuses.Queued;

    public DateTime RequestedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? FailedAt { get; set; }

    // Stable, client-safe failure code + message (PlateAnalysisErrorCodes).
    // NEVER a stack trace / model path / storage internal.
    public string? ErrorCode { get; set; }
    public string? ErrorMessageSafe { get; set; }

    // ALPR model profile key (e.g. plate-alpr-v1) — a label, never a path/secret.
    public string ProfileKey { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
