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
    /// <summary>The source commit the agent was built from, as it reports it; null from an agent that does not.</summary>
    public string? AgentBuild { get; set; }
    /// <summary>The agent said it can update itself (a Print Box with the update service installed).</summary>
    public bool AgentSelfUpdate { get; set; }
    /// <summary>The version an administrator or the owner asked this agent to update to; null when none is pending.</summary>
    public string? AgentUpdateTarget { get; set; }
    public DateTime? AgentUpdateRequestedAt { get; set; }
    /// <summary>How the last requested update ended: <c>ok</c> or <c>failed</c>; null before the first.</summary>
    public string? AgentUpdateResult { get; set; }
    /// <summary>The version that last update was for.</summary>
    public string? AgentUpdateResultVersion { get; set; }
    public DateTime? AgentUpdateResultAt { get; set; }
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
