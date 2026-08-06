using NubArca.Api.Aesthetics;
using NubArca.Api.Aesthetics.Sidecar;
using Xunit;

namespace NubArca.Api.Tests.Aesthetics;

// Pure unit tests for the STRICT sidecar-response validator — the gate that
// keeps partial / malformed / out-of-range / duplicated model output out of the
// database. No DB, no model.
public class AestheticSidecarResponseValidatorTests
{
    private static AestheticSidecarRequest Request() => new(
        AestheticSidecarContract.Version,
        "human-aesexpert-1b-expert-v1",
        new[] { AestheticCapabilities.ExpertScores },
        "it",
        AestheticPreprocessingProfiles.OfficialV1);

    private static AestheticSidecarResponse Valid() =>
        FakeAestheticModelClient.Build(Request(), FakeAestheticModelClient.Mode.ValidExpertScores);

    [Fact]
    public void Accepts_a_well_formed_expert_scores_response()
    {
        var result = AestheticSidecarResponseValidator.Validate(Valid(), Request());
        Assert.True(result.Ok);
        Assert.Equal(12, result.Metrics.Count);
        Assert.Contains(result.Metrics, m => m.Key == AestheticMetricCatalog.OverallKey);
        Assert.All(result.Metrics, m => Assert.InRange(m.Value, 0.0, 1.0));
    }

    [Fact]
    public void Rejects_wrong_contract_version()
    {
        var bad = Valid() with { ContractVersion = 999 };
        var result = AestheticSidecarResponseValidator.Validate(bad, Request());
        Assert.False(result.Ok);
        Assert.Equal(AestheticErrorCodes.InvalidModelOutput, result.ErrorCode);
    }

    [Fact]
    public void Rejects_mismatched_profile_key()
    {
        var bad = Valid() with { ProfileKey = "someone-else" };
        Assert.False(AestheticSidecarResponseValidator.Validate(bad, Request()).Ok);
    }

    [Fact]
    public void Rejects_capability_not_requested()
    {
        var bad = FakeAestheticModelClient.Build(Request(), FakeAestheticModelClient.Mode.Malformed);
        Assert.False(AestheticSidecarResponseValidator.Validate(bad, Request()).Ok);
    }

    [Fact]
    public void Rejects_missing_expert_metric()
    {
        var bad = FakeAestheticModelClient.Build(Request(), FakeAestheticModelClient.Mode.MissingMetric);
        Assert.False(AestheticSidecarResponseValidator.Validate(bad, Request()).Ok);
    }

    [Fact]
    public void Rejects_out_of_range_value()
    {
        var bad = FakeAestheticModelClient.Build(Request(), FakeAestheticModelClient.Mode.OutOfRange);
        Assert.False(AestheticSidecarResponseValidator.Validate(bad, Request()).Ok);
    }

    [Fact]
    public void Rejects_duplicate_metric_key()
    {
        var bad = FakeAestheticModelClient.Build(Request(), FakeAestheticModelClient.Mode.Duplicate);
        Assert.False(AestheticSidecarResponseValidator.Validate(bad, Request()).Ok);
    }

    [Fact]
    public void Rejects_non_finite_value()
    {
        var metrics = Valid().Metrics.ToList();
        metrics[0] = metrics[0] with { Value = double.NaN };
        var bad = Valid() with { Metrics = metrics };
        Assert.False(AestheticSidecarResponseValidator.Validate(bad, Request()).Ok);
    }

    [Fact]
    public void Rejects_wrong_declared_scale_for_known_key()
    {
        var metrics = Valid().Metrics.ToList();
        metrics[0] = metrics[0] with { ScaleMax = 10.0 }; // catalog says 1.0
        var bad = Valid() with { Metrics = metrics };
        Assert.False(AestheticSidecarResponseValidator.Validate(bad, Request()).Ok);
    }
}
