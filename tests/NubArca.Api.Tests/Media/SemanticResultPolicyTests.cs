using Microsoft.Extensions.Options;
using NubArca.Api.Media.Semantic;
using Xunit;

namespace NubArca.Api.Tests.Media;

// SEARCH-SEM-01: the result policy's boundaries, and — just as important — the
// fact that it is currently running in UNCALIBRATED COMPATIBILITY MODE.
//
// Thresholds are deliberately not set: the automated fixtures use deterministic
// 32-dimension embeddings and cannot justify a cosine cut-off for the real
// 1152-dimension SigLIP2 profile. These tests pin that this mode behaves
// EXACTLY like the pre-SEARCH-SEM-01 top-300 cut, so nobody can mistake the
// presence of the mechanism for the presence of a quality gate.
public sealed class SemanticResultPolicyTests
{
    private static SemanticResultPolicy Policy(SemanticResultPolicyOptions? options = null)
        => new(Options.Create(options ?? new SemanticResultPolicyOptions()));

    private static IReadOnlyList<double> Scores(SemanticResultPolicy policy, params double[] scores)
        => policy.Apply(scores, s => s);

    // ---- uncalibrated compatibility mode ------------------------------------

    [Fact]
    public void Defaults_Are_Uncalibrated()
    {
        var policy = Policy();

        // The single fact an operator needs: no score-based filtering is active.
        Assert.False(policy.IsCalibrated);
        Assert.Equal(300, policy.SoftResultLimit);
        Assert.Equal(1_000, policy.AbsoluteSafetyLimit);
        Assert.Null(policy.StrongResultScore);
        Assert.Null(policy.MinimumScoreFor(SemanticModality.Photo));
        Assert.Null(policy.MinimumScoreFor(SemanticModality.Video));
    }

    [Fact]
    public void Uncalibrated_Minimum_Score_Admits_Everything_Including_Zero_And_Negative()
    {
        var policy = Policy();

        // Disabled must mean DISABLED, not "an implicit zero" — a zero would
        // quietly discard legitimately weak-but-real cosine matches and would
        // look calibrated while being arbitrary.
        Assert.True(policy.Admits(SemanticModality.Photo, 0.0));
        Assert.True(policy.Admits(SemanticModality.Photo, -0.5));
        Assert.True(policy.Admits(SemanticModality.Video, 0.0));
        Assert.True(policy.Admits(SemanticModality.Video, -0.5));
    }

    [Fact]
    public void Uncalibrated_Mode_Stops_At_300_Exactly()
    {
        var policy = Policy();
        var scores = Enumerable.Range(0, 500).Select(i => 1.0 - (i * 0.001)).ToArray();

        var kept = Scores(policy, scores);

        // Identical to the behaviour the product shipped before this slice.
        Assert.Equal(300, kept.Count);
        Assert.Equal(scores[299], kept[^1]);
    }

    // ---- calibrated behaviour, once thresholds are supplied ------------------

    [Fact]
    public void Minimum_Score_Rejects_Below_And_Admits_Exactly_At_The_Boundary()
    {
        var policy = Policy(new SemanticResultPolicyOptions { MinimumScore = 0.25 });

        Assert.True(policy.IsCalibrated);
        Assert.False(policy.Admits(SemanticModality.Photo, 0.2499999));
        Assert.True(policy.Admits(SemanticModality.Photo, 0.25));
        Assert.True(policy.Admits(SemanticModality.Photo, 0.2500001));
    }

    [Fact]
    public void Strong_Results_Continue_Past_The_Soft_Limit()
    {
        var policy = Policy(new SemanticResultPolicyOptions
        {
            SoftResultLimit = 10,
            StrongResultScore = 0.9,
        });

        // 10 ordinary results, then 5 strong ones that must survive the cut.
        var scores = Enumerable.Repeat(0.5, 10).Concat(Enumerable.Repeat(0.95, 5)).ToArray();
        var kept = Scores(policy, scores);

        Assert.Equal(15, kept.Count);
    }

    [Fact]
    public void Weak_Results_Stop_At_The_Soft_Limit()
    {
        var policy = Policy(new SemanticResultPolicyOptions
        {
            SoftResultLimit = 10,
            StrongResultScore = 0.9,
        });

        var scores = Enumerable.Repeat(0.95, 10).Concat(Enumerable.Repeat(0.5, 20)).ToArray();
        var kept = Scores(policy, scores);

        Assert.Equal(10, kept.Count);
    }

    [Fact]
    public void Exactly_At_The_Strong_Score_Continues()
    {
        var policy = Policy(new SemanticResultPolicyOptions
        {
            SoftResultLimit = 2,
            StrongResultScore = 0.8,
        });

        var kept = Scores(policy, 0.99, 0.9, 0.8, 0.7999999);

        // 0.8 continues (>=), 0.7999999 stops.
        Assert.Equal(3, kept.Count);
    }

    [Fact]
    public void The_Absolute_Safety_Limit_Is_Never_Exceeded()
    {
        var policy = Policy(new SemanticResultPolicyOptions
        {
            SoftResultLimit = 10,
            StrongResultScore = 0.1,
            AbsoluteSafetyLimit = 25,
        });

        // Everything is "strong", so only the hard backstop can stop this.
        var kept = Scores(policy, Enumerable.Repeat(0.99, 5_000).ToArray());

        Assert.Equal(25, kept.Count);
    }

    [Fact]
    public void The_Safety_Limit_Can_Never_Fall_Below_The_Soft_Limit()
    {
        // A misconfiguration must not silently truncate below the soft limit.
        var policy = Policy(new SemanticResultPolicyOptions
        {
            SoftResultLimit = 300,
            AbsoluteSafetyLimit = 10,
        });

        Assert.Equal(300, policy.SoftResultLimit);
        Assert.Equal(300, policy.AbsoluteSafetyLimit);
    }

    [Fact]
    public void Per_Modality_Minimums_Override_The_Shared_Value()
    {
        var policy = Policy(new SemanticResultPolicyOptions
        {
            MinimumScore = 0.2,
            VideoMinimumScore = 0.5,
        });

        // Photos keep the shared value, videos take their explicit one — the
        // point being that a difference must be NAMED, never assumed.
        Assert.Equal(0.2, policy.MinimumScoreFor(SemanticModality.Photo));
        Assert.Equal(0.5, policy.MinimumScoreFor(SemanticModality.Video));
        Assert.True(policy.Admits(SemanticModality.Photo, 0.3));
        Assert.False(policy.Admits(SemanticModality.Video, 0.3));
    }

    [Fact]
    public void The_Accumulator_Capacity_Follows_The_Safety_Limit_Not_The_Old_Candidate_Cap()
    {
        var policy = Policy(new SemanticResultPolicyOptions { AbsoluteSafetyLimit = 750 });

        // Bounded by the RESULT policy, never by the former 20,000 candidate cap.
        Assert.Equal(750, policy.AccumulatorCapacity);
    }
}
