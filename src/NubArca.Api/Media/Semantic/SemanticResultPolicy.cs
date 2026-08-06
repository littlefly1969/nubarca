using Microsoft.Extensions.Options;

namespace NubArca.Api.Media.Semantic;

// SEARCH-SEM-01: the ONE place that decides how many ranked results a semantic
// query returns and where it stops. Structurally shared by photos and videos so
// the two can never drift; per-modality values are permitted but must be named
// and tested, never assumed.
//
// The shape, in order:
//
//     discard score < MinimumScore
//     return normally up to SoftResultLimit
//     past SoftResultLimit, continue ONLY while score >= StrongResultScore
//     never exceed AbsoluteSafetyLimit
//
// WHY THE THRESHOLDS DEFAULT TO DISABLED
// --------------------------------------
// Cosine similarity in a SigLIP2 text↔image space has no universal "good"
// value: it depends on the tower pair, the normalization and the query
// phrasing. NubArca's own dev/test fixtures run the DETERMINISTIC 32-dimension
// backend, whose scores are not semantically meaningful and therefore cannot
// calibrate the real 1152-dimension profile production resolves. Inventing a
// number here would silently hide real matches from a live library.
//
// So MinimumScore and StrongResultScore default to null = DISABLED, which
// reproduces today's behaviour exactly (a plain SoftResultLimit cut at 300).
// The mechanism, the boundaries and the tests are all in place; switching it on
// is a one-line configuration change once a distribution has been measured
// against the real profile. `IsCalibrated` reports which mode is active so
// diagnostics can say so out loud rather than implying a quality gate exists.
public sealed class SemanticResultPolicyOptions
{
    public const string SectionName = "Semantic:ResultPolicy";

    // Bump when the MEANING of the policy changes. It is part of the ranking
    // cache identity, so a redeploy that changes selection semantics cannot
    // serve a page from a ranking built under the old rules.
    public const int PolicyVersion = 1;

    // Null = disabled (keep every ranked candidate). A value applies to the
    // best score of a result: for a video that is its best segment.
    public double? MinimumScore { get; set; }

    // The soft cut. 300 is the value the product already shipped as a hard cap,
    // kept as the canonical default so behaviour is unchanged until thresholds
    // are calibrated.
    public int SoftResultLimit { get; set; } = 300;

    // Null = disabled, which makes SoftResultLimit behave as a hard cut (the
    // pre-SEARCH-SEM-01 behaviour).
    public double? StrongResultScore { get; set; }

    // The backstop that bounds memory and serialization no matter what the
    // scores look like. Also sizes the ranking accumulator.
    public int AbsoluteSafetyLimit { get; set; } = 1_000;

    // Optional per-modality overrides. Left null, photos and videos share the
    // values above — which is the default precisely because assuming the two
    // distributions match is exactly the assumption this slice must not make
    // silently.
    public double? PhotoMinimumScore { get; set; }
    public double? VideoMinimumScore { get; set; }
}

public enum SemanticModality
{
    Photo,
    Video,
}

public sealed class SemanticResultPolicy
{
    private readonly SemanticResultPolicyOptions _options;

    public SemanticResultPolicy(IOptions<SemanticResultPolicyOptions> options)
        : this(options?.Value ?? new SemanticResultPolicyOptions())
    {
    }

    public SemanticResultPolicy(SemanticResultPolicyOptions options)
    {
        _options = options;
        SoftResultLimit = Math.Max(1, options.SoftResultLimit);
        AbsoluteSafetyLimit = Math.Max(SoftResultLimit, options.AbsoluteSafetyLimit);
    }

    public int SoftResultLimit { get; }

    public int AbsoluteSafetyLimit { get; }

    public double? StrongResultScore => _options.StrongResultScore;

    // True once an operator has supplied at least one real threshold. Reported
    // in diagnostics so "no quality gate" is visible rather than implied.
    public bool IsCalibrated =>
        _options.MinimumScore is not null
        || _options.StrongResultScore is not null
        || _options.PhotoMinimumScore is not null
        || _options.VideoMinimumScore is not null;

    // How many ranked hits a modality must accumulate for the policy to be able
    // to make every decision it might need to. Bounded by construction.
    public int AccumulatorCapacity => AbsoluteSafetyLimit;

    public double? MinimumScoreFor(SemanticModality modality) => modality switch
    {
        SemanticModality.Photo => _options.PhotoMinimumScore ?? _options.MinimumScore,
        SemanticModality.Video => _options.VideoMinimumScore ?? _options.MinimumScore,
        _ => _options.MinimumScore,
    };

    // Applied per modality DURING ranking, so a below-threshold candidate never
    // occupies accumulator space that a real match could use.
    public bool Admits(SemanticModality modality, double score)
    {
        var minimum = MinimumScoreFor(modality);
        return minimum is null || score >= minimum.Value;
    }

    // Applied to the MERGED, already-sorted list. `ordered` must be sorted by
    // (score desc, id asc) — the same total order pagination uses — so the
    // cut-off is deterministic and equal scores never straddle it arbitrarily.
    public IReadOnlyList<T> Apply<T>(IReadOnlyList<T> ordered, Func<T, double> scoreOf)
    {
        ArgumentNullException.ThrowIfNull(ordered);
        ArgumentNullException.ThrowIfNull(scoreOf);

        var kept = new List<T>(Math.Min(ordered.Count, AbsoluteSafetyLimit));
        foreach (var item in ordered)
        {
            if (kept.Count >= AbsoluteSafetyLimit)
            {
                break;
            }
            if (kept.Count >= SoftResultLimit)
            {
                // Past the soft limit only strong results continue. With no
                // calibrated strong score the soft limit is a hard cut, which
                // is exactly the pre-existing behaviour.
                if (_options.StrongResultScore is not double strong || scoreOf(item) < strong)
                {
                    break;
                }
            }
            kept.Add(item);
        }
        return kept;
    }
}
