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
    /// <param name="participantId">
    /// The caller's own guest identity, when it already has one. A television
    /// passes null and is never given one: a display must not become a voter,
    /// and counting it would inflate "8 of 12 have voted".
    /// </param>
    /// <param name="isDisplay">
    /// True when the caller says it is a television. It stamps the party's
    /// display heartbeat, which is the only way the control room can honestly
    /// say whether a screen is showing the game. Saying so grants nothing.
    /// </param>
    Task<PartyGamePublicSnapshotDto?> GetPublicSnapshotAsync(
        PartyAccess access, Guid? participantId = null, bool isDisplay = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records one guest's answer for one round, or refuses it.
    ///
    /// The client never decides whether a vote is valid. The participant, the
    /// session, the round being played, the phase and the activity's own voting
    /// mode are all re-read here, on every tap.
    /// </summary>
    /// <param name="participantId">
    /// An identity this party ISSUED and the caller resolved server-side, or
    /// null. Null is refused: a vote may never mint a voter, so the only way to
    /// acquire the right to change a result is a successful join.
    /// </param>
    Task<PartyGameVoteResult> VoteAsync(
        PartyAccess access, Guid? participantId, Guid? roundId, string? value,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Edits the host's PLAN: moves one activity that is still to come, or
    /// takes it out of this match.
    ///
    /// <para>It is an owner command like any other — it quotes a version, it
    /// refuses a stale caller with the current snapshot, and it spends a
    /// version when it changes anything — because the order the game will walk
    /// is authoritative state and not a client's preference. It moves no phase:
    /// a host may plan the rest of the evening from the lobby, from an
    /// intermission, or while an activity is on the screen.</para>
    ///
    /// <para>What it may NOT touch is the past and the present. A played round
    /// is what the room experienced and a current one is on the screen; both are
    /// <see cref="PartyGameCommandError.InvalidPlan"/> rather than quietly
    /// skipped, so a control room that offered the control learns it was wrong.</para>
    /// </summary>
    Task<PartyGameCommandResult> PlanAsync(
        Guid ownerUserId, Guid albumId, PartyGamePlanRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The guest's pre-game preferences. Null when this party does not offer
    /// them at all — the game is off, or the host did not ask the room.
    /// </summary>
    /// <param name="participantId">
    /// The caller's own identity when it has one. A caller without one still
    /// sees the activities (there is nothing private about the host's deck) and
    /// simply has nothing selected.
    /// </param>
    Task<PartyGamePreferencesDto?> GetPreferencesAsync(
        PartyAccess access, Guid? participantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds or removes one preference, or refuses it.
    ///
    /// <para>Advisory by construction: it writes a
    /// <see cref="Domain.PartyChallengeVote"/> and claims a slot of the guest's
    /// budget, and it touches no session, no round, no phase and no result.
    /// There is no path from this method to the game moving.</para>
    /// </summary>
    Task<PartyGamePreferenceResult> SetPreferenceAsync(
        PartyAccess access, Guid? participantId, Guid? challengeId, bool selected,
        CancellationToken cancellationToken = default);
}
