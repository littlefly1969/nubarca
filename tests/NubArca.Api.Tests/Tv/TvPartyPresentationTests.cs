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
        string? status = null, string? phase = null, DateTime? finishedAt = null,
        DateTime? now = null, bool guestbook = false) =>
        TvPartyPresentations.Decide(
            showable, gameEnabled, gamesPermitted, status, phase, finishedAt, now ?? Now, guestbook);

    // ── The guest book ──────────────────────────────────────────────────────

    [Fact]
    public void A_requested_guest_book_takes_a_screen_nothing_else_holds()
    {
        Assert.Equal(TvPartyPresentations.Guestbook, Decide(gameEnabled: false, guestbook: true));
        // A paused game has handed the screen back, so the book may have it…
        Assert.Equal(TvPartyPresentations.Guestbook, Decide(
            status: PartyGameStatuses.Live, phase: PartyGamePhases.Intermission, guestbook: true));
        // …and so has a finished game once its closing card has had its time.
        Assert.Equal(TvPartyPresentations.Guestbook, Decide(
            status: PartyGameStatuses.Finished, phase: PartyGamePhases.Finished,
            finishedAt: Now.AddSeconds(-16), guestbook: true));
        // Not requested (or nothing to show, which the caller folds in): slideshow.
        Assert.Equal(TvPartyPresentations.Slideshow, Decide(gameEnabled: false, guestbook: false));
    }

    [Fact]
    public void The_game_wins_over_the_guest_book_whenever_it_holds_the_screen()
    {
        // The defensive rule. Normal commands never let both be held; should the
        // stored state claim both anyway, the game — which may be mid-activity —
        // is what the room sees.
        Assert.Equal(TvPartyPresentations.Game, Decide(guestbook: true)); // the lobby, before any match
        foreach (var phase in PartyGamePhases.All.Where(p => p != PartyGamePhases.Intermission))
            Assert.Equal(TvPartyPresentations.Game, Decide(
                status: PartyGameStatuses.Live, phase: phase, guestbook: true));
        // Including the finished game's closing card, for its whole dwell.
        Assert.Equal(TvPartyPresentations.Game, Decide(
            status: PartyGameStatuses.Finished, phase: PartyGamePhases.Finished,
            finishedAt: Now.AddSeconds(-5), guestbook: true));
    }

    [Fact]
    public void A_party_that_cannot_be_shown_is_unavailable_even_with_its_book_requested()
    {
        Assert.Equal(TvPartyPresentations.Unavailable, Decide(showable: false, guestbook: true));
    }

    [Fact]
    public void Game_holds_the_screen_is_exactly_when_the_rule_answers_game()
    {
        // One question, asked by the television's rule and by the guest book's
        // command: they must never disagree.
        var statuses = new string?[] { null, PartyGameStatuses.Lobby, PartyGameStatuses.Live, PartyGameStatuses.Finished };
        var phases = PartyGamePhases.All.Cast<string?>().Append(null);
        foreach (var enabled in new[] { true, false })
        foreach (var permitted in new[] { true, false })
        foreach (var status in statuses)
        foreach (var phase in phases)
        foreach (var finishedAt in new DateTime?[] { null, Now.AddSeconds(-5), Now.AddSeconds(-60) })
        {
            var holds = TvPartyPresentations.GameHoldsTheScreen(enabled, permitted, status, phase, finishedAt, Now);
            var decided = Decide(gameEnabled: enabled, gamesPermitted: permitted, status: status,
                phase: phase, finishedAt: finishedAt, guestbook: true);
            Assert.Equal(holds, decided == TvPartyPresentations.Game);
            Assert.Equal(!holds, decided == TvPartyPresentations.Guestbook);
        }
    }

    [Fact]
    public void An_intermission_hands_the_television_back_with_no_dwell()
    {
        // The game is LIVE and paused, which is the whole point: the status says
        // nothing has ended, and only the phase says the room has the screen.
        Assert.Equal(TvPartyPresentations.Slideshow, Decide(
            status: PartyGameStatuses.Live, phase: PartyGamePhases.Intermission));

        // No dwell, unlike a finished match: the answer does not depend on when.
        Assert.Equal(TvPartyPresentations.Slideshow, Decide(
            status: PartyGameStatuses.Live, phase: PartyGamePhases.Intermission,
            finishedAt: Now, now: Now.AddSeconds(1)));

        // And it is the phase alone that says so — every other phase of a live
        // game keeps the television.
        foreach (var phase in PartyGamePhases.All.Where(p => p != PartyGamePhases.Intermission))
            Assert.Equal(TvPartyPresentations.Game, Decide(status: PartyGameStatuses.Live, phase: phase));
    }

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
