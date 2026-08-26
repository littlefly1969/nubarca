using System.Text.Json.Serialization;

namespace NubArca.Api.Rag.ProductHelp;

/// Controlled vocabularies for the first domain.
///
/// Small closed sets rather than free tags: ranking has to be able to prefer a
/// user guide over a technical reference for a how-to question, and it can only
/// do that if the values mean the same thing in every document. An open tag
/// space would make every new source a new ranking special case.
public static class ProductHelpVocabulary
{
    public static class Audience
    {
        public const string User = "user";
        public const string Admin = "admin";
        public const string Technical = "technical";
    }

    public static class Intent
    {
        public const string HowTo = "how-to";
        public const string Explanation = "explanation";
        public const string Troubleshooting = "troubleshooting";
        public const string Reference = "reference";
    }

    public static class SourceKind
    {
        public const string UserGuide = "user-guide";
        public const string UiContract = "ui-contract";
        public const string FeatureCatalog = "feature-catalog";
        public const string AdminGuide = "admin-guide";
        public const string TechnicalReference = "technical-reference";
    }

    public static class Language
    {
        public const string Italian = "it";
        public const string English = "en";
    }
}

/// One retrievable chunk of PUBLIC product material, with the metadata that
/// makes it rankable.
///
/// `Priority` is the manifest's editorial judgement about how much this source
/// should be trusted to answer a product question, 1–100. It multiplies the
/// lexical score rather than replacing it, so a high-priority document still has
/// to actually match.
public sealed record ProductHelpDocument(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("section")] string Section,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("feature")] string Feature,
    [property: JsonPropertyName("intent")] string Intent,
    [property: JsonPropertyName("audience")] string Audience,
    [property: JsonPropertyName("language")] string Language,
    [property: JsonPropertyName("sourceKind")] string SourceKind,
    [property: JsonPropertyName("aliases")] IReadOnlyList<string> Aliases,
    [property: JsonPropertyName("priority")] int Priority);

/// The bounded public knowledge NubArca is willing to retrieve from.
///
/// `Revision` is the source commit the corpus was built from. The running
/// application refuses a corpus whose revision does not match its own, so Help
/// cannot describe a feature the installed release does not have — an operator
/// being told to click something that is not there is worse than no Help.
public sealed record ProductHelpCorpus(
    [property: JsonPropertyName("domain")] string Domain,
    [property: JsonPropertyName("revision")] string Revision,
    [property: JsonPropertyName("documents")] IReadOnlyList<ProductHelpDocument> Documents)
{
    public static ProductHelpCorpus Empty { get; } = new(
        RagDomainKey.ProductHelp.Value, string.Empty, Array.Empty<ProductHelpDocument>());
}
