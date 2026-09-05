namespace NubArca.Api.Party;

// Resolution of an anonymous guest's participant session on one party link.
// `NewRawToken` is non-null ONLY when a session was just minted, which is the
// single moment the raw token exists — the endpoint writes it to the guest's
// cookie and nothing else ever sees it again (the row stores only its hash).
public sealed record PartyParticipantResolution(Guid ParticipantId, string? NewRawToken);

// What one participant has used and may still use on one link. `max` values of
// 0 mean unlimited in the DOMAIN; the public DTO translates that to null so a
// client cannot mistake "no limit" for "no slots".
public sealed record PartyQuotaSnapshot(
    int MaxPhotos,
    int MaxVideos,
    int UsedPhotos,
    int UsedVideos);

// Server-issued, link-scoped identity for anonymous party guests, and the
// atomic quota claim built on it.
//
// The party upload token is shared by everyone at the party, so it cannot say
// who is uploading. This service supplies the missing identity WITHOUT
// fingerprinting: the server mints a random token, hands it back as a cookie,
// and stores only its hash. See PartyParticipant for why IP/User-Agent/
// client-supplied ids were all rejected.
public interface IPartyParticipantService
{
    // Idempotent: returns the existing session for `rawToken` when it resolves
    // on THIS link, otherwise mints a new one. A token from another party never
    // resolves here, so each link keeps independent counters.
    Task<PartyParticipantResolution> ResolveOrCreateAsync(
        Guid partyAlbumLinkId, string? rawToken, CancellationToken cancellationToken = default);

    // ATOMIC per-guest print claim, on the same principle as the upload slot:
    // one statement decides and records. `max` of 0 means the host set no
    // per-guest limit, so the claim always succeeds and only counts.
    Task<bool> TryClaimPrintAsync(
        Guid participantId, bool isStrip, int max, CancellationToken cancellationToken = default);

    // Give a claimed slot back when the sheet never happened.
    Task ReleasePrintAsync(
        Guid participantId, bool isStrip, int max, CancellationToken cancellationToken = default);

    Task<PartyQuotaSnapshot> GetQuotaAsync(
        Guid partyAlbumLinkId, Guid participantId, CancellationToken cancellationToken = default);

    // ATOMIC. Increments the counter for `isVideo` if and only if the quota
    // still allows it, in ONE conditional UPDATE — never read-then-write, which
    // would let two concurrent uploads both observe the last free slot and both
    // proceed. Returns false when the quota is already exhausted.
    //
    // Must be called inside the caller's transaction so a later failure
    // (membership, moderation row) rolls the counter back with it.
    Task<bool> TryClaimSlotAsync(
        Guid participantId, bool isVideo, int max, CancellationToken cancellationToken = default);

    Task<bool> TryClaimChallengeVoteAsync(
        Guid participantId, int max, CancellationToken cancellationToken = default);

    Task ReleaseChallengeVoteAsync(
        Guid participantId, CancellationToken cancellationToken = default);
}
