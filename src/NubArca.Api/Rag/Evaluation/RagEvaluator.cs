namespace NubArca.Api.Rag.Evaluation;

/// One golden case's outcome.
public sealed record RagGoldenOutcome(
    RagGoldenCase Case,
    bool Recalled,
    int FirstExpectedRank,
    bool ExpectedInTopThree,
    bool ForbiddenAtTop,
    IReadOnlyList<string> TopSources)
{
    public double ReciprocalRank => FirstExpectedRank > 0 ? 1.0 / FirstExpectedRank : 0.0;

    public bool Passed => ExpectedInTopThree && !ForbiddenAtTop;
}

/// Aggregate metrics over a golden set. Deliberately three numbers.
public sealed record RagEvaluationReport(
    string Domain,
    string? Revision,
    string Mode,
    string? EmbeddingProfileKey,
    int Queries,
    double RecallAtFive,
    double MeanReciprocalRank,
    int TopThreePassed,
    IReadOnlyList<RagGoldenOutcome> Outcomes);

/// Runs a golden set through the real retriever and reports whether retrieval
/// got better or worse.
///
/// No generative model is involved, and that is the point. A benchmark that asks
/// an LLM to judge the answer measures the LLM, costs money per run, and is not
/// reproducible — so it cannot be the thing a change is held against. This
/// measures RETRIEVAL: did the right source come back, how high, and did the
/// wrong one lead.
///
/// The three metrics answer different questions. Recall@5 asks whether the
/// evidence was there at all. MRR asks how high — a right answer at rank 5 is
/// worse than at rank 1, because six chunks of context means the first one
/// dominates the answer. Top-3-expected-with-no-forbidden-lead is the pass/fail
/// gate, and it is the one that would have caught the `docs/OPERATIONS.md`
/// regression that started all of this.
public sealed class RagEvaluator
{
    private readonly IRagRetriever _retriever;

    public RagEvaluator(IRagRetriever retriever)
    {
        _retriever = retriever;
    }

    /// `ownerUserId` is required for an owner-scoped domain and ignored by every
    /// system one — measuring `user-documents` means measuring ONE person's
    /// corpus, because there is no other kind. Optional in the signature so the
    /// system callers stay honest about having no owner rather than passing
    /// `Guid.Empty` and making the interesting case invisible.
    public async Task<RagEvaluationReport> EvaluateAsync(
        string domain,
        IReadOnlyList<RagGoldenCase> cases,
        int maxEvidence = 5,
        int maxCharacters = 12000,
        Guid? ownerUserId = null,
        CancellationToken cancellationToken = default)
    {
        var key = new RagDomainKey(domain);
        var outcomes = new List<RagGoldenOutcome>(cases.Count);
        var mode = RagRetrievalModes.Lexical;
        string? profile = null;
        string? revision = null;

        foreach (var golden in cases)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = await _retriever.RetrieveAsync(
                new RagQuery(key, golden.Query, ownerUserId, maxEvidence, maxCharacters),
                cancellationToken);
            mode = result.Mode;
            profile ??= result.EmbeddingProfileKey;
            revision ??= result.Revision;

            var sources = result.Evidence.Select(e => e.SourceKey.Length > 0 ? e.SourceKey : e.Path).ToList();

            var firstExpected = 0;
            for (var i = 0; i < sources.Count; i++)
            {
                if (!Matches(sources[i], golden.ExpectedSourcePrefixes)) continue;
                firstExpected = i + 1;
                break;
            }

            outcomes.Add(new RagGoldenOutcome(
                golden,
                Recalled: firstExpected is > 0 and <= 5,
                FirstExpectedRank: firstExpected,
                ExpectedInTopThree: firstExpected is > 0 and <= 3,
                ForbiddenAtTop: sources.Count > 0
                                && golden.ForbiddenTopSources.Any(f =>
                                    string.Equals(sources[0], f, StringComparison.Ordinal)),
                TopSources: sources.Take(3).ToList()));
        }

        return new RagEvaluationReport(
            domain,
            revision,
            mode,
            profile,
            outcomes.Count,
            outcomes.Count == 0 ? 0 : outcomes.Count(o => o.Recalled) / (double)outcomes.Count,
            outcomes.Count == 0 ? 0 : outcomes.Average(o => o.ReciprocalRank),
            outcomes.Count(o => o.Passed),
            outcomes);
    }

    private static bool Matches(string sourceKey, IReadOnlyList<string> prefixes)
        => prefixes.Any(p => sourceKey.StartsWith(p, StringComparison.Ordinal));
}
