namespace NubArca.Api.Domain.Ai;

// Owner + profile-scoped algorithmic face grouping (a "suggested group"). ALWAYS
// scoped to one owner AND one face-embedding profile (model space) — never global,
// never cross-owner. The clustering job produces these from the owner's visible,
// non-vault faces; the owner then names one (→ Person) or ignores it.
//
// RepresentativeFaceDetectionId is a soft reference (no FK) to a blob-level
// FaceDetection chosen as the cluster's cover face. A cluster becomes linked to a
// Person (PersonId set, Status=Confirmed) when the owner names it.
public class FaceCluster
{
    public Guid Id { get; set; }

    public Guid OwnerUserId { get; set; }

    public Guid ProfileId { get; set; }

    // Cover face (blob-level FaceDetection.Id). Soft reference; nullable.
    public Guid? RepresentativeFaceDetectionId { get; set; }

    // One of FaceClusterStatuses.
    public string Status { get; set; } = FaceClusterStatuses.Suggested;

    // Aggregate cohesion/quality signal ([0..1]); nullable until computed.
    public double? ConfidenceAggregate { get; set; }

    // Number of member faces at last (re)cluster — a denormalized convenience for
    // suggestion ordering; authoritative count is the member rows.
    public int MemberCount { get; set; }

    // Set when the owner names this group; links the cluster to its Person.
    public Guid? PersonId { get; set; }

    // Opaque clustering-run label. Nullable.
    public string? ClusterKey { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

public static class FaceClusterStatuses
{
    // Auto-suggested, awaiting owner action.
    public const string Suggested = "suggested";

    // Owner named it → linked to a Person.
    public const string Confirmed = "confirmed";

    // Owner dismissed it; never re-suggested.
    public const string Ignored = "ignored";

    // Small/low-cohesion group surfaced under the Review tab.
    public const string NeedsReview = "needs_review";
}
