using NubArca.Api.Rag.Domains;

namespace NubArca.Api.Rag.Evaluation;

/// One question with a known right answer.
///
/// `ExpectedSourcePrefixes` are matched as PREFIXES of a source key so a golden
/// case survives `docs/help/faces.md` and `docs/help/faces.en.md` being two
/// files, and so a rename inside a directory does not invalidate the case's
/// intent. `ForbiddenTopSources` is the other half and is the one that catches
/// regressions: a technical document reaching the top of a how-to answer is the
/// specific failure this whole retrieval design exists to prevent, and it is
/// invisible to a recall metric that only asks whether the right document is
/// somewhere in the list.
public sealed record RagGoldenCase(
    string Domain,
    string Language,
    string Query,
    IReadOnlyList<string> ExpectedSourcePrefixes,
    IReadOnlyList<string> ForbiddenTopSources)
{
    public RagGoldenCase(string domain, string language, string query, params string[] expected)
        : this(domain, language, query, expected, Array.Empty<string>())
    {
    }
}

/// The queries NubArca measures its own retrieval with.
///
/// Small and deliberate rather than large and generated. The point is REGRESSION
/// DETECTION, not a leaderboard: each case is a question somebody would actually
/// ask, whose right answer somebody decided on, and a change that breaks one is
/// a change worth looking at. A thousand synthetic paraphrases would produce a
/// number that moves without telling anyone what broke.
public static class RagGoldenSet
{
    /// The permanent canary. Before Slice 1's retrieval rewrite this question
    /// returned `docs/OPERATIONS.md` — a runbook that mentions faces, written
    /// for somebody restoring a backup. It is kept forever, in Italian, because
    /// it is the exact shape of failure that is easy to reintroduce: a long
    /// technical document outranking a short user guide on word count.
    public const string FacesCanary = "come faccio a utilizzare la funzione dei volti?";

    public static IReadOnlyList<RagGoldenCase> ProductHelp { get; } = new[]
    {
        // ---- Italian, the interface language --------------------------------
        new RagGoldenCase(
            RagDomains.ProductHelp, RagLanguages.Italian, FacesCanary,
            new[] { "docs/help/faces" },
            new[] { "docs/OPERATIONS.md", "docs/multimodal-photo-search.md", "ARCHITECTURE.md" }),
        new RagGoldenCase(
            RagDomains.ProductHelp, RagLanguages.Italian,
            "come assegno un volto a una persona?",
            new[] { "docs/help/faces" }, new[] { "docs/OPERATIONS.md" }),
        new RagGoldenCase(
            RagDomains.ProductHelp, RagLanguages.Italian,
            "come do un nome alle facce?", "docs/help/faces"),
        new RagGoldenCase(
            RagDomains.ProductHelp, RagLanguages.Italian,
            "come recupero un volto ignorato?", "docs/help/faces"),
        new RagGoldenCase(
            RagDomains.ProductHelp, RagLanguages.Italian,
            "dove trovo le foto da rivedere?", "docs/help/faces"),
        new RagGoldenCase(
            RagDomains.ProductHelp, RagLanguages.Italian,
            "come funziona il riconoscimento facciale?", "docs/help/faces"),
        new RagGoldenCase(
            RagDomains.ProductHelp, RagLanguages.Italian,
            "come trasmetto le foto sulla tv?", "docs/google-cast.md"),
        new RagGoldenCase(
            RagDomains.ProductHelp, RagLanguages.Italian,
            "come cerco le foto per data di scatto?", "docs/photo-date-taken-organizer.md"),

        // ---- English ---------------------------------------------------------
        new RagGoldenCase(
            RagDomains.ProductHelp, RagLanguages.English,
            "how do I name a detected person?",
            new[] { "docs/help/faces" }, new[] { "docs/OPERATIONS.md" }),
        new RagGoldenCase(
            RagDomains.ProductHelp, RagLanguages.English,
            "where do I review detected faces?", "docs/help/faces"),
        new RagGoldenCase(
            RagDomains.ProductHelp, RagLanguages.English,
            "how do I restore an ignored face?", "docs/help/faces"),
        new RagGoldenCase(
            RagDomains.ProductHelp, RagLanguages.English,
            "how do I use the faces feature?",
            new[] { "docs/help/faces" }, new[] { "docs/OPERATIONS.md" }),
        new RagGoldenCase(
            RagDomains.ProductHelp, RagLanguages.English,
            "where do I find suggested face groups?", "docs/help/faces"),
        new RagGoldenCase(
            RagDomains.ProductHelp, RagLanguages.English,
            "what is NubArca?", "README.md"),
        new RagGoldenCase(
            RagDomains.ProductHelp, RagLanguages.English,
            "how does the media grid work?", "docs/media-wall.md"),
        new RagGoldenCase(
            RagDomains.ProductHelp, RagLanguages.English,
            "how do I cast photos to a television?", "docs/google-cast.md"),
    };

