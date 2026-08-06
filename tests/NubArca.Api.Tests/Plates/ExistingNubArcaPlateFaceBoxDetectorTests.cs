using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NubArca.Api.Ai;
using NubArca.Api.Ai.Backends;
using NubArca.Api.Ai.Resolution;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Plates;
using NubArca.Api.Plates.Redaction;
using Xunit;

namespace NubArca.Api.Tests.Plates;

// Verifies the existing-detector reuse adapter: it maps the AI face detector's
// boxes to privacy candidates (bbox + score only), and it fails SAFE (never
// serving the unredacted image) when the underlying detector/profile is
// unavailable. The adapter has NO AppDbContext / People dependency and calls
// only IFaceDetector.DetectFacesAsync (the evaluation-only path that persists
// nothing) — so it creates no FaceDetection/FaceEmbedding/cluster/person rows by
// construction. Confidence filtering + the MaxFaces cap are applied by
// PlateFaceRedactionService (covered in PlateFaceRedactionServiceTests).
public sealed class ExistingNubArcaPlateFaceBoxDetectorTests
{
    private static readonly AiProfile Profile = new()
    {
        Id = Guid.NewGuid(),
        Key = "face-insightface-antelopev2-v1",
        Capability = AiCapabilities.FaceEmbedding,
        Modality = "face",
        Dimension = 512,
        DistanceMetric = "cosine",
        Enabled = true,
    };

    private static ExistingNubArcaPlateFaceBoxDetector Build(
        AiBackendResolution<IFaceDetector> resolution, AiProfile? profile)
        => new(
            new FakeResolver(resolution),
            new FakeProfiles(profile),
            NullLogger<ExistingNubArcaPlateFaceBoxDetector>.Instance,
            Options.Create(new PlatesFaceRedactionOptions
            {
                Enabled = true,
                Provider = "ExistingNubArcaFaceDetector",
            }));

    private static PlateRedactionImageInput Image() =>
        new(new byte[] { 1, 2, 3 }, 200, 160);

    [Fact]
    public async Task Maps_Face_Boxes_To_Candidates_BoxesOnly()
    {
        var faces = new List<DetectedFace>
        {
            new(0.40, 0.20, 0.12, 0.16, 0.92, new List<FaceLandmark> { new(0.1, 0.1) }),
            new(0.60, 0.55, 0.10, 0.10, 0.71, null),
        };
        var backend = new FakeFaceDetector(new AiFaceDetectionResult(faces));
        var detector = Build(
            AiBackendResolution<IFaceDetector>.Available(
                backend, AiResolution.Available(AiCapabilities.FaceEmbedding, AiProviders.Onnx, Profile)),
            Profile);

        var result = await detector.DetectAsync(Image(), CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Equal(0.40, result[0].X, 3);
        Assert.Equal(0.16, result[0].Height, 3);
        Assert.Equal(0.92, result[0].Confidence, 3);
        Assert.Equal(1, backend.Calls); // detection-only reuse
    }

    [Fact]
    public async Task Throws_Safe_When_Detector_Unavailable()
    {
        var detector = Build(
            AiBackendResolution<IFaceDetector>.Unavailable(
                AiResolution.Unavailable(AiCapabilities.FaceEmbedding, "onnx-face-detector-not-found")),
            profile: null);

        await Assert.ThrowsAsync<PlateFaceRedactionUnavailableException>(
            () => detector.DetectAsync(Image(), CancellationToken.None));
    }

    [Fact]
    public async Task Throws_Safe_When_Profile_Missing()
    {
        var backend = new FakeFaceDetector(new AiFaceDetectionResult(new List<DetectedFace>()));
        var detector = Build(
            AiBackendResolution<IFaceDetector>.Available(
                backend, AiResolution.Available(AiCapabilities.FaceEmbedding, AiProviders.Onnx, Profile)),
            profile: null); // registry returns null

        await Assert.ThrowsAsync<PlateFaceRedactionUnavailableException>(
            () => detector.DetectAsync(Image(), CancellationToken.None));
    }

    [Fact]
    public async Task Throws_Safe_When_Backend_Inference_Fails()
    {
        var backend = new FakeFaceDetector(throws: true);
        var detector = Build(
            AiBackendResolution<IFaceDetector>.Available(
                backend, AiResolution.Available(AiCapabilities.FaceEmbedding, AiProviders.Onnx, Profile)),
            Profile);

        await Assert.ThrowsAsync<PlateFaceRedactionUnavailableException>(
            () => detector.DetectAsync(Image(), CancellationToken.None));
    }

    // ---- minimal fakes ----------------------------------------------------

    private sealed class FakeResolver : IAiBackendResolver
    {
        private readonly AiBackendResolution<IFaceDetector> _resolution;
        public FakeResolver(AiBackendResolution<IFaceDetector> resolution) => _resolution = resolution;

        public Task<AiBackendResolution<T>> ResolveForCapabilityAsync<T>(
            string capability, CancellationToken cancellationToken = default) where T : class, IAiBackend
            => Task.FromResult((AiBackendResolution<T>)(object)_resolution);

        public Task<AiBackendResolution<T>> ResolveForProfileKeyAsync<T>(
            string profileKey, CancellationToken cancellationToken = default) where T : class, IAiBackend
            => Task.FromResult((AiBackendResolution<T>)(object)_resolution);

        public Task<AiResolution> GetCapabilityAvailabilityAsync(
            string capability, CancellationToken cancellationToken = default)
            => Task.FromResult(_resolution.Resolution);
    }

    private sealed class FakeProfiles : IAiProfileRegistry
    {
        private readonly AiProfile? _profile;
        public FakeProfiles(AiProfile? profile) => _profile = profile;

        public Task<AiProfile?> GetProfileByKeyAsync(string key, CancellationToken cancellationToken = default)
            => Task.FromResult(_profile);

        public Task<IReadOnlyList<AiModel>> ListModelsAsync(bool enabledOnly = false, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<AiProfile>> ListProfilesAsync(bool enabledOnly = false, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<AiProfile?> GetDefaultProfileAsync(string capability, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<AiModel?> GetModelAsync(Guid modelId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public AiProfileCompatibility ValidateCompatibility(AiProfile profile, AiModel? model, IAiBackend? backend)
            => throw new NotSupportedException();
        public Task<AiSeedResult> SeedDeterministicProfilesAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<AiSeedResult> SeedOnnxImageEvalProfilesAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<AiSeedResult> SeedOnnxFaceEvalProfilesAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class FakeFaceDetector : IFaceDetector
    {
        private readonly AiFaceDetectionResult? _result;
        private readonly bool _throws;
        public int Calls { get; private set; }

        public FakeFaceDetector(AiFaceDetectionResult result) => _result = result;
        public FakeFaceDetector(bool throws) => _throws = throws;

        public string Provider => AiProviders.Onnx;
        public bool Supports(string capability) => capability == AiCapabilities.FaceDetection;

        public Task<AiFaceDetectionResult> DetectFacesAsync(
            ReadOnlyMemory<byte> imageBytes, AiProfile profile, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (_throws)
            {
                throw new InvalidOperationException("boom");
            }
            return Task.FromResult(_result!);
        }
    }
}
