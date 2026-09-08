using NubArca.Api.Domain;

namespace NubArca.Api.Party;

/// <summary>
/// What a round transition does to the round it leaves and the round it enters.
/// </summary>
[Flags]
public enum PartyGameRoundEffect
{
    None = 0,

    /// The current round is resolved: the room played it and saw the outcome.
    CompleteRound = 1,

    /// The current round ends without an outcome — skipped, or the game was
    /// finished while it was still running.
    AbandonRound = 2,

    /// The next activity in the deck becomes the current round.
    StartRound = 4,

    /// <summary>
    /// Everything the finished game played is discarded and the session returns
    /// to the lobby it started from: its rounds, the votes hanging off them, the
    /// round counter and the two timestamps that bounded the match.
    ///
    /// It is the only effect that removes rows rather than resolving them, and
    /// it deliberately does NOT touch the session's identity or its version —
    /// the row, the party link it belongs to and the monotonic command token all
    /// survive a restart, which is what keeps a command written for the previous
    /// game stale forever.
    /// </summary>
    ResetGame = 8,
}

public sealed record PartyGameTransition(
    string Phase, string Status, PartyGameRoundEffect Effect);

/// <summary>
/// The Party Game transition matrix, as a pure function.
///
/// The whole machine lives here so it can be exhausted by tests without a
/// database, a clock or an HTTP request — the same reason
/// <see cref="PartyChallengePolicy"/> is pure. The service applies transitions;
/// it never decides one.
///
/// LOBBY → CHALLENGE_REVEAL → CHALLENGE_ACTIVE → VOTING_OPEN → VOTING_CLOSED
///       → RESULT → CHALLENGE_REVEAL → … → FINISHED
///
/// FINISHED → LOBBY is the one backwards edge, and it belongs to
/// <c>restart_game</c>: the same party plays again on the same link, with the
/// previous match's rounds and votes discarded. Every other edge moves forward.
///
/// One naming note against the programme brief. "Reveal challenge" is not a
/// separate command: revealing IS the transition into CHALLENGE_REVEAL, and it
/// is performed by <c>start</c> (from the lobby) and by <c>next_challenge</c>
/// (from a result). Giving it a third name would have produced a command that
/// is never legal anywhere, and an owner UI with a primary action that does
/// nothing. The control room's six primary commands map one-to-one onto the six
/// non-terminal phases — and FINISHED has one too, <c>restart_game</c>, so no
/// phase leaves a host holding a screen with nothing on it to press.
/// </summary>
public static class PartyGameStateMachine
{
    /// <summary>
    /// The transition for a command, or null when the command is illegal in this
    /// phase.
    ///
    /// <para><paramref name="hasNextChallenge"/> decides whether leaving a round
    /// starts another one or ends the game; it never makes a legal command
    /// illegal, except for <c>start</c>, because a game with nothing to play is
    /// not a game.</para>
    ///
    /// <para><paramref name="currentActivityVotes"/> is the running activity's
    /// own voting mode. An activity nobody votes on goes straight from being
    /// performed to its result, and <c>open_voting</c> is not offered at all —
    /// the alternative is a control room whose primary action opens a vote that
    /// can never receive one. It defaults to true, which is both the domain
    /// default and what every activity written before the composer meant.</para>
    /// </summary>
    public static PartyGameTransition? Resolve(
        string phase, string command, bool hasNextChallenge, bool currentActivityVotes = true) =>
        (phase, command) switch
        {
            (PartyGamePhases.Lobby, PartyGameCommands.Start) => hasNextChallenge
                ? new(PartyGamePhases.ChallengeReveal, PartyGameStatuses.Live, PartyGameRoundEffect.StartRound)
                : null,

            (PartyGamePhases.ChallengeReveal, PartyGameCommands.StartChallenge) =>
                new(PartyGamePhases.ChallengeActive, PartyGameStatuses.Live, PartyGameRoundEffect.None),

            (PartyGamePhases.ChallengeActive, PartyGameCommands.OpenVoting) => currentActivityVotes
                ? new(PartyGamePhases.VotingOpen, PartyGameStatuses.Live, PartyGameRoundEffect.None)
                : null,

            // The unvoted activity's whole shortcut: performed, then shown. It
            // never enters VOTING_OPEN, so it never reaches VOTING_CLOSED
            // either, and RESULT is where the host says how it went.
            (PartyGamePhases.ChallengeActive, PartyGameCommands.RevealResult) => currentActivityVotes
                ? null
                : new(PartyGamePhases.Result, PartyGameStatuses.Live, PartyGameRoundEffect.None),

            (PartyGamePhases.VotingOpen, PartyGameCommands.CloseVoting) =>
                new(PartyGamePhases.VotingClosed, PartyGameStatuses.Live, PartyGameRoundEffect.None),

            (PartyGamePhases.VotingClosed, PartyGameCommands.RevealResult) =>
                new(PartyGamePhases.Result, PartyGameStatuses.Live, PartyGameRoundEffect.None),

            // The round is resolved. Another activity continues the game; no
            // more activities ends it, which is the ONLY way a game finishes by
            // itself.
            (PartyGamePhases.Result, PartyGameCommands.NextChallenge) => hasNextChallenge
                ? new(PartyGamePhases.ChallengeReveal, PartyGameStatuses.Live,
                    PartyGameRoundEffect.CompleteRound | PartyGameRoundEffect.StartRound)
                : new(PartyGamePhases.Finished, PartyGameStatuses.Finished,
                    PartyGameRoundEffect.CompleteRound),

            // Skip abandons an unresolved round. Legal for as long as the round
            // is unresolved, which is every phase up to and including
            // VOTING_CLOSED — a host who has lost the room should not have to
            // reveal a result first.
            (PartyGamePhases.ChallengeReveal or PartyGamePhases.ChallengeActive
                or PartyGamePhases.VotingOpen or PartyGamePhases.VotingClosed,
                PartyGameCommands.SkipChallenge) => hasNextChallenge
                ? new(PartyGamePhases.ChallengeReveal, PartyGameStatuses.Live,
                    PartyGameRoundEffect.AbandonRound | PartyGameRoundEffect.StartRound)
                : new(PartyGamePhases.Finished, PartyGameStatuses.Finished,
                    PartyGameRoundEffect.AbandonRound),

            // Finishing is legal from anywhere the game is not already over. It
            // resolves the current round if the room saw its outcome and
            // abandons it otherwise.
            (PartyGamePhases.Lobby, PartyGameCommands.Finish) =>
                new(PartyGamePhases.Finished, PartyGameStatuses.Finished, PartyGameRoundEffect.None),
            (PartyGamePhases.Result, PartyGameCommands.Finish) =>
                new(PartyGamePhases.Finished, PartyGameStatuses.Finished, PartyGameRoundEffect.CompleteRound),
            (PartyGamePhases.ChallengeReveal or PartyGamePhases.ChallengeActive
                or PartyGamePhases.VotingOpen or PartyGamePhases.VotingClosed,
                PartyGameCommands.Finish) =>
                new(PartyGamePhases.Finished, PartyGameStatuses.Finished, PartyGameRoundEffect.AbandonRound),

            // The one edge that leads OUT of the terminal phase, and the only
            // one that runs backwards: the evening ended, and the host wants to
            // play the same party again.
            //
            // It ignores hasNextChallenge on purpose. At `finished` there is by
            // definition no unplayed activity left in the ordinary case — that
            // is usually WHY the game ended — and the whole point of the restart
            // is that discarding the rounds makes the deck playable again. Only
            // the `start` that follows needs an activity, and it is the command
            // that checks for one.
            (PartyGamePhases.Finished, PartyGameCommands.RestartGame) =>
                new(PartyGamePhases.Lobby, PartyGameStatuses.Lobby, PartyGameRoundEffect.ResetGame),

            _ => null,
        };

