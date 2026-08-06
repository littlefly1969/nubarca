using Microsoft.Extensions.Options;

namespace NubArca.Api.Plates.Redaction;

// Deterministic, dependency-free PRIVACY-ONLY face detector for DEV/TEST ONLY.
// It returns a stable, NON-SEMANTIC face box (it does not actually look at the
// pixels), mirroring the ALPR deterministic backend rule: it is not a real face
// detector and must never be relied on in production as a substitute for a
// trained model. It creates NO People/Face identity artifacts, no embeddings, no
// clusters — it only yields a rectangle to pixelate.
//
// Availability follows the feature switch (Plates:FaceRedaction:Enabled), so
// with the feature disabled a blurFaces=true request returns a safe
// "not configured" error instead of the unredacted image.
public sealed class DeterministicPlateFaceRedactionDetector : IPlateFaceRedactionDetector
{
    private readonly IOptions<PlatesFaceRedactionOptions> _options;

    public DeterministicPlateFaceRedactionDetector(IOptions<PlatesFaceRedactionOptions> options)
        => _options = options;

    // Always runnable when selected: provider routing (the selector) plus the
    // service's Enabled gate decide whether it is used. Kept dependency-free.
    public bool IsAvailable => true;

    public string ProfileKey => _options.Value.ProfileKey;

    // A single fixed face region (normalized), matching the documented dev
    // fixture, high enough confidence to clear any reasonable MinConfidence.
    private static readonly PlateFaceRedactionCandidate[] Candidates =
    {
        new(X: 0.40, Y: 0.20, Width: 0.12, Height: 0.16, Confidence: 0.92),
    };

    public Task<IReadOnlyList<PlateFaceRedactionCandidate>> DetectAsync(
        PlateRedactionImageInput image, CancellationToken cancellationToken)
    {
        IReadOnlyList<PlateFaceRedactionCandidate> result = Candidates;
        return Task.FromResult(result);
    }
}
