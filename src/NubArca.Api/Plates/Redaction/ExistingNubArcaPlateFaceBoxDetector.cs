using Microsoft.Extensions.Options;
using NubArca.Api.Ai;
using NubArca.Api.Ai.Backends;
using NubArca.Api.Ai.Faces;
using NubArca.Api.Ai.Resolution;
using NubArca.Api.Domain.Ai;

namespace NubArca.Api.Plates.Redaction;

// PRIVACY-ONLY face-box detector that REUSES NubArca's existing face detector
// (the AI substrate's ONNX SCRFD IFaceDetector) to obtain BOUNDING BOXES ONLY.
//
// The `ExistingNubArcaFaceDetector` provider enum member is parsed BY NAME from
// operator configuration (Plates:FaceRedaction:Provider), so the spelling here is
// the configuration contract, not just an internal type name.
//
// It calls IFaceDetector.DetectFacesAsync, which is evaluation-only: it runs
// detection inference and returns normalized boxes WITHOUT persisting any
// FaceDetection/FaceEmbedding row, creating clusters/people/assignments, or
// computing embeddings (see OnnxFaceBackend). This adapter therefore reuses the
// effective detector while keeping Plates redaction completely separate from
// People identity. It deliberately does NOT touch the People/Face domain tables,
// does NOT resolve an embedder, and does NOT carry landmarks forward (only the
// bbox + score are used, for pixelation).
//
// Availability: "configured" is a synchronous fact (provider selected). The REAL
// model/profile availability is only known at detect time (it needs an async DB
// profile lookup + a model-file check), so DetectAsync throws
// PlateFaceRedactionUnavailableException when the underlying detector is
// unavailable — the endpoint maps that to a safe 409 and NEVER serves the
// unredacted image.
public sealed class ExistingNubArcaPlateFaceBoxDetector : IPlateFaceRedactionDetector
{
    private readonly IAiBackendResolver _resolver;
    private readonly IAiProfileRegistry _profiles;
    private readonly PlatesFaceRedactionOptions _options;
    private readonly ILogger<ExistingNubArcaPlateFaceBoxDetector> _logger;

    public ExistingNubArcaPlateFaceBoxDetector(
        IAiBackendResolver resolver,
        IAiProfileRegistry profiles,
        ILogger<ExistingNubArcaPlateFaceBoxDetector> logger,
        IOptions<PlatesFaceRedactionOptions>? options = null)
    {
        _resolver = resolver;
        _profiles = profiles;
        _logger = logger;
        _options = options?.Value ?? new PlatesFaceRedactionOptions();
    }

    // Configured (the real model check happens in DetectAsync). The selector only
    // routes here when the provider is ExistingNubArcaFaceDetector.
    public bool IsAvailable => true;

    public string ProfileKey => _options.ProfileKey;

    public async Task<IReadOnlyList<PlateFaceRedactionCandidate>> DetectAsync(
        PlateRedactionImageInput image, CancellationToken cancellationToken)
    {
        var configuredKey = string.IsNullOrWhiteSpace(_options.ExistingDetectorProfileKey)
            ? null
            : _options.ExistingDetectorProfileKey;

        // Resolve the existing face DETECTOR only (never an embedder). Any
        // unavailability (AI disabled, no profile, model files missing) is a safe
        // unavailable state, mapped to 409 — never a leak of the unredacted image.
        var detector = await FaceProfileResolver.ResolveDetectorAsync(
            _resolver, payloadProfileKey: configuredKey, configuredProfileKey: null, cancellationToken);
        if (!detector.IsAvailable || detector.Backend is null || detector.Resolution.ProfileKey is null)
        {
            _logger.LogInformation(
                "Plates face redaction: existing NubArca face detector unavailable ({Reason}).",
                detector.Resolution.UnavailableReason ?? "unknown");
            throw new PlateFaceRedactionUnavailableException();
        }

        var profile = await _profiles.GetProfileByKeyAsync(detector.Resolution.ProfileKey!, cancellationToken);
        if (profile is null)
        {
            throw new PlateFaceRedactionUnavailableException();
        }

        AiFaceDetectionResult detection;
        try
        {
            // Boxes only. This runs detection inference and returns normalized
            // boxes; it persists NOTHING (see OnnxFaceBackend).
            detection = await detector.Backend.DetectFacesAsync(image.Bytes, profile, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A whole-image decode/inference failure is an environment/processing
            // state — never serve the unredacted image.
            _logger.LogWarning(ex, "Plates face redaction: existing detector inference failed.");
            throw new PlateFaceRedactionUnavailableException();
        }

        // Map to privacy candidates: bbox + score only (drop landmarks). Filtering
        // by confidence and the MaxFaces cap are applied by PlateFaceRedactionService.
        return detection.Faces
            .Select(f => new PlateFaceRedactionCandidate(
                f.X, f.Y, f.Width, f.Height, f.Confidence ?? 1.0))
            .ToList();
    }
}
