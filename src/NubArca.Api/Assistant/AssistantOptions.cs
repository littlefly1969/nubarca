using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace NubArca.Api.Assistant;

// Operator configuration for the Assistant substrate.
//
// Bound from the "Assistant" section:
//
//   Assistant__Enabled=true
//   Assistant__PrivateKnowledgeModel=documents-local
//   Assistant__Models__documents-local__Protocol=OpenAiCompatible
//   Assistant__Models__documents-local__Trust=LocalTrusted
//   Assistant__Models__documents-local__BaseUrl=http://model-host:8080
//   Assistant__Models__documents-local__Model=...
//
// NAMED PROFILES rather than one set of provider fields, because "which model
// does this feature use" should be a configuration answer rather than a code
// change in the feature.
//
// OFF by default. An installation that configures nothing makes no outbound
// call and presents no surface implying one is possible.
public sealed class AssistantOptions
{
    public const string SectionName = "Assistant";

    public bool Enabled { get; set; } = false;

    /// Which entry of `Models` answers questions about the caller's OWN
    /// DOCUMENTS (`Assistant__PrivateKnowledgeModel=my-local-llm`).
    ///
    /// There is no fallback of any kind: unset means the private feature is off.
    ///
    /// The named profile must additionally be `LocalTrusted`. That is checked in
    /// AssistantModelResolver, not here, because it is a property of the resolved
    /// model rather than of the string.
    public string PrivateKnowledgeModel { get; set; } = string.Empty;

    /// Case-insensitive because a profile name is an operator's label, and
    /// `PrivateKnowledgeModel: Documents-Local` matching `Models: documents-local`
    /// is the behaviour a person expects from configuration.
    public Dictionary<string, AssistantModelOptions> Models { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);

    public AssistantBoundsOptions Bounds { get; set; } = new();

    /// Binds the section, and carries over any bound still written under the
    /// pre-0.4.6 `Assistant:Help:*` name. Those keys bounded the document
    /// assistant too, so dropping them on upgrade would silently LOOSEN an
    /// installation's limits to the defaults. A key set under `Bounds` wins.
    public static IServiceCollection Register(IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(SectionName);
        services.Configure<AssistantOptions>(section);
        services.PostConfigure<AssistantOptions>(options => ApplyLegacyBounds(section, options));
        return services;
    }

    internal static void ApplyLegacyBounds(IConfiguration section, AssistantOptions options)
    {
        var current = section.GetSection(nameof(Bounds));
        var legacy = section.GetSection("Help").GetChildren()
            .Where(c => c.Value is not null && current[c.Key] is null)
            .ToDictionary(c => c.Key, c => c.Value);
        if (legacy.Count == 0) return;

        new ConfigurationBuilder().AddInMemoryCollection(legacy).Build().Bind(options.Bounds);
    }
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

/// Bounds on what a question to the Assistant may carry
/// (`Assistant__Bounds__MaxQuestionCharacters`, …).
///
/// Configuration rather than constants because a deployment may want them
/// TIGHTER, never because it may want them absent: every accessor below is
/// clamped, so a hand-edited value can narrow a boundary and cannot remove one.
public sealed class AssistantBoundsOptions
{
    public int MaxQuestionCharacters { get; set; } = 2000;

    public int MaxHistoryTurns { get; set; } = 8;

    public int MaxHistoryCharacters { get; set; } = 8000;

    public int MaxEvidenceChunks { get; set; } = 6;

    public int MaxEvidenceCharacters { get; set; } = 12000;

    public int EffectiveQuestionCharacters => Math.Clamp(MaxQuestionCharacters, 1, 8000);
    public int EffectiveHistoryTurns => Math.Clamp(MaxHistoryTurns, 0, 20);
    public int EffectiveHistoryCharacters => Math.Clamp(MaxHistoryCharacters, 0, 32000);
    public int EffectiveEvidenceChunks => Math.Clamp(MaxEvidenceChunks, 0, 20);
    public int EffectiveEvidenceCharacters => Math.Clamp(MaxEvidenceCharacters, 0, 60000);
}