    /// <summary>
    /// Every command that is legal right now, in the order a control room should
    /// offer them: the phase-advancing command first, then the secondary ones.
    /// At <c>finished</c> the leading command is <c>restart_game</c>, which is
    /// the only one there is.
    ///
    /// The server answering this is what lets the owner UI keep an illegal
    /// command ABSENT rather than disabled without re-implementing the matrix in
    /// TypeScript — and the server still validates, because a client is never an
    /// authority.
    /// </summary>
    public static IReadOnlyList<string> LegalCommands(
        string phase, bool hasNextChallenge, bool currentActivityVotes = true)
    {
        var ordered = new[]
        {
            PrimaryCommand(phase, currentActivityVotes),
            PartyGameCommands.SkipChallenge,
            PartyGameCommands.Finish,
        };
        var legal = new List<string>(3);
        foreach (var command in ordered)
        {
            if (command is null || legal.Contains(command)) continue;
            if (Resolve(phase, command, hasNextChallenge, currentActivityVotes) is not null)
                legal.Add(command);
        }
        return legal;
    }

    /// <summary>
    /// The one command that advances this phase — the control room's single
    /// primary action. Null in a phase that has none (finished), and it is not
    /// necessarily legal: <c>start</c> needs an activity to play.
    /// </summary>
    public static string? PrimaryCommand(string phase, bool currentActivityVotes = true) => phase switch
    {
        PartyGamePhases.Lobby => PartyGameCommands.Start,
        PartyGamePhases.ChallengeReveal => PartyGameCommands.StartChallenge,
        PartyGamePhases.ChallengeActive => currentActivityVotes
            ? PartyGameCommands.OpenVoting : PartyGameCommands.RevealResult,
        PartyGamePhases.VotingOpen => PartyGameCommands.CloseVoting,
        PartyGamePhases.VotingClosed => PartyGameCommands.RevealResult,
        PartyGamePhases.Result => PartyGameCommands.NextChallenge,
        // Finished is terminal for the MATCH, not for the party: its one action
        // is to play again, so the control room still has a single primary
        // button rather than a dead end.
        PartyGamePhases.Finished => PartyGameCommands.RestartGame,
        _ => null,
    };
}
