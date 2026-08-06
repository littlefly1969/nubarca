using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Plates;
using NubArca.Api.Plates.Redaction;
using Xunit;

namespace NubArca.Api.Tests.Plates;

// Service-level coverage for PlateFaceRedactionService box detection/persistence:
// detector-invocation, box reuse under the current profile, confidence filtering,
// the MaxFaces cap, profile-change regeneration, and the unavailable guard. Uses
// a real in-memory SQLite AppDbContext and a spy detector.
public sealed class PlateFaceRedactionServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly Guid _ownerId = Guid.NewGuid();
    private readonly Guid _plateId = Guid.NewGuid();

    public PlateFaceRedactionServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        Seed();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private void Seed()
    {
        var now = DateTime.UtcNow;
        _db.Users.Add(new User { Id = _ownerId, Email = "o@example.com", DisplayName = "O", CreatedAt = now });
        var blobId = Guid.NewGuid();
        _db.BlobObjects.Add(new BlobObject
        {
            Id = blobId,
            Sha256 = "deadbeef",
            SizeBytes = 3,
            StorageKey = "ab/cd/deadbeef",
            ReferenceCount = 1,
            CreatedAt = now,
        });
        _db.PlateImages.Add(new PlateImage
        {
            Id = _plateId,
            OwnerUserId = _ownerId,
            BlobObjectId = blobId,
            OriginalFileName = "p.png",
            ContentType = "image/png",
            SizeBytes = 3,
            Width = 100,
            Height = 100,
            LogicalContainerKey = "__nubarca_plates_x",
            Status = PlateImageStatuses.Uploaded,
            CreatedAt = now,
            UpdatedAt = now,
        });
        _db.SaveChanges();
    }

    private sealed class SpyDetector : IPlateFaceRedactionDetector
    {
        private readonly PlateFaceRedactionCandidate[] _candidates;
        public int Calls { get; private set; }
        public bool IsAvailable { get; set; } = true;
        public string ProfileKey { get; set; } = "v1";

        public SpyDetector(params PlateFaceRedactionCandidate[] candidates) => _candidates = candidates;

        public Task<IReadOnlyList<PlateFaceRedactionCandidate>> DetectAsync(
            PlateRedactionImageInput image, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<PlateFaceRedactionCandidate>>(_candidates);
        }
    }

    private PlateFaceRedactionService Service(
        SpyDetector detector, PlatesFaceRedactionOptions options)
        => new(_db, detector, TimeProvider.System,
            NullLogger<PlateFaceRedactionService>.Instance, Options.Create(options));

    private static PlatesFaceRedactionOptions Opts(string profile = "v1", double minConf = 0.35, int maxFaces = 64)
        => new()
        {
            Enabled = true,
            ProfileKey = profile,
            MinConfidence = minConf,
            MaxFacesPerImage = maxFaces,
        };

    private Task<PlateRedactionImageInput?> SourceAsync(CancellationToken ct)
        => Task.FromResult<PlateRedactionImageInput?>(new PlateRedactionImageInput(new byte[] { 1, 2, 3 }, 100, 100));

    [Fact]
    public async Task Detects_And_Persists_Boxes_When_Missing()
    {
        var detector = new SpyDetector(new PlateFaceRedactionCandidate(0.4, 0.2, 0.12, 0.16, 0.92));
        var svc = Service(detector, Opts());

        var result = await svc.EnsureBoxesAsync(_ownerId, _plateId, SourceAsync);

        Assert.True(result.Regenerated);
        Assert.Single(result.Boxes);
        Assert.Equal(1, detector.Calls);
        Assert.Equal(1, await _db.PlateFaceRedactionBoxes.CountAsync(
            b => b.OwnerUserId == _ownerId && b.PlateImageId == _plateId && b.ModelProfileKey == "v1"));
    }

    [Fact]
    public async Task Reuses_Boxes_When_Profile_Is_Current()
    {
        var detector = new SpyDetector(new PlateFaceRedactionCandidate(0.4, 0.2, 0.12, 0.16, 0.92));
        var svc = Service(detector, Opts());

        var first = await svc.EnsureBoxesAsync(_ownerId, _plateId, SourceAsync);
        var second = await svc.EnsureBoxesAsync(_ownerId, _plateId, SourceAsync);

        // No re-detection; the same persisted row id is returned.
        Assert.Equal(1, detector.Calls);
        Assert.False(second.Regenerated);
        Assert.Equal(first.Boxes[0].Id, second.Boxes[0].Id);
    }

    [Fact]
    public async Task Filters_Candidates_Below_MinConfidence()
    {
        var detector = new SpyDetector(
            new PlateFaceRedactionCandidate(0.4, 0.2, 0.12, 0.16, 0.92),
            new PlateFaceRedactionCandidate(0.1, 0.1, 0.05, 0.05, 0.10));
        var svc = Service(detector, Opts(minConf: 0.5));

        var result = await svc.EnsureBoxesAsync(_ownerId, _plateId, SourceAsync);

        Assert.Single(result.Boxes);
        Assert.Equal(0.92, result.Boxes[0].Confidence, 3);
    }

    [Fact]
    public async Task Enforces_MaxFacesPerImage()
    {
        var many = Enumerable.Range(0, 10)
            .Select(i => new PlateFaceRedactionCandidate(0.01 * i, 0.01 * i, 0.05, 0.05, 0.9))
            .ToArray();
        var detector = new SpyDetector(many);
        var svc = Service(detector, Opts(maxFaces: 3));

        var result = await svc.EnsureBoxesAsync(_ownerId, _plateId, SourceAsync);

        Assert.Equal(3, result.Boxes.Count);
        Assert.Equal(3, await _db.PlateFaceRedactionBoxes.CountAsync());
    }

    [Fact]
    public async Task Regenerates_And_Replaces_Boxes_When_Profile_Changes()
    {
        var d1 = new SpyDetector(new PlateFaceRedactionCandidate(0.4, 0.2, 0.12, 0.16, 0.92));
        var first = await Service(d1, Opts(profile: "v1")).EnsureBoxesAsync(_ownerId, _plateId, SourceAsync);

        var d2 = new SpyDetector(new PlateFaceRedactionCandidate(0.5, 0.3, 0.10, 0.10, 0.88));
        var second = await Service(d2, Opts(profile: "v2")).EnsureBoxesAsync(_ownerId, _plateId, SourceAsync);

        Assert.True(second.Regenerated);
        Assert.Equal(1, d2.Calls);
        // The v1 rows were replaced by v2 rows (no stale accumulation).
        Assert.Equal(0, await _db.PlateFaceRedactionBoxes.CountAsync(b => b.ModelProfileKey == "v1"));
        Assert.Equal(1, await _db.PlateFaceRedactionBoxes.CountAsync(b => b.ModelProfileKey == "v2"));
        Assert.NotEqual(first.Boxes[0].Id, second.Boxes[0].Id);
    }

    [Fact]
    public async Task EnsureBoxes_Throws_When_Unavailable()
    {
        var detector = new SpyDetector { IsAvailable = false };
        var svc = Service(detector, Opts());

        await Assert.ThrowsAsync<PlateFaceRedactionUnavailableException>(
            () => svc.EnsureBoxesAsync(_ownerId, _plateId, SourceAsync));
        // Feature-disabled also reports unavailable via GetInfoAsync.
        var info = await Service(new SpyDetector(), new PlatesFaceRedactionOptions { Enabled = false })
            .GetInfoAsync(_ownerId, _plateId);
        Assert.False(info.Available);
    }

    [Fact]
    public async Task GetInfo_Reports_Availability_And_Persisted_Count()
    {
        var detector = new SpyDetector(new PlateFaceRedactionCandidate(0.4, 0.2, 0.12, 0.16, 0.92));
        var svc = Service(detector, Opts());
        await svc.EnsureBoxesAsync(_ownerId, _plateId, SourceAsync);

        var info = await svc.GetInfoAsync(_ownerId, _plateId);

        Assert.True(info.Available);
        Assert.Equal(1, info.FacesCount);
        Assert.Equal("v1", info.ProfileKey);
    }
}
