using Microsoft.Extensions.Options;
using NubArca.Api.Ai.Onnx.Face;

namespace NubArca.Api.Ai.Faces;

// Sanitized face-substrate diagnostics for the admin/CLI surface: enabled flags,
// active profile key, the ACTIVE similarity thresholds (from IFaceSettingsProvider),
// and per-package model-file presence. Booleans/keys/counts only — never a model
// directory path, file path, raw vector, or any storage identifier.
public sealed class FaceDiagnosticsService
{
    private readonly IOptions<AiOptions> _options;
    private readonly IFaceSettingsProvider _settings;

    public FaceDiagnosticsService(IOptions<AiOptions> options, IFaceSettingsProvider settings)
    {
        _options = options;
        _settings = settings;
    }

    public async Task<FaceDiagnostics> GetAsync(CancellationToken cancellationToken = default)
    {
        var o = _options.Value;
        var s = await _settings.GetAsync(cancellationToken);
        var modelDir = o.Onnx.ModelDir;
        var modelDirConfigured = !string.IsNullOrWhiteSpace(modelDir);

        var models = OnnxFaceModels.ProfileToCatalogKey
            .Select(kv =>
            {
                var config = OnnxFaceModels.Catalog[kv.Value];
                var detectorPresent = modelDirConfigured
                    && File.Exists(Path.Combine(modelDir!, config.PackageSubdir, config.DetectorFile));
                var recognitionPresent = modelDirConfigured
                    && File.Exists(Path.Combine(modelDir!, config.PackageSubdir, config.RecognitionFile));
                return new FaceModelPresence(kv.Key, config.Dimension, detectorPresent, recognitionPresent);
            })
            .ToList();

        return new FaceDiagnostics(
            o.Enabled,
            o.FaceDetectionEnabled,
            o.FaceEmbeddingsEnabled,
            o.FaceClusteringEnabled,
            o.FaceProfileKey,
            modelDirConfigured,
            o.Onnx.IntraOpThreads,
            o.MaxConcurrency,
            s,
            models,
            new FaceClusteringInfo(
                o.Face.ClusteringMode,
                o.Face.KnnNeighbors,
                o.Face.KnnEfSearch,
                // Effective edge/cohesion + graph thresholds (the admin-editable values,
                // not the raw config), so the CLI/admin read-out matches what the
                // pgvector_knn+Louvain path actually uses.
                s.ClusterSimilarityThreshold,
                s.CandidateSimilarityThreshold,
                o.Face.KnnMaxEligibleFacesPerRun,
                o.Face.KnnMaxClusterSize,
                FaceClusteringService.MaxFacesToCluster));
    }
}

public sealed record FaceModelPresence(string ProfileKey, int Dimension, bool DetectorPresent, bool RecognitionPresent);

// Read-only view of the active clustering strategy (mode + kNN tunables). No
// internals; drives the admin display. Control is via Ai:Face:* config.
public sealed record FaceClusteringInfo(
    string Mode,
    int KnnNeighbors,
    int KnnEfSearch,
    double KnnMinSimilarity,
    double KnnCandidateSimilarity,
    int KnnMaxEligibleFacesPerRun,
    int KnnMaxClusterSize,
    int ExactMaxFacesToCluster);

public sealed record FaceDiagnostics(
    bool AiEnabled,
    bool FaceDetectionEnabled,
    bool FaceEmbeddingsEnabled,
    bool FaceClusteringEnabled,
    string? ActiveFaceProfileKey,
    bool ModelDirConfigured,
    int? OnnxIntraOpThreads,
    int MaxConcurrency,
    FaceSettings Thresholds,
    IReadOnlyList<FaceModelPresence> Models,
    FaceClusteringInfo Clustering);
