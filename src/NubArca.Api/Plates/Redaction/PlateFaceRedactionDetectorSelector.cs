using Microsoft.Extensions.Options;

namespace NubArca.Api.Plates.Redaction;

// Routes privacy face-box detection to the configured provider:
//   Disabled                      -> unavailable (blurFaces=true → safe 409)
//   DeterministicDev              -> the deterministic dev/test detector (Slice 3)
//   ExistingNubArcaFaceDetector -> reuse the AI substrate's face-box detector
//   OnnxDedicatedFaceDetector     -> not implemented this slice → unavailable
//
// This is the SINGLE IPlateFaceRedactionDetector the rest of Plates depends on;
// the concrete detectors are registered as themselves and composed here.
public sealed class PlateFaceRedactionDetectorSelector : IPlateFaceRedactionDetector
{
    private readonly DeterministicPlateFaceRedactionDetector _deterministic;
    private readonly ExistingNubArcaPlateFaceBoxDetector _existing;
    private readonly PlatesFaceRedactionOptions _options;

    public PlateFaceRedactionDetectorSelector(
        DeterministicPlateFaceRedactionDetector deterministic,
        ExistingNubArcaPlateFaceBoxDetector existing,
        IOptions<PlatesFaceRedactionOptions>? options = null)
    {
        _deterministic = deterministic;
        _existing = existing;
        _options = options?.Value ?? new PlatesFaceRedactionOptions();
    }

    public string ProfileKey => _options.ProfileKey;

    public bool IsAvailable => _options.ResolveProvider() switch
    {
        PlateFaceRedactionProvider.DeterministicDev => _deterministic.IsAvailable,
        PlateFaceRedactionProvider.ExistingNubArcaFaceDetector => _existing.IsAvailable,
        // Disabled + the unimplemented dedicated ONNX provider are unavailable.
        _ => false,
    };

    public Task<IReadOnlyList<PlateFaceRedactionCandidate>> DetectAsync(
        PlateRedactionImageInput image, CancellationToken cancellationToken)
        => Selected().DetectAsync(image, cancellationToken);

    private IPlateFaceRedactionDetector Selected() => _options.ResolveProvider() switch
    {
        PlateFaceRedactionProvider.DeterministicDev => _deterministic,
        PlateFaceRedactionProvider.ExistingNubArcaFaceDetector => _existing,
        // Selecting an unavailable provider and still calling DetectAsync throws
        // the safe unavailable error (never serves the unredacted image).
        _ => ThrowingUnavailableDetector.Instance,
    };

    // Backstop so a DetectAsync on an unavailable provider fails safely rather
    // than serving unredacted media (the callers gate on IsAvailable first, but
    // this guarantees the invariant even if a caller forgets).
    private sealed class ThrowingUnavailableDetector : IPlateFaceRedactionDetector
    {
        public static readonly ThrowingUnavailableDetector Instance = new();
        public bool IsAvailable => false;
        public string ProfileKey => string.Empty;

        public Task<IReadOnlyList<PlateFaceRedactionCandidate>> DetectAsync(
            PlateRedactionImageInput image, CancellationToken cancellationToken)
            => throw new PlateFaceRedactionUnavailableException();
    }
}
