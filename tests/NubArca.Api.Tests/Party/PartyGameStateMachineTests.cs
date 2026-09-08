using NubArca.Api.Domain;
using NubArca.Api.Party;

namespace NubArca.Api.Tests.Party;

/// <summary>
/// The transition matrix, exhausted. Every phase × every command × both answers
/// to "is there another activity" is asserted, so an edge cannot be added,
/// removed or quietly widened without this file disagreeing.
/// </summary>
public sealed class PartyGameStateMachineTests
{
    // The complete legal set, as a table independent of the implementation.
    // (phase, command, hasNext) → (phase, status, effect).
    private static readonly Dictionary<(string, string, bool), (string, string, PartyGameRoundEffect)> Legal = new()
    {
        [(PartyGamePhases.Lobby, PartyGameCommands.Start, true)] =
            (PartyGamePhases.ChallengeReveal, PartyGameStatuses.Live, PartyGameRoundEffect.StartRound),
        [(PartyGamePhases.Lobby, PartyGameCommands.Finish, true)] =
            (PartyGamePhases.Finished, PartyGameStatuses.Finished, PartyGameRoundEffect.None),
        [(PartyGamePhases.Lobby, PartyGameCommands.Finish, false)] =
            (PartyGamePhases.Finished, PartyGameStatuses.Finished, PartyGameRoundEffect.None),

        [(PartyGamePhases.ChallengeReveal, PartyGameCommands.StartChallenge, true)] =
            (PartyGamePhases.ChallengeActive, PartyGameStatuses.Live, PartyGameRoundEffect.None),
        [(PartyGamePhases.ChallengeReveal, PartyGameCommands.StartChallenge, false)] =
            (PartyGamePhases.ChallengeActive, PartyGameStatuses.Live, PartyGameRoundEffect.None),

        [(PartyGamePhases.ChallengeActive, PartyGameCommands.OpenVoting, true)] =
            (PartyGamePhases.VotingOpen, PartyGameStatuses.Live, PartyGameRoundEffect.None),
        [(PartyGamePhases.ChallengeActive, PartyGameCommands.OpenVoting, false)] =
            (PartyGamePhases.VotingOpen, PartyGameStatuses.Live, PartyGameRoundEffect.None),

        [(PartyGamePhases.VotingOpen, PartyGameCommands.CloseVoting, true)] =
            (PartyGamePhases.VotingClosed, PartyGameStatuses.Live, PartyGameRoundEffect.None),
        [(PartyGamePhases.VotingOpen, PartyGameCommands.CloseVoting, false)] =
            (PartyGamePhases.VotingClosed, PartyGameStatuses.Live, PartyGameRoundEffect.None),

        [(PartyGamePhases.VotingClosed, PartyGameCommands.RevealResult, true)] =
            (PartyGamePhases.Result, PartyGameStatuses.Live, PartyGameRoundEffect.None),
        [(PartyGamePhases.VotingClosed, PartyGameCommands.RevealResult, false)] =
            (PartyGamePhases.Result, PartyGameStatuses.Live, PartyGameRoundEffect.None),

        [(PartyGamePhases.Result, PartyGameCommands.NextChallenge, true)] =
            (PartyGamePhases.ChallengeReveal, PartyGameStatuses.Live,
                PartyGameRoundEffect.CompleteRound | PartyGameRoundEffect.StartRound),
        [(PartyGamePhases.Result, PartyGameCommands.NextChallenge, false)] =
            (PartyGamePhases.Finished, PartyGameStatuses.Finished, PartyGameRoundEffect.CompleteRound),
        [(PartyGamePhases.Result, PartyGameCommands.Finish, true)] =
            (PartyGamePhases.Finished, PartyGameStatuses.Finished, PartyGameRoundEffect.CompleteRound),
        [(PartyGamePhases.Result, PartyGameCommands.Finish, false)] =
            (PartyGamePhases.Finished, PartyGameStatuses.Finished, PartyGameRoundEffect.CompleteRound),

        // The one backwards edge. Stated for BOTH answers to "is there another
        // activity", because the ordinary way to reach `finished` is to run out
        // of them — a restart that needed an unplayed activity would be illegal
        // exactly when a host wants it.
        [(PartyGamePhases.Finished, PartyGameCommands.RestartGame, true)] =
            (PartyGamePhases.Lobby, PartyGameStatuses.Lobby, PartyGameRoundEffect.ResetGame),
        [(PartyGamePhases.Finished, PartyGameCommands.RestartGame, false)] =
            (PartyGamePhases.Lobby, PartyGameStatuses.Lobby, PartyGameRoundEffect.ResetGame),
    };

