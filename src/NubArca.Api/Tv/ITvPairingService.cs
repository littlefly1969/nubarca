namespace NubArca.Api.Tv;

public interface ITvPairingService
{
    Task<TvPairingStartedDto> StartAsync(string approvalBaseUrl, CancellationToken cancellationToken = default);
    Task<TvPairingPollResult?> PollAsync(string publicCode, string? pairingSecret, string? userAgent,
        CancellationToken cancellationToken = default);
    // Atomic approval: for an owner WITHOUT a Personal Area PIN this validates
    // and creates the PIN in the same commit as the approval (PinRequired /
    // InvalidPin / PinMismatch leave the pairing pending and commit nothing);
    // an owner WITH a PIN approves normally and the PIN fields are ignored.
    Task<TvPairingApproveResult> ApproveAsync(string publicCode, string? pairingSecret, Guid ownerUserId,
        string? personalPin, string? personalPinConfirmation,
        CancellationToken cancellationToken = default);
    Task<TvSessionDto?> GetSessionAsync(string? sessionToken, bool heartbeat,
        CancellationToken cancellationToken = default);
    Task<bool> RevokeSessionAsync(string? sessionToken, CancellationToken cancellationToken = default);

    // Server-side resolution of a TV session cookie to its owner user id. Live:
    // re-checks revocation + expiry on every call. Returns null for an unknown,
    // revoked, or expired session. The owner id is used only for internal
    // authorization and is never returned to a client DTO.
    Task<Guid?> ResolveOwnerUserIdAsync(string? sessionToken,
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
