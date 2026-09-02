namespace NubArca.Api.Domain.Print;

public sealed class PrintStation
{
    public Guid Id { get; set; }
    public Guid OwnerUserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public string DesiredState { get; set; } = PrintDesiredStates.Running;
    public string? CredentialHash { get; set; }
    public DateTime? LastSeenAt { get; set; }
    public string? AgentVersion { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
}

public static class PrintDesiredStates
{
    public const string Running = "running";
    public const string Paused = "paused";
    public const string Disabled = "disabled";

    public static bool IsValid(string value) => value is Running or Paused or Disabled;
}
