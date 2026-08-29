using System.Diagnostics;
using NubArca.Api.Rag;

namespace NubArca.Api.Ai.DocumentVisual;

/// One question with a known right answer, for the visual golden set.
///
/// `ExpectedDocuments` are FILE NAMES, because that is what a citation names and
/// what a person recognises. `Visual` marks the cases the visual signal is
/// SUPPOSED to help with — a table, a form, a slide, a layout — so a report can
/// say what happened on those separately instead of hiding a category behind one
/// aggregate.
public sealed record DocumentVisualGoldenCase(
    string Query,
    IReadOnlyList<string> ExpectedDocuments,
    bool Visual = false,
    string? Note = null)
{
    public bool Answerable => ExpectedDocuments.Count > 0;
}

/// What one retrieval mode scored.
public sealed record DocumentVisualModeReport(
    string Mode,
    int Queries,
    double RecallAtFive,
    double MeanReciprocalRank,
    int TopThreePassed,
    double VisualNdcgAtFive,
    long MedianLatencyMs,
    long P95LatencyMs,
    IReadOnlyList<DocumentVisualCaseOutcome> Outcomes);

public sealed record DocumentVisualCaseOutcome(
    DocumentVisualGoldenCase Case,
    int? FirstExpectedRank,
    IReadOnlyList<string> TopDocuments,
    long LatencyMs);

/// The comparison the promotion decision is actually made on.
public sealed record DocumentVisualComparison(
    DocumentVisualModeReport Baseline,
    DocumentVisualModeReport Candidate,
    IReadOnlyList<string> Recovered,
    IReadOnlyList<string> Regressed);

/// DOES THE VISUAL SIGNAL EARN ITS COST — measured, per mode, per category.
///
/// The claim this slice makes is `text + visual > text alone`, and a claim like
/// that is either measured or it is marketing. So the harness runs the SAME
/// pipeline the Assistant runs, three times over one golden set:
///
///     text-only
///     dense-visual-expanded
///     dense + late-interaction-expanded   (when a profile is promoted)
///
/// and reports each separately, plus which queries were RECOVERED and which
/// REGRESSED. Aggregates hide the interesting half: a change that gains two
/// table questions and loses two identifier lookups scores flat, and is a bad
/// change.
///
/// NO GENERATIVE MODEL IS INVOLVED. A benchmark that asks an LLM to judge the
/// answer measures the LLM, costs a provider call per run, and cannot be a
/// regression gate.
public sealed class DocumentVisualEvaluator
{
    private readonly OwnerDocumentRetrievalPipeline _pipeline;

    public DocumentVisualEvaluator(OwnerDocumentRetrievalPipeline pipeline)
    {
        _pipeline = pipeline;
    }

    public async Task<DocumentVisualComparison> CompareAsync(
        Guid ownerUserId,
        IReadOnlyList<DocumentVisualGoldenCase> cases,
        int maxEvidence = 5,
        int maxCharacters = 8_000,
        CancellationToken cancellationToken = default)
    {
        var baseline = await EvaluateAsync(
            ownerUserId, cases, useVisual: false, maxEvidence, maxCharacters, cancellationToken);
        var candidate = await EvaluateAsync(
            ownerUserId, cases, useVisual: true, maxEvidence, maxCharacters, cancellationToken);

        // A query is RECOVERED when the candidate finds an expected document in
        // the top five and the baseline did not, and REGRESSED the other way
        // round. Both directions are reported, because a signal that helps on
        // average and quietly breaks identifier lookups is not an improvement.
        var before = baseline.Outcomes.ToDictionary(o => o.Case.Query, o => o.FirstExpectedRank);
        var recovered = new List<string>();
        var regressed = new List<string>();

        foreach (var outcome in candidate.Outcomes)
        {
            if (!before.TryGetValue(outcome.Case.Query, out var baselineRank)) continue;
            var wasFound = baselineRank is >= 1 and <= 5;
            var isFound = outcome.FirstExpectedRank is >= 1 and <= 5;
            if (isFound && !wasFound) recovered.Add(outcome.Case.Query);
            if (wasFound && !isFound) regressed.Add(outcome.Case.Query);
        }

        return new DocumentVisualComparison(baseline, candidate, recovered, regressed);
    }

