namespace NubArca.Api.Assistant;

// Operator configuration for the Assistant substrate.
//
// Bound from the "Assistant" section:
//
//   Assistant__Enabled=true
//   Assistant__HelpModel=help-default
//   Assistant__Models__help-default__Protocol=OpenAiCompatible
//   Assistant__Models__help-default__Trust=External
//   Assistant__Models__help-default__BaseUrl=https://provider.example
//   Assistant__Models__help-default__ApiKey=...
//   Assistant__Models__help-default__Model=...
//
// NAMED PROFILES rather than one set of provider fields, because a later
// Assistant will want a different model for Help than for routing or
// generation, and "which model does this feature use" should be a configuration
// answer rather than a code change in the feature.
//
// OFF by default. An installation that configures nothing makes no outbound
// call and presents no surface implying one is possible.
public sealed class AssistantOptions
{
    public const string SectionName = "Assistant";

    public bool Enabled { get; set; } = false;

    /// Which entry of `Models` the Help feature uses.
    public string HelpModel { get; set; } = string.Empty;

    /// Case-insensitive because a profile name is an operator's label, and
    /// `HelpModel: Help-Default` matching `Models: help-default` is the
    /// behaviour a person expects from configuration.
    public Dictionary<string, AssistantModelOptions> Models { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);

    public AssistantHelpOptions Help { get; set; } = new();
}

/// One named model endpoint.
///
/// `Trust` has NO default. An enabled profile that does not state it is invalid
/// rather than assumed — see AssistantModelResolver. Defaulting would mean
/// either silently treating an unclassified endpoint as external (annoying) or
/// as local (a leak), and the second failure mode is the reason this field
/// exists at all.
public sealed class AssistantModelOptions
{
    public string Protocol { get; set; } = nameof(AssistantModelProtocol.OpenAiCompatible);

    public string Trust { get; set; } = string.Empty;

    /// Endpoint root; the protocol adapter appends the path. Operator
    /// configuration: a user can never supply this, or the Assistant would
    /// become an arbitrary outbound HTTP client.
    public string BaseUrl { get; set; } = string.Empty;

    /// Optional for a LocalTrusted endpoint — most local OpenAI-compatible
    /// servers want no auth. Required for External. Either way it lives HERE and
    /// nowhere else: never persisted, never returned by an endpoint, never
    /// logged.
    public string ApiKey { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    /// Shown in the UI so a person can see WHICH service is involved.
    /// Deliberately a label rather than the URL: the boundary a user needs to
    /// understand is "external provider" or "the operator's own endpoint", and
    /// the host is operator detail.
    public string Label { get; set; } = string.Empty;

    public int TimeoutSeconds { get; set; } = 30;

    public int MaxOutputTokens { get; set; } = 800;
}

/// Bounds on what a Help request may carry, and where the approved product
/// knowledge lives.
///
/// Configuration rather than constants because a deployment may want them
/// TIGHTER, never because it may want them absent: every accessor below is
/// clamped, so a hand-edited value can narrow a boundary and cannot remove one.
public sealed class AssistantHelpOptions
{
    public int MaxQuestionCharacters { get; set; } = 2000;

    public int MaxHistoryTurns { get; set; } = 8;

    public int MaxHistoryCharacters { get; set; } = 8000;

    public int MaxEvidenceChunks { get; set; } = 6;

    public int MaxEvidenceCharacters { get; set; } = 12000;

    /// Where the pre-built public product-help corpus lives inside the image.
    public string CorpusPath { get; set; } = "help-corpus.json";

    public int EffectiveQuestionCharacters => Math.Clamp(MaxQuestionCharacters, 1, 8000);
    public int EffectiveHistoryTurns => Math.Clamp(MaxHistoryTurns, 0, 20);
    public int EffectiveHistoryCharacters => Math.Clamp(MaxHistoryCharacters, 0, 32000);
    public int EffectiveEvidenceChunks => Math.Clamp(MaxEvidenceChunks, 0, 20);
    public int EffectiveEvidenceCharacters => Math.Clamp(MaxEvidenceCharacters, 0, 60000);
}
