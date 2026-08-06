using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;

namespace NubArca.Api.Tests.Ai;

// Phase 0A: schema + constraint tests for the AI substrate. Two flavours:
//   * model-metadata assertions (no DB) for indexes / FK delete behaviour /
//     check constraints — mirrors DomainModelTests;
//   * real SQLite in-memory enforcement (EnsureCreated) for uniqueness, sparse
//     status, and the generic diagnostics shape.
public sealed class AiSubstrateModelTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;

    public AiSubstrateModelTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;
        _db = new AppDbContext(options);
        // Proves the AI schema (incl. the partial-unique default index and
        // check constraints) is valid on SQLite as well as PostgreSQL.
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    // ---- model-metadata assertions ---------------------------------------

    [Fact]
    public void Model_Has_All_Ai_Entities()
    {
        Assert.NotNull(_db.Model.FindEntityType(typeof(AiModel)));
        Assert.NotNull(_db.Model.FindEntityType(typeof(AiProfile)));
        Assert.NotNull(_db.Model.FindEntityType(typeof(BlobAiArtifactStatus)));
        Assert.NotNull(_db.Model.FindEntityType(typeof(BlobEmbedding)));
        Assert.NotNull(_db.Model.FindEntityType(typeof(DocumentText)));
        Assert.NotNull(_db.Model.FindEntityType(typeof(DocumentChunk)));
        Assert.NotNull(_db.Model.FindEntityType(typeof(DocumentChunkEmbedding)));
        Assert.NotNull(_db.Model.FindEntityType(typeof(FaceDetection)));
        Assert.NotNull(_db.Model.FindEntityType(typeof(FaceEmbedding)));
        Assert.NotNull(_db.Model.FindEntityType(typeof(PersonGroup)));
        Assert.NotNull(_db.Model.FindEntityType(typeof(FaceAssignment)));
        Assert.NotNull(_db.Model.FindEntityType(typeof(AiAnnotation)));
        Assert.NotNull(_db.Model.FindEntityType(typeof(AiIndexDiagnostic)));
    }

    [Fact]
    public void AiModel_Key_Is_Unique()
    {
        var entity = _db.Model.FindEntityType(typeof(AiModel))!;
        var index = entity.GetIndexes()
            .Single(i => i.Properties.Count == 1 && i.Properties[0].Name == nameof(AiModel.Key));
        Assert.True(index.IsUnique);
    }

    [Fact]
    public void AiProfile_Has_Partial_Unique_Default_Per_Capability()
    {
        var entity = _db.Model.FindEntityType(typeof(AiProfile))!;
        var index = entity.GetIndexes().Single(i =>
            i.IsUnique &&
            i.Properties.Count == 1 &&
            i.Properties[0].Name == nameof(AiProfile.Capability));
        Assert.Equal("\"IsDefault\"", index.GetFilter());
    }

    [Fact]
    public void FaceAssignment_Unique_Index_Includes_FaceEmbeddingProfileId()
    {
        var entity = _db.Model.FindEntityType(typeof(FaceAssignment))!;
        Assert.Contains(entity.GetIndexes(), i =>
            i.IsUnique &&
            i.Properties.Select(p => p.Name).SequenceEqual(new[]
            {
                nameof(FaceAssignment.OwnerUserId),
                nameof(FaceAssignment.FaceDetectionId),
                nameof(FaceAssignment.FaceEmbeddingProfileId),
            }));
    }

    [Theory]
    // Derived data cascades from its source blob/file (keeps the blob janitor
    // working without code changes once rows exist).
    [InlineData(typeof(BlobEmbedding), nameof(BlobEmbedding.BlobObjectId), typeof(BlobObject), DeleteBehavior.Cascade)]
    [InlineData(typeof(BlobAiArtifactStatus), nameof(BlobAiArtifactStatus.BlobObjectId), typeof(BlobObject), DeleteBehavior.Cascade)]
    [InlineData(typeof(DocumentText), nameof(DocumentText.FileItemId), typeof(FileItem), DeleteBehavior.Cascade)]
    // Face Substrate v0: detections are BLOB-level (shared across owners for the
    // same blob) and cascade from their source blob.
    [InlineData(typeof(FaceDetection), nameof(FaceDetection.BlobObjectId), typeof(BlobObject), DeleteBehavior.Cascade)]
    [InlineData(typeof(AiAnnotation), nameof(AiAnnotation.FileItemId), typeof(FileItem), DeleteBehavior.Cascade)]
    // Profiles/owners are never deleted out from under outputs.
    [InlineData(typeof(BlobEmbedding), nameof(BlobEmbedding.ProfileId), typeof(AiProfile), DeleteBehavior.Restrict)]
    [InlineData(typeof(AiProfile), nameof(AiProfile.AiModelId), typeof(AiModel), DeleteBehavior.Restrict)]
    [InlineData(typeof(DocumentText), nameof(DocumentText.OwnerUserId), typeof(User), DeleteBehavior.Restrict)]
    [InlineData(typeof(FaceAssignment), nameof(FaceAssignment.FaceEmbeddingProfileId), typeof(AiProfile), DeleteBehavior.Restrict)]
    public void ForeignKey_Has_Expected_Delete_Behavior(
        Type dependent, string fkProperty, Type principal, DeleteBehavior expected)
    {
        var entity = _db.Model.FindEntityType(dependent)!;
        var fk = entity.GetForeignKeys().SingleOrDefault(f =>
            f.Properties.Count == 1 && f.Properties[0].Name == fkProperty);

        Assert.NotNull(fk);
        Assert.Equal(principal, fk!.PrincipalEntityType.ClrType);
        Assert.Equal(expected, fk.DeleteBehavior);
    }

    [Fact]
    public void AiIndexDiagnostic_Target_Columns_Are_Plain_Nullable_Without_ForeignKeys()
    {
        var entity = _db.Model.FindEntityType(typeof(AiIndexDiagnostic))!;

        // No FK constraints on the heterogeneous correlation columns.
        Assert.Empty(entity.GetForeignKeys());

        foreach (var name in new[]
                 {
                     nameof(AiIndexDiagnostic.BlobObjectId),
                     nameof(AiIndexDiagnostic.DocumentChunkId),
                     nameof(AiIndexDiagnostic.FaceDetectionId),
                     nameof(AiIndexDiagnostic.OwnerUserId),
                     nameof(AiIndexDiagnostic.ProfileId),
                 })
        {
            Assert.True(entity.FindProperty(name)!.IsNullable, $"{name} should be nullable");
        }
    }

    [Theory]
    [InlineData(typeof(AiModel), "ck_ai_models_dimension_positive")]
    [InlineData(typeof(BlobEmbedding), "ck_blob_embeddings_dimension_positive")]
    [InlineData(typeof(BlobAiArtifactStatus), "ck_blob_ai_artifact_statuses_attempt_count_non_negative")]
    [InlineData(typeof(AiIndexDiagnostic), "ck_ai_index_diagnostics_attempt_count_non_negative")]
    public void Check_Constraint_Is_Registered(Type entityType, string name)
    {
        var designModel = _db.GetService<IDesignTimeModel>().Model;
        var entity = designModel.FindEntityType(entityType)!;
        var check = entity.GetCheckConstraints().SingleOrDefault(c => c.Name == name);
        Assert.NotNull(check);
        Assert.False(string.IsNullOrWhiteSpace(check!.Sql));
    }

    // ---- real SQLite enforcement -----------------------------------------

    [Fact]
    public async Task BlobEmbedding_Is_Unique_Per_Blob_And_Profile()
    {
        var (blob, _, profile, _) = await SeedCoreAsync();

        _db.BlobEmbeddings.Add(NewBlobEmbedding(blob.Id, profile.Id));
        await _db.SaveChangesAsync();

        _db.BlobEmbeddings.Add(NewBlobEmbedding(blob.Id, profile.Id));
        await Assert.ThrowsAsync<DbUpdateException>(() => _db.SaveChangesAsync());
    }

    [Fact]
    public async Task FaceAssignment_Uniqueness_Is_Scoped_By_FaceEmbeddingProfileId()
    {
        var (blob, user, profileA, _) = await SeedCoreAsync();
        var profileB = NewProfile("face-emb-v2", AiCapabilities.FaceEmbedding);
        profileB.AiModelId = profileA.AiModelId;
        _db.AiProfiles.Add(profileB);

        var detection = new FaceDetection
        {
            Id = Guid.NewGuid(),
            BlobObjectId = blob.Id,
            ProfileId = profileA.Id,
            FaceIndex = 0,
            BoundingBoxX = 0.1,
            BoundingBoxY = 0.1,
            BoundingBoxWidth = 0.2,
            BoundingBoxHeight = 0.2,
            CreatedAt = DateTime.UtcNow,
        };
        var groupA = NewPersonGroup(user.Id, profileA.Id);
        var groupB = NewPersonGroup(user.Id, profileB.Id);
        _db.FaceDetections.Add(detection);
        _db.PersonGroups.AddRange(groupA, groupB);
        await _db.SaveChangesAsync();

        // Same (owner, face) in two different model spaces is allowed.
        _db.FaceAssignments.Add(NewAssignment(user.Id, detection.Id, groupA.Id, profileA.Id));
        _db.FaceAssignments.Add(NewAssignment(user.Id, detection.Id, groupB.Id, profileB.Id));
        await _db.SaveChangesAsync();

        // A second assignment in the SAME model space violates uniqueness.
        _db.FaceAssignments.Add(NewAssignment(user.Id, detection.Id, groupA.Id, profileA.Id));
        await Assert.ThrowsAsync<DbUpdateException>(() => _db.SaveChangesAsync());
    }

    [Fact]
    public async Task AiProfile_Allows_Only_One_Default_Per_Capability()
    {
        var model = NewModel("m1");
        _db.AiModels.Add(model);

        var d1 = NewProfile("img-v1", AiCapabilities.ImageEmbedding); d1.AiModelId = model.Id; d1.IsDefault = true;
        _db.AiProfiles.Add(d1);
        await _db.SaveChangesAsync();

        // A non-default sibling in the same capability is fine.
        var n2 = NewProfile("img-v2", AiCapabilities.ImageEmbedding); n2.AiModelId = model.Id; n2.IsDefault = false;
        _db.AiProfiles.Add(n2);
        await _db.SaveChangesAsync();

        // A default for a DIFFERENT capability is fine.
        var dOther = NewProfile("faces-v1", AiCapabilities.FaceEmbedding); dOther.AiModelId = model.Id; dOther.IsDefault = true;
        _db.AiProfiles.Add(dOther);
        await _db.SaveChangesAsync();

        // A SECOND default in the same capability is rejected.
        var d3 = NewProfile("img-v3", AiCapabilities.ImageEmbedding); d3.AiModelId = model.Id; d3.IsDefault = true;
        _db.AiProfiles.Add(d3);
        await Assert.ThrowsAsync<DbUpdateException>(() => _db.SaveChangesAsync());
    }

    [Fact]
    public async Task Creating_Profile_Does_Not_Materialize_Any_Status_Rows()
    {
        // Implicit-pending semantics: registering a model/profile (with blobs
        // present) must NOT create BlobAiArtifactStatus rows.
        await SeedCoreAsync();

        Assert.Equal(0, await _db.BlobAiArtifactStatuses.CountAsync());
    }

    [Fact]
    public async Task AiIndexDiagnostic_Supports_Provider_And_Owner_Targets_Without_A_Blob()
    {
        var (_, user, _, _) = await SeedCoreAsync();

        // Provider-availability aggregate: no target id at all.
        _db.AiIndexDiagnostics.Add(new AiIndexDiagnostic
        {
            Id = Guid.NewGuid(),
            Capability = AiCapabilities.ImageEmbedding,
            TargetKind = AiDiagnosticTargetKinds.Provider,
            ErrorCode = "provider_unavailable",
            IsPermanent = false,
            AttemptCount = 0,
            OccurredAt = DateTime.UtcNow,
        });

        // Owner-scoped clustering diagnostic: owner set, no blob/chunk/face.
        _db.AiIndexDiagnostics.Add(new AiIndexDiagnostic
        {
            Id = Guid.NewGuid(),
            Capability = AiCapabilities.FaceClustering,
            TargetKind = AiDiagnosticTargetKinds.Clustering,
            OwnerUserId = user.Id,
            ErrorCode = "cluster_failed",
            IsPermanent = false,
            AttemptCount = 1,
            OccurredAt = DateTime.UtcNow,
        });

        await _db.SaveChangesAsync();
        Assert.Equal(2, await _db.AiIndexDiagnostics.CountAsync());
    }

    // ---- seed helpers -----------------------------------------------------

    private async Task<(BlobObject Blob, User User, AiProfile Profile, FileItem File)> SeedCoreAsync()
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = $"owner-{Guid.NewGuid():N}@example.com",
            DisplayName = "Owner",
            CreatedAt = DateTime.UtcNow,
        };
        var blob = new BlobObject
        {
            Id = Guid.NewGuid(),
            Sha256 = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
            StorageKey = "objects/ab/cd/" + Guid.NewGuid().ToString("N"),
            SizeBytes = 123,
            ReferenceCount = 1,
            CreatedAt = DateTime.UtcNow,
        };
        var model = NewModel("seed-model-" + Guid.NewGuid().ToString("N")[..8]);
        var profile = NewProfile("seed-profile-" + Guid.NewGuid().ToString("N")[..8], AiCapabilities.FaceEmbedding);
        profile.AiModelId = model.Id;

        _db.Users.Add(user);
        _db.BlobObjects.Add(blob);
        _db.AiModels.Add(model);
        _db.AiProfiles.Add(profile);
        await _db.SaveChangesAsync();

        var file = new FileItem
        {
            Id = Guid.NewGuid(),
            OwnerUserId = user.Id,
            BlobObjectId = blob.Id,
            Name = "doc.txt",
            MimeType = "text/plain",
            SizeBytes = 123,
            CreatedAt = DateTime.UtcNow,
            EffectiveDateTaken = DateTime.UtcNow,
            EffectiveDateTakenSource = "uploaded",
        };
        _db.FileItems.Add(file);
        await _db.SaveChangesAsync();

        return (blob, user, profile, file);
    }

    private static AiModel NewModel(string key) => new()
    {
        Id = Guid.NewGuid(),
        Key = key,
        Provider = AiProviders.Deterministic,
        Capability = AiCapabilities.ImageEmbedding,
        Modality = AiModalities.Image,
        Version = 1,
        Enabled = true,
        CreatedAt = DateTime.UtcNow,
    };

    private static AiProfile NewProfile(string key, string capability) => new()
    {
        Id = Guid.NewGuid(),
        Key = key,
        Capability = capability,
        Modality = AiModalities.Image,
        Dimension = 8,
        DistanceMetric = AiDistanceMetrics.Cosine,
        Enabled = true,
        CreatedAt = DateTime.UtcNow,
    };

    private static BlobEmbedding NewBlobEmbedding(Guid blobId, Guid profileId) => new()
    {
        Id = Guid.NewGuid(),
        BlobObjectId = blobId,
        ProfileId = profileId,
        EmbeddingBytes = new byte[] { 1, 2, 3, 4 },
        Dimension = 8,
        CreatedAt = DateTime.UtcNow,
    };

    private static PersonGroup NewPersonGroup(Guid ownerId, Guid profileId) => new()
    {
        Id = Guid.NewGuid(),
        OwnerUserId = ownerId,
        ProfileId = profileId,
        CreatedAt = DateTime.UtcNow,
    };

    private static FaceAssignment NewAssignment(Guid ownerId, Guid detectionId, Guid groupId, Guid profileId) => new()
    {
        Id = Guid.NewGuid(),
        OwnerUserId = ownerId,
        FaceDetectionId = detectionId,
        PersonGroupId = groupId,
        FaceEmbeddingProfileId = profileId,
        Source = "auto",
        CreatedAt = DateTime.UtcNow,
    };
}
