namespace NubArca.Api.Print;

public sealed class PrintOptions
{
    public const string SectionName = "Print";

    public int EnrollmentMinutes { get; set; } = 10;
    public int HeartbeatOnlineSeconds { get; set; } = 90;
    public int HeartbeatOfflineSeconds { get; set; } = 300;
    public int ClaimLeaseSeconds { get; set; } = 120;
}
