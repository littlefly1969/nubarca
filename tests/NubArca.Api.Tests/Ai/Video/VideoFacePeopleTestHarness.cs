using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Ai;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Tests.Endpoints;

namespace NubArca.Api.Tests.Ai.Video;

// VFACE-02: shared seeding for the owner-level video-identity tests.
//
// Builds the full canonical chain a decision hangs off — blob → FileItem →
// VideoSemanticIndex → VideoFaceAnalysisStatus → VideoFaceTrack — so each test
// can state only what it actually cares about (who owns what, which references
// are vaulted, which embeddings are close).
internal static class VideoFacePeopleTestHarness
{
    public const string FaceProfileKey = "det-face-embedding-v1";
    public const int Dim = 32;

    public static readonly string[] Forbidden =
    {
        "EmbeddingBytes", "embeddingBytes", "StorageKey", "storageKey", "storage_key",
        "BlobObjectId", "blobObjectId", "Sha256", "sha256", "/storage/objects/",
        "PrivateVaultId", "privateVaultId", "ProfileId", "profileId",
        "AnalysisVersion", "analysisVersion", "at NubArca.",
    };

    public static void AssertNoLeak(string text)
    {
        foreach (var needle in Forbidden)
        {
            Xunit.Assert.DoesNotContain(needle, text, StringComparison.Ordinal);
        }
    }

    public static SqliteWebApplicationFactory Factory()
        => Factory(new Dictionary<string, string?> { ["Ai:Enabled"] = "true" });

    public static SqliteWebApplicationFactory Factory(Dictionary<string, string?> settings)
    {
        var factory = new SqliteWebApplicationFactory(settings, poolHost: true);
        factory.EnsureDatabaseCreated();
        return factory;
    }

    // VFACE-02C: co-presence must be stable across sampling configuration, so its
    // tests need to vary the very setting the answer must NOT depend on.
    public static SqliteWebApplicationFactory FactoryWithFrameInterval(int frameIntervalMilliseconds)
        => Factory(new Dictionary<string, string?>
        {
            ["Ai:Enabled"] = "true",
            ["Ai:VideoFaceAnalysis:FrameIntervalMilliseconds"] =
                frameIntervalMilliseconds.ToString(CultureInfo.InvariantCulture),
        });

    // VFACE-02C: generation disabled, everything else untouched. Persisted tracks
    // and decisions must stay fully readable and decidable.
    public static SqliteWebApplicationFactory FactoryWithAnalysisDisabled()
        => Factory(new Dictionary<string, string?>
        {
            ["Ai:Enabled"] = "true",
            ["Ai:VideoFaceAnalysis:Enabled"] = "false",
        });

    public static SqliteWebApplicationFactory FactoryWithAnalysisEnabled()
        => Factory(new Dictionary<string, string?>
        {
            ["Ai:Enabled"] = "true",
            ["Ai:VideoFaceAnalysis:Enabled"] = "true",
        });

    public static async Task<Guid> SeedProfileAsync(SqliteWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<IAiProfileRegistry>();
        await registry.SeedDeterministicProfilesAsync();
        return (await registry.GetProfileByKeyAsync(FaceProfileKey))!.Id;
    }

    public static float[] OneHot(int index, int dimension = Dim)
    {
        var vector = new float[dimension];
        vector[index] = 1f;
        return vector;
    }

    // A vector that leans mostly on `index` but tilts slightly towards `other`,
    // so tests can produce a similarity that is high but not 1.0.
    public static float[] Tilted(int index, int other, float tilt, int dimension = Dim)
    {
        var vector = new float[dimension];
        vector[index] = 1f;
        vector[other] = tilt;
        return vector;
    }

    public sealed record SeededVideo(Guid BlobId, Guid FileId, Guid IndexId, Guid AnalysisId);

    // A video blob with one owner FileItem, a completed temporal manifest and a
    // completed face analysis — everything a track needs to exist.
    public static async Task<SeededVideo> SeedVideoAsync(
        SqliteWebApplicationFactory factory,
        Guid ownerUserId,
        Guid profileId,
        Guid? vaultId = null,
        int analysisVersion = 1)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var blobId = Guid.NewGuid();
        db.BlobObjects.Add(new BlobObject
        {
            Id = blobId, Sha256 = $"sha-{blobId:N}", SizeBytes = 1,
            StorageKey = $"sk/{blobId:N}", ReferenceCount = 1, CreatedAt = DateTime.UtcNow,
        });

        var fileId = await AddFileAsync(db, ownerUserId, blobId, vaultId);
        var (indexId, analysisId) = AddAnalysis(db, blobId, profileId, analysisVersion);