    /// Repository dogfooding: exact-identifier questions and conceptual ones,
    /// because the two exercise different halves of hybrid retrieval and a
    /// change that helps one usually costs the other.
    public static IReadOnlyList<RagGoldenCase> Repository { get; } = new[]
    {
        new RagGoldenCase(
            RagDomains.NubArcaRepository, RagLanguages.English,
            "where is the external Help privacy boundary enforced?",
            "src/NubArca.Api/Help/HelpAssistantService.cs",
            "src/NubArca.Api/Assistant/AssistantCapabilities.cs",
            "src/NubArca.Api/Assistant/AssistantRagPolicy.cs"),
        new RagGoldenCase(
            RagDomains.NubArcaRepository, RagLanguages.English,
            "which test proves private library data is not sent to the external provider?",
            "tests/NubArca.Api.Tests/Help/HelpPrivacyTests.cs",
            "tests/NubArca.Api.Tests/Help/LocalTrustedHelpBoundaryTests.cs"),
        new RagGoldenCase(
            RagDomains.NubArcaRepository, RagLanguages.English,
            "where is pgvector photo search implemented?",
            "src/NubArca.Api/Ai/Photos/PhotoVectorIndexService.cs",
            "docs/ai-photo-pgvector.md"),
        new RagGoldenCase(
            RagDomains.NubArcaRepository, RagLanguages.English,
            "how is model trust parsed?",
            "src/NubArca.Api/Assistant/AssistantModelResolver.cs",
            "src/NubArca.Api/Assistant/AssistantModelTrust.cs"),
        new RagGoldenCase(
            RagDomains.NubArcaRepository, RagLanguages.English,
            "where are the face tabs defined?",
            "frontend/src/pages/PeoplePage.tsx",
            "frontend/src/pages/facesTabs.ts"),
        new RagGoldenCase(
            RagDomains.NubArcaRepository, RagLanguages.English,
            "which code prevents an External model from using repository knowledge?",
            "src/NubArca.Api/Assistant/AssistantRagPolicy.cs",
            "src/NubArca.Api/Rag/Domains/RagDomainRegistry.cs"),
        new RagGoldenCase(
            RagDomains.NubArcaRepository, RagLanguages.English,
            "what does DocumentChunk store?",
            "src/NubArca.Api/Domain/Ai/DocumentChunk.cs",
            "src/NubArca.Api/Data/Configurations/Ai/DocumentChunkConfiguration.cs"),
        new RagGoldenCase(
            RagDomains.NubArcaRepository, RagLanguages.English,
            "PhotoVectorIndexService", "src/NubArca.Api/Ai/Photos/PhotoVectorIndexService.cs"),
        new RagGoldenCase(
            RagDomains.NubArcaRepository, RagLanguages.English,
            "face_previews table", "src/NubArca.Api/Data/Configurations/Ai/FacePreviewConfiguration.cs"),
        new RagGoldenCase(
            RagDomains.NubArcaRepository, RagLanguages.English,
            "RevisionMismatch_DoesNotCallModel",
            "tests/NubArca.Api.Tests/Help/HelpAssistantServiceTests.cs"),
    };

    public static IReadOnlyList<RagGoldenCase> For(string domain) => domain switch
    {
        RagDomains.ProductHelp => ProductHelp,
        RagDomains.NubArcaRepository => Repository,
        _ => Array.Empty<RagGoldenCase>(),
    };
}
