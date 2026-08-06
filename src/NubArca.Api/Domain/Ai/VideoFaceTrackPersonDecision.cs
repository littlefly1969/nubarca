namespace NubArca.Api.Domain.Ai;

// VFACE-02: ONE owner's identity decision about ONE canonical VideoFaceTrack.
//
// The canonical track stays pure blob-level EVIDENCE — no PersonId is ever added
// to it. Identity is a decision ABOUT that evidence, and decisions are
// owner-level: two owners whose libraries happen to share the same deduplicated
// blob decide independently, and neither can see or influence the other's row.
//
// Shape. The static-photo path models the same two outcomes as two tables
// (PersonFaceAssignment + IgnoredFace, both keyed on FaceDetectionId, both
// non-nullable). Neither can carry a track without becoming polymorphic through
// nullable columns, which is exactly what this slice must not do. So this is a
// dedicated entity, but it deliberately reuses the SAME identity system: the
// PersonId points at the ordinary owner-level Person the photo path already
// names, populates and archives. There is no second notion of a person.
//
// One row exists per (OwnerUserId, VideoFaceTrackId), and a MISSING row means
// UNDECIDED — suggestions are never persisted as decisions, so absence always
// means "the owner has not said anything about this track yet".
//
// Duplicate FileItems of one owner pointing at the same blob share this single
// decision by construction: the row is keyed by track, not by file.
public class VideoFaceTrackPersonDecision
{
    public Guid Id { get; set; }

    public Guid OwnerUserId { get; set; }

    public Guid VideoFaceTrackId { get; set; }

    // The owner's own Person. Required when Decision is `assigned`, null when
    // `ignored`. A database check constraint enforces the pairing, and a
    // composite foreign key enforces that the person belongs to THIS owner —
    // a cross-owner assignment is not merely rejected by the service, it is
    // unrepresentable.
    public Guid? PersonId { get; set; }

    // One of VideoFaceTrackDecisions.
    public string Decision { get; set; } = VideoFaceTrackDecisions.Assigned;

    // One of VideoFaceTrackDecisionSources. Automated processing NEVER writes a
    // row here: model output is a suggestion, and only a user action becomes a
    // decision.
    public string Source { get; set; } = VideoFaceTrackDecisionSources.User;

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }

    // When the current `assigned` outcome was established. Null for `ignored`.
    public DateTime? ConfirmedAt { get; set; }
}

// The closed set of owner decisions about a canonical track. Absence of a row is
// the third state (undecided) and is deliberately not a value here — exactly the
// sparse rule BlobAiArtifactStatus and VideoSemanticIndex already follow.
public static class VideoFaceTrackDecisions
{
    // The owner confirmed this track shows a specific Person of theirs.
    public const string Assigned = "assigned";

    // The owner dismissed the track (a stranger, a mis-detection, someone they
    // do not want to organise). It stops surfacing in the review queue.
    public const string Ignored = "ignored";

    public static bool IsKnown(string? value)
        => value is Assigned or Ignored;
}

// How a decision came to exist. Both values are explicit human intent: `imported`
// is reserved for a future owner-initiated migration of their own decisions, and
// is NEVER written by a model.
public static class VideoFaceTrackDecisionSources
{
    public const string User = "user";
    public const string Imported = "imported";
}
