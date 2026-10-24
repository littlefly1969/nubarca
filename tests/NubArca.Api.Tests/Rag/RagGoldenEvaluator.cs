using NubArca.Api.Rag;

namespace NubArca.Api.Tests.Rag;

/// One question with a known right answer. `ExpectedSourcePrefixes` match as
/// PREFIXES of a source key; an empty list is a question the corpus cannot
/// answer.
internal sealed record RagGoldenCase(string Query, IReadOnlyList<string> ExpectedSourcePrefixes);

internal sealed record RagGoldenOutcome(
    RagGoldenCase Case,
    int FirstExpectedRank,
    IReadOnlyList<string> TopSources)
{
    public bool Recalled => FirstExpectedRank is > 0 and <= 5;
    public bool InTopThree => FirstExpectedRank is > 0 and <= 3;
    public double ReciprocalRank => FirstExpectedRank > 0 ? 1.0 / FirstExpectedRank : 0.0;
}

/// Aggregate metrics over a golden set. Deliberately three numbers.
internal sealed record RagEvaluationReport(
    string Mode,
    string? EmbeddingProfileKey,
    int Queries,
    double RecallAtFive,
    double MeanReciprocalRank,
    int TopThreePassed,
    IReadOnlyList<RagGoldenOutcome> Outcomes);

/// Runs a golden set through the REAL retriever and measures retrieval: did
/// the right source come back, and how high. No generative model is involved —
/// a benchmark that asks an LLM to judge measures the LLM.
///
/// Recall@5 asks whether the evidence was there at all; MRR asks how high;
/// top-3 is the pass/fail count.
internal sealed class RagGoldenEvaluator
{
    private readonly IRagRetriever _retriever;

    public RagGoldenEvaluator(IRagRetriever retriever)
    {
        _retriever = retriever;
    }

    public async Task<RagEvaluationReport> EvaluateAsync(
        string domain, IReadOnlyList<RagGoldenCase> cases, Guid ownerUserId)
    {
        var key = new RagDomainKey(domain);
        var outcomes = new List<RagGoldenOutcome>(cases.Count);
        var mode = RagRetrievalModes.Lexical;
        string? profile = null;

        foreach (var golden in cases)
        {
            var result = await _retriever.RetrieveAsync(
                new RagQuery(key, golden.Query, ownerUserId, 5, 12000));
            mode = result.Mode;
            profile ??= result.EmbeddingProfileKey;

            var sources = result.Evidence
                .Select(e => e.SourceKey.Length > 0 ? e.SourceKey : e.Path).ToList();
            var first = sources.FindIndex(s => golden.ExpectedSourcePrefixes.Any(
                p => s.StartsWith(p, StringComparison.Ordinal))) + 1;

            outcomes.Add(new RagGoldenOutcome(golden, first, sources.Take(3).ToList()));
        }

        return new RagEvaluationReport(
            mode,
            profile,
            outcomes.Count,
            outcomes.Count == 0 ? 0 : outcomes.Count(o => o.Recalled) / (double)outcomes.Count,
            outcomes.Count == 0 ? 0 : outcomes.Average(o => o.ReciprocalRank),
            outcomes.Count(o => o.InTopThree),
            outcomes);
    }
}