        await db.SaveChangesAsync();
        return new SeededVideo(blobId, fileId, indexId, analysisId);
    }

    // A second, independent FileItem of (possibly) another owner on the SAME blob
    // — the deduplication case every ownership test needs.
    public static async Task<Guid> AddFileReferenceAsync(
        SqliteWebApplicationFactory factory, Guid ownerUserId, Guid blobId, Guid? vaultId = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var fileId = await AddFileAsync(db, ownerUserId, blobId, vaultId);
        await db.SaveChangesAsync();
        return fileId;
    }

    public static async Task<Guid> AddTrackAsync(
        SqliteWebApplicationFactory factory,
        Guid analysisId,
        float[] embedding,
        long startMilliseconds = 1_000,
        long endMilliseconds = 5_000,
        int trackIndex = 0,
        double quality = 0.5)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var serializer = scope.ServiceProvider.GetRequiredService<IAiVectorSerializer>();

        var trackId = Guid.NewGuid();
        db.VideoFaceTracks.Add(new VideoFaceTrack
        {
            Id = trackId,
            VideoFaceAnalysisStatusId = analysisId,
            TrackIndex = trackIndex,
            StartMilliseconds = startMilliseconds,
            EndMilliseconds = endMilliseconds,
            RepresentativeTimestampMilliseconds = (startMilliseconds + endMilliseconds) / 2,
            DetectionCount = 4,
            EmbeddingBytes = serializer.Serialize(embedding, embedding.Length),
            EmbeddingDimension = embedding.Length,
            QualityScore = quality,
            RepresentativeBoundingBoxX = 0.2,
            RepresentativeBoundingBoxY = 0.2,
            RepresentativeBoundingBoxWidth = 0.3,
            RepresentativeBoundingBoxHeight = 0.3,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return trackId;
    }

    // A confirmed static face on a person — the primary suggestion evidence.
    public static async Task SeedConfirmedFaceAsync(
        SqliteWebApplicationFactory factory,
        Guid ownerUserId,
        Guid profileId,
        Guid personId,
        float[] embedding,
        bool ignored = false)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var serializer = scope.ServiceProvider.GetRequiredService<IAiVectorSerializer>();

        var blobId = Guid.NewGuid();
        db.BlobObjects.Add(new BlobObject
        {
            Id = blobId, Sha256 = $"sha-{blobId:N}", SizeBytes = 1,
            StorageKey = $"sk/{blobId:N}", ReferenceCount = 1, CreatedAt = DateTime.UtcNow,
        });
        db.FileItems.Add(new FileItem
        {
            Id = Guid.NewGuid(), OwnerUserId = ownerUserId, BlobObjectId = blobId,
            Name = $"photo-{blobId:N}.png", MimeType = "image/png", SizeBytes = 1,
            CreatedAt = DateTime.UtcNow, EffectiveDateTaken = DateTime.UtcNow,
        });

        var faceId = Guid.NewGuid();
        db.FaceDetections.Add(new FaceDetection
        {
            Id = faceId, BlobObjectId = blobId, ProfileId = profileId, FaceIndex = 0,
            BoundingBoxX = 0.1, BoundingBoxY = 0.1, BoundingBoxWidth = 0.2, BoundingBoxHeight = 0.2,
            DetectionScore = 0.9, LandmarksJson = "[]", CreatedAt = DateTime.UtcNow,
        });
        db.FaceEmbeddings.Add(new FaceEmbedding
        {
            Id = Guid.NewGuid(), FaceDetectionId = faceId, ProfileId = profileId,
            EmbeddingBytes = serializer.Serialize(embedding, embedding.Length),
            Dimension = embedding.Length,
            EmbeddingStatus = AiArtifactStatuses.Completed, CreatedAt = DateTime.UtcNow,
        });
        db.PersonFaceAssignments.Add(new PersonFaceAssignment
        {
            Id = Guid.NewGuid(), OwnerUserId = ownerUserId, PersonId = personId,
            FaceDetectionId = faceId, Source = PersonFaceAssignmentSources.UserConfirmed,
            CreatedAt = DateTime.UtcNow,
        });
        if (ignored)
        {
            db.IgnoredFaces.Add(new IgnoredFace
            {
                Id = Guid.NewGuid(), OwnerUserId = ownerUserId, FaceDetectionId = faceId,
                CreatedAt = DateTime.UtcNow,
            });
        }

        await db.SaveChangesAsync();
    }

    public static async Task<Guid> CreatePersonAsync(
        SqliteWebApplicationFactory factory, Guid ownerUserId, string name)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var person = new Person
        {
            Id = Guid.NewGuid(), OwnerUserId = ownerUserId, DisplayName = name,
            CreatedAt = DateTime.UtcNow,
        };
        db.People.Add(person);
        await db.SaveChangesAsync();
        return person.Id;
    }

    // A SECOND analysis of an existing manifest — another version, or another
    // face profile. Co-presence must never compare across these.
    public static async Task<Guid> AddAnalysisVersionAsync(
        SqliteWebApplicationFactory factory, Guid indexId, Guid profileId, int version)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var analysis = new VideoFaceAnalysisStatus
        {
            Id = Guid.NewGuid(), VideoSemanticIndexId = indexId, AnalysisVersion = version,
            DetectionProfileId = profileId, EmbeddingProfileId = profileId,
            Status = VideoFaceAnalysisStatuses.Completed,
            PlannedFrameCount = 10, ProcessedFrameCount = 10, TrackCount = 1,
            AttemptCount = 1, CreatedAt = DateTime.UtcNow, CompletedAt = DateTime.UtcNow,
        };
        db.VideoFaceAnalysisStatuses.Add(analysis);
        await db.SaveChangesAsync();
        return analysis.Id;
    }

    public static async Task<Guid> AddFaceProfileAsync(SqliteWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var model = new AiModel
        {
            Id = Guid.NewGuid(), Key = $"m-{Guid.NewGuid():N}", Provider = AiProviders.Deterministic,
            Capability = AiCapabilities.FaceEmbedding, Modality = AiModalities.Face,
            Dimension = Dim, DistanceMetric = "cosine", Enabled = true, CreatedAt = DateTime.UtcNow,
        };
        var profile = new AiProfile
        {
            Id = Guid.NewGuid(), Key = $"p-{Guid.NewGuid():N}", AiModelId = model.Id,
            Capability = AiCapabilities.FaceEmbedding, Modality = AiModalities.Face,
            Dimension = Dim, DistanceMetric = "cosine", Enabled = true, CreatedAt = DateTime.UtcNow,
        };
        db.AiModels.Add(model);
        db.AiProfiles.Add(profile);
        await db.SaveChangesAsync();
        return profile.Id;
    }

    public static async Task MoveToVaultAsync(
        SqliteWebApplicationFactory factory, Guid fileId, Guid vaultId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var file = await db.FileItems.IgnoreQueryFilters().SingleAsync(f => f.Id == fileId);
        file.PrivateVaultId = vaultId;
        await db.SaveChangesAsync();
    }

    public static async Task<Guid> CreateVaultAsync(SqliteWebApplicationFactory factory, Guid ownerUserId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var vault = new PrivateVault
        {
            Id = Guid.NewGuid(), OwnerUserId = ownerUserId, DisplayName = "Private",
            PasswordHash = "x", EncryptionMode = PrivateVaultEncryptionModes.None,
            CreatedAt = DateTime.UtcNow,
        };
        db.PrivateVaults.Add(vault);
        await db.SaveChangesAsync();
        return vault.Id;
    }

    public static async Task DeleteFileAsync(SqliteWebApplicationFactory factory, Guid fileId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var file = await db.FileItems.IgnoreQueryFilters().SingleAsync(f => f.Id == fileId);
        file.DeletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    public static async Task<VideoFaceTrackPersonDecision?> LoadDecisionAsync(
        SqliteWebApplicationFactory factory, Guid ownerUserId, Guid trackId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.VideoFaceTrackPersonDecisions.AsNoTracking()
            .FirstOrDefaultAsync(d => d.OwnerUserId == ownerUserId && d.VideoFaceTrackId == trackId);
    }

    public static async Task<int> DecisionCountAsync(SqliteWebApplicationFactory factory, Guid trackId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.VideoFaceTrackPersonDecisions.CountAsync(d => d.VideoFaceTrackId == trackId);
    }

    // ---- internals ---------------------------------------------------------

    private static Task<Guid> AddFileAsync(
        AppDbContext db, Guid ownerUserId, Guid blobId, Guid? vaultId)
    {
        var fileId = Guid.NewGuid();
        db.FileItems.Add(new FileItem
        {
            Id = fileId, OwnerUserId = ownerUserId, BlobObjectId = blobId,
            Name = $"clip-{fileId:N}.mp4", MimeType = "video/mp4", SizeBytes = 1,
            PrivateVaultId = vaultId, CreatedAt = DateTime.UtcNow,
            EffectiveDateTaken = DateTime.UtcNow,
        });
        return Task.FromResult(fileId);
    }

    private static (Guid IndexId, Guid AnalysisId) AddAnalysis(
        AppDbContext db, Guid blobId, Guid profileId, int analysisVersion)
    {
        var index = new VideoSemanticIndex
        {
            Id = Guid.NewGuid(), BlobObjectId = blobId, SegmentationVersion = 1,
            Status = AiArtifactStatuses.Completed, AttemptCount = 1,
            DurationMilliseconds = 60_000, SegmentCount = 1, SampleCount = 1,
            CreatedAt = DateTime.UtcNow, CompletedAt = DateTime.UtcNow,
        };
        db.VideoSemanticIndexes.Add(index);

        var analysis = new VideoFaceAnalysisStatus
        {
            Id = Guid.NewGuid(), VideoSemanticIndexId = index.Id, AnalysisVersion = analysisVersion,
            DetectionProfileId = profileId, EmbeddingProfileId = profileId,
            Status = VideoFaceAnalysisStatuses.Completed,
            PlannedFrameCount = 10, ProcessedFrameCount = 10, TrackCount = 1,
            AttemptCount = 1, CreatedAt = DateTime.UtcNow, CompletedAt = DateTime.UtcNow,
        };
        db.VideoFaceAnalysisStatuses.Add(analysis);
        return (index.Id, analysis.Id);
    }
}
