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
    /// Monotonic, incremented by every command that changes anything. It is the
    /// optimistic-concurrency token an owner command must quote, and the value a
    /// polling client compares to decide whether it has to re-render.
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
