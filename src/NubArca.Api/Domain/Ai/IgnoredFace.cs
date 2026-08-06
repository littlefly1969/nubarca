namespace NubArca.Api.Domain.Ai;

// Owner-level dismissal of a single blob-level FaceDetection: the owner has marked
// this face as "not a person I care about" (a mis-detection, a stranger, an
// object). Owner-scoped and reversible (delete the row to un-ignore). An ignored
// face is excluded from clustering candidates and from the "unassigned faces"
// view, so it stops resurfacing in suggestions. Distinct from an Ignored
// FaceCluster (a whole suggested group dismissed) — this is per-face.
public class IgnoredFace
{
    public Guid Id { get; set; }

    public Guid OwnerUserId { get; set; }

    public Guid FaceDetectionId { get; set; }

    public DateTime CreatedAt { get; set; }
}