    public async Task<DocumentVisualModeReport> EvaluateAsync(
        Guid ownerUserId,
        IReadOnlyList<DocumentVisualGoldenCase> cases,
        bool useVisual,
        int maxEvidence = 5,
        int maxCharacters = 8_000,
        CancellationToken cancellationToken = default)
    {
        var answerable = cases.Where(c => c.Answerable).ToList();
        var outcomes = new List<DocumentVisualCaseOutcome>(answerable.Count);
        var mode = useVisual ? "visual-expanded" : "text-only";

        foreach (var golden in answerable)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var stopwatch = Stopwatch.StartNew();
            var result = await _pipeline.RetrieveAsync(
                ownerUserId, golden.Query, maxEvidence, maxCharacters, useVisual, cancellationToken);
            stopwatch.Stop();

            // The citation's own name, which is what the golden set names — not
            // a chunk id, not a path.
            var documents = result.Evidence.Select(e => e.Title).ToList();
            int? rank = null;
            for (var i = 0; i < documents.Count; i++)
            {
                if (!golden.ExpectedDocuments.Contains(documents[i], StringComparer.Ordinal)) continue;
                rank = i + 1;
                break;
            }

            outcomes.Add(new DocumentVisualCaseOutcome(
                golden, rank, documents, stopwatch.ElapsedMilliseconds));

            if (useVisual && result.VisualMode is { Length: > 0 })
            {
                mode = result.VisualMode;
            }
        }

        var latencies = outcomes.Select(o => o.LatencyMs).OrderBy(v => v).ToList();

        return new DocumentVisualModeReport(
            Mode: mode,
            Queries: outcomes.Count,
            RecallAtFive: Fraction(outcomes, o => o.FirstExpectedRank is >= 1 and <= 5),
            MeanReciprocalRank: outcomes.Count == 0
                ? 0
                : outcomes.Average(o => o.FirstExpectedRank is { } r ? 1.0 / r : 0.0),
            TopThreePassed: outcomes.Count(o => o.FirstExpectedRank is >= 1 and <= 3),
            // The SAME metric restricted to the deliberately visual cases. One
            // aggregate over a mixed set cannot tell "visual retrieval works"
            // from "the text path was already good at most of these".
            VisualNdcgAtFive: NdcgAtFive(outcomes.Where(o => o.Case.Visual).ToList()),
            MedianLatencyMs: Percentile(latencies, 0.50),
            P95LatencyMs: Percentile(latencies, 0.95),
            Outcomes: outcomes);
    }

    private static double Fraction(
        IReadOnlyList<DocumentVisualCaseOutcome> outcomes,
        Func<DocumentVisualCaseOutcome, bool> predicate)
        => outcomes.Count == 0 ? 0 : (double)outcomes.Count(predicate) / outcomes.Count;

    /// nDCG@5 with binary relevance and at most one relevant document per query,
    /// which makes the ideal DCG exactly 1 and the whole thing `1 / log2(rank+1)`
    /// averaged. Stated plainly rather than generalised: a graded-relevance
    /// implementation nobody grades for would be a more impressive-looking
    /// number computed from the same information.
    private static double NdcgAtFive(IReadOnlyList<DocumentVisualCaseOutcome> outcomes)
        => outcomes.Count == 0
            ? 0
            : outcomes.Average(o => o.FirstExpectedRank is >= 1 and <= 5
                ? 1.0 / Math.Log2(o.FirstExpectedRank.Value + 1)
                : 0.0);

    private static long Percentile(IReadOnlyList<long> sorted, double percentile)
    {
        if (sorted.Count == 0) return 0;
        var index = (int)Math.Ceiling(percentile * sorted.Count) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
    }
}
