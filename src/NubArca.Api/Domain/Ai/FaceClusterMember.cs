namespace NubArca.Api.Domain.Ai;

// Membership of a blob-level FaceDetection in an owner's FaceCluster. Owner scope
// is inherited from the parent cluster. A face appears in at most one cluster per
// owner (enforced by a unique index on FaceDetectionId within the owner's cluster
// set at the service layer + a unique (FaceClusterId, FaceDetectionId)).
public class FaceClusterMember
{
    public Guid Id { get; set; }

    public Guid FaceClusterId { get; set; }

    public Guid FaceDetectionId { get; set; }

    // Cosine similarity to the cluster representative ([0..1]); nullable.
    public double? SimilarityScore { get; set; }

    // One of FaceClusterMemberSources.
    public string MembershipSource { get; set; } = FaceClusterMemberSources.AutoCluster;

    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

public static class FaceClusterMemberSources
{
    public const string AutoCluster = "auto_cluster";
    public const string UserConfirmed = "user_confirmed";
    public const string UserRejected = "user_rejected";
    public const string ManualAdd = "manual_add";
}