    static PartyGameStateMachineTests()
    {
        // Skip and finish behave identically in every unresolved round phase, so
        // the table states them once rather than sixteen times by hand.
        foreach (var phase in new[]
        {
            PartyGamePhases.ChallengeReveal, PartyGamePhases.ChallengeActive,
            PartyGamePhases.VotingOpen, PartyGamePhases.VotingClosed,
        })
        {
            Legal[(phase, PartyGameCommands.SkipChallenge, true)] =
                (PartyGamePhases.ChallengeReveal, PartyGameStatuses.Live,
                    PartyGameRoundEffect.AbandonRound | PartyGameRoundEffect.StartRound);
            Legal[(phase, PartyGameCommands.SkipChallenge, false)] =
                (PartyGamePhases.Finished, PartyGameStatuses.Finished, PartyGameRoundEffect.AbandonRound);
            Legal[(phase, PartyGameCommands.Finish, true)] =
                (PartyGamePhases.Finished, PartyGameStatuses.Finished, PartyGameRoundEffect.AbandonRound);
            Legal[(phase, PartyGameCommands.Finish, false)] =
                (PartyGamePhases.Finished, PartyGameStatuses.Finished, PartyGameRoundEffect.AbandonRound);
        }
    }

    [Fact]
    public void Every_phase_command_pair_matches_the_declared_matrix()
    {
        foreach (var phase in PartyGamePhases.All)
        foreach (var command in PartyGameCommands.All)
        foreach (var hasNext in new[] { true, false })
        {
            var actual = PartyGameStateMachine.Resolve(phase, command, hasNext);
            if (Legal.TryGetValue((phase, command, hasNext), out var expected))
            {
                Assert.NotNull(actual);
                Assert.Equal(expected.Item1, actual!.Phase);
                Assert.Equal(expected.Item2, actual.Status);
                Assert.Equal(expected.Item3, actual.Effect);
            }
            else
            {
                Assert.Null(actual);
            }
        }
    }

    [Fact]
    public void An_unvoted_activity_changes_exactly_two_edges_and_nothing_else()
    {
        // The whole difference a voting mode makes: CHALLENGE_ACTIVE stops
        // offering a vote and starts offering the result. Every other cell of
        // the matrix is identical, and this asserts that rather than trusting it.
        foreach (var phase in PartyGamePhases.All)
        foreach (var command in PartyGameCommands.All)
        foreach (var hasNext in new[] { true, false })
        {
            var voted = PartyGameStateMachine.Resolve(phase, command, hasNext, true);
            var unvoted = PartyGameStateMachine.Resolve(phase, command, hasNext, false);

            if (phase == PartyGamePhases.ChallengeActive && command == PartyGameCommands.OpenVoting)
            {
                Assert.NotNull(voted);
                Assert.Null(unvoted);
            }
            else if (phase == PartyGamePhases.ChallengeActive && command == PartyGameCommands.RevealResult)
            {
                Assert.Null(voted);
                Assert.Equal(PartyGamePhases.Result, unvoted!.Phase);
                Assert.Equal(PartyGameStatuses.Live, unvoted.Status);
                Assert.Equal(PartyGameRoundEffect.None, unvoted.Effect);
            }
            else
            {
                Assert.Equal(voted, unvoted);
            }
        }
    }

    [Fact]
    public void An_unvoted_activity_offers_the_result_as_its_primary_action()
    {
        Assert.Equal(PartyGameCommands.RevealResult,
            PartyGameStateMachine.PrimaryCommand(PartyGamePhases.ChallengeActive, currentActivityVotes: false));
        Assert.Equal(PartyGameCommands.OpenVoting,
            PartyGameStateMachine.PrimaryCommand(PartyGamePhases.ChallengeActive, currentActivityVotes: true));

        // And the vote is ABSENT from what the control room may offer, not
        // present-but-refused.
        var legal = PartyGameStateMachine.LegalCommands(
            PartyGamePhases.ChallengeActive, hasNextChallenge: true, currentActivityVotes: false);
        Assert.Equal(PartyGameCommands.RevealResult, legal[0]);
        Assert.DoesNotContain(PartyGameCommands.OpenVoting, legal);
    }

    [Fact]
    public void A_finished_game_accepts_nothing_but_playing_again()
    {
        foreach (var command in PartyGameCommands.All.Where(x => x != PartyGameCommands.RestartGame))
        foreach (var hasNext in new[] { true, false })
            Assert.Null(PartyGameStateMachine.Resolve(PartyGamePhases.Finished, command, hasNext));
    }

