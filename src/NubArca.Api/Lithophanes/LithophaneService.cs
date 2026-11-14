using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NubArca.Api.Access;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Jobs;
using NubArca.Api.PhotoExport;
using NubArca.Api.Print;
using NubArca.Api.Storage;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace NubArca.Api.Lithophanes;

// The memory-heavy original decode is bounded independently of worker slots.
// One gate per process; HEIC additionally uses OriginalImageReader's own gate.
public sealed class LithophaneDecodeGate : IDisposable
{
    internal SemaphoreSlim Semaphore { get; } = new(1, 1);
    public void Dispose() => Semaphore.Dispose();
}

public sealed class LithophaneService(AppDbContext db, IJobQueue jobs, IDerivedBlobStorage artifacts,
    IOriginalImageReader originals, IUserPermissionService permissions, TimeProvider clock,
    LithophaneDecodeGate gate, ILogger<LithophaneService> logger)
{
    public const int StripRows = 16;
    public const int MaxProjectsPerOwner = 100;
    private const long MaxSourceBytes = 64 * 1024 * 1024;
    private const long MaxSourcePixels = 64_000_000;

    private static LithophaneSettings Settings(LithophaneProject p) =>
        JsonSerializer.Deserialize<LithophaneSettings>(p.SettingsJson) ?? throw new LithophaneException("invalid_settings");

    private static void Validate(LithophaneSettings? s)
    {
        if (s is null || !s.IsValid()) throw new LithophaneException("invalid_settings");
    }

    private IQueryable<FileItem> Sources(Guid owner) => PhotoExportEligibility.EligiblePhotos(db, owner);

    internal Task<bool> CancellationRequestedAsync(Guid jobId, CancellationToken ct) =>
        db.BackgroundJobs.AsNoTracking().AnyAsync(x => x.Id == jobId && x.CancellationRequested, ct);

    public async Task<LithophaneSummary> CreateAsync(Guid owner, LithophaneCreateRequest request, CancellationToken ct)
    {
        Validate(request.Settings);
        if (request.RequestId == Guid.Empty || request.FileItemId == Guid.Empty) throw new LithophaneException("invalid_request");
        var id = await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await TreeMutationLock.AcquireAsync(db, owner, ct);
            var json = JsonSerializer.Serialize(request.Settings);
            var existing = await db.LithophaneProjects.AsNoTracking()
                .SingleOrDefaultAsync(x => x.OwnerUserId == owner && x.RequestId == request.RequestId, ct);
            if (existing is not null)
            {
                if (existing.SourceFileItemId != request.FileItemId || existing.SettingsJson != json)
                    throw new LithophaneException("request_conflict", 409);
                return existing.Id;
            }
            var source = await Sources(owner).SingleOrDefaultAsync(x => x.Id == request.FileItemId, ct);
            if (source is null) throw new LithophaneException("source_unavailable", 404);
            if (source.SizeBytes > MaxSourceBytes) throw new LithophaneException("image_too_large", 413);
            if (await db.LithophaneProjects.CountAsync(x => x.OwnerUserId == owner, ct) >= MaxProjectsPerOwner)
                throw new LithophaneException("project_limit", 409);
            var project = new LithophaneProject
            {
                Id = Guid.NewGuid(), OwnerUserId = owner, RequestId = request.RequestId,
                FileItemId = source.Id, SourceFileItemId = source.Id, Name = source.Name, SettingsJson = json, CreatedAt = clock.GetUtcNow().UtcDateTime,
            };
            var job = await jobs.EnqueueAsync(JobTypes.LithophaneGenerate, new LithophaneJobPayload(project.Id), cancellationToken: ct);
            project.JobId = job.Id;
            db.LithophaneProjects.Add(project);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return project.Id;
        });
        return (await GetAsync(owner, id, ct))!;
    }

    public async Task<LithophanePage> ListAsync(Guid owner, Guid? cursor, CancellationToken ct)
    {
        var query = db.LithophaneProjects.AsNoTracking().Where(x => x.OwnerUserId == owner);
        if (cursor is { } after)
        {
            var anchor = await query.SingleOrDefaultAsync(x => x.Id == after, ct);
            if (anchor is null) throw new LithophaneException("invalid_cursor");
            query = query.Where(x => x.CreatedAt < anchor.CreatedAt
                || (x.CreatedAt == anchor.CreatedAt && x.Id.CompareTo(after) < 0));
        }
        var projects = await query.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).Take(21).ToListAsync(ct);
        var summaries = await SummariesAsync(projects.Take(20).ToList(), ct);
        return new(summaries, projects.Count > 20 ? projects[19].Id : null);
    }

    public async Task<IReadOnlyList<LithophaneSummary>> GetStatusesAsync(Guid owner, IReadOnlyList<Guid>? ids, CancellationToken ct)
    {
        if (ids is null || ids.Count > MaxProjectsPerOwner) throw new LithophaneException("invalid_request");
        var projects = await db.LithophaneProjects.AsNoTracking()
            .Where(x => x.OwnerUserId == owner && ids.Contains(x.Id)).ToListAsync(ct);
        return await SummariesAsync(projects, ct);
    }

    public async Task<LithophaneSummary?> GetAsync(Guid owner, Guid id, CancellationToken ct)
    {
        var p = await FindAsync(owner, id, ct);
        return p is null ? null : await SummaryAsync(p, ct);
    }

    private Task<LithophaneProject?> FindAsync(Guid owner, Guid id, CancellationToken ct) =>
        db.LithophaneProjects.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.OwnerUserId == owner, ct);

    private async Task<LithophaneSummary> SummaryAsync(LithophaneProject p, CancellationToken ct)
    {
        return (await SummariesAsync([p], ct))[0];
    }

    private async Task<IReadOnlyList<LithophaneSummary>> SummariesAsync(IReadOnlyList<LithophaneProject> projects, CancellationToken ct)
    {
        var jobIds = projects.Select(x => x.JobId).ToList();
        var ids = projects.Select(x => x.Id).ToList();
        var jobById = await db.BackgroundJobs.AsNoTracking().Where(x => jobIds.Contains(x.Id))
            .Select(x => new { x.Id, x.Status, x.CancellationRequested }).ToDictionaryAsync(x => x.Id, ct);
        var rowsById = await db.LithophaneStrips.Where(x => ids.Contains(x.ProjectId)).GroupBy(x => x.ProjectId)
            .Select(g => new { Id = g.Key, Rows = g.Max(x => x.EndRow) }).ToDictionaryAsync(x => x.Id, x => x.Rows, ct);
        return projects.Select(p =>
        {
            jobById.TryGetValue(p.JobId, out var job);
            var status = job?.Status ?? JobStatuses.Cancelled;
            if (job?.CancellationRequested == true && !JobStatuses.IsTerminal(status)) status = "cancelling";
            var ready = status == JobStatuses.Succeeded && p.ArtifactStorageKey is not null;
            if (ready) status = "ready";
            else if (status == JobStatuses.Succeeded) status = JobStatuses.Failed;
            var s = Settings(p); var grid = new LithophaneGrid(s);
            // A ready project has released its strips: every row is done.
            var completed = p.ArtifactStorageKey is not null ? grid.Rows : rowsById.GetValueOrDefault(p.Id);
            return new LithophaneSummary(p.Id, p.Name, p.FileItemId, s, status, completed,
                grid.Rows, grid.TriangleCount, grid.StlBytes, p.CreatedAt, ready);
        }).ToList();
    }

    public async Task<bool> CancelAsync(Guid owner, Guid id, CancellationToken ct)
    {
        var p = await FindAsync(owner, id, ct);
        if (p is null) return false;
        await jobs.RequestCancellationAsync(p.JobId, ct);
        return true;
    }

    public async Task<bool> DeleteAsync(Guid owner, Guid id, CancellationToken ct)
    {
        var keys = new List<string>();
        var removed = await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            // Keep the owner row locked while collecting its keys. A strip's
            // FK insert and a worker's map/final update cannot pass this lock
            // and escape the set that is released below.
            if (db.Database.IsNpgsql())
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    SELECT 1 FROM lithophane_projects WHERE "Id" = {id} AND "OwnerUserId" = {owner} FOR UPDATE
                    """, ct);
            var p = await FindAsync(owner, id, ct);
            if (p is null) return false;
            keys = await db.LithophaneStrips.Where(x => x.ProjectId == id).Select(x => x.StorageKey).ToListAsync(ct);
            if (p.PixelMapStorageKey is { } map) keys.Add(map);
            if (p.ArtifactStorageKey is { } stl) keys.Add(stl);
            await jobs.RequestCancellationAsync(p.JobId, ct);
            await db.LithophaneProjects.Where(x => x.Id == id && x.OwnerUserId == owner).ExecuteDeleteAsync(ct);
            await tx.CommitAsync(ct);
            return true;
        });
        if (!removed) return false;
        foreach (var key in keys.Distinct())
        {
            try { await LithophaneArtifacts.DeleteUnownedAsync(db, artifacts, key, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The ordinary storage reconciler can retry this unowned object.
                logger.LogWarning("Lithophane artifact cleanup deferred ({ExceptionType}).", ex.GetType().Name);
            }
        }
        return true;
    }

    public async Task<Stream?> OpenDownloadAsync(Guid owner, Guid id, CancellationToken ct)
    {
        var p = await FindAsync(owner, id, ct);
        if (p?.ArtifactStorageKey is null || !(await SummaryAsync(p, ct)).DownloadReady) return null;
        try { return await artifacts.OpenReadAsync(p.ArtifactStorageKey, ct); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return null; }
    }

    public async Task<LithophanePreview> PreviewAsync(Guid owner, LithophanePreviewRequest request, CancellationToken ct)
    {
        Validate(request.Settings);
        var (map, photoAspect) = await ReadMapAsync(owner, request.FileItemId, request.Settings, ct);
        using (map)
        {
            // Below this the photo is whole on the sphere already: a smaller zoom
            // changes the number, not the map. The form shows it after a preview.
            var minimumZoom = PhotoPlacementGeometry.ContainZoom(photoAspect,
                request.Settings.PictureWidthDegrees / request.Settings.PictureHeightDegrees);
            return await PreviewOfAsync(map, request.Settings, ct) with { MinimumZoom = minimumZoom };
        }
    }

    public async Task<LithophanePreview?> SavedPreviewAsync(Guid owner, Guid id, CancellationToken ct)
    {
        var p = await FindAsync(owner, id, ct);
        if (p?.PixelMapStorageKey is null) return null;
        Stream stream;
        try { stream = await artifacts.OpenReadAsync(p.PixelMapStorageKey, ct); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return null; }
        await using var streamScope = stream;
        using var map = await Image.LoadAsync<L8>(stream, ct);
        return await PreviewOfAsync(map, Settings(p), ct);
    }

    private static async Task<LithophanePreview> PreviewOfAsync(Image<L8> map, LithophaneSettings s, CancellationToken ct)
    {
        await using var stream = new MemoryStream();
        await map.SaveAsPngAsync(stream, ct);
        var grid = new LithophaneGrid(s);
        return new(Convert.ToBase64String(stream.GetBuffer(), 0, (int)stream.Length), grid.Columns, grid.Rows,
            grid.TopRows, grid.BottomAngle, grid.TriangleCount, grid.StlBytes);
    }

    private async Task<(Image<L8> Map, double PhotoAspect)> ReadMapAsync(Guid owner, Guid fileId, LithophaneSettings s, CancellationToken ct)
    {
        var source = await Sources(owner).SingleOrDefaultAsync(x => x.Id == fileId, ct);
        if (source is null) throw new LithophaneException("source_unavailable", 404);
        if (source.SizeBytes > MaxSourceBytes) throw new LithophaneException("image_too_large", 413);
        await gate.Semaphore.WaitAsync(ct);
        try
        {
            await using var lease = await originals.OpenForPixelsAsync(source.BlobObjectId, ct);
            if (lease is null) throw new LithophaneException("invalid_image");
            var info = await Image.IdentifyAsync(lease.Content, ct);
            if ((long)info.Width * info.Height > MaxSourcePixels) throw new LithophaneException("image_too_large", 413);
            lease.Content.Position = 0;
            using var image = await Image.LoadAsync<Rgba32>(new DecoderOptions
            {
                MaxFrames = 1, TargetSize = new Size(2048, 2048),
            }, lease.Content, ct);
            image.Mutate(x => x.AutoOrient());
            return (LithophaneImage.CreateMap(image, s, ct), (double)image.Width / image.Height);
        }
        catch (Exception ex) when (ex is UnknownImageFormatException or InvalidImageContentException)
        {
            throw new LithophaneException("invalid_image");
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new LithophaneException("source_unavailable", 404);
        }
        finally { gate.Semaphore.Release(); }
    }

    // Strips are only the checkpoints of assembly. Once the STL is committed
    // they would double the project's storage, so their ownership is released
    // and their bytes removed unless shared content still owns them. A failed
    // physical delete is left to ordinary storage reconciliation.
    private async Task ReleaseStripsAsync(Guid projectId, CancellationToken ct)
    {
        var keys = await db.LithophaneStrips.Where(x => x.ProjectId == projectId).Select(x => x.StorageKey).ToListAsync(ct);
        if (keys.Count == 0) return;
        await db.LithophaneStrips.Where(x => x.ProjectId == projectId).ExecuteDeleteAsync(ct);
        foreach (var key in keys.Distinct())
        {
            try { await LithophaneArtifacts.DeleteUnownedAsync(db, artifacts, key, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("Lithophane strip cleanup deferred ({ExceptionType}).", ex.GetType().Name);
            }
        }
    }

    public async Task RunSliceAsync(Guid projectId, JobContext context, CancellationToken ct)
    {
        var p = await db.LithophaneProjects.SingleOrDefaultAsync(x => x.Id == projectId, ct);
        if (p is null) return;
        if (p.EngineVersion != 1) throw new LithophaneException("unsupported_version");
        var effective = await permissions.GetCurrentAsync(p.OwnerUserId, ct);
        if (!effective.Has(Permissions.LaboratoryAccess) || !effective.Has(Permissions.LaboratoryLithophanes))
        {
            await CancelWorkAsync(); return;
        }
        if (p.ArtifactStorageKey is not null)
        {
            // Retry after publication, before the job's success commit or the
            // strip release finished: only the release can still be pending.
            await ReleaseStripsAsync(p.Id, ct);
            return;
        }
        var s = Settings(p); Validate(s);
        var grid = new LithophaneGrid(s);
        var units = 0;
        if (p.PixelMapStorageKey is null)
        {
            Image<L8> map;
            try { (map, _) = await ReadMapAsync(p.OwnerUserId, p.FileItemId ?? Guid.Empty, s, ct); }
            catch (LithophaneException ex) when (ex.Code == "source_unavailable") { await CancelWorkAsync(); return; }
            using (map)
            {
                await using var encoded = new MemoryStream();
                await map.SaveAsPngAsync(encoded, ct); encoded.Position = 0;
                await using var staged = await artifacts.StageAsync(encoded, ct);
                await StoragePublish.PublishOwnedAsync(db, artifacts, staged, async (stored, token) =>
                {
                    // A reclaimed lease can overlap a slow old worker. The
                    // first committed map wins; all strips must use that map,
                    // including when the source FileItem changed meanwhile.
                    await db.LithophaneProjects.Where(x => x.Id == p.Id && x.PixelMapStorageKey == null)
                        .ExecuteUpdateAsync(x => x.SetProperty(v => v.PixelMapStorageKey, stored.StorageKey), token);
                }, ct);
                await db.Entry(p).ReloadAsync(ct);
                if (p.PixelMapStorageKey is null) throw new LithophaneException("source_unavailable", 404);
            }
            units++;
        }

        var strips = await db.LithophaneStrips.AsNoTracking().Where(x => x.ProjectId == p.Id)
            .OrderBy(x => x.FirstRow).ToListAsync(ct);
        var next = 0;
        foreach (var strip in strips)
        {
            if (strip.FirstRow != next) throw new LithophaneException("invalid_checkpoint");
            next = strip.EndRow;
        }
        if (context.ShouldYield(units)) { Yield(next); return; }

        await using (var stream = await artifacts.OpenReadAsync(p.PixelMapStorageKey!, ct))
        using (var map = await Image.LoadAsync<L8>(stream, ct))
        {
            while (next < grid.Rows)
            {
                ct.ThrowIfCancellationRequested();
                if (await CancellationRequestedAsync(context.JobId, ct)) throw new OperationCanceledException(ct);
                if (context.ShouldYield(units)) { Yield(next); return; }
                var end = Math.Min(grid.Rows, next + StripRows);
                await using var records = new MemoryStream();
                LithophaneMesh.WriteStrip(records, s, map, next, end, ct); records.Position = 0;
                await using var staged = await artifacts.StageAsync(records, ct);
                var strip = new LithophaneStrip { ProjectId = p.Id, FirstRow = next, EndRow = end };
                await StoragePublish.PublishOwnedAsync(db, artifacts, staged, async (stored, token) =>
                {
                    strip.StorageKey = stored.StorageKey;
                    // Lease recovery can briefly overlap the previous worker.
                    // Both calculate identical immutable bytes; only one row
                    // may own a given strip, with no failed INSERT transaction.
                    var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
                        INSERT INTO lithophane_strips ("ProjectId", "FirstRow", "EndRow", "StorageKey")
                        VALUES ({strip.ProjectId}, {strip.FirstRow}, {strip.EndRow}, {strip.StorageKey})
                        ON CONFLICT ("ProjectId", "FirstRow") DO NOTHING
                        """, token);
                    if (inserted == 0 && !await db.LithophaneStrips.AsNoTracking().AnyAsync(x =>
                            x.ProjectId == strip.ProjectId && x.FirstRow == strip.FirstRow
                            && x.EndRow == strip.EndRow && x.StorageKey == strip.StorageKey, token))
                        throw new LithophaneException("invalid_checkpoint");
                }, ct);
                strips.Add(strip); next = end; units++;
                await context.ReportProgressAsync(next, grid.Rows, "geometry", ct);
            }
        }
        if (context.ShouldYield(units)) { Yield(next); return; }

        // A bounded, retryable assembly stage: at most ~100 MB streamed through
        // a 64 KB buffer. Its mutable temporary file is never a checkpoint.
        var temp = Path.Combine(Path.GetTempPath(), $"nubarca-lithophane-{Guid.NewGuid():N}");
        await using var assembled = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
            65536, FileOptions.DeleteOnClose | FileOptions.Asynchronous | FileOptions.SequentialScan);
        LithophaneMesh.WriteHeader(assembled, grid);
        foreach (var strip in strips)
        {
            ct.ThrowIfCancellationRequested();
            await using var part = await artifacts.OpenReadAsync(strip.StorageKey, ct);
            await part.CopyToAsync(assembled, 65536, ct);
        }
        if (assembled.Length != grid.StlBytes) throw new LithophaneException("invalid_artifact");
        assembled.Position = 0;
        await using var finalStage = await artifacts.StageAsync(assembled, ct);
        if (await CancellationRequestedAsync(context.JobId, ct)) throw new OperationCanceledException(ct);
        await StoragePublish.PublishOwnedAsync(db, artifacts, finalStage, async (stored, token) =>
        {
            p.ArtifactStorageKey = stored.StorageKey;
            await db.SaveChangesAsync(token);
        }, ct);
        await ReleaseStripsAsync(p.Id, ct);

        void Yield(int row)
        {
            if (context.IsCancellationRequested) throw new OperationCanceledException(ct);
            context.RequestContinuation(
                context.HigherPriorityWaiting ? JobYieldReasons.HigherPriority : JobYieldReasons.SliceBudget,
                JsonSerializer.Serialize(new { Version = 1, NextRow = row }));
        }
        async Task CancelWorkAsync()
        {
            await jobs.RequestCancellationAsync(context.JobId, ct);
            context.MarkCancellationRequested();
            throw new OperationCanceledException(ct);
        }
    }
}
