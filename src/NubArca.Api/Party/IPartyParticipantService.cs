namespace NubArca.Api.Party;

// Resolution of an anonymous guest's participant session on one party link.
//
// `AdoptedLegacy` reports that a pre-migration, capability-scoped session was
// carried into the browser's single identity for this party — either taken over
// or folded in. It exists so the migration is observable in a test rather than
// inferred from counters that happen to look right.
public sealed record PartyParticipantResolution(Guid ParticipantId, bool AdoptedLegacy = false);

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
    // Idempotent: the guest this browser IS on this link, created if this is the
    // first time. The browser token is the same one for every capability, and
    // the stored key is derived per link — so view, upload, print, messages and
    // the game all resolve one guest, and two parties never share a counter.
    //
    // `legacyParticipantToken` is the pre-migration, capability-scoped cookie
    // when the browser still holds one. It is adopted or folded in here, so a
    // party that is running right now keeps its guests and their allowances.
    // Session-ESTABLISHING operations pass it; nothing else does.
    Task<PartyParticipantResolution> ResolveOrCreateAsync(
        Guid partyAlbumLinkId, string browserToken, string? legacyParticipantToken = null,
        CancellationToken cancellationToken = default);

    // Resolve an EXISTING guest without creating one, refreshing their presence.
    // Two things depend on this being the whole of it: a television polling the
    // game must not become a participant, and a privileged action must never
    // manufacture the identity that authorises it.
    Task<Guid?> ResolveAsync(
        Guid partyAlbumLinkId, string? browserToken, CancellationToken cancellationToken = default);

    // ATOMIC per-guest greeting claim, on the same principle as the upload slot.
    // `max` of 0 means the host set no limit, so the claim always succeeds and
    // only counts. Must be called inside the caller's transaction: a claim whose
    // message then fails to insert has to roll back with it, or a guest loses a
    // slot to a message nobody ever sees.
    Task<bool> TryClaimMessageAsync(
        Guid participantId, int max, CancellationToken cancellationToken = default);

    // What this guest has spent on greetings, for the surfaces that show it.
    Task<int> MessageCountAsync(Guid participantId, CancellationToken cancellationToken = default);

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