    [Fact]
    public void Restarting_is_legal_only_from_finished_and_only_ever_lands_in_the_lobby()
    {
        foreach (var phase in PartyGamePhases.All.Where(x => x != PartyGamePhases.Finished))
        foreach (var hasNext in new[] { true, false })
            Assert.Null(PartyGameStateMachine.Resolve(phase, PartyGameCommands.RestartGame, hasNext));

        foreach (var hasNext in new[] { true, false })
        foreach (var votes in new[] { true, false })
        {
            var restart = PartyGameStateMachine.Resolve(
                PartyGamePhases.Finished, PartyGameCommands.RestartGame, hasNext, votes);
            Assert.NotNull(restart);
            Assert.Equal(PartyGamePhases.Lobby, restart!.Phase);
            Assert.Equal(PartyGameStatuses.Lobby, restart.Status);
            // Nothing is completed or abandoned on the way out: the rounds are
            // discarded, not resolved.
            Assert.Equal(PartyGameRoundEffect.ResetGame, restart.Effect);
        }
    }

    [Fact]
    public void Start_needs_an_activity_to_play()
    {
        Assert.Null(PartyGameStateMachine.Resolve(PartyGamePhases.Lobby, PartyGameCommands.Start, false));
        Assert.NotNull(PartyGameStateMachine.Resolve(PartyGamePhases.Lobby, PartyGameCommands.Start, true));
    }

    [Fact]
    public void An_unknown_command_is_never_legal()
    {
        foreach (var phase in PartyGamePhases.All)
            Assert.Null(PartyGameStateMachine.Resolve(phase, "reveal_challenge", true));
    }

    [Fact]
    public void Every_phase_has_exactly_one_primary_command()
    {
        foreach (var phase in PartyGamePhases.All)
        {
            var primary = PartyGameStateMachine.PrimaryCommand(phase);
            Assert.NotNull(primary);
            Assert.NotNull(PartyGameStateMachine.Resolve(phase, primary!, hasNextChallenge: true));
        }
        // Including the terminal one: a host looking at a finished game is not
        // looking at a screen with nothing on it to press.
        Assert.Equal(PartyGameCommands.RestartGame,
            PartyGameStateMachine.PrimaryCommand(PartyGamePhases.Finished));
    }

    [Fact]
    public void Legal_commands_lead_with_the_primary_and_hold_only_legal_entries()
    {
        foreach (var phase in PartyGamePhases.All)
        foreach (var hasNext in new[] { true, false })
        {
            var legal = PartyGameStateMachine.LegalCommands(phase, hasNext);
            Assert.Equal(legal.Distinct(), legal);
            foreach (var command in legal)
                Assert.NotNull(PartyGameStateMachine.Resolve(phase, command, hasNext));

            var primary = PartyGameStateMachine.PrimaryCommand(phase);
            if (primary is not null && PartyGameStateMachine.Resolve(phase, primary, hasNext) is not null)
                Assert.Equal(primary, legal[0]);
        }
    }

    [Fact]
    public void An_empty_deck_leaves_the_lobby_with_only_finish()
    {
        Assert.Equal([PartyGameCommands.Finish],
            PartyGameStateMachine.LegalCommands(PartyGamePhases.Lobby, hasNextChallenge: false));
    }

    [Fact]
    public void A_finished_game_offers_exactly_one_command_playing_it_again()
    {
        // Not skip, not finish: the evening is over, and the only thing left to
        // do with it is another one.
        Assert.Equal([PartyGameCommands.RestartGame],
            PartyGameStateMachine.LegalCommands(PartyGamePhases.Finished, true));
        Assert.Equal([PartyGameCommands.RestartGame],
            PartyGameStateMachine.LegalCommands(PartyGamePhases.Finished, false));
    }

    [Fact]
    public void Only_the_phases_that_put_an_activity_on_screen_say_so()
    {
        Assert.False(PartyGamePhases.ShowsChallenge(PartyGamePhases.Lobby));
        Assert.False(PartyGamePhases.ShowsChallenge(PartyGamePhases.Finished));
        foreach (var phase in new[]
        {
            PartyGamePhases.ChallengeReveal, PartyGamePhases.ChallengeActive,
            PartyGamePhases.VotingOpen, PartyGamePhases.VotingClosed, PartyGamePhases.Result,
        })
            Assert.True(PartyGamePhases.ShowsChallenge(phase));
    }
}
