using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Print;
using NubArca.Api.Party;
using NubArca.Api.Print;
using NubArca.Api.Storage;
using NubArca.Api.Tests.Endpoints;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace NubArca.Api.Tests.Print;

/// <summary>
/// Submission is where budget, idempotency and source validation meet, and
/// where a mistake becomes a sheet of paper nobody asked for.
/// </summary>
public sealed class PartyPrintSubmissionTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory = new();
    public PartyPrintSubmissionTests() => _factory.EnsureDatabaseCreated();
    public void Dispose() => _factory.Dispose();

    private int _seeded;
    private readonly List<Guid> _photos = [];
    private readonly List<Guid> _videos = [];

    /// <summary>A party whose guest gallery holds four photographs and one video.</summary>
    private async Task<(PartyPrintAccess Access, Guid AlbumId)> SeedAsync(
        int photoMax = 5, int stripMax = 5, bool photoEnabled = true, bool stripEnabled = true)
    {
        var ownerId = await _factory.SeedUserAsync($"o{Interlocked.Increment(ref _seeded)}@example.com");
        var albumId = Guid.NewGuid();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Albums.Add(new Album
        {
            Id = albumId, OwnerUserId = ownerId, Name = "Giulia & Matteo",
            CreatedAt = DateTime.UtcNow,
        });
        db.PartyPrintProfiles.Add(new PartyPrintProfile
        {
            Id = Guid.NewGuid(), PartyAlbumId = albumId, OwnerUserId = ownerId,
            Enabled = true,
            PhotoEnabled = photoEnabled, PhotoMaxPrints = photoMax,
            StripEnabled = stripEnabled, StripMaxPrints = stripMax,
            PublicSequenceNext = 1,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        // A job's station, printer and every source are real foreign keys, so the
        // seed honours them — a test that faked them would be asserting against a
        // database the application could never produce.
        var stationId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        db.PrintStations.Add(new PrintStation
        {
            Id = stationId, OwnerUserId = ownerId, Name = "Postazione",
            Enabled = true, CreatedAt = DateTime.UtcNow,
        });
        db.PrinterDevices.Add(new PrinterDevice
        {
            Id = deviceId, PrintStationId = stationId, DeviceKey = "dev-1",
            DisplayName = "DNP DS620", AdapterKind = "fake",
            CapabilitiesJson = "{\"formats\":[\"10x15\"]}",
            LastObservedState = PrintDeviceStates.Ready, LastSeenAt = DateTime.UtcNow,
        });

        _photos.Clear();
        _videos.Clear();
        for (var i = 0; i < 6; i++)
        {
            var blobId = Guid.NewGuid();
            var fileId = Guid.NewGuid();
            db.BlobObjects.Add(new BlobObject
            {
                Id = blobId, Sha256 = $"sha-{blobId:N}", SizeBytes = 1,
                StorageKey = $"sk/{blobId:N}", ReferenceCount = 1, CreatedAt = DateTime.UtcNow,
            });
            db.FileItems.Add(new FileItem
            {
                Id = fileId, OwnerUserId = ownerId, BlobObjectId = blobId,
                Name = $"p{i}.jpg", MimeType = "image/jpeg", SizeBytes = 1,
                CreatedAt = DateTime.UtcNow, EffectiveDateTaken = DateTime.UtcNow,
            });
            if (i < 5) _photos.Add(fileId); else _videos.Add(fileId);
        }
        await db.SaveChangesAsync();

        return (new PartyPrintAccess(
            albumId, ownerId, stationId, deviceId,
            "Giulia & Matteo", "Una notte da ricordare",
            new PartyPrintProductState(photoEnabled, photoMax),
            new PartyPrintProductState(stripEnabled, stripMax)), albumId);
    }

    private IPartyPrintSubmissionService Service(IServiceScope scope) =>
        new PartyPrintSubmissionService(
            scope.ServiceProvider.GetRequiredService<AppDbContext>(),
            new PartyPrintBudget(scope.ServiceProvider.GetRequiredService<AppDbContext>()),
            new FakeMedia(_photos, _videos),
            new FakeArtifacts(),
            new PartyPrintComposer(),
            new FakeSources());

    private static PartyPrintSubmitRequest Request(string product, params Guid[] ids) =>
        new(product, "pure",
            ids.Select(id => new PartyPrintSlotRequest(id, 0, 0, 1, 1)).ToList());

    private async Task<PartyPrintSubmitResult> SubmitAsync(
        PartyPrintAccess access, PartyPrintSubmitRequest request, string key)
    {
        using var scope = _factory.Services.CreateScope();
        return await Service(scope).SubmitAsync(access, request, key, default);
    }

    private async Task<PartyPrintProfile> ProfileAsync(Guid albumId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.PartyPrintProfiles.AsNoTracking()
            .SingleAsync(p => p.PartyAlbumId == albumId);
    }

    [Fact]
    public async Task A_Photo_Becomes_A_Ready_Job_That_Cost_One_Photo()
    {
        var (access, albumId) = await SeedAsync();
        var result = await SubmitAsync(access, Request(PartyPrintProducts.Photo, _photos[0]), "k1");

        Assert.True(result.Ok);
        Assert.Equal(1, result.Accepted!.PublicSequence);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var job = await db.PrintJobs.AsNoTracking().SingleAsync(j => j.Id == result.Accepted.JobId);
        // Ready is what the agent claims: the artifact exists before anyone is
        // told the print is coming.
        Assert.Equal(PrintJobStates.Ready, job.State);
        Assert.Equal(PrintJobKinds.PartyPhoto, job.Kind);
        // The strip is a composition, never a second paper size.
        Assert.Equal(PrintFormats.Photo10x15, job.Format);
        Assert.NotNull(job.ArtifactStorageKey);

        var profile = await ProfileAsync(albumId);
        Assert.Equal(1, profile.PhotoAcceptedCount);
        Assert.Equal(0, profile.StripAcceptedCount);
    }

    [Fact]
    public async Task A_Strip_Records_Its_Four_Sources_In_Order_And_Costs_One_Strip()
    {
        var (access, albumId) = await SeedAsync();
        var result = await SubmitAsync(access,
            Request(PartyPrintProducts.Strip4, _photos[0], _photos[1], _photos[2], _photos[3]), "k1");
        Assert.True(result.Ok);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var sources = await db.PrintJobSources.AsNoTracking()
            .Where(s => s.PrintJobId == result.Accepted!.JobId)
            .OrderBy(s => s.SlotIndex)
            .ToListAsync();

        // Four real rows with real foreign keys, in the order the guest chose —
        // not three ids buried in a JSON blob.
        Assert.Equal(4, sources.Count);
        Assert.Equal(
            new[] { _photos[0], _photos[1], _photos[2], _photos[3] },
            sources.Select(s => s.FileItemId));
        Assert.Equal([0, 1, 2, 3], sources.Select(s => s.SlotIndex));

        // Four photographs, one strip.
        var profile = await ProfileAsync(albumId);
        Assert.Equal(1, profile.StripAcceptedCount);
        Assert.Equal(0, profile.PhotoAcceptedCount);
    }

    [Fact]
    public async Task The_Same_Idempotency_Key_Prints_Once()
    {
        var (access, albumId) = await SeedAsync();
        var request = Request(PartyPrintProducts.Photo, _photos[0]);

        var first = await SubmitAsync(access, request, "same-key");
        var second = await SubmitAsync(access, request, "same-key");
        var third = await SubmitAsync(access, request, "same-key");

        Assert.True(first.Ok && second.Ok && third.Ok);
        // One job, one artifact, one unit of budget — whatever the network did.
        Assert.Equal(first.Accepted!.JobId, second.Accepted!.JobId);
        Assert.Equal(first.Accepted.JobId, third.Accepted!.JobId);
        Assert.Equal(1, (await ProfileAsync(albumId)).PhotoAcceptedCount);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await db.PrintJobs.CountAsync());
    }

    [Fact]
    public async Task A_Different_Key_Is_A_Different_Print()
    {
        var (access, albumId) = await SeedAsync();
        var a = await SubmitAsync(access, Request(PartyPrintProducts.Photo, _photos[0]), "k1");
        var b = await SubmitAsync(access, Request(PartyPrintProducts.Photo, _photos[0]), "k2");

        Assert.NotEqual(a.Accepted!.JobId, b.Accepted!.JobId);
        // A second keepsake of the same photograph is a second print.
        Assert.Equal(2, (await ProfileAsync(albumId)).PhotoAcceptedCount);
        Assert.NotEqual(a.Accepted.PublicSequence, b.Accepted.PublicSequence);
    }

    [Fact]
    public async Task A_Strip_Needs_Four_DIFFERENT_Photographs()
    {
        var (access, albumId) = await SeedAsync();

        // Too few, too many, and the same picture four times are all refused.
        Assert.Equal(PartyPrintRefusal.Invalid,
            (await SubmitAsync(access, Request(PartyPrintProducts.Strip4, _photos[0]), "a")).Refusal);
        Assert.Equal(PartyPrintRefusal.Invalid,
            (await SubmitAsync(access, Request(PartyPrintProducts.Strip4,
                _photos[0], _photos[1], _photos[2], _photos[3], _photos[4]), "b")).Refusal);
        Assert.Equal(PartyPrintRefusal.Invalid,
            (await SubmitAsync(access, Request(PartyPrintProducts.Strip4,
                _photos[0], _photos[0], _photos[1], _photos[2]), "c")).Refusal);

        // None of them cost anything.
        Assert.Equal(0, (await ProfileAsync(albumId)).StripAcceptedCount);
    }

    [Fact]
    public async Task Videos_And_Photographs_From_Elsewhere_Are_Refused()
    {
        var (access, albumId) = await SeedAsync();

        // A video is not printable, and its poster is not a photograph.
        Assert.Equal(PartyPrintRefusal.InvalidSource,
            (await SubmitAsync(access, Request(PartyPrintProducts.Photo, _videos[0]), "v")).Refusal);
        // Nor is a photograph that is not in THIS party's guest gallery — the
        // browser's list is a suggestion, not an authority.
        Assert.Equal(PartyPrintRefusal.InvalidSource,
            (await SubmitAsync(access, Request(PartyPrintProducts.Photo, Guid.NewGuid()), "x")).Refusal);

        Assert.Equal(0, (await ProfileAsync(albumId)).PhotoAcceptedCount);
    }

    [Fact]
    public async Task A_Crop_That_Is_Not_A_Crop_Is_Refused_Before_Anything_Is_Spent()
    {
        var (access, albumId) = await SeedAsync();
        var bad = new PartyPrintSubmitRequest(PartyPrintProducts.Photo, "pure",
            [new PartyPrintSlotRequest(_photos[0], 0.9, 0, 0.5, 0.5)]);

        Assert.Equal(PartyPrintRefusal.Invalid, (await SubmitAsync(access, bad, "k")).Refusal);
        Assert.Equal(0, (await ProfileAsync(albumId)).PhotoAcceptedCount);
    }

    [Fact]
    public async Task Running_Out_Of_One_Product_Leaves_The_Other_Printing()
    {
        // The rule the whole feature is built around.
        var (access, albumId) = await SeedAsync(photoMax: 1, stripMax: 1);

        Assert.True((await SubmitAsync(access,
            Request(PartyPrintProducts.Photo, _photos[0]), "p1")).Ok);
        var second = await SubmitAsync(access, Request(PartyPrintProducts.Photo, _photos[1]), "p2");
        Assert.Equal(PartyPrintRefusal.BudgetExhausted, second.Refusal);

        // Photos are gone; strips are untouched.
        var strip = await SubmitAsync(access,
            Request(PartyPrintProducts.Strip4, _photos[0], _photos[1], _photos[2], _photos[3]), "s1");
        Assert.True(strip.Ok);

        var profile = await ProfileAsync(albumId);
        Assert.Equal(1, profile.PhotoAcceptedCount);
        Assert.Equal(1, profile.StripAcceptedCount);
    }

    [Fact]
    public async Task A_Disabled_Product_Refuses_Without_Spending()
    {
        var (access, albumId) = await SeedAsync(photoEnabled: false);
        var result = await SubmitAsync(access, Request(PartyPrintProducts.Photo, _photos[0]), "k");
        Assert.Equal(PartyPrintRefusal.Unavailable, result.Refusal);
        Assert.Equal(0, (await ProfileAsync(albumId)).PhotoAcceptedCount);
    }

    [Fact]
    public async Task A_Render_Failure_Costs_Nothing()
    {
        var (access, albumId) = await SeedAsync();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = new PartyPrintSubmissionService(
            db, new PartyPrintBudget(db), new FakeMedia(_photos, _videos),
            new FakeArtifacts(), new PartyPrintComposer(),
            // Bytes that are not an image: composing them throws.
            new BrokenSources());

        var result = await service.SubmitAsync(
            access, Request(PartyPrintProducts.Photo, _photos[0]), "k", default);

        Assert.Equal(PartyPrintRefusal.RenderFailed, result.Refusal);
        // Nothing was accepted, so the unit went back.
        Assert.Equal(0, (await ProfileAsync(albumId)).PhotoAcceptedCount);
        Assert.Equal(0, await db.PrintJobs.CountAsync());
    }

    [Fact]
    public async Task The_Idempotency_Key_Is_Stored_Hashed()
    {
        var (access, _) = await SeedAsync();
        await SubmitAsync(access, Request(PartyPrintProducts.Photo, _photos[0]), "secret-key");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await db.PartyPrintRequests.AsNoTracking().SingleAsync();
        // The raw key is never kept, like every other capability secret here.
        Assert.DoesNotContain("secret-key", stored.IdempotencyKeyHash);
        Assert.Equal(64, stored.IdempotencyKeyHash.Length);
    }

    // --- Fakes: the party's gallery, its originals, and the artifact store ---

    private sealed class FakeMedia(List<Guid> photos, List<Guid> videos) : IPartyMediaService
    {
        public Task<PartyAlbumHeader?> GetAlbumAsync(Guid o, Guid a, CancellationToken c) =>
            Task.FromResult<PartyAlbumHeader?>(new PartyAlbumHeader("Festa", 5, null));
        public Task<IReadOnlyList<PartyMediaItem>?> ListItemsAsync(Guid o, Guid a, CancellationToken c) =>
            Task.FromResult<IReadOnlyList<PartyMediaItem>?>(
            [
                .. photos.Select(p => new PartyMediaItem(p, PartyMediaKind.Image)),
                .. videos.Select(v => new PartyMediaItem(v, PartyMediaKind.Video)),
            ]);
        public Task<PartyMediaKind?> GetVisibleMediaKindAsync(Guid o, Guid a, Guid f, CancellationToken c) =>
            Task.FromResult<PartyMediaKind?>(
                photos.Contains(f) ? PartyMediaKind.Image
                : videos.Contains(f) ? PartyMediaKind.Video : null);
    }

    private sealed class FakeSources : IPartyPrintSourceReader
    {
        public Task<byte[]?> ReadAsync(Guid owner, Guid fileItemId, CancellationToken c)
        {
            using var image = new Image<Rgba32>(1200, 900);
            image.Mutate(x => x.Fill(new Rgba32(0xC9, 0x76, 0x2F)));
            using var ms = new MemoryStream();
            image.SaveAsJpeg(ms);
            return Task.FromResult<byte[]?>(ms.ToArray());
        }
    }

    private sealed class BrokenSources : IPartyPrintSourceReader
    {
        public Task<byte[]?> ReadAsync(Guid owner, Guid fileItemId, CancellationToken c) =>
            Task.FromResult<byte[]?>([0x00, 0x01, 0x02, 0x03]);
    }

    /// <summary>Accepts the artifact and remembers nothing: the store is not what
    /// these tests are about, and the real one has its own.</summary>
    private sealed class FakeArtifacts : IDerivedBlobStorage
    {
        public async Task<BlobWriteResult> WriteAsync(
            Stream content, CancellationToken c = default)
        {
            using var ms = new MemoryStream();
            await content.CopyToAsync(ms, c);
            return new BlobWriteResult(
                new string('a', 64), $"party-print/{Guid.NewGuid():N}", ms.Length, false);
        }
        public Task<Stream> OpenReadAsync(string key, CancellationToken c = default) =>
            Task.FromResult<Stream>(new MemoryStream());
        public Task<bool> ExistsAsync(string key, CancellationToken c = default) =>
            Task.FromResult(true);
        public Task DeleteAsync(string key, CancellationToken c = default) => Task.CompletedTask;
        public async IAsyncEnumerable<string> EnumerateStorageKeysAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken c = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
