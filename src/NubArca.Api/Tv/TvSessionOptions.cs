namespace NubArca.Api.Tv;

public sealed class TvSessionOptions
{
    public const string SectionName = "Tv";

    public int PairingLifetimeMinutes { get; set; } = 10;
    public int SessionLifetimeDays { get; set; } = 30;
}
