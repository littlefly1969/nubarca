using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Ai;
using NubArca.Api.Ai.Backends;
using NubArca.Api.Ai.Faces;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Storage;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Users;
using Xunit;

namespace NubArca.Api.Tests.Ai;

// Regression suite for the per-face resilient face-embedding backfill. Faces are
// seeded directly and a STUB aligned embedder drives controlled per-face outcomes
// (Ok / AlignmentInvalid / RecognitionFailed) plus whole-image failures, so the
// isolation + status classification is deterministic without ONNX weights.
public sealed class FaceEmbeddingResilienceTests
{
    private const string FaceProfileKey = "det-face-embedding-v1";
    private const int Dim = 32;

    private static async Task<(SqliteWebApplicationFactory f, Guid profileId, Guid ownerId)> NewAsync()
    {
        var f = new SqliteWebApplicationFactory(new Dictionary<string, string?> { ["Ai:Enabled"] = "true" });
        f.EnsureDatabaseCreated();
        Guid profileId, ownerId;
        using (var scope = f.Services.CreateScope())
        {
            var registry = scope.ServiceProvider.GetRequiredService<IAiProfileRegistry>();
            await registry.SeedDeterministicProfilesAsync();
            profileId = (await registry.GetProfileByKeyAsync(FaceProfileKey))!.Id;
            ownerId = (await scope.ServiceProvider.GetRequiredService<IUserService>()
                .CreateAsync($"o-{Guid.NewGuid():N}@example.com", "O")).Id;
        }
        return (f, profileId, ownerId);
    }

