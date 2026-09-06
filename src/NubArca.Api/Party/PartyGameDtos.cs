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
/// <summary>
/// What the room has said, and how much of the room has said it.
///
/// <para><c>Received</c> and <c>Eligible</c> are safe at any moment: they say
/// how many people have answered, never what they answered. <c>Yes</c>,
/// <c>No</c> and <c>Passed</c> are the result, and they are null until the
/// audience being served is allowed to know it — the owner from the moment
/// voting closes, because the host decides when to reveal; a television or a
/// guest only once it IS revealed.</para>
///
/// <para>Counts rather than a percentage: a percentage is presentation, and
/// two surfaces rounding it differently would show a party two different
/// results. <c>Passed</c> is on the server for the same reason — whether a tie
/// counts as passing is a product rule with exactly one answer.</para>
/// </summary>
public sealed record PartyGameVotingDto(
    int Received,
    int Eligible,
    int? Yes = null,
    int? No = null,
    bool? Passed = null);

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
    IReadOnlyList<string> AvailableCommands,
    PartyGameVotingDto? Voting = null,
    // --- What the room looks like from the control room ---
    // How many guests are in it, whether a screen is showing the game, and
    // where the two surfaces live. All owner-facing, none of it derivable
    // client-side: a browser cannot know how long ago a television polled, and
    // a clock-skewed one cannot be trusted to subtract two timestamps.
    int GuestsPresent = 0,
    int? DisplaySeenSecondsAgo = null,
    string? TvUrl = null,
    string? GuestUrl = null);

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
    PartyChallengePresentationDto? Challenge,
    // The identity of what a vote would be ABOUT. A guest quotes it, so a phone
    // that fell behind cannot land last round's answer on this round's activity
    // — and unlike the version, it is stable for the whole round, so an
    // ordinary lagging poll does not cost somebody their vote.
    Guid? RoundId = null,
    PartyGameVotingDto? Voting = null,
    // This caller's own current answer, when this caller has one. Null for a
    // television, which holds no participant cookie and is never given one.
    string? MyVote = null);

public enum PartyGameVoteError
{
    /// The token, the album, the party or the game switch does not resolve.
    NotFound,

    /// There is no vote to cast: no game, no round, or a phase that is not
    /// collecting answers — including the instant after the host closed it.
    VotingClosed,

    /// The guest is answering a round that is no longer the one being played.
    StaleRound,

    /// Not `yes` or `no`.
    UnknownValue,
}

/// <summary>
/// The outcome of one tap. A refusal still carries the current public snapshot
/// wherever one exists, for the same reason an owner command does: the guest is
/// holding a phone at a party, and the useful answer is the state of the game.
/// </summary>
public sealed record PartyGameVoteResult(
    PartyGamePublicSnapshotDto? Snapshot,
    PartyGameVoteError? Error)
{
    public static PartyGameVoteResult Ok(PartyGamePublicSnapshotDto snapshot) => new(snapshot, null);
    public static PartyGameVoteResult Fail(
        PartyGameVoteError error, PartyGamePublicSnapshotDto? current = null) => new(current, error);
}

public sealed record PartyGameVoteRequest(Guid? RoundId, string? Value);

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

/// <summary>
/// The room around the game: how many guests are in it, how long ago a screen
/// last looked at it, and where the two public surfaces live.
///
/// Internal to the service layer — it exists so the snapshot builder can be
/// handed one resolved answer instead of querying for it four times.
/// </summary>
internal sealed record PartyGameRoomDto(
    int GuestsPresent, int? DisplaySeenSecondsAgo, string TvUrl, string GuestUrl);
