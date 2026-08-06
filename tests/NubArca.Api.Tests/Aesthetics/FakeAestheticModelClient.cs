using NubArca.Api.Aesthetics;
using NubArca.Api.Aesthetics.Sidecar;

namespace NubArca.Api.Tests.Aesthetics;

// Strict FAKE sidecar for CI/tests. NEVER loads the real model. Returns a valid,
// contract-shaped expert_scores response by default; individual tests flip
// `Behavior` to exercise the malformed/unavailable/timeout paths. `Configured`
// controls IsConfigured so the "model unavailable" branch is testable.
public sealed class FakeAestheticModelClient : IAestheticModelClient
{
    public enum Mode { ValidExpertScores, Malformed, MissingMetric, OutOfRange, Duplicate, Unavailable, Timeout, Cancelled }

    public Mode Behavior { get; set; } = Mode.ValidExpertScores;
    public bool Configured { get; set; } = true;
    public int CallCount { get; private set; }

    public bool IsConfigured => Configured;

    public void Reset()
    {
        Behavior = Mode.ValidExpertScores;
        Configured = true;
        CallCount = 0;
    }

    public Task<AestheticSidecarResponse> AnalyzeAsync(
        AestheticSidecarRequest request, byte[] imageBytes, string imageContentType, CancellationToken cancellationToken)
    {
        CallCount++;
        switch (Behavior)
        {
            case Mode.Unavailable:
                throw new AestheticSidecarException(AestheticErrorCodes.ModelUnavailable, "unavailable");
            case Mode.Timeout:
                throw new AestheticSidecarException(AestheticErrorCodes.Timeout, "timeout");
            case Mode.Cancelled:
                throw new OperationCanceledException();
            default:
                return Task.FromResult(Build(request, Behavior));
        }
    }

    public static AestheticSidecarResponse Build(AestheticSidecarRequest request, Mode mode)
    {
        var metrics = new List<AestheticSidecarMetric>();
        var keys = AestheticMetricCatalog.ExpertScoreKeys;
        for (int i = 0; i < keys.Count; i++)
        {
            // Skip the last key for MissingMetric; scale value in [0,1].
            if (mode == Mode.MissingMetric && i == keys.Count - 1)
            {
                continue;
            }
            var value = mode == Mode.OutOfRange && i == 0 ? 5.0 : (0.1 + (i % 9) * 0.1);
            metrics.Add(new AestheticSidecarMetric(keys[i], value, 0.0, 1.0, null, 1));
        }
        if (mode == Mode.Duplicate)
        {
            metrics.Add(new AestheticSidecarMetric(keys[0], 0.5, 0.0, 1.0, null, 1));
        }

        var completed = mode == Mode.Malformed
            ? new List<string> { "not_a_capability" }
            : new List<string> { AestheticCapabilities.ExpertScores };

        return new AestheticSidecarResponse(
            AestheticSidecarContract.Version,
            request.ProfileKey,
            "KlingTeam/HumanAesExpert-1B",
            "test-revision",
            "transformers",
            "4.44.2",
            request.PreprocessingProfileKey,
            completed,
            metrics,
            Array.Empty<AestheticSidecarText>(),
            Array.Empty<string>(),
            42);
    }
}
