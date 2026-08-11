namespace NubArca.Api.Ai.Jobs;

// The payload of ONE owner's face-clustering run (JobTypes.AiFacesClusterOwner).
//
// Deliberately not an AiBackfillJobPayload with an extra field. That record is
// shared by every AI backfill and means "flags for a job that decides its own
// scope"; adding an owner to it would make the owner OPTIONAL everywhere and
// make "whose faces does this job touch" a question you answer by reading a
// handler. Here it is the type.
//
// OwnerUserId is written EXCLUSIVELY server-side from the authenticated caller.
// It never arrives from a request body, and the status endpoint re-reads it to
// decide whether the caller may see the job at all — so the owner boundary is
// carried by the job itself rather than by whoever happens to ask about it.
//
// ProfileKey is the stable face-embedding profile key resolved at enqueue time,
// matching the rest of the AI job contract (a key, never a GUID): a job that
// outlived a profile edit must fail to resolve rather than silently cluster
// against a different model.
public sealed record FaceOwnerClusterJobPayload(
    Guid OwnerUserId,
    string ProfileKey);
