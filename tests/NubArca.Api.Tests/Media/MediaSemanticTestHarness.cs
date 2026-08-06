using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Ai;
using NubArca.Api.Ai.Backends;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Files;
using NubArca.Api.Media.Semantic;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Tests.Metadata;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace NubArca.Api.Tests.Media;

// VSEM-03 shared harness: a SQLite web host with the DETERMINISTIC backend and
// a seeded 1152-dim paired profile, plus controlled-similarity photo/video
// seeding. Runs on SQLite (no pgvector), so both vector layers exercise their
// mandatory exact fallbacks; the pgvector SQL paths are proven equivalent by
// their own integration suites (PhotoVectorPg / VideoSemanticVectorPg).
internal static class MediaSemanticTestHarness
{
    public const string ProfileKey = "test-multimodal-1152";
    public const string Query = "cane nero sulla neve";
    public const int Dim = 1152;

    public static SqliteWebApplicationFactory Factory()
    {
        var f = new SqliteWebApplicationFactory(new Dictionary<string, string?>
        {
            ["Ai:Enabled"] = "true",
            ["Ai:PhotoSimilarityProfileKey"] = ProfileKey,
        }, poolHost: true);
        f.EnsureDatabaseCreated();
        return f;
    }

    public static async Task<AiProfile> SeedProfileAsync(
        SqliteWebApplicationFactory factory, string? key = null, bool isDefault = false)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var model = new AiModel
        {
            Id = Guid.NewGuid(), Key = $"m-{Guid.NewGuid():N}",
            Provider = AiProviders.Deterministic, Capability = AiCapabilities.ImageEmbedding,
            Modality = AiModalities.Image, Dimension = Dim, DistanceMetric = AiDistanceMetrics.Cosine,
            Version = 1, Enabled = true, CreatedAt = DateTime.UtcNow,
        };
        var profile = new AiProfile
        {
            Id = Guid.NewGuid(), Key = key ?? ProfileKey, AiModelId = model.Id,
            Capability = AiCapabilities.ImageEmbedding, Modality = AiModalities.Image,
            Dimension = Dim, DistanceMetric = AiDistanceMetrics.Cosine,
            IsDefault = isDefault, Enabled = true, CreatedAt = DateTime.UtcNow,
        };
        db.AiModels.Add(model);
        db.AiProfiles.Add(profile);
        await db.SaveChangesAsync();
        return profile;
    }

    // The deterministic text-tower vector for the fixed query — the anchor all
    // controlled-similarity media vectors are built from.
    public static float[] QueryVector(AiProfile profile)
        => new DeterministicAiBackend().EmbedTextAsync(Query, profile).GetAwaiter().GetResult().Vector;

    // A vector whose cosine similarity to `query` is EXACTLY `similarity`:
    // s·q̂ + √(1-s²)·ê with ê ⟂ q̂. Lets tests dictate the ranking order.
    public static float[] WithSimilarity(float[] query, double similarity)
    {
        var q = Normalize(query);
        var axis = 0;
        var min = Math.Abs(q[0]);
        for (var i = 1; i < q.Length; i++)
        {
            if (Math.Abs(q[i]) < min)
            {
                min = Math.Abs(q[i]);
                axis = i;
            }
        }

        var e = new float[q.Length];
        e[axis] = 1f;
        var dot = q[axis];
        for (var i = 0; i < e.Length; i++)
        {
            e[i] -= dot * q[i];
        }
        e = Normalize(e);

        var c = Math.Sqrt(Math.Max(0, 1 - similarity * similarity));
        var result = new float[q.Length];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = (float)(similarity * q[i] + c * e[i]);
        }
        return result;
    }

    private static float[] Normalize(float[] v)
    {
        double norm = 0;
        foreach (var x in v) norm += (double)x * x;
        norm = Math.Sqrt(norm);
        var result = new float[v.Length];
        for (var i = 0; i < v.Length; i++) result[i] = (float)(v[i] / norm);
        return result;
    }

    // ---- media seeding -----------------------------------------------------

    public static byte[] Png(byte color)
    {
        using var image = new Image<Rgba32>(160, 160, new Rgba32(color, color, color));
        using var stream = new MemoryStream();
        image.Save(stream, new PngEncoder());
        return stream.ToArray();
    }

    public static async Task<(Guid FileId, Guid BlobId)> UploadPhotoAsync(
        SqliteWebApplicationFactory factory, Guid ownerId, byte color, string? name = null)
    {
        using var scope = factory.Services.CreateScope();
        var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
        var f = await files.CreateAsync(
            ownerId, null, name ?? $"p-{color}-{Guid.NewGuid():N}.png", "image/png",
            new MemoryStream(Png(color)));
        return (f.Id, f.BlobObjectId);
    }

    public static async Task<(Guid FileId, Guid BlobId)> UploadVideoAsync(
        SqliteWebApplicationFactory factory, Guid ownerId, byte[]? bytes = null, string? name = null)
    {
        using var scope = factory.Services.CreateScope();
        var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
        // Default uploads get UNIQUE bytes (content-addressed dedup would
        // otherwise collapse every default video onto one blob and collide on
        // the one-manifest-per-blob invariant); pass `bytes` explicitly to test
        // shared-blob behaviour.
        var content = bytes ?? ImageFixtures.MinimalMp4()
            .Concat(Guid.NewGuid().ToByteArray()).ToArray();
        var f = await files.CreateAsync(
            ownerId, null, name ?? $"v-{Guid.NewGuid():N}.mp4", "video/mp4",
            new MemoryStream(content));
        return (f.Id, f.BlobObjectId);
    }

    public static async Task SeedPhotoEmbeddingAsync(
        SqliteWebApplicationFactory factory, AiProfile profile, Guid blobId, float[] vector)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var serializer = scope.ServiceProvider.GetRequiredService<IAiVectorSerializer>();
        db.BlobEmbeddings.Add(new BlobEmbedding
        {
            Id = Guid.NewGuid(), BlobObjectId = blobId, ProfileId = profile.Id,
            EmbeddingBytes = serializer.Serialize(vector, Dim), Dimension = Dim,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    // One temporal sample to seed: parent segment interval, manifest timestamp
    // and its embedding similarity (null = NO completed embedding; a failed row
    // is written instead so retry semantics stay realistic).
    public sealed record SeedSample(
        long SegmentStart, long SegmentEnd, long Timestamp, double? Similarity);

    // Seeds the COMPLETED manifest tree for a blob (version 1) and one
    // completed/failed embedding per sample as dictated by Similarity.
    public static async Task SeedVideoManifestAsync(
        SqliteWebApplicationFactory factory,
        AiProfile profile,
        Guid blobId,
        float[] queryVector,
        IReadOnlyList<SeedSample> samples)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var serializer = scope.ServiceProvider.GetRequiredService<IAiVectorSerializer>();

        var duration = samples.Max(s => s.SegmentEnd);
        var index = new VideoSemanticIndex
        {
            Id = Guid.NewGuid(), BlobObjectId = blobId, SegmentationVersion = 1,
            Status = AiArtifactStatuses.Completed, AttemptCount = 1,
            DurationMilliseconds = duration, SegmentCount = 0, SampleCount = samples.Count,
            CreatedAt = DateTime.UtcNow, CompletedAt = DateTime.UtcNow,
        };
        db.VideoSemanticIndexes.Add(index);

        var segmentsByInterval = samples
            .GroupBy(s => (s.SegmentStart, s.SegmentEnd))
            .OrderBy(g => g.Key.SegmentStart)
            .ToList();
        index.SegmentCount = segmentsByInterval.Count;

        var segmentIndex = 0;
        foreach (var group in segmentsByInterval)
        {
            var segment = new VideoSemanticSegment
            {
                Id = Guid.NewGuid(), VideoSemanticIndexId = index.Id, SegmentIndex = segmentIndex++,
                StartMilliseconds = group.Key.SegmentStart, EndMilliseconds = group.Key.SegmentEnd,
                BoundaryReason = VideoSemanticBoundaryReasons.Scene, CreatedAt = DateTime.UtcNow,
            };
            db.VideoSemanticSegments.Add(segment);

            var sampleIndex = 0;
            foreach (var seed in group.OrderBy(s => s.Timestamp))
            {
                var sample = new VideoSemanticSample
                {
                    Id = Guid.NewGuid(), VideoSemanticSegmentId = segment.Id,
                    SampleIndex = sampleIndex++, TimestampMilliseconds = seed.Timestamp,
                    SelectionReason = VideoSemanticSelectionReasons.Interior,
                    CreatedAt = DateTime.UtcNow,
                };
                db.VideoSemanticSamples.Add(sample);

                db.VideoSemanticSampleEmbeddings.Add(seed.Similarity is double sim
                    ? new VideoSemanticSampleEmbedding
                    {
                        Id = Guid.NewGuid(), VideoSemanticSampleId = sample.Id, ProfileId = profile.Id,
                        EmbeddingBytes = serializer.Serialize(WithSimilarity(queryVector, sim), Dim),
                        Dimension = Dim, Status = AiArtifactStatuses.Completed, AttemptCount = 1,
                        CreatedAt = DateTime.UtcNow, CompletedAt = DateTime.UtcNow,
                    }
                    : new VideoSemanticSampleEmbedding
                    {
                        Id = Guid.NewGuid(), VideoSemanticSampleId = sample.Id, ProfileId = profile.Id,
                        EmbeddingBytes = Array.Empty<byte>(), Dimension = 0,
                        Status = AiArtifactStatuses.Failed,
                        ErrorCode = VideoSemanticErrorCodes.FrameExtraction, AttemptCount = 1,
                        CreatedAt = DateTime.UtcNow,
                    });
            }
        }

        await db.SaveChangesAsync();
    }

    // ---- search ------------------------------------------------------------

    public static async Task<SemanticMediaPage> SearchAsync(
        SqliteWebApplicationFactory factory,
        Guid ownerUserId,
        string? query = null,
        MediaKindScope kind = MediaKindScope.All,
        int limit = 50,
        string? cursor = null,
        ImageFilters? filters = null)
    {
        using var scope = factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<MediaSemanticSearchService>();
        return await svc.SearchAsync(
            ownerUserId, query ?? Query, kind, limit, cursor, filters ?? new ImageFilters());
    }

    // ---- library-state helpers ---------------------------------------------

    public static async Task SetFavoriteAsync(
        SqliteWebApplicationFactory factory, Guid fileId, bool favorite)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.FileItemUserMetadata.Add(new FileItemUserMetadata
        {
            Id = Guid.NewGuid(), FileItemId = fileId, IsFavorite = favorite, CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    public static async Task SoftDeleteAsync(SqliteWebApplicationFactory factory, Guid fileId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var f = await db.FileItems.FirstAsync(x => x.Id == fileId);
        f.DeletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    public static async Task ExcludeFromLibraryAsync(SqliteWebApplicationFactory factory, Guid fileId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var f = await db.FileItems.FirstAsync(x => x.Id == fileId);
        f.MediaLibraryState = MediaLibraryState.Excluded;
        await db.SaveChangesAsync();
    }

    public static async Task MoveToVaultAsync(
        SqliteWebApplicationFactory factory, Guid ownerUserId, Guid fileId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var vault = await db.PrivateVaults.FirstOrDefaultAsync(v => v.OwnerUserId == ownerUserId);
        if (vault is null)
        {
            vault = new PrivateVault
            {
                Id = Guid.NewGuid(), OwnerUserId = ownerUserId, DisplayName = "Private",
                PasswordHash = "x", EncryptionMode = PrivateVaultEncryptionModes.None,
                CreatedAt = DateTime.UtcNow,
            };
            db.PrivateVaults.Add(vault);
            await db.SaveChangesAsync();
        }

        var file = await db.FileItems.IgnoreQueryFilters().FirstAsync(f => f.Id == fileId);
        file.PrivateVaultId = vault.Id;
        await db.SaveChangesAsync();
    }
}
