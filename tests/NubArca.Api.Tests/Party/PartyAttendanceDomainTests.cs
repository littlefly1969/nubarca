using NubArca.Api.Domain;
using NubArca.Api.Party;

namespace NubArca.Api.Tests.Party;

/// <summary>
/// The attendance rules as pure functions: the counts, when arrivals may be
/// written, and where one may come from. The service and the endpoints ask
/// these rather than restating them.
/// </summary>
public sealed class PartyAttendanceDomainTests
{
    private const string Attending = PartyRsvpStatuses.Attending;
    private const string Pending = PartyRsvpStatuses.Pending;
    private const string Declined = PartyRsvpStatuses.Declined;

    [Fact]
    public void An_open_party_counts_only_the_people_recorded_and_expects_nobody()
    {
        var summary = PartyAttendanceService.Summarize([], otherArrivals: 3);

        Assert.Equal(new PartyAttendanceSummaryDto(0, 0, 0, 0, 3, 3), summary);
    }

    [Fact]
    public void An_invited_party_counts_every_combination_of_what_was_declared_and_who_came()
    {
        var summary = PartyAttendanceService.Summarize(
        [
            (Attending, true),  // confirmed and came
            (Attending, false), // confirmed, not here (yet)
            (Attending, false),
            (Pending, true),    // never answered, came
            (Declined, true),   // said no, came anyway
            (Declined, false),  // said no, stayed home
            (Pending, false),
        ], otherArrivals: 0);

        Assert.Equal(3, summary.ExpectedPeople);
        Assert.Equal(1, summary.ExpectedArrived);
        Assert.Equal(2, summary.ExpectedMissing);
        Assert.Equal(2, summary.UnexpectedKnownGuests);
        Assert.Equal(0, summary.OtherArrivals);
        Assert.Equal(3, summary.TotalArrivals);
    }

    [Fact]
    public void A_mixed_party_adds_the_people_not_on_the_list_to_the_arrivals_and_to_nothing_expected()
    {
        var summary = PartyAttendanceService.Summarize([(Attending, true), (Attending, false)], otherArrivals: 2);

        Assert.Equal(new PartyAttendanceSummaryDto(2, 1, 1, 0, 2, 3), summary);
        // Every arrival is either an expected one or not: nothing is counted twice.
        Assert.Equal(
            summary.TotalArrivals,
            summary.ExpectedArrived + summary.UnexpectedKnownGuests + summary.OtherArrivals);
    }

    [Theory]
    [InlineData(PartyStatuses.Draft, false)]
    [InlineData(PartyStatuses.Published, false)]
    [InlineData(PartyStatuses.Live, true)]
    [InlineData(PartyStatuses.Ended, true)]
    [InlineData("archived", false)]
    [InlineData(null, false)]
    public void Arrivals_are_written_while_the_party_is_on_and_after_it(string? status, bool open) =>
        Assert.Equal(open, PartyAttendancePolicy.IsOpen(status));

    [Fact]
    public void An_arrival_comes_from_the_host_or_from_the_group_and_nowhere_else_yet()
    {
        Assert.True(PartyAttendanceSources.IsKnown("owner"));
        Assert.True(PartyAttendanceSources.IsKnown("invitation"));
        Assert.False(PartyAttendanceSources.IsKnown("kiosk"));
        Assert.False(PartyAttendanceSources.IsKnown("qr"));
        Assert.False(PartyAttendanceSources.IsKnown(null));
        // A new row says who recorded it; it never defaults to a guest.
        Assert.Equal(PartyAttendanceSources.Owner, new PartyGuestAttendance().Source);
    }
}
