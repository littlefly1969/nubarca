using NubArca.Api.Domain;

namespace NubArca.Api.Party;

/// <summary>
/// One activity as the owner's control room and composer see it. The media URL
/// is the owner-authorized thumbnail, never a storage key.
/// </summary>
public sealed record PartyGameChallengeDto(
    Guid Id, string Title, string Body, string Kind, string? MediaUrl,
    // How the activity is PLAYED, carried beside what it says. A control room
    // that had to read the instructions to learn whether there is a vote would
    // be guessing at exactly the moment it must not.
    int? DurationSeconds = null,
    string VotingMode = PartyChallengeVotingModes.Binary,
    string? VoteQuestion = null);

/// <summary>
/// The complete owner-facing state of a party game. Everything a control room
/// renders comes from one of these, so a reconnect, a refresh and a poll all
/// converge on the same screen.
///
/// <para><c>Version</c> is the concurrency token. It is 0 when no session row
/// exists yet: a missing row IS the lobby, and reading it must never write one.
/// A <c>start</c> command therefore quotes 0.</para>
///
/// <para><c>AvailableCommands</c> is the server's own answer to "what may I do
/// now", so the control room can omit illegal actions instead of disabling
/// them. It is advisory: every command is validated again on arrival.</para>
/// </summary>
public sealed record PartyGameSnapshotDto(
    Guid AlbumId,
    Guid? SessionId,
    string Status,
    string Phase,
    int Version,
    int RoundNumber,
    int TotalChallenges,
    int PlayedRounds,
    DateTime? StartedAt,
    DateTime? FinishedAt,
    DateTime? PhaseStartedAt,
    DateTime? PhaseEndsAt,
    PartyGameChallengeDto? CurrentChallenge,
    PartyGameChallengeDto? NextChallenge,
    IReadOnlyList<string> AvailableCommands);

/// <summary>
/// What a guest phone or a television is told. A strict subset: no session id,
/// no round history, no command vocabulary, and the activity only in the phases
/// that put it on screen.
///
/// <c>Version</c> is carried because a display needs to know that something
/// changed; it is an opaque counter and grants nothing.
/// </summary>
public sealed record PartyGamePublicSnapshotDto(
    string AlbumName,
    string Status,
    string Phase,
    int Version,
    int RoundNumber,
    int TotalChallenges,
    DateTime? PhaseEndsAt,
    PartyChallengePresentationDto? Challenge);

public enum PartyGameCommandError
{
    /// The album is missing, not the caller's, or party mode is off.
    NotFound,

    /// The album is the caller's and party mode is on, but the game switch is not.
    GameDisabled,

    /// Not a member of PartyGameCommands.
    UnknownCommand,

    /// The command quoted a version that is no longer current. Somebody else —
    /// another tab, a second device, a double tap — already moved the game.
    VersionConflict,

    /// A known command that this phase does not allow.
    IllegalTransition,

    /// `start` with an empty deck.
    NoChallenges,
}

/// <summary>
/// The outcome of an owner command. A refusal still carries the CURRENT
/// snapshot whenever one exists, so a stale control room recovers in the same
/// round trip that told it off — the alternative is an error toast followed by a
/// refetch the client has to remember to make.
/// </summary>
public sealed record PartyGameCommandResult(
    PartyGameSnapshotDto? Snapshot,
    PartyGameCommandError? Error)
{
    public static PartyGameCommandResult Ok(PartyGameSnapshotDto snapshot) => new(snapshot, null);
    public static PartyGameCommandResult Fail(PartyGameCommandError error, PartyGameSnapshotDto? current = null) =>
        new(current, error);
}

public sealed record PartyGameCommandRequest(string? Command, int? ExpectedVersion);
