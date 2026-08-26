namespace NubArca.Api.Help;

// LEGACY configuration, kept so an installation deployed before named model
// profiles existed keeps working.
//
// Bound from the "ExternalHelp" section (env: ExternalHelp__Enabled,
// ExternalHelp__BaseUrl, …) and adapted by AssistantModelResolver into exactly
// ONE model profile, always classified External. A configuration shape that
// predates the trust axis cannot assert a trust classification, and the safe
// reading of "an operator pointed this at a provider" is that the provider is a
// provider — so this path can never produce a LocalTrusted model, however the
// URL is written.
//
// It is a DEPRECATION PATH. New deployments configure `Assistant__*`, which is
// what the documentation describes; this section exists to keep an upgrade from
// silently turning Help off, and the `Assistant` section wins whenever it is
// configured at all.
//
// One thing deliberately did NOT survive the move: `AllowInsecureBaseUrl`. A
// switch that let an External model use plaintext transport is exactly the
// ambiguity the trust classification removes — a plaintext endpoint is now
// expressed as `Trust=LocalTrusted`, which is a statement about who holds the
// bytes rather than a hole in a statement about TLS.
//
// The API key lives HERE and nowhere else. It is never persisted, never returned
// by an endpoint, and never logged.
public sealed class ExternalHelpOptions
{
    public const string SectionName = "ExternalHelp";

    public bool Enabled { get; set; } = false;

    /// Provider root, e.g. https://api.example.com. The protocol adapter appends
    /// the OpenAI-compatible path. Operator configuration: a user can never
    /// supply this, or Help would become an arbitrary outbound HTTP client.
    public string BaseUrl { get; set; } = string.Empty;

    public string ApiKey { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    /// Shown in the UI so a person can see WHICH external service is involved.
    public string ProviderLabel { get; set; } = "External AI";

    public int TimeoutSeconds { get; set; } = 30;

    public int MaxOutputTokens { get; set; } = 800;

    // ---- bounds on what may be sent ---------------------------------------
    //
    // Carried across into AssistantHelpOptions, which is where they are clamped.

    public int MaxQuestionCharacters { get; set; } = 2000;

    public int MaxHistoryTurns { get; set; } = 8;

    public int MaxHistoryCharacters { get; set; } = 8000;

    public int MaxContextExcerpts { get; set; } = 6;

    public int MaxContextCharacters { get; set; } = 12000;

    /// Where the pre-built public product-help corpus lives inside the image.
    public string CorpusPath { get; set; } = "help-corpus.json";
}
