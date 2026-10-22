using NubArca.Api.Rag.Domains;
using NubArca.Api.Rag.Text;

namespace NubArca.Api.Rag.Retrieval;

/// The query, after normalization. Computed once per retrieval and handed to
/// the scorer and the excerpt builder, so tokenization is decided in one place.
public sealed record RagQueryShape(IReadOnlyList<string> Literal)
{
    public IReadOnlyList<string> AllTerms => Literal;

    public static RagQueryShape For(string text) => new(Dedupe(RagText.ContentTokens(text)));

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
/// The scorer is generic; the field weights and gates are a per-domain value,
/// and the BM25F implementation has no opinion.
public sealed record RagRankingProfile(
    string Domain,
    double FeatureWeight,
    double SectionWeight,
    double TitleWeight,
    double BodyWeight,
    double MinimumScore,
    double RelativeFloor)
{
    public const double K1 = 1.2;
    public const double B = 0.75;
}

public static class RagRankingProfiles
{
    // ---- user-documents ----------------------------------------------------
    //
    // Somebody's own prose, and the thing it does NOT have is editorial
    // metadata. A document in a person's library has a filename, its headings
    // and its text, and inventing a classification for it would be a guess
    // presented as a signal.
    //
    // So heading and title carry real weight because a private document's
    // section titles are usually what it is about, there is no alias
    // vocabulary to expand against, and no boost: one person's documents do not
    // rank each other editorially.
    public static RagRankingProfile UserDocuments { get; } = new(
        Domain: RagDomains.UserDocuments,
        // No feature field exists for these, so its weight is irrelevant rather
        // than tuned; stated explicitly so it cannot read as an oversight.
        FeatureWeight: 1.0,
        SectionWeight: 2.5,
        TitleWeight: 2.5,
        BodyWeight: 1.0,
        // Natural-language questions over prose: one accidental shared word is
        // not evidence.
        MinimumScore: 0.35,
        RelativeFloor: 0.25);

    public static RagRankingProfile For(RagDomainKey domain) => domain.Value switch
    {
        RagDomains.UserDocuments => UserDocuments,
        _ => throw new ArgumentOutOfRangeException(
            nameof(domain), $"No ranking profile for RAG domain '{domain.Value}'."),
    };
}
