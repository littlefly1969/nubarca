using NubArca.Api.Cast;
using Xunit;

namespace NubArca.Api.Tests.Cast;

// A grant lifetime is a security parameter an operator types into a .env file.
// It has a safe answer for every input, so an absurd value is corrected rather
// than allowed to take an installation's API down at startup.
public sealed class CastOptionsTests
{
    [Theory]
    [InlineData(0, CastOptions.MinimumGrantLifetimeMinutes)]
    [InlineData(-100, CastOptions.MinimumGrantLifetimeMinutes)]
    [InlineData(1, CastOptions.MinimumGrantLifetimeMinutes)]
    [InlineData(29, CastOptions.MinimumGrantLifetimeMinutes)]
    [InlineData(30, 30)]
    [InlineData(360, 360)]
    [InlineData(720, 720)]
    [InlineData(721, CastOptions.MaximumGrantLifetimeMinutes)]
    [InlineData(100_000, CastOptions.MaximumGrantLifetimeMinutes)]
    public void An_Absurd_Lifetime_Is_Clamped_To_The_Allowed_Range(int configured, int expected)
    {
        var options = new CastOptions { GrantLifetimeMinutes = configured };

        Assert.Equal(expected, options.EffectiveGrantLifetimeMinutes);
        Assert.Equal(TimeSpan.FromMinutes(expected), options.EffectiveGrantLifetime);
    }

    [Fact]
    public void The_Default_Lifetime_Is_Six_Hours()
    {
        Assert.Equal(360, new CastOptions().EffectiveGrantLifetimeMinutes);
    }

    [Fact]
    public void Receiver_Origins_Are_Normalised_And_Deduplicated()
    {
        var options = new CastOptions
        {
            AllowedReceiverOrigins =
            [
                "  https://receiver.test.invalid/  ",
                "https://receiver.test.invalid",
                string.Empty,
                "   ",
                "https://second.test.invalid",
            ],
        };

        Assert.Equal(
            ["https://receiver.test.invalid", "https://second.test.invalid"],
            options.NormalizedReceiverOrigins);
    }

    [Fact]
    public void No_Receiver_Origin_Is_Configured_By_Default()
    {
        Assert.Empty(new CastOptions().NormalizedReceiverOrigins);
    }
}
