using static NubArca.Api.Rag.ProductHelp.ProductHelpVocabulary;

namespace NubArca.Api.Rag.ProductHelp;

/// One approved Product Help source, and what it is.
///
/// `Feature` and `Aliases` are the vocabulary a person would use for the thing
/// the document is about, in both interface languages. They are ranked far above
/// body text, which is how an Italian question about "volti" reaches the Faces
/// guide instead of whichever technical document happens to contain the most
/// words.
public sealed record ProductHelpSource(
    string Path,
    string Feature,
    string Audience,
    string Intent,
    string SourceKind,
    string Language,
    int Priority,
    IReadOnlyList<string> Aliases);

/// THE MANIFEST: the complete list of documents Product Help may answer from.
///
/// This replaces "every `docs/**.md`, automatically". That rule was convenient
/// and wrong in one specific way: it made an operations runbook and a model
/// benchmark compete, on equal footing, with the guidance a person asking "how
/// do I use faces?" actually needs — and the runbooks are longer, so they often
/// won.
///
/// It is still an ALLOWLIST rather than a denylist of secrets. A denylist is a
/// claim to have thought of everything; a manifest is a statement of what was
/// deliberately included, so an `.env`, an operator Compose override, a source
/// file, a build output, a scratch file and an internal implementation plan are
/// all out by construction — as is any new document nobody added here.
///
/// The cost is that a new product document must be named to become Help
/// knowledge. That is the intended trade: adding a line is cheap, and it is the
/// moment to say who the document is for.
public static class ProductHelpSources
{
    /// Every alias in the manifest, lowercased, is also expanded at query time —
    /// see ProductHelpAliases.
    public static IReadOnlyList<ProductHelpSource> Manifest { get; } = new[]
    {
        // ---- user-facing product guidance -----------------------------------
        //
        // Highest priority: these are written FOR the person asking, and they
        // are the answer to a how-to question when they match at all.
        new ProductHelpSource(
            "docs/help/faces.md", "faces", Audience.User, Intent.HowTo,
            SourceKind.UserGuide, Language.Italian, 100,
            new[]
            {
                "volti", "volto", "faccia", "facce", "persone", "persona",
                "riconoscimento facciale", "gruppi suggeriti", "face", "faces",
                "people", "person", "face recognition",
            }),
        new ProductHelpSource(
            "docs/help/faces.en.md", "faces", Audience.User, Intent.HowTo,
            SourceKind.UserGuide, Language.English, 100,
            new[]
            {
                "faces", "face", "people", "person", "face recognition",
                "suggested groups", "volti", "persone", "riconoscimento facciale",
            }),

        // ---- product overview ------------------------------------------------
        new ProductHelpSource(
            "README.md", "nubarca", Audience.User, Intent.Explanation,
            SourceKind.FeatureCatalog, Language.English, 70,
            new[] { "nubarca", "overview", "panoramica", "getting started" }),

        // ---- feature documentation -------------------------------------------
        //
        // Product behaviour a user can meet, written for a reader who is
        // technical. Below the user guides, above the runbooks.
        new ProductHelpSource(
            "docs/media-wall.md", "media library", Audience.User, Intent.Explanation,
            SourceKind.UiContract, Language.English, 60,
            new[] { "media wall", "griglia", "grid", "libreria", "library", "gallery", "galleria" }),
        new ProductHelpSource(
            "docs/multimodal-photo-search.md", "photo search", Audience.User, Intent.Explanation,
            SourceKind.TechnicalReference, Language.English, 45,
            new[] { "search", "ricerca", "semantic search", "ricerca semantica", "siglip" }),
        new ProductHelpSource(
            "docs/photo-date-taken-organizer.md", "dates", Audience.User, Intent.Explanation,
            SourceKind.UiContract, Language.English, 55,
            new[] { "date", "data", "datetaken", "data scatto", "organizer", "timeline" }),
        new ProductHelpSource(
            "docs/google-cast.md", "casting", Audience.User, Intent.Explanation,
            SourceKind.UiContract, Language.English, 55,
            new[] { "cast", "chromecast", "google cast", "trasmetti", "tv" }),
        new ProductHelpSource(
            "docs/media-derivatives.md", "thumbnails", Audience.Admin, Intent.Troubleshooting,
            SourceKind.AdminGuide, Language.English, 45,
            new[] { "thumbnail", "miniatura", "anteprima", "preview", "poster", "derivative" }),
        new ProductHelpSource(
            "docs/brand.md", "brand", Audience.User, Intent.Reference,
            SourceKind.FeatureCatalog, Language.English, 40,
            new[] { "brand", "marchio", "logo", "identity" }),

        // ---- administration ---------------------------------------------------
        new ProductHelpSource(
            "docs/OPERATIONS.md", "operations", Audience.Admin, Intent.Reference,
            SourceKind.AdminGuide, Language.English, 30,
            new[] { "operations", "operazioni", "backup", "restore", "deploy", "runbook" }),
        new ProductHelpSource(
            "docs/job-scheduling.md", "background jobs", Audience.Admin, Intent.Reference,
            SourceKind.AdminGuide, Language.English, 30,
            new[] { "job", "jobs", "processi", "background", "queue", "coda" }),
        new ProductHelpSource(
            "docs/ai-substrate.md", "ai", Audience.Admin, Intent.Reference,
            SourceKind.AdminGuide, Language.English, 30,
            new[] { "ai", "ia", "intelligenza artificiale", "model", "modello", "profile", "profilo" }),
        new ProductHelpSource(
            "docs/help-assistant.md", "assistant", Audience.Admin, Intent.Reference,
            SourceKind.AdminGuide, Language.English, 30,
            new[] { "help", "aiuto", "assistant", "assistente", "smart help", "chiedi a nubarca" }),
        new ProductHelpSource(
            "docs/tv-platform-contract.md", "nubarca tv", Audience.Admin, Intent.Reference,
            SourceKind.AdminGuide, Language.English, 30,
            new[] { "tv", "android tv", "televisore", "telecomando", "remote" }),

        // ---- technical reference ----------------------------------------------
        //
        // Deliberately the LOWEST priority. These are true and useful, and they
        // are not what "how do I…" is asking for; they used to outrank the
        // answer because they are long.
        new ProductHelpSource(
            "ARCHITECTURE.md", "architecture", Audience.Technical, Intent.Reference,
            SourceKind.TechnicalReference, Language.English, 20,
            new[] { "architecture", "architettura", "design", "internals" }),
        new ProductHelpSource(
            "docs/testing.md", "testing", Audience.Technical, Intent.Reference,
            SourceKind.TechnicalReference, Language.English, 15,
            new[] { "test", "testing", "suite" }),
        new ProductHelpSource(
            "docs/development-environment.md", "development", Audience.Technical, Intent.Reference,
            SourceKind.TechnicalReference, Language.English, 15,
            new[] { "development", "sviluppo", "dev environment" }),
        new ProductHelpSource(
            "docs/ai-photo-profile-lifecycle.md", "ai profiles", Audience.Technical, Intent.Reference,
            SourceKind.TechnicalReference, Language.English, 15,
            new[] { "profile", "profilo", "embedding", "lifecycle" }),
        new ProductHelpSource(
            "docs/ai-photo-pgvector.md", "ai vectors", Audience.Technical, Intent.Reference,
            SourceKind.TechnicalReference, Language.English, 15,
            new[] { "pgvector", "vector", "vettore", "ann", "similarity" }),
    };

    private static readonly Dictionary<string, ProductHelpSource> ByPath =
        Manifest.ToDictionary(s => Normalize(s.Path), StringComparer.Ordinal);

    /// Public so a test can assert the boundary directly rather than inferring
    /// it from what happened to be indexed.
    public static bool IsApproved(string relativePath) => Find(relativePath) is not null;

    public static ProductHelpSource? Find(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return null;
        var normalized = Normalize(relativePath);
        // No traversal and no hidden segment can reach the manifest, but the
        // check is stated rather than implied: an approved path is compared as a
        // literal, so `docs/../.env` matches nothing even before this.
        if (normalized.Contains("..", StringComparison.Ordinal)) return null;
        if (normalized.Split('/').Any(s => s.StartsWith('.'))) return null;
        return ByPath.GetValueOrDefault(normalized);
    }

    private static string Normalize(string path)
        => path.Replace('\\', '/').TrimStart('/');
}
