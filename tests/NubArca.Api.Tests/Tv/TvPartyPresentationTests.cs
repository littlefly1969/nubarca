using NubArca.Api.Domain;
using NubArca.Api.Tv;

namespace NubArca.Api.Tests.Tv;

/// <summary>
/// The presentation rule, exhausted without a database.
///
/// It is the whole of "when does the game take the television, and when does it
/// give it back", so every branch is asserted here as a value. The integration
/// tests then only have to prove that the service feeds it the real state.
/// </summary>
public sealed class TvPartyPresentationTests
{
    private static readonly DateTime Now = new(2026, 9, 12, 21, 0, 0, DateTimeKind.Utc);

    private static string Decide(
        bool showable = true, bool gameEnabled = true, bool gamesPermitted = true,
        string? status = null, DateTime? finishedAt = null, DateTime? now = null) =>
        TvPartyPresentations.Decide(showable, gameEnabled, gamesPermitted, status, finishedAt, now ?? Now);

    [Fact]
    public void A_party_that_cannot_be_shown_is_unavailable_whatever_its_game_is_doing()
    {
        foreach (var status in new string?[] { null, PartyGameStatuses.Lobby, PartyGameStatuses.Live, PartyGameStatuses.Finished })
            Assert.Equal(TvPartyPresentations.Unavailable, Decide(showable: false, status: status));
    }

    [Fact]
    public void Without_a_game_the_party_is_its_slideshow()
    {
        Assert.Equal(TvPartyPresentations.Slideshow, Decide(gameEnabled: false));
        // A host whose role no longer permits games is the same answer: the
        // display snapshot would refuse, so the television must not be sent to it.
        Assert.Equal(TvPartyPresentations.Slideshow, Decide(gamesPermitted: false, status: PartyGameStatuses.Live));
    }

    [Fact]
    public void The_game_takes_the_screen_before_the_first_match_and_while_one_is_played()
    {
        // No session row at all: a game that has not started has none, and the
        // lobby with its QR is exactly what the room should see.
        Assert.Equal(TvPartyPresentations.Game, Decide(status: null));
        Assert.Equal(TvPartyPresentations.Game, Decide(status: PartyGameStatuses.Lobby));
        Assert.Equal(TvPartyPresentations.Game, Decide(status: PartyGameStatuses.Live));
    }

    [Fact]
    public void A_finished_game_keeps_its_closing_card_for_the_dwell_and_then_gives_the_screen_back()
    {
        var finished = Now;
        Assert.Equal(TimeSpan.FromSeconds(15), TvPartyPresentations.FinishedDwell);

        Assert.Equal(TvPartyPresentations.Game,
            Decide(status: PartyGameStatuses.Finished, finishedAt: finished, now: finished));
        Assert.Equal(TvPartyPresentations.Game,
            Decide(status: PartyGameStatuses.Finished, finishedAt: finished,
                now: finished + TvPartyPresentations.FinishedDwell - TimeSpan.FromMilliseconds(1)));

        // Bounded: the closing card can never hold the television.
        Assert.Equal(TvPartyPresentations.Slideshow,
            Decide(status: PartyGameStatuses.Finished, finishedAt: finished,
                now: finished + TvPartyPresentations.FinishedDwell));
        Assert.Equal(TvPartyPresentations.Slideshow,
            Decide(status: PartyGameStatuses.Finished, finishedAt: finished, now: finished.AddHours(3)));

        // A finished row with no timestamp has nothing to dwell on.
        Assert.Equal(TvPartyPresentations.Slideshow, Decide(status: PartyGameStatuses.Finished));
    }

    [Fact]
    public void The_assignment_key_names_one_television_and_one_party_link_and_reveals_neither()
    {
        var tvA = Guid.NewGuid();
        var tvB = Guid.NewGuid();
        var linkA = Guid.NewGuid();
        var linkB = Guid.NewGuid();

        var key = TvPartyPresentations.AssignmentKey(tvA, linkA);
        Assert.Equal(key, TvPartyPresentations.AssignmentKey(tvA, linkA));
        // A different party — including a new link for the same album — is a
        // different key, which is what makes the television start again.
        Assert.NotEqual(key, TvPartyPresentations.AssignmentKey(tvA, linkB));
        // Per device, so it cannot be compared across televisions.
        Assert.NotEqual(key, TvPartyPresentations.AssignmentKey(tvB, linkA));

        Assert.Equal(24, key.Length);
        Assert.DoesNotContain(linkA.ToString("N"), key);
        Assert.DoesNotContain(linkA.ToString(), key);
        Assert.DoesNotContain(tvA.ToString("N"), key);
    }
}
