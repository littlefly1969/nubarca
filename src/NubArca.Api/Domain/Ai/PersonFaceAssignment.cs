namespace NubArca.Api.Domain.Ai;

// Owner-level, confirmed assignment of a blob-level FaceDetection to a Person.
// This is the authoritative "who is in this face" record (distinct from the
// algorithmic FaceCluster suggestion). Always owner-scoped; a face belongs to at
// most one person per owner (unique (OwnerUserId, FaceDetectionId)). User-confirmed
// assignments override auto suggestions.
public class PersonFaceAssignment
{
    public Guid Id { get; set; }

    public Guid OwnerUserId { get; set; }

    public Guid PersonId { get; set; }

    public Guid FaceDetectionId { get; set; }

    // One of PersonFaceAssignmentSources.
    public string Source { get; set; } = PersonFaceAssignmentSources.ClusterConfirmed;

    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

public static class PersonFaceAssignmentSources
{
    public const string UserConfirmed = "user_confirmed";
    public const string ClusterConfirmed = "cluster_confirmed";
    public const string ManualAdd = "manual_add";
}