    // One blob + owner file + N landmarked face detections (a "crowded" image).
    private static async Task<List<Guid>> SeedCrowdedBlobAsync(
        SqliteWebApplicationFactory f, Guid ownerId, Guid profileId, int n)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var blobId = Guid.NewGuid();
        db.BlobObjects.Add(new BlobObject
        {
            Id = blobId, Sha256 = $"sha-{blobId:N}", SizeBytes = 1,
            StorageKey = $"sk/{blobId:N}", ReferenceCount = 1, CreatedAt = DateTime.UtcNow,
        });
        db.FileItems.Add(new FileItem
        {
            Id = Guid.NewGuid(), OwnerUserId = ownerId, BlobObjectId = blobId, Name = "crowd.jpg",
            MimeType = "image/jpeg", SizeBytes = 1, CreatedAt = DateTime.UtcNow, EffectiveDateTaken = DateTime.UtcNow,
        });
        var faceIds = new List<Guid>();
        for (var i = 0; i < n; i++)
        {
            var id = Guid.NewGuid();
            faceIds.Add(id);
            db.FaceDetections.Add(new FaceDetection
            {
                Id = id, BlobObjectId = blobId, ProfileId = profileId, FaceIndex = i,
                BoundingBoxX = 0.1, BoundingBoxY = 0.1, BoundingBoxWidth = 0.2, BoundingBoxHeight = 0.2,
                DetectionScore = 0.9, LandmarksJson = "[{\"X\":0.1,\"Y\":0.1}]", CreatedAt = DateTime.UtcNow,
            });
        }
        await db.SaveChangesAsync();
        return faceIds;
    }

    private static async Task<(FaceBackfillResult result, List<FaceEmbedding> rows, int diagnostics)> RunAsync(
        SqliteWebApplicationFactory f, Guid profileId, IFaceEmbedder embedder, IBlobService? blobs = null)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var serializer = scope.ServiceProvider.GetRequiredService<IAiVectorSerializer>();
        var vectors = new FaceVectorIndexService(db, serializer, TimeProvider.System);
        var service = new FaceEmbeddingBackfillService(
            db, blobs ?? new StubBlobService(), serializer, vectors, TimeProvider.System);
        var profile = await db.AiProfiles.FirstAsync(p => p.Id == profileId);
        var result = await service.RunAsync(embedder, profile, new FaceBackfillOptions());
        var rows = await db.FaceEmbeddings.AsNoTracking().Where(e => e.ProfileId == profileId).ToListAsync();
        var diag = await db.AiIndexDiagnostics.AsNoTracking()
            .CountAsync(d => d.Capability == AiCapabilities.FaceEmbedding);
        return (result, rows, diag);
    }

    private static FaceEmbedAttempt Ok() =>
        FaceEmbedAttempt.Ok(new AiEmbeddingResult(UnitVector(), Dim, AiDistanceMetrics.Cosine));

    private static float[] UnitVector()
    {
        var v = new float[Dim];
        v[0] = 1f;
        return v;
    }

    // ---- tests -----------------------------------------------------------

    [Fact]
    public async Task One_Face_Fails_But_Others_Are_Saved()
    {
        var (f, profileId, ownerId) = await NewAsync();
        using var _ = f;
        var faceIds = await SeedCrowdedBlobAsync(f, ownerId, profileId, 3);

        var embedder = new StubAlignedEmbedder(i => i == 1 ? FaceEmbedAttempt.RecognitionFailed : Ok());
        var (result, rows, _) = await RunAsync(f, profileId, embedder);

        Assert.Equal(2, result.Produced);
        Assert.Equal(1, result.Failed);
        Assert.Equal(2, rows.Count(r => r.EmbeddingStatus == AiArtifactStatuses.Completed));
        var failedRow = Assert.Single(rows, r => r.EmbeddingStatus == AiArtifactStatuses.Failed);
        Assert.Equal(FaceEmbeddingErrorCodes.RecognitionFailed, failedRow.ErrorCode);
        Assert.Equal(faceIds[1], failedRow.FaceDetectionId);
        Assert.Empty(failedRow.EmbeddingBytes); // no vector stored for a failed face
    }

    [Fact]
    public async Task Several_Fail_Several_Succeed_And_One_Skips()
    {
        var (f, profileId, ownerId) = await NewAsync();
        using var _ = f;
        await SeedCrowdedBlobAsync(f, ownerId, profileId, 5);

        // 0 ok, 1 recognition-fail, 2 ok, 3 alignment-invalid, 4 ok.
        var embedder = new StubAlignedEmbedder(i => i switch
        {
            1 => FaceEmbedAttempt.RecognitionFailed,
            3 => FaceEmbedAttempt.AlignmentInvalid,
            _ => Ok(),
        });
        var (result, rows, _) = await RunAsync(f, profileId, embedder);

        Assert.Equal(3, result.Produced);
        Assert.Equal(1, result.Failed);
        Assert.Equal(1, result.Skipped);
        Assert.Equal(3, rows.Count(r => r.EmbeddingStatus == AiArtifactStatuses.Completed));
        Assert.Equal(1, rows.Count(r => r.EmbeddingStatus == AiArtifactStatuses.Failed));
        var skipped = Assert.Single(rows, r => r.EmbeddingStatus == AiArtifactStatuses.Skipped);
        Assert.Equal(FaceEmbeddingErrorCodes.AlignmentInvalid, skipped.ErrorCode);
    }

    [Fact]
    public async Task Failed_And_Skipped_Are_Not_Counted_As_Missing()
    {
        var (f, profileId, ownerId) = await NewAsync();
        using var _ = f;
        await SeedCrowdedBlobAsync(f, ownerId, profileId, 5);
        var embedder = new StubAlignedEmbedder(i => i switch
        {
            1 => FaceEmbedAttempt.RecognitionFailed,
            3 => FaceEmbedAttempt.AlignmentInvalid,
            _ => Ok(),
        });
        await RunAsync(f, profileId, embedder);

        using var scope = f.Services.CreateScope();
        var coverage = scope.ServiceProvider.GetRequiredService<FaceCoverageService>();
        var c = await coverage.GetCoverageAsync(FaceProfileKey);
        Assert.NotNull(c);
        Assert.Equal(3, c!.EmbeddingsCompleted);
        Assert.Equal(1, c.EmbeddingsFailed);
        Assert.Equal(1, c.EmbeddingsSkipped);
        Assert.Equal(0, c.EmbeddingsMissing); // all 5 attempted → none missing
    }

    [Fact]
    public async Task Rerun_Retries_Failed_But_Not_Skipped()
    {
        var (f, profileId, ownerId) = await NewAsync();
        using var _ = f;
        await SeedCrowdedBlobAsync(f, ownerId, profileId, 3);

        // Run 1: 0 ok, 1 failed, 2 skipped.
        var run1 = new StubAlignedEmbedder(i => i switch
        {
            1 => FaceEmbedAttempt.RecognitionFailed,
            2 => FaceEmbedAttempt.AlignmentInvalid,
            _ => Ok(),
        });
        await RunAsync(f, profileId, run1);

        // Run 2: everything would succeed now — but only the FAILED face is a
        // candidate (completed + skipped are excluded).
        var run2 = new StubAlignedEmbedder(_ => Ok());
        var (result2, rows, _) = await RunAsync(f, profileId, run2);

        Assert.Equal(1, run2.LastFaceCount); // only the transient-failed face retried
        Assert.Equal(1, result2.Produced);
        Assert.Equal(3, rows.Count(r => r.EmbeddingStatus == AiArtifactStatuses.Completed) +
            rows.Count(r => r.EmbeddingStatus == AiArtifactStatuses.Skipped)); // 2 completed + 1 skipped
        Assert.Equal(1, rows.Count(r => r.EmbeddingStatus == AiArtifactStatuses.Skipped));
        Assert.Equal(0, rows.Count(r => r.EmbeddingStatus == AiArtifactStatuses.Failed));
    }

    [Fact]
    public async Task Whole_Image_Unreadable_Marks_All_Failed_And_Is_Retryable()
    {
        var (f, profileId, ownerId) = await NewAsync();
        using var _ = f;
        await SeedCrowdedBlobAsync(f, ownerId, profileId, 4);

        // Bytes unreadable → all pending faces FAILED (transient, shared reason).
        var okEmbedder = new StubAlignedEmbedder(_ => Ok());
        var (r1, rows1, _) = await RunAsync(f, profileId, okEmbedder, new StubBlobService(throwOnOpen: true));
        Assert.Equal(0, r1.Produced);
        Assert.Equal(4, r1.Failed);
        Assert.Equal(4, rows1.Count(r => r.EmbeddingStatus == AiArtifactStatuses.Failed));
        Assert.All(rows1, r => Assert.Equal(FaceEmbeddingErrorCodes.Unknown, r.ErrorCode));

        // Retry with readable bytes → all complete.
        var (r2, rows2, _) = await RunAsync(f, profileId, okEmbedder);
        Assert.Equal(4, r2.Produced);
        Assert.Equal(4, rows2.Count(r => r.EmbeddingStatus == AiArtifactStatuses.Completed));
        Assert.Equal(0, rows2.Count(r => r.EmbeddingStatus == AiArtifactStatuses.Failed));
    }

    [Fact]
    public async Task Batch_Exception_Marks_All_Failed_Not_Skipped()
    {
        var (f, profileId, ownerId) = await NewAsync();
        using var _ = f;
        await SeedCrowdedBlobAsync(f, ownerId, profileId, 3);

        var throwing = new StubAlignedEmbedder(_ => Ok(), throwBatch: true);
        var (result, rows, _) = await RunAsync(f, profileId, throwing);

        Assert.Equal(0, result.Produced);
        Assert.Equal(3, result.Failed);
        Assert.All(rows, r => Assert.Equal(AiArtifactStatuses.Failed, r.EmbeddingStatus));
        Assert.All(rows, r => Assert.Equal(FaceEmbeddingErrorCodes.Unknown, r.ErrorCode));
    }

    [Fact]
    public async Task Records_Bounded_Aggregate_Diagnostics()
    {
        var (f, profileId, ownerId) = await NewAsync();
        using var _ = f;
        await SeedCrowdedBlobAsync(f, ownerId, profileId, 3);

        // Run 1: 1 failed + 1 skipped → 2 diagnostics.
        var run1 = new StubAlignedEmbedder(i => i switch
        {
            1 => FaceEmbedAttempt.RecognitionFailed,
            2 => FaceEmbedAttempt.AlignmentInvalid,
            _ => Ok(),
        });
        var (_, _, diag1) = await RunAsync(f, profileId, run1);
        Assert.Equal(2, diag1);

        // Run 2: the failed face fails again (same status) → NO new diagnostic
        // (bounded: only new/transition states are recorded).
        var run2 = new StubAlignedEmbedder(_ => FaceEmbedAttempt.RecognitionFailed);
        var (_, _, diag2) = await RunAsync(f, profileId, run2);
        Assert.Equal(2, diag2);

        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var codes = await db.AiIndexDiagnostics.AsNoTracking()
            .Where(d => d.Capability == AiCapabilities.FaceEmbedding)
            .Select(d => d.ErrorCode).ToListAsync();
        Assert.Contains(FaceEmbeddingErrorCodes.RecognitionFailed, codes);
        Assert.Contains(FaceEmbeddingErrorCodes.AlignmentInvalid, codes);
        Assert.All(await db.AiIndexDiagnostics.AsNoTracking().ToListAsync(),
            d => Assert.Equal(AiDiagnosticTargetKinds.FaceDetection, d.TargetKind));
    }

    // ---- stubs -----------------------------------------------------------

    private sealed class StubAlignedEmbedder : IAlignedFaceEmbedder
    {
        private readonly Func<int, FaceEmbedAttempt> _perIndex;
        private readonly bool _throwBatch;
        public int LastFaceCount { get; private set; } = -1;

        public StubAlignedEmbedder(Func<int, FaceEmbedAttempt> perIndex, bool throwBatch = false)
        {
            _perIndex = perIndex;
            _throwBatch = throwBatch;
        }

        public string Provider => "stub";
        public bool Supports(string capability) =>
            capability == AiCapabilities.FaceEmbedding || capability == AiCapabilities.FaceDetection;

        public Task<AiEmbeddingResult> EmbedFaceAsync(
            ReadOnlyMemory<byte> faceCropBytes, AiProfile profile, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<FaceEmbedAttempt>> EmbedAlignedFacesAsync(
            ReadOnlyMemory<byte> imageBytes,
            IReadOnlyList<IReadOnlyList<FaceLandmark>> normalizedLandmarksPerFace,
            AiProfile profile,
            CancellationToken cancellationToken = default)
        {
            LastFaceCount = normalizedLandmarksPerFace.Count;
            if (_throwBatch)
            {
                throw new InvalidOperationException("batch boom");
            }
            var results = new FaceEmbedAttempt[normalizedLandmarksPerFace.Count];
            for (var i = 0; i < results.Length; i++)
            {
                results[i] = _perIndex(i);
            }
            return Task.FromResult<IReadOnlyList<FaceEmbedAttempt>>(results);
        }
    }

    private sealed class StubBlobService : IBlobService
    {
        private readonly bool _throw;
        public StubBlobService(bool throwOnOpen = false) => _throw = throwOnOpen;

        public Task<Stream> OpenContentAsync(Guid blobObjectId, CancellationToken cancellationToken = default)
            => _throw
                ? throw new IOException("unreadable")
                : Task.FromResult<Stream>(new MemoryStream(new byte[] { 1, 2, 3, 4 }));

        public Task<BlobObject> StoreAsync(Stream content, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BlobStoreResult> StoreMeasuredAsync(Stream content, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BlobObject> StoreDerivedAsync(Stream content, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Stream?> OpenDerivedContentAsync(Guid blobObjectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ReleaseAsync(Guid blobObjectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task MarkPurgeEligibleIfUnreferencedAsync(Guid blobObjectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BlobObject> AcquireExistingAsync(Guid blobObjectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> TryRestoreDerivedFromOriginalAsync(Guid blobObjectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
