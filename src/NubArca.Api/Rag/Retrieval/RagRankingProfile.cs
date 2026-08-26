using NubArca.Api.Rag.Domains;
using NubArca.Api.Rag.Text;

namespace NubArca.Api.Rag.Retrieval;

/// The query, after normalization. Computed once per retrieval and handed to
/// both the scorer and the domain's boost function, so "is this a how-to
/// question" is decided in one place rather than three.
public sealed record RagQueryShape(
    IReadOnlyList<string> Literal,
    IReadOnlyList<string> Expanded,
    bool LooksLikeHowTo)
{
    public IReadOnlyList<string> AllTerms { get; } = Literal.Concat(Expanded).ToList();

    public static RagQueryShape For(string text, bool expandAliases)
    {
        var content = RagText.ContentTokens(text);
        var (literal, expanded) = expandAliases
            ? RagAliasCatalog.Expand(content)
            : (Dedupe(content), Array.Empty<string>());
        return new RagQueryShape(literal, expanded, RagText.LooksLikeHowTo(text));
    }

    private static IReadOnlyList<string> Dedupe(IReadOnlyList<string> tokens)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>(tokens.Count);
        foreach (var token in tokens)
        {
            if (seen.Add(token)) result.Add(token);
        }
        return result;
    }
}

/// How ONE domain wants its lexical retrieval to behave.
///
/// The scorer is generic; the judgement is not. Product Help prefers a user
/// guide over a technical reference for a how-to question — a rule that would
/// be actively wrong for a domain made entirely of source code, where the
/// technical reference IS the answer. So the weights and the shaping function
/// are a per-domain value, and the BM25F implementation has no opinion.
public sealed record RagRankingProfile(
    string Domain,
    double FeatureWeight,
    double SectionWeight,
    double TitleWeight,
    double BodyWeight,
    double ExpandedTermWeight,
    double MinimumScore,
    double RelativeFloor,
    bool ExpandAliases,
    Func<RagIndexedChunk, RagQueryShape, double> Boost)
{
    public const double K1 = 1.2;
    public const double B = 0.75;
}

public static class RagRankingProfiles
{
    // ---- product-help ------------------------------------------------------
    //
    // Slice 1's tuning, unchanged. These numbers are the reason
    // "come faccio a utilizzare la funzione dei volti?" reaches the Faces guide
    // instead of docs/OPERATIONS.md, and the golden tests hold them there.
    private const double HowToIntentBoost = 1.4;
    private const double UserGuideBoost = 1.3;
    private const double TechnicalReferencePenalty = 0.6;
    private const double TechnicalAudiencePenalty = 0.8;

    public static RagRankingProfile ProductHelp { get; } = new(
        Domain: RagDomains.ProductHelp,
        // Metadata over prose. A document's feature name and aliases say what it
        // is ABOUT; its body says everything it happens to mention.
        FeatureWeight: 3.0,
        SectionWeight: 2.5,
        TitleWeight: 2.0,
        BodyWeight: 1.0,
        // An alias-expanded term is a guess about what the person meant, so it
        // contributes less than a word they actually typed.
        ExpandedTermWeight: 0.45,
        MinimumScore: 0.35,
        RelativeFloor: 0.25,
        ExpandAliases: true,
        Boost: (chunk, shape) =>
        {
            // The manifest's editorial judgement, as a multiplier rather than a
            // replacement: a high-priority source still has to match.
            var boost = 0.5 + chunk.Priority / 100.0;
            if (!shape.LooksLikeHowTo) return boost;

            if (chunk.Intent == RagIntents.HowTo) boost *= HowToIntentBoost;
            if (chunk.SourceKind == RagSourceKinds.UserGuide) boost *= UserGuideBoost;
            if (chunk.SourceKind == RagSourceKinds.TechnicalReference) boost *= TechnicalReferencePenalty;
            if (chunk.Audience == RagAudiences.Technical) boost *= TechnicalAudiencePenalty;
            return boost;
        });

    // ---- nubarca-repository ------------------------------------------------
    //
    // A different question shape entirely. "Where is X declared?" is answered by
    // a path and a symbol, so the FEATURE field — which for a repository source
    // holds path segments and declared symbols — carries most of the weight, and
    // alias expansion is off: `persona` and `person` are one concept to somebody
    // asking how to use NubArca and two identifiers to somebody reading it.
    public static RagRankingProfile Repository { get; } = new(
        Domain: RagDomains.NubArcaRepository,
        FeatureWeight: 4.0,
        SectionWeight: 3.0,
        TitleWeight: 2.5,
        BodyWeight: 1.0,
        ExpandedTermWeight: 0.45,
        // Lower than Product Help's gate. A code question is often ONE rare
        // identifier, and one exact hit on a rare token is strong evidence where
        // one exact hit on a common word is not — the IDF already says which.
        MinimumScore: 0.25,
        RelativeFloor: 0.20,
        ExpandAliases: false,
        Boost: (chunk, _) => chunk.SourceKind switch
        {
            // A declaration is what "where is this" wants; a test is what
            // "which test proves this" wants; both beat a passing mention.
            RagSourceKinds.SourceCode => 1.15,
            RagSourceKinds.Test => 1.05,
            RagSourceKinds.Migration => 1.0,
            RagSourceKinds.Documentation => 1.0,
            _ => 0.95,
        });

    public static RagRankingProfile For(RagDomainKey domain) => domain.Value switch
    {
        RagDomains.ProductHelp => ProductHelp,
        RagDomains.NubArcaRepository => Repository,
        _ => throw new ArgumentOutOfRangeException(
            nameof(domain), $"No ranking profile for RAG domain '{domain.Value}'."),
    };
}
