using NubArca.Api.Party;

namespace NubArca.Api.Tests.Party;

public sealed class PartyChallengePolicyTests
{
    [Theory]
    [InlineData(0, 30)]
    [InlineData(20, 50)]
    [InlineData(999, 60)]
    public void Deadline_is_inside_inclusive_range(int sample, int seconds)
    {
        var now = new DateTime(2026, 8, 31, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(now.AddSeconds(seconds), PartyChallengePolicy.NextDeadline(now, 30, 60, sample));
    }

    [Fact]
    public void Selects_most_voted_remaining()
    {
        var winner = Guid.NewGuid();
        var got = PartyChallengePolicy.Select([
            new(Guid.NewGuid(), 2), new(winner, 9), new(Guid.NewGuid(), 4)], 0);
        Assert.Equal(winner, got);
    }

    [Fact]
    public void Tie_break_is_deterministic_from_sample()
    {
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() }.Order().ToArray();
        Assert.Equal(ids[0], PartyChallengePolicy.Select([
            new(ids[1], 3), new(ids[0], 3)], 0));
        Assert.Equal(ids[1], PartyChallengePolicy.Select([
            new(ids[1], 3), new(ids[0], 3)], 1));
    }

    [Fact]
    public void Disabled_and_completed_are_excluded()
    {
        var winner = Guid.NewGuid();
        Assert.Equal(winner, PartyChallengePolicy.Select([
            new(Guid.NewGuid(), 99, IsEnabled: false),
            new(Guid.NewGuid(), 80, IsCompleted: true),
            new(winner, 1)], 0));
    }

    [Fact]
    public void Zero_votes_falls_back_to_tie_break_and_empty_returns_null()
    {
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() }.Order().ToArray();
        Assert.Equal(ids[1], PartyChallengePolicy.Select([new(ids[0], 0), new(ids[1], 0)], 1));
        Assert.Null(PartyChallengePolicy.Select([], 0));
    }
}
