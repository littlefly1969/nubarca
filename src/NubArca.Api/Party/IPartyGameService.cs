namespace NubArca.Api.Party;

/// <summary>
/// The server-authoritative Party Game runtime.
///
/// Every question about what is happening at the party is answered here, from
/// persisted state. No client — owner tab, guest phone, television — ever holds
/// game state that the server cannot reconstruct, which is what makes a refresh
/// during a party a non-event.
/// </summary>
public interface IPartyGameService
{
    /// <summary>
    /// The owner's complete view. Null when the album is missing, foreign, or
    /// not in party mode. A game that has not started yet returns a lobby
    /// snapshot with <c>Version = 0</c> and no session id — reading never
    /// creates a row.
    /// </summary>
    Task<PartyGameSnapshotDto?> GetOwnerSnapshotAsync(
        Guid ownerUserId, Guid albumId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies one owner command, or refuses it. The refusal carries the current
    /// snapshot so a stale caller can re-render without a second request.
    /// </summary>
    Task<PartyGameCommandResult> ExecuteAsync(
        Guid ownerUserId, Guid albumId, string? command, int expectedVersion,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The public view for a validated party token. Null when the link carries
    /// no game. The activity is included only in the phases that put it on
    /// screen; its media URL is a token-less sentinel the endpoint rewrites.
    /// </summary>
    Task<PartyGamePublicSnapshotDto?> GetPublicSnapshotAsync(
        PartyAccess access, CancellationToken cancellationToken = default);
}
