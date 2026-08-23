namespace NubArca.Api.Help;

// Configuration for the optional external Smart Help assistant.
//
// Bound from the "ExternalHelp" section (env: ExternalHelp__Enabled,
// ExternalHelp__BaseUrl, ...). OFF by default: an installation that configures
// nothing must never make an outbound call, and must never present a Help
// surface that implies one is possible.
//
// The API key lives HERE and nowhere else. It is never persisted, never returned
// by an endpoint, and never logged — see ExternalHelpService and HelpEndpoints,
// which are written so that the key has no path to either.
public sealed class ExternalHelpOptions
{
    public const string SectionName = "ExternalHelp";

    public bool Enabled { get; set; } = false;

    /// Provider root, e.g. https://api.example.com. The client appends the
    /// OpenAI-compatible path. Operator configuration: a user can never supply
    /// this, or Help would become an arbitrary outbound HTTP client.
    public string BaseUrl { get; set; } = string.Empty;

    public string ApiKey { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    /// Shown in the UI so a person can see WHICH external service is involved.
    /// Deliberately a label rather than the URL: the boundary the user needs to
    /// understand is "an external provider", and the host is operator detail.
    public string ProviderLabel { get; set; } = "External AI";

    public int TimeoutSeconds { get; set; } = 30;

    public int MaxOutputTokens { get; set; } = 800;

    // ---- bounds on what may be sent ---------------------------------------
    //
    // Limits are configuration rather than constants because a deployment may
    // want them tighter, never because it may want them absent: each one is
    // clamped below.

    public int MaxQuestionCharacters { get; set; } = 2000;

    public int MaxHistoryTurns { get; set; } = 8;

    public int MaxHistoryCharacters { get; set; } = 8000;

    public int MaxContextExcerpts { get; set; } = 6;

    public int MaxContextCharacters { get; set; } = 12000;

    /// Where the pre-built public knowledge corpus lives inside the image.
    public string CorpusPath { get; set; } = "help-corpus.json";

    /// Allow a plaintext provider URL. Off by default: a key travels in the
    /// Authorization header of every request, so http:// would put it on the
    /// wire. Exists for a local test double, never for production.
    public bool AllowInsecureBaseUrl { get; set; } = false;

    // Clamped accessors. Callers use these, so a hand-edited configuration
    // cannot widen a boundary to zero or to something unbounded.
    public int EffectiveQuestionCharacters => Math.Clamp(MaxQuestionCharacters, 1, 8000);
    public int EffectiveHistoryTurns => Math.Clamp(MaxHistoryTurns, 0, 20);
    public int EffectiveHistoryCharacters => Math.Clamp(MaxHistoryCharacters, 0, 32000);
    public int EffectiveContextExcerpts => Math.Clamp(MaxContextExcerpts, 0, 20);
    public int EffectiveContextCharacters => Math.Clamp(MaxContextCharacters, 0, 60000);
    public int EffectiveTimeoutSeconds => Math.Clamp(TimeoutSeconds, 1, 120);
    public int EffectiveMaxOutputTokens => Math.Clamp(MaxOutputTokens, 1, 4000);

    /// Configured well enough to attempt a call at all.
    public bool IsUsable =>
        Enabled
        && !string.IsNullOrWhiteSpace(BaseUrl)
        && !string.IsNullOrWhiteSpace(ApiKey)
        && !string.IsNullOrWhiteSpace(Model)
        && (AllowInsecureBaseUrl
            || BaseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
}
