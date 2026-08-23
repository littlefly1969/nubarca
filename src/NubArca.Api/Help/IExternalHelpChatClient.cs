namespace NubArca.Api.Help;

/// One turn of a Help conversation, in the only three roles this feature uses.
public enum HelpChatRole
{
    System,
    User,
    Assistant,
}

public sealed record HelpChatMessage(HelpChatRole Role, string Text);

/// What came back, or why nothing did. `Reason` is a SANITIZED code — never a
/// provider body, never an exception message, never a URL — because it travels
/// to the browser.
public sealed record HelpChatResult(bool Ok, string? Text, string? Reason)
{
    public static HelpChatResult Success(string text) => new(true, text, null);
    public static HelpChatResult Failure(string reason) => new(false, null, reason);
}

/// The provider seam.
///
/// It takes MESSAGES and returns TEXT. That is the entire contract, and the
/// narrowness is the point: there is no place in this signature to pass tools,
/// function definitions, a tool choice, an attachment, an image, or a callback
/// the model could invoke. A capability that cannot be expressed here cannot be
/// granted by a later edit to a prompt.
///
/// Vendor neutrality is the other half: NubArca depends on this interface and on
/// its own DTOs, never on a provider SDK, so a second adapter is a new class
/// rather than a change to everything that talks to it.
public interface IExternalHelpChatClient
{
    Task<HelpChatResult> CompleteAsync(
        IReadOnlyList<HelpChatMessage> messages,
        CancellationToken cancellationToken = default);
}

/// Sanitized failure codes. The UI maps these to copy; none carries provider
/// detail.
public static class HelpFailureReasons
{
    public const string Disabled = "help_disabled";
    public const string NotConfigured = "help_not_configured";
    public const string ProviderUnauthorized = "provider_unauthorized";
    public const string ProviderRateLimited = "provider_rate_limited";
    public const string ProviderUnavailable = "provider_unavailable";
    public const string ProviderTimeout = "provider_timeout";
    public const string ProviderMalformed = "provider_malformed_response";
    public const string ProviderEmpty = "provider_empty_response";
    public const string KnowledgeUnavailable = "help_knowledge_unavailable";
}
