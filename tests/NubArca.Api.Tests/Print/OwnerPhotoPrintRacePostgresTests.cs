using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Print;
using NubArca.Api.Print;
using NubArca.Api.Storage;
using NubArca.Api.Tests.Integration;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace NubArca.Api.Tests.Print;

/// <summary>
/// An owner's direct print under real concurrency on real PostgreSQL: a key
/// sent many times at once is one sheet, and a loan's last sheet is taken once
/// — whatever the requests do, nothing is spent without a job to show for it.
/// </summary>
[Collection(PostgresIntegrationCollection.Name)]
[Trait("Category", "External")]
public sealed class OwnerPhotoPrintRacePostgresTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private DbContextOptions<AppDbContext>? _dbOptions;

    private readonly Guid _ownerId = Guid.NewGuid();
    private readonly Guid _marioId = Guid.NewGuid();
    private readonly Guid _stationId = Guid.NewGuid();
    private readonly Guid _deviceId = Guid.NewGuid();
    private readonly Guid _ownerPhoto = Guid.NewGuid();
    private readonly Guid _marioPhoto = Guid.NewGuid();
    private readonly Guid _shareId = Guid.NewGuid();

    public OwnerPhotoPrintRacePostgresTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        if (!_fixture.Available) return;
        _dbOptions = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(_fixture.ConnectionString!).Options;
        await _fixture.ResetDatabaseAsync();

        var now = DateTime.UtcNow;
        await using var db = NewContext();
        foreach (var (id, name) in new[] { (_ownerId, "stefano"), (_marioId, "mario") })
            db.Users.Add(new User { Id = id, Email = $"{name}-{id:N}@example.com", DisplayName = name, CreatedAt = now });
        db.PrintStations.Add(new PrintStation
        {
            Id = _stationId, OwnerUserId = _ownerId, Name = "Sala", Enabled = true, CreatedAt = now, LastSeenAt = now,
        });
        db.PrinterDevices.Add(new PrinterDevice
        {
            Id = _deviceId, PrintStationId = _stationId, DeviceKey = "dnp", DisplayName = "DNP", AdapterKind = "cups",
            CapabilitiesJson = "{\"formats\":[\"10x15\"]}", LastObservedState = PrintDeviceStates.Ready, LastSeenAt = now,
        });
        db.PrinterShares.Add(new PrinterShare
        {
            Id = _shareId, OwnerUserId = _ownerId, GranteeUserId = _marioId, PrinterDeviceId = _deviceId,
            MaxSheets = 1, CreatedAt = now,
        });
        foreach (var (file, owner) in new[] { (_ownerPhoto, _ownerId), (_marioPhoto, _marioId) })
        {
            var blob = Guid.NewGuid();
            db.BlobObjects.Add(new BlobObject
            {
                Id = blob, Sha256 = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"), SizeBytes = 1,
                StorageKey = $"objects/aa/bb/{blob:N}", ReferenceCount = 1, CreatedAt = now,
            });
            db.BlobMetadata.Add(new BlobMetadata
            {
                Id = Guid.NewGuid(), BlobObjectId = blob, MediaCategory = MediaCategories.Image, DetectedContentType = "image/jpeg",
                Width = 600, Height = 400,
            });
            db.FileItems.Add(new FileItem
            {
                Id = file, OwnerUserId = owner, BlobObjectId = blob, Name = "p.jpg", MimeType = "image/jpeg",
                SizeBytes = 1, Width = 600, Height = 400, CreatedAt = now, EffectiveDateTaken = now,
            });
        }
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private OwnerPhotoPrintSubmitRequest For(Guid photo) => new(
        photo, _stationId, _deviceId, "10x15", "landscape", new PartyPrintPlacementRequest(0.5, 0.5, 1));

    private async Task<OwnerPhotoPrintResult[]> RaceAsync(Guid user, Guid photo, Func<int, string> key, int racers)
    {
        var contexts = Enumerable.Range(0, racers).Select(_ => NewContext()).ToList();
        try
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var tasks = contexts.Select((db, i) => Task.Run(async () =>
            {
                await gate.Task;
                return await Service(db).SubmitAsync(user, For(photo), key(i), default);
            })).ToList();
            gate.SetResult();
            return await Task.WhenAll(tasks);
        }
        finally
        {
            foreach (var db in contexts) await db.DisposeAsync();
        }
    }

    private async Task<(int Jobs, int Requests, int UsedSheets)> CountsAsync()
    {
        await using var db = NewContext();
        return (await db.PrintJobs.CountAsync(), await db.OwnerPhotoPrintRequests.CountAsync(),
            await db.PrinterShares.Where(s => s.Id == _shareId).Select(s => s.UsedSheets).SingleAsync());
    }

    [SkippableFact]
    public async Task One_Key_Sent_Six_Times_At_Once_Is_One_Sheet()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");
        var results = await RaceAsync(_ownerId, _ownerPhoto, _ => "same-key", 6);

        Assert.All(results, r => Assert.NotNull(r.Accepted));
        Assert.Single(results.Select(r => r.Accepted!.JobId).Distinct());
        var (jobs, requests, _) = await CountsAsync();
        Assert.Equal((1, 1), (jobs, requests));
    }

    [SkippableFact]
    public async Task The_Last_Sheet_Of_A_Loan_Is_Taken_Once()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");
        var results = await RaceAsync(_marioId, _marioPhoto, i => $"key-{i}", 4);

        Assert.Single(results, r => r.Accepted is not null);
        Assert.All(results.Where(r => r.Accepted is null), r => Assert.Equal(OwnerPhotoPrintErrors.ShareExhausted, r.Error));
        Assert.Equal((1, 1, 1), await CountsAsync());
    }

    [SkippableFact]
    public async Task One_Key_Racing_For_The_Last_Sheet_Spends_It_Once()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");
        var results = await RaceAsync(_marioId, _marioPhoto, _ => "twin", 4);

        var accepted = results.Where(r => r.Accepted is not null).Select(r => r.Accepted!.JobId).Distinct().ToList();
        Assert.Single(accepted);
        // A twin that lost before the winner committed is told the loan is
        // spent; a retry with the same key is then answered with the job.
        Assert.All(results.Where(r => r.Accepted is null), r => Assert.Equal(OwnerPhotoPrintErrors.ShareExhausted, r.Error));
        Assert.Equal((1, 1, 1), await CountsAsync());
        await using var db = NewContext();
        var retry = await Service(db).SubmitAsync(_marioId, For(_marioPhoto), "twin", default);
        Assert.Equal(accepted[0], retry.Accepted!.JobId);
    }

    // --- helpers -----------------------------------------------------------

    private OwnerPhotoPrintService Service(AppDbContext db) => new(
        db, new PrinterAccess(db), new Sources(), new PrintArtifactRenderer(), new MemoryArtifacts(),
        TimeProvider.System, Options.Create(new PrintOptions()), NullLogger<OwnerPhotoPrintService>.Instance);

    private AppDbContext NewContext() => new(_dbOptions!);

    private sealed class Sources : IPrintPhotoSourceReader
    {
        public Task<PrintPhotoSources?> OpenAsync(Guid owner, IReadOnlyList<Guid> fileItemIds, CancellationToken c)
        {
            using var image = new Image<Rgba32>(600, 400, new Rgba32(0xC9, 0x20, 0x20));
            using var ms = new MemoryStream();
            image.SaveAsJpeg(ms);
            var jpeg = ms.ToArray();
            return Task.FromResult<PrintPhotoSources?>(new PrintPhotoSources(fileItemIds.Select(_ => jpeg).ToList()));
        }
    }

    /// <summary>Keeps nothing; the store has its own tests. A distinct key per write.</summary>
    private sealed class MemoryArtifacts : IDerivedBlobStorage
    {
        public async Task<BlobWriteResult> WriteAsync(Stream content, CancellationToken c = default) =>
            await PublishAsync(await StageAsync(content, c), c);

        public async Task<StagedBlobWrite> StageAsync(Stream content, CancellationToken c = default)
        {
            using var ms = new MemoryStream();
            await content.CopyToAsync(ms, c);
            var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(ms.ToArray()));
            return new StagedBlobWrite(sha, $"objects/{sha[..2]}/{sha[2..4]}/{sha}", ms.Length,
                stagedPath: string.Empty, discard: static _ => ValueTask.CompletedTask);
        }

        public Task<BlobWriteResult> PublishAsync(StagedBlobWrite staged, CancellationToken c = default)
        {
            staged.MarkConsumed();
            return Task.FromResult(new BlobWriteResult(staged.Sha256, staged.StorageKey, staged.SizeBytes, false));
        }

        public Task<Stream> OpenReadAsync(string key, CancellationToken c = default) => Task.FromResult<Stream>(new MemoryStream());
        public Task<bool> ExistsAsync(string key, CancellationToken c = default) => Task.FromResult(true);
        public Task DeleteAsync(string key, CancellationToken c = default) => Task.CompletedTask;
        public Task<DateTimeOffset?> GetLastWriteTimeUtcAsync(string key, CancellationToken c = default) =>
            Task.FromResult<DateTimeOffset?>(null);

        public async IAsyncEnumerable<string> EnumerateStorageKeysAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken c = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
