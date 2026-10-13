namespace NubArca.Api.Print;

/// <summary>An agent older than <see cref="PrintAgentCompatibility.MinimumVersion"/> asked to enroll.</summary>
public sealed class PrintAgentIncompatibleException : Exception
{
    public PrintAgentIncompatibleException()
        : base("agent_incompatible")
    {
    }
}
