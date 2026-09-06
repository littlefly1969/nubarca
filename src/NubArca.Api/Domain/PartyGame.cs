namespace NubArca.Api.Domain;

/// <summary>
/// The live Party Game: what is happening at the party RIGHT NOW.
///
/// A <see cref="PartyChallenge"/> is content the host prepared. This is the
/// performance of it — one session per party link, one round per activity
/// played, and one phase saying where the room currently is.
///
/// It is deliberately NOT the same thing as <see cref="PartyChallengeSession"/>,
/// which drives the older interval-based slideshow interruption ("hold the
/// photos, show a dare, resume"). That feature keeps working untouched; this one
/// is a hosted game the owner conducts.
///
/// The server is the only authority. A missing row means the game has not
/// started, exactly as a missing AI artifact-status row means implicit pending:
/// a snapshot read never writes, and the first `start` command creates the row.
/// </summary>
public sealed class PartyGameSession
{
    public Guid Id { get; set; }

    // Both references are carried. The link is the identity (a re-enabled party
    // mints a new link and therefore a new game); the album is what every owner
    // query is scoped by, and keeping it here saves a join on every read.
    public Guid AlbumId { get; set; }
    public Guid PartyAlbumLinkId { get; set; }

    public string Status { get; set; } = PartyGameStatuses.Lobby;
    public string Phase { get; set; } = PartyGamePhases.Lobby;

    public Guid? CurrentRoundId { get; set; }

    // 1-based, and it counts rounds STARTED — a skipped activity still used up
    // a round number, because the room saw it.
    public int CurrentRoundNumber { get; set; }

    /// <summary>
    /// The OWNER COMMAND version: monotonic, incremented by every owner command
    /// that moves the game, and the optimistic-concurrency token the next
    /// command must quote.
    ///
    /// <para>It is deliberately NOT a revision of everything a snapshot can
    /// say. A guest voting changes what the snapshot reports — participation,
    /// and eventually the result — without touching this, because it is not the
    /// guest's place to invalidate the host's command token. If a vote bumped
    /// it, every vote cast during a round would make the host's next command
    /// fail as stale.</para>
    ///
    /// <para>So a polling client must NOT use it to decide whether a response is
    /// worth consuming: every successful poll carries the current truth, whether
    /// or not this number moved.</para>
    /// </summary>
    public int Version { get; set; } = 1;

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
}

/// <summary>
/// One activity, played once. Rounds are append-only history: the sequence is
/// the order the room experienced them, and a completed or abandoned round is
/// never reopened.
/// </summary>
public sealed class PartyGameRound
{
    public Guid Id { get; set; }
    public Guid PartyGameSessionId { get; set; }
    public Guid PartyChallengeId { get; set; }

    // 1-based and equal to the session's CurrentRoundNumber while this is the
    // current round.
    public int Sequence { get; set; }

    public string Status { get; set; } = PartyGameRoundStatuses.Active;

    public DateTime StartedAt { get; set; }

    // The phase clock. The session owns WHICH phase the game is in; the round
    // owns WHEN the current one began and when it is due to end, because every
    // phase except lobby and finished is a phase OF a round. PhaseEndsAt is null
    // whenever the phase has no deadline, which today is always: activity
    // durations arrive with the composer.
    public DateTime PhaseStartedAt { get; set; }
    public DateTime? PhaseEndsAt { get; set; }

    public DateTime? CompletedAt { get; set; }
}

public static class PartyGameStatuses
{
    public const string Lobby = "lobby";
    public const string Live = "live";
    public const string Finished = "finished";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>([Lobby, Live, Finished], StringComparer.Ordinal);
}

public static class PartyGamePhases
{
    public const string Lobby = "lobby";
    public const string ChallengeReveal = "challenge_reveal";
    public const string ChallengeActive = "challenge_active";
    public const string VotingOpen = "voting_open";
    public const string VotingClosed = "voting_closed";
    public const string Result = "result";
    public const string Finished = "finished";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(
        [Lobby, ChallengeReveal, ChallengeActive, VotingOpen, VotingClosed, Result, Finished],
        StringComparer.Ordinal);

    /// <summary>
    /// Phases in which an activity is on screen, and therefore the only phases
    /// whose challenge content a guest or a television may be told about.
    /// </summary>
    public static bool ShowsChallenge(string phase) =>
        phase is ChallengeReveal or ChallengeActive or VotingOpen or VotingClosed or Result;
}

public static class PartyGameRoundStatuses
{
    public const string Active = "active";
    public const string Completed = "completed";

    // The owner moved on before the activity was resolved (skip, or finishing
    // the game mid-round). Deliberately distinct from Completed: it is the
    // difference between "the room decided" and "we moved on".
    public const string Abandoned = "abandoned";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>([Active, Completed, Abandoned], StringComparer.Ordinal);
}

/// <summary>
/// The owner's vocabulary. Wire values, so they are stable strings rather than
/// an enum whose ordinals a client could come to depend on.
/// </summary>
public static class PartyGameCommands
{
    public const string Start = "start";
    public const string StartChallenge = "start_challenge";
    public const string OpenVoting = "open_voting";
    public const string CloseVoting = "close_voting";
    public const string RevealResult = "reveal_result";
    public const string NextChallenge = "next_challenge";
    public const string SkipChallenge = "skip_challenge";
    public const string Finish = "finish";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(
        [Start, StartChallenge, OpenVoting, CloseVoting, RevealResult, NextChallenge, SkipChallenge, Finish],
        StringComparer.Ordinal);

    public static bool IsKnown(string? value) => value is not null && All.Contains(value);
}

/// <summary>
/// One guest's answer for one round.
///
/// The row is the vote: there is no separate "ballot" and no history of what
/// somebody chose before. A guest who changes their mind while voting is open
/// UPDATES this row, because the question is what the room thinks now, not what
/// it thought thirty seconds ago.
///
/// Identity is the anonymous <see cref="PartyParticipant"/> — a server-issued
/// cookie, never an IP, a fingerprint or anything the client chose. The row
/// carries no name and no device information; it is an answer with a key.
/// </summary>
public sealed class PartyGameVote
{
    public Guid Id { get; set; }

    // The round is what a vote is ABOUT, and the session is carried beside it so
    // an aggregate for a whole game needs no join through rounds.
    public Guid PartyGameSessionId { get; set; }
    public Guid PartyGameRoundId { get; set; }
    public Guid PartyParticipantId { get; set; }

    public string Value { get; set; } = PartyGameVoteValues.Yes;

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// The binary verdict. Two values, because the only voting mode the runtime can
/// run is `binary`; rating and multiple choice will bring their own columns
/// rather than overloading this one with a stringly-typed number.
/// </summary>
public static class PartyGameVoteValues
{
    public const string Yes = "yes";
    public const string No = "no";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>([Yes, No], StringComparer.Ordinal);

    public static bool IsKnown(string? value) => value is not null && All.Contains(value);
}

public static class PartyGamePresence
{
    /// <summary>
    /// How recently a guest must have been seen to count as "in the room".
    ///
    /// A phone on the voting screen polls every few seconds, so this is
    /// generous enough to survive a locked screen and a walk to the kitchen,
    /// and short enough that somebody who left an hour ago is not still being
    /// counted in "8 of 12 have voted". It is a soft signal about a party, not
    /// an attendance register, and the count is floored at the number of votes
    /// actually received — whoever voted is by definition present.
    /// </summary>
    public const int WindowSeconds = 180;
}
