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
    public void A_finished_game_accepts_nothing()
    {
        foreach (var command in PartyGameCommands.All)
        foreach (var hasNext in new[] { true, false })
            Assert.Null(PartyGameStateMachine.Resolve(PartyGamePhases.Finished, command, hasNext));
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
    public void Each_non_terminal_phase_has_exactly_one_primary_command()
    {
        foreach (var phase in PartyGamePhases.All.Where(x => x != PartyGamePhases.Finished))
        {
            var primary = PartyGameStateMachine.PrimaryCommand(phase);
            Assert.NotNull(primary);
            Assert.NotNull(PartyGameStateMachine.Resolve(phase, primary!, hasNextChallenge: true));
        }
        Assert.Null(PartyGameStateMachine.PrimaryCommand(PartyGamePhases.Finished));
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
    public void A_finished_game_offers_no_command_at_all()
    {
        Assert.Empty(PartyGameStateMachine.LegalCommands(PartyGamePhases.Finished, true));
        Assert.Empty(PartyGameStateMachine.LegalCommands(PartyGamePhases.Finished, false));
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
