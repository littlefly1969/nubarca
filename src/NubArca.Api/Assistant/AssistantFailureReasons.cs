namespace NubArca.Api.Assistant;

/// Sanitized failure codes. These travel to the browser, which maps them to
/// copy, so none of them carries a provider body, a URL, a configuration value
/// or an exception message.
///
/// The string VALUES are the wire contract and are deliberately unchanged from
/// the external-Help-only era: an older browser bundle still maps them.
public static class AssistantFailureReasons
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

    /// The corpus is present and healthy, and nothing in it answers the
    /// question well enough to be worth grounding on. Distinct from
    /// KnowledgeUnavailable because the operator has nothing to fix.
    public const string NoSupportingKnowledge = "help_no_supporting_knowledge";
}
