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

    /// <summary>
    /// The game is alive and nothing is being played: the host has sent the room
    /// back to the party between two activities.
    ///
    /// <para>It is deliberately NOT <see cref="Finished"/>. A finished game is
    /// over — its television dwells on the closing card and then leaves, and the
    /// only way back is <c>restart_game</c>, which DISCARDS the match. An
    /// intermission keeps every round that has been played, keeps the plan the
    /// host is still editing, and resumes with <c>next_challenge</c>. The
    /// television returns to the party slideshow because the presentation
    /// projection says so, not because the game ended.</para>
    /// </summary>
    public const string Intermission = "intermission";

    public const string Finished = "finished";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(
        [Lobby, ChallengeReveal, ChallengeActive, VotingOpen, VotingClosed, Result,
            Intermission, Finished],
        StringComparer.Ordinal);

    /// <summary>
    /// Whether the room's screen belongs to the GAME in this phase. False in the
    /// lobby-before-the-first-match sense is deliberately not the rule here: the
    /// lobby IS the takeover (it shows the join code). Only an intermission and
    /// a match that is over hand the screen back, and the second does so after a
    /// dwell the projection owns.
    /// </summary>
    public static bool HoldsTheScreen(string phase) => phase != Intermission;

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

    /// <summary>
    /// Give the room back to the party between two activities.
    ///
    /// <para>Legal only from <c>result</c>, because it RESOLVES the round the
    /// room just saw the outcome of: an intermission is a pause between
    /// activities, never a way to leave one unfinished. <c>next_challenge</c>
    /// from the intermission resumes exactly where <c>next_challenge</c> from
    /// the result would have gone, which is what makes it a pause rather than a
    /// second way to end a game.</para>
    /// </summary>
    public const string ReturnToParty = "return_to_party";

    /// <summary>
    /// Play the same party again. Legal only from <c>finished</c>, and the only
    /// command whose effect is to DISCARD rather than to advance: the rounds and
    /// votes of the game that just ended go, and the session returns to its
    /// lobby.
    ///
    /// <para>It is a command rather than a new session because the party link is
    /// the game's identity. The guests, their photographs, their greetings,
    /// their prints and the QR on the table all belong to the link, and none of
    /// them is replayed — only the match is.</para>
    /// </summary>
    public const string RestartGame = "restart_game";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(
        [Start, StartChallenge, OpenVoting, CloseVoting, RevealResult, NextChallenge, SkipChallenge,
            Finish, RestartGame, ReturnToParty],
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

/// <summary>
/// One activity the host has decided NOT to play in this match.
///
/// <para>It is a decision about the MATCH, not about the deck. The activity
/// keeps its <see cref="PartyChallenge.IsEnabled"/>, its position and — the
/// point of the row existing at all — every guest preference cast for it: the
/// room said it wanted this, and the host deciding there is no time for it
/// tonight does not unsay that. Deleting the preferences would destroy the one
/// piece of evidence the exclusion is a judgement about.</para>
///
/// <para>Keyed on the SESSION rather than the link, because "this match" is
/// what a session is. A restart discards it with the rounds and lets the host
/// plan the replay from a clean deck.</para>
/// </summary>
public sealed class PartyGameExclusion
{
    public Guid Id { get; set; }
    public Guid PartyGameSessionId { get; set; }
    public Guid PartyChallengeId { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Where one activity stands in the match, as the control room reads it.
/// Wire values, like every other Party Game vocabulary.
/// </summary>
public static class PartyGamePlanStates
{
    /// The room has already seen it — completed or abandoned. Immutable.
    public const string Played = "played";

    /// It is on the screen right now. Immutable.
    public const string Current = "current";

    /// Still to come, and the only state the host may reorder or exclude.
    public const string Remaining = "remaining";
}

/// <summary>
/// The host's PLANNING vocabulary — what to play next, and what not to play at
/// all tonight. Separate from <see cref="PartyGameCommands"/> because these move
/// no phase: they edit the plan the phase-advancing commands then walk.
/// </summary>
public static class PartyGamePlanActions
{
    /// Put one remaining activity at a position among the remaining ones.
    public const string Move = "move";

    /// Do not play it in this match. Reversible, and it destroys no preference.
    public const string Exclude = "exclude";

    /// Put an excluded activity back into the match.
    public const string Include = "include";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>([Move, Exclude, Include], StringComparer.Ordinal);

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
