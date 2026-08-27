using System.Text.Json.Serialization;

namespace NubArca.Api.Rag.ProductHelp;

/// The controlled vocabulary the Product Help manifest classifies with.
///
/// These are now ALIASES of the generic RAG vocabulary rather than a second set
/// of strings. The values were always the same; keeping two declarations of
/// them meant a domain could be classified with a constant the retriever's
/// ranking profile does not recognise, and nothing would say so — the document
/// would simply stop being preferred.
public static class ProductHelpVocabulary
{
    public static class Audience
    {
        public const string User = RagAudiences.User;
        public const string Admin = RagAudiences.Admin;
        public const string Technical = RagAudiences.Technical;
    }

    public static class Intent
    {
        public const string HowTo = RagIntents.HowTo;
        public const string Explanation = RagIntents.Explanation;
        public const string Troubleshooting = RagIntents.Troubleshooting;
        public const string Reference = RagIntents.Reference;
    }

    public static class SourceKind
    {
        public const string UserGuide = RagSourceKinds.UserGuide;
        public const string UiContract = RagSourceKinds.UiContract;
        public const string FeatureCatalog = RagSourceKinds.FeatureCatalog;
        public const string AdminGuide = RagSourceKinds.AdminGuide;
        public const string TechnicalReference = RagSourceKinds.TechnicalReference;
    }

    public static class Language
    {
        public const string Italian = RagLanguages.Italian;
        public const string English = RagLanguages.English;
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
