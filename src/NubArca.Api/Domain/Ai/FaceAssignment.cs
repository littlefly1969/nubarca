namespace NubArca.Api.Domain.Ai;

// Assigns a FaceDetection to a PersonGroup within one owner's library and ONE
// explicit face-embedding model space.
//
// FaceEmbeddingProfileId is an explicit column (not derived) so the
// one-assignment-per-(owner, face, model-space) rule is enforced directly by a
// database unique index — and so a v1->v2 face-embedding reindex can keep both
// clusterings side by side, with rollback being a default-profile flip rather
// than data deletion. PersonGroup.ProfileId and FaceAssignment.FaceEmbeddingProfileId
// are intended to always match (service-level invariant).
public class FaceAssignment
{
    public Guid Id { get; set; }

    public Guid OwnerUserId { get; set; }

    public Guid FaceDetectionId { get; set; }

    public Guid PersonGroupId { get; set; }

    // Explicit model-space. Same value as the assigned PersonGroup.ProfileId.
    public Guid FaceEmbeddingProfileId { get; set; }

    public double? Confidence { get; set; }

    // Owner curation state: "auto" | "confirmed" | "rejected".
    public string Source { get; set; } = "auto";

    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}
