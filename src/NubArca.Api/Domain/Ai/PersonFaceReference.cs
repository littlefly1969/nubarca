namespace NubArca.Api.Domain.Ai;

// One of a Person's 1..6 persistent REFERENCE faces for the active embedding
// profile — the multi-reference template similar-face search queries with.
//
// This is DERIVED, cacheable state: it only points at face detections the owner
// has already confirmed (PersonFaceAssignments stays authoritative), stores no
// embedding of its own, and an empty table is always valid. It is rebuilt lazily
// from the person's own confirmed assignments the first time a similar-face
// search asks for it, and replenished when a reference stops being eligible.
//
// Profile-scoped because a reference is only meaningful inside the embedding
// space it was chosen in: changing the active profile naturally yields a new
// (empty → lazily bootstrapped) reference set rather than a stale one.
public class PersonFaceReference
{
    public Guid Id { get; set; }

    public Guid OwnerUserId { get; set; }

    public Guid PersonId { get; set; }

    // The embedding profile this reference was selected in.
    public Guid ProfileId { get; set; }

    public Guid FaceDetectionId { get; set; }

    // Slot within the person's reference set, 0..MaxPersonReferenceFaces-1.
    // Unique per (owner, person, profile); holes are allowed after a reference is
    // invalidated and are refilled by the next replenishment.
    public int Ordinal { get; set; }

    public DateTime CreatedAt { get; set; }
}
