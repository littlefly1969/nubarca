namespace NubArca.Api.Tv;

public interface ITvPairingService
{
    Task<TvPairingStartedDto> StartAsync(string approvalBaseUrl, CancellationToken cancellationToken = default);
    Task<TvPairingPollResult?> PollAsync(string publicCode, string? pairingSecret, string? userAgent,
        CancellationToken cancellationToken = default);
    // Atomic approval: for an owner WITHOUT a Personal Area credential this
    // validates and creates the DIRECTIONAL code in the same commit as the
    // approval (PinRequired / InvalidPin / PinMismatch leave the pairing pending
    // and commit nothing); an owner WITH a credential approves normally and the
    // code fields are ignored.
    Task<TvPairingApproveResult> ApproveAsync(string publicCode, string? pairingSecret, Guid ownerUserId,
        string? personalCode, string? personalCodeConfirmation,
        CancellationToken cancellationToken = default);
    // Resolves the limited TV session cookie. Returns the session's own id
    // alongside its status, so a caller can ask a DIFFERENT service what this
    // television is for without resolving the token a second time. Null for an
    // unknown, revoked or expired session.
    Task<TvSessionStateDto?> GetSessionAsync(string? sessionToken, bool heartbeat,
        CancellationToken cancellationToken = default);

    // The session a pairing produced, for the owner who approved it. The pairing
    // secret is required as well as the owner cookie, so holding one without the
    // other names nothing; null until the television has actually claimed the
    // pairing, and for a pairing this owner did not approve.
    //
    // It exists so the approval page can finish the job it started — "what is
    // this television for?" — on the session that pairing minted, without the
    // owner having to find the device in a list afterwards.
    Task<Guid?> FindPairedSessionIdAsync(string publicCode, string? pairingSecret, Guid ownerUserId,
        CancellationToken cancellationToken = default);
    Task<bool> RevokeSessionAsync(string? sessionToken, CancellationToken cancellationToken = default);

    // Server-side resolution of a TV session cookie to its owner user id. Live:
    // re-checks revocation + expiry on every call. Returns null for an unknown,
    // revoked, or expired session. The owner id is used only for internal
    // authorization and is never returned to a client DTO.
    Task<Guid?> ResolveOwnerUserIdAsync(string? sessionToken,
        CancellationToken cancellationToken = default);

    // The same resolution, plus the one extra thing a TV media route needs to
    // know: the album of the party this television is assigned to, if any, and
    // only while that party is display-resolvable by the canonical projection
    // (ITvPartyPresentationService). Re-read on every call, so reassigning the
    // television, the party becoming unavailable for ANY reason, or unpairing
    // closes it on the next request.
    Task<TvViewer?> ResolveViewerAsync(string? sessionToken,
        CancellationToken cancellationToken = default);

    // Owner-side management: list this owner's TV sessions (safe DTOs; no token
    // hash / secret / owner id), most recent first.
    Task<IReadOnlyList<TvDeviceDto>> ListOwnerSessionsAsync(Guid ownerUserId,
        CancellationToken cancellationToken = default);

    // Owner-side management: revoke one of this owner's TV sessions by id.
    // Returns false when the session is missing or belongs to another owner
    // (generic 404); idempotent when already revoked.
    Task<bool> RevokeOwnerSessionAsync(Guid ownerUserId, Guid sessionId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A live TV session as a media route sees it. INTERNAL: it never reaches a
/// client.
///
/// <para><c>AssignedPartyAlbumId</c> is the album of the party this television
/// is assigned to, and only while that party is display-resolvable — exactly
/// when the control plane would NOT tell this television `unavailable`; null
/// for a general television and for one assigned to a party that cannot be
/// shown, whatever the reason. It widens what THIS device may read by exactly one album —
/// the one its owner told it to show — and nothing else: not the album list,
/// not another television of the same owner.</para>
/// </summary>
public sealed record TvViewer(Guid OwnerUserId, Guid? AssignedPartyAlbumId);
