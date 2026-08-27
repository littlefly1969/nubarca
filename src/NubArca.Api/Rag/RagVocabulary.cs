namespace NubArca.Api.Rag;

/// What kind of document a source is.
///
/// A small closed set, because ranking has to be able to prefer a user guide
/// over a technical reference for a how-to question, and it can only do that if
/// the values mean the same thing everywhere. The Product Help vocabulary from
/// Slice 1 is preserved verbatim — those STRINGS are already in a shipped
/// corpus — and the code/config kinds are added beside it.
public static class RagSourceKinds
{
    // ---- product material (Slice 1 vocabulary, unchanged) ------------------
    public const string UserGuide = "user-guide";
    public const string UiContract = "ui-contract";
    public const string FeatureCatalog = "feature-catalog";
    public const string AdminGuide = "admin-guide";
    public const string TechnicalReference = "technical-reference";

    // ---- repository material ----------------------------------------------
    public const string SourceCode = "source-code";
    public const string Test = "test";
    public const string Migration = "migration";
    public const string Configuration = "configuration";
    public const string Script = "script";
    public const string Documentation = "documentation";
    public const string ExampleConfiguration = "example-configuration";
}

/// Natural language of a source's prose.
public static class RagLanguages
{
    public const string Italian = "it";
    public const string English = "en";
    public const string Unknown = "";
}

/// Programming or markup language, where a source has one. Derived from a
/// file's extension, which is reliable for THIS purpose (metadata for ranking
/// and display) and would not be reliable as a safety decision — see
/// RepositorySourcePolicy, which never trusts an extension to prove a file is
/// safe to index.
public static class RagCodeLanguages
{
    public const string CSharp = "csharp";
    public const string TypeScript = "typescript";
    public const string Tsx = "tsx";
    public const string JavaScript = "javascript";
    public const string Kotlin = "kotlin";
    public const string Markdown = "markdown";
    public const string Json = "json";
    public const string Yaml = "yaml";
    public const string Sql = "sql";
    public const string Shell = "shell";
    public const string Css = "css";
    public const string Xml = "xml";
    public const string Toml = "toml";
    public const string Text = "text";
    public const string None = "";
}

/// Metadata keys used inside a domain membership's MetadataJson.
///
/// Named constants rather than string literals scattered across the indexer,
/// the retriever and the ranking policy: these three have to agree, and a typo
/// in one of them is a silent ranking regression rather than a compile error.
public static class RagMetadataKeys
{
    public const string Feature = "feature";
    public const string Aliases = "aliases";
    public const string Audience = "audience";
    public const string Intent = "intent";
    public const string SourceKind = "sourceKind";
    public const string Language = "language";
    public const string Symbols = "symbols";
}

/// Who a Product Help source is written for, and what kind of answer it gives.
/// Preserved from Slice 1 — these are ranking inputs for the `product-help`
/// domain and are not required of every RAG source.
public static class RagAudiences
{
    public const string User = "user";
    public const string Admin = "admin";
    public const string Technical = "technical";
}

public static class RagIntents
{
    public const string HowTo = "how-to";
    public const string Explanation = "explanation";
    public const string Troubleshooting = "troubleshooting";
    public const string Reference = "reference";
}
