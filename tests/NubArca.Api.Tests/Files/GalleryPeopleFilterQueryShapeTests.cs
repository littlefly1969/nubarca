using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Files;
using Xunit;

namespace NubArca.Api.Tests.Files;

// Regression guard for the Gallery People-filter query SHAPE (perf optimization).
// The include/exclude people filters must translate to a fully server-side,
// detection-first nested EXISTS: correlate FaceDetection on the FileItem's blob
// FIRST, then check the owner-scoped PersonFaceAssignment. That shape lets
// Postgres index-seek file_items by blob and hash-join the small
// (detections x owner assignments) set once. The prior assignment-first shape
// forced a nested-loop semi/anti join evaluated once per (file x assigned face)
// — ~60-290x slower on a person present in many photos. If the nesting is ever
// flipped back, the ordering assertions below fail.
//
// These assertions run on the captured EF-generated SQL (no DB rows / no Docker
// needed), so they are deterministic and cheap.
public sealed class GalleryPeopleFilterQueryShapeTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly List<string> _sql = new();
    private readonly AppDbContext _db;
    private readonly FileItemService _service;
    private readonly Guid _owner = Guid.NewGuid();

    public GalleryPeopleFilterQueryShapeTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .LogTo(s => { if (s.Contains("SELECT", StringComparison.Ordinal)) _sql.Add(s); }, LogLevel.Information)
            .Options;
        _db = new AppDbContext(options);
        _db.Database.EnsureCreated();
        _db.Users.Add(new User { Id = _owner, Email = $"o-{_owner:N}@x.t", DisplayName = "O", CreatedAt = DateTime.UtcNow });
        _db.SaveChanges();
        _service = new FileItemService(_db, null!, null!, TimeProvider.System);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private string LastPeopleSql()
    {
        // The gallery list issues a single SELECT over file_items with the
        // people EXISTS; grab the most recent one that carries the filter.
        var sql = _sql.LastOrDefault(s => s.Contains("face_detections", StringComparison.Ordinal));
        Assert.NotNull(sql);
        return sql!;
    }

    private static void AssertDetectionFirstInverted(string sql)
    {
        // Server-side: the filter is in SQL, never client-evaluated.
        Assert.Contains("face_detections", sql, StringComparison.Ordinal);
        Assert.Contains("person_face_assignments", sql, StringComparison.Ordinal);
        // Detection-first: face_detections is the OUTER correlated table (appears
        // before person_face_assignments), correlated on the FileItem blob, and
        // the assignment is correlated to the detection id.
        var detIdx = sql.IndexOf("face_detections", StringComparison.Ordinal);
        var assignIdx = sql.IndexOf("person_face_assignments", StringComparison.Ordinal);
        Assert.True(detIdx < assignIdx,
            $"Expected detection-first EXISTS (face_detections before person_face_assignments).\nSQL: {sql}");
        // Blob correlation (outer) + detection-id correlation (inner) + owner
        // scoping are all present (quoting/alias form is provider-specific, so
        // assert on the bare column names).
        Assert.Contains("BlobObjectId", sql, StringComparison.Ordinal);
        Assert.Contains("FaceDetectionId", sql, StringComparison.Ordinal);
        Assert.Contains("OwnerUserId", sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IncludeAny_Translates_To_DetectionFirst_ServerSide_Exists()
    {
        _sql.Clear();
        await _service.ListImagesPageAsync(_owner, 50, null, new ImageFilters
        {
            IncludePersonIds = new[] { Guid.NewGuid(), Guid.NewGuid() },
            IncludePeopleMode = PeopleFilterMode.Any,
        });
        AssertDetectionFirstInverted(LastPeopleSql());
    }

    [Fact]
    public async Task IncludeAll_Translates_To_DetectionFirst_ServerSide_Exists()
    {
        _sql.Clear();
        await _service.ListImagesPageAsync(_owner, 50, null, new ImageFilters
        {
            IncludePersonIds = new[] { Guid.NewGuid(), Guid.NewGuid() },
            IncludePeopleMode = PeopleFilterMode.All,
        });
        var sql = LastPeopleSql();
        AssertDetectionFirstInverted(sql);
        // include-all = one EXISTS per person → two detection-first subqueries.
        var occurrences = sql.Split("face_detections").Length - 1;
        Assert.True(occurrences >= 2, $"Expected one EXISTS per person. SQL: {sql}");
    }

    [Fact]
    public async Task Exclude_Translates_To_DetectionFirst_ServerSide_NotExists()
    {
        _sql.Clear();
        await _service.ListImagesPageAsync(_owner, 50, null, new ImageFilters
        {
            ExcludePersonIds = new[] { Guid.NewGuid() },
        });
        var sql = LastPeopleSql();
        AssertDetectionFirstInverted(sql);
        Assert.Contains("NOT EXISTS", sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IncludeAny_Returns_Only_Photos_Containing_The_Person_Server_Side()
    {
        // Sanity: the shape is not just syntactically right — it selects the
        // correct FileItems (semantics preserved by the inversion).
        var profileId = SeedProfile();
        var personId = SeedPerson();
        var match = SeedImageWithPerson(profileId, personId);
        var noMatch = SeedImage();

        _sql.Clear();
        var page = await _service.ListImagesPageAsync(_owner, 50, null, new ImageFilters
        {
            IncludePersonIds = new[] { personId },
            IncludePeopleMode = PeopleFilterMode.Any,
        });
        var ids = page.Items.Select(i => i.Id).ToList();
        Assert.Contains(match, ids);
        Assert.DoesNotContain(noMatch, ids);
        AssertDetectionFirstInverted(LastPeopleSql());
    }

    // ---- minimal seeding (image FileItem + optional face/assignment) --------

    private Guid SeedProfile()
    {
        var model = new AiModel
        {
            Id = Guid.NewGuid(), Key = $"m-{Guid.NewGuid():N}", Provider = AiProviders.Onnx,
            Capability = AiCapabilities.FaceEmbedding, Modality = AiModalities.Face, Version = 1,
            Dimension = 512, DistanceMetric = AiDistanceMetrics.Cosine, Enabled = true, CreatedAt = DateTime.UtcNow,
        };
        _db.AiModels.Add(model);
        var profileId = Guid.NewGuid();
        _db.AiProfiles.Add(new AiProfile
        {
            Id = profileId, Key = $"p-{Guid.NewGuid():N}", AiModelId = model.Id,
            Capability = AiCapabilities.FaceEmbedding, Modality = AiModalities.Face,
            Dimension = 512, DistanceMetric = AiDistanceMetrics.Cosine, IsDefault = false,
            Enabled = true, CreatedAt = DateTime.UtcNow,
        });
        _db.SaveChanges();
        return profileId;
    }

    private Guid SeedPerson()
    {
        var id = Guid.NewGuid();
        _db.People.Add(new Person { Id = id, OwnerUserId = _owner, DisplayName = "P", CreatedAt = DateTime.UtcNow });
        _db.SaveChanges();
        return id;
    }

    private Guid SeedImage()
    {
        var blobId = Guid.NewGuid();
        _db.BlobObjects.Add(new BlobObject { Id = blobId, Sha256 = $"sha-{blobId:N}", SizeBytes = 1, StorageKey = $"sk/{blobId:N}", ReferenceCount = 1, CreatedAt = DateTime.UtcNow });
        _db.BlobMetadata.Add(new BlobMetadata { Id = Guid.NewGuid(), BlobObjectId = blobId, SizeBytes = 1, DetectedContentType = "image/jpeg", MediaCategory = MediaCategories.Image, CreatedAt = DateTime.UtcNow });
        var fileId = Guid.NewGuid();
        _db.FileItems.Add(new FileItem { Id = fileId, OwnerUserId = _owner, BlobObjectId = blobId, Name = $"f-{fileId:N}.jpg", MimeType = "image/jpeg", SizeBytes = 1, CreatedAt = DateTime.UtcNow, EffectiveDateTaken = DateTime.UtcNow });
        _db.SaveChanges();
        return fileId;
    }

    private Guid SeedImageWithPerson(Guid profileId, Guid personId)
    {
        var blobId = Guid.NewGuid();
        _db.BlobObjects.Add(new BlobObject { Id = blobId, Sha256 = $"sha-{blobId:N}", SizeBytes = 1, StorageKey = $"sk/{blobId:N}", ReferenceCount = 1, CreatedAt = DateTime.UtcNow });
        _db.BlobMetadata.Add(new BlobMetadata { Id = Guid.NewGuid(), BlobObjectId = blobId, SizeBytes = 1, DetectedContentType = "image/jpeg", MediaCategory = MediaCategories.Image, CreatedAt = DateTime.UtcNow });
        var fileId = Guid.NewGuid();
        _db.FileItems.Add(new FileItem { Id = fileId, OwnerUserId = _owner, BlobObjectId = blobId, Name = $"f-{fileId:N}.jpg", MimeType = "image/jpeg", SizeBytes = 1, CreatedAt = DateTime.UtcNow, EffectiveDateTaken = DateTime.UtcNow });
        var detId = Guid.NewGuid();
        _db.FaceDetections.Add(new FaceDetection { Id = detId, BlobObjectId = blobId, ProfileId = profileId, FaceIndex = 0, BoundingBoxX = 0.1, BoundingBoxY = 0.1, BoundingBoxWidth = 0.2, BoundingBoxHeight = 0.2, DetectionScore = 0.9, LandmarksJson = "[]", CreatedAt = DateTime.UtcNow });
        _db.PersonFaceAssignments.Add(new PersonFaceAssignment { Id = Guid.NewGuid(), OwnerUserId = _owner, PersonId = personId, FaceDetectionId = detId, Source = PersonFaceAssignmentSources.UserConfirmed, CreatedAt = DateTime.UtcNow });
        _db.SaveChanges();
        return fileId;
    }
}
