using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;

namespace NubArca.Api.Ai.Faces;

// Aggregate, sanitized face coverage for an operator/admin. Counts only — never
// vectors, BlobObjectId, SHA, StorageKey, paths, owner ids, or vault/private
// signals. "Eligible" counts image blobs referenced by an active NON-VAULT
// FileItem (vault-only blobs are never detected, so they are never counted).
public sealed class FaceCoverageService
{
    private readonly AppDbContext _db;
    private readonly IAiProfileRegistry _registry;
    private readonly FaceVectorIndexService _vectors;

    public FaceCoverageService(AppDbContext db, IAiProfileRegistry registry, FaceVectorIndexService vectors)
    {
        _db = db;
        _registry = registry;
        _vectors = vectors;
    }

    public async Task<FaceCoverage?> GetCoverageAsync(string profileKey, CancellationToken cancellationToken = default)
    {
        var profile = await _registry.GetProfileByKeyAsync(profileKey, cancellationToken);
        if (profile is null)
        {
            return null;
        }

        var eligibleImages = await _db.BlobObjects.AsNoTracking().LongCountAsync(b =>
            _db.BlobMetadata.Any(m => m.BlobObjectId == b.Id && m.MediaCategory == MediaCategories.Image)
            && _db.FileItems.Any(f => f.BlobObjectId == b.Id && f.DeletedAt == null),
            cancellationToken);

        var detectionCompletedBlobs = await _db.BlobAiArtifactStatuses.AsNoTracking().LongCountAsync(s =>
            s.ProfileId == profile.Id
            && s.Capability == AiCapabilities.FaceDetection
            && s.Status == AiArtifactStatuses.Completed,
            cancellationToken);

        var detectionMissingBlobs = Math.Max(0, eligibleImages - detectionCompletedBlobs);

        var facesDetected = await _db.FaceDetections.AsNoTracking()
            .LongCountAsync(d => d.ProfileId == profile.Id, cancellationToken);

        var facesEmbeddable = await _db.FaceDetections.AsNoTracking()
            .LongCountAsync(d => d.ProfileId == profile.Id && d.LandmarksJson != null, cancellationToken);

        var embeddingsCompleted = await _db.FaceEmbeddings.AsNoTracking()
            .LongCountAsync(e => e.ProfileId == profile.Id && e.EmbeddingStatus == AiArtifactStatuses.Completed, cancellationToken);
        var embeddingsFailed = await _db.FaceEmbeddings.AsNoTracking()
            .LongCountAsync(e => e.ProfileId == profile.Id && e.EmbeddingStatus == AiArtifactStatuses.Failed, cancellationToken);
        var embeddingsSkipped = await _db.FaceEmbeddings.AsNoTracking()
            .LongCountAsync(e => e.ProfileId == profile.Id && e.EmbeddingStatus == AiArtifactStatuses.Skipped, cancellationToken);

        // Missing = embeddable faces with NO row at all (never attempted). Failed
        // and skipped are attempted, so they are NOT counted as missing.
        var attempted = embeddingsCompleted + embeddingsFailed + embeddingsSkipped;
        var embeddingsMissing = Math.Max(0, facesEmbeddable - attempted);

        var vectorSupported = FaceVectorIndexService.SupportsDimension(profile.Dimension);
        var vectorIndexed = await _vectors.CountIndexedAsync(profile.Id, cancellationToken);
        var missingVectors = Math.Max(0, embeddingsCompleted - vectorIndexed);

        return new FaceCoverage(
            profile.Key,
            profile.Dimension,
            profile.DistanceMetric,
            eligibleImages,
            detectionCompletedBlobs,
            detectionMissingBlobs,
            facesDetected,
            embeddingsCompleted,
            embeddingsMissing,
            embeddingsFailed,
            embeddingsSkipped,
            vectorSupported,
            vectorIndexed,
            missingVectors,
            Pct(embeddingsCompleted, facesEmbeddable),
            Pct(vectorIndexed, embeddingsCompleted));
    }

    private static double Pct(long numerator, long denominator)
        => denominator <= 0 ? 100d : Math.Round(100d * numerator / denominator, 2);
}

public sealed record FaceCoverage(
    string ProfileKey,
    int? Dimension,
    string? DistanceMetric,
    long EligibleImages,
    long DetectionCompletedBlobs,
    long DetectionMissingBlobs,
    long FacesDetected,
    long EmbeddingsCompleted,
    long EmbeddingsMissing,
    long EmbeddingsFailed,
    long EmbeddingsSkipped,
    bool VectorSupported,
    long VectorIndexed,
    long MissingVectors,
    double EmbeddingCoveragePercent,
    double VectorCoveragePercent);
