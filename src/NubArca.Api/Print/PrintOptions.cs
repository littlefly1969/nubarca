namespace NubArca.Api.Print;

public sealed class PrintOptions
{
    public const string SectionName = "Print";

    public int EnrollmentMinutes { get; set; } = 10;
    public int HeartbeatOnlineSeconds { get; set; } = 90;
    public int HeartbeatOfflineSeconds { get; set; } = 300;
    // How long a claimed job waits for its agent's "submitting" before it is
    // handed out again. Only the station's own agent can claim it, one job at
    // a time, so this bounds how long a claim lost to a network drop (its
    // response never arrived, or its download failed) holds the queue. The
    // claim token stays valid after it, so a slow download is not cut short.
    public int ClaimLeaseSeconds { get; set; } = 30;
}
