using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NubArca.Api.Audit;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Jobs;

namespace NubArca.Api.Files;

// Cloud Function for owner-scoped, exact original-media deduplication.
// BlobObject is NubArca's immutable SHA-256 content identity, so this never
// re-reads or hashes media bytes. Candidate grouping and deletion traversal are
// keyset-paged; only a small page of ids and one logical path are held at once.
public sealed class ExactMediaDuplicateCleanupService
{
    private const int DeletePageSize = 100;

    private readonly AppDbContext _db;
    private readonly IFileItemService _files;
    private readonly IJobQueue _jobs;
    private readonly IAuditLogger _audit;
    private readonly TimeProvider _clock;

    public ExactMediaDuplicateCleanupService(
        AppDbContext db,
        IFileItemService files,
        IJobQueue jobs,
        IAuditLogger audit,
        TimeProvider clock)
    {
        _db = db;
        _files = files;
        _jobs = jobs;
        _audit = audit;
        _clock = clock;
    }

    public async Task<MediaDuplicateCleanupStartResponse> StartAsync(
        Guid ownerUserId,
        CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var run = new MediaDuplicateCleanupRun
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerUserId,
            Status = MediaDuplicateCleanupStatuses.Queued,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.MediaDuplicateCleanupRuns.Add(run);
        await _db.SaveChangesAsync(cancellationToken);

        var job = await _jobs.EnqueueAsync(
            JobTypes.MediaExactDuplicateCleanup,
            new MediaDuplicateCleanupJobPayload(run.Id),
            cancellationToken: cancellationToken);
        run.JobId = job.Id;
        await _db.SaveChangesAsync(cancellationToken);

        await _audit.LogAsync(
            ownerUserId,
            AuditActions.MediaDuplicateCleanupStart,
            AuditEntityTypes.MediaDuplicateCleanupRun,
            run.Id,
            null,
            new { operation = "exact_media_duplicate_cleanup" },
            cancellationToken);

        return new MediaDuplicateCleanupStartResponse(run.Id, job.Id, run.Status);
    }

    public async Task<MediaDuplicateCleanupStatusResponse?> GetStatusAsync(
        Guid ownerUserId,
        Guid runId,
        CancellationToken cancellationToken)
    {
        var run = await _db.MediaDuplicateCleanupRuns
            .AsNoTracking()
            .FirstOrDefaultAsync(
                r => r.Id == runId && r.OwnerUserId == ownerUserId,
                cancellationToken);
        if (run is null) return null;

        var status = run.Status;
        if (run.JobId is Guid jobId && !MediaDuplicateCleanupStatuses.IsTerminal(status))
        {
            var jobStatus = await _db.BackgroundJobs
                .AsNoTracking()
                .Where(j => j.Id == jobId)
                .Select(j => j.Status)
                .FirstOrDefaultAsync(cancellationToken);
            status = jobStatus switch
            {
                JobStatuses.Failed => MediaDuplicateCleanupStatuses.Failed,
                JobStatuses.Cancelled => MediaDuplicateCleanupStatuses.Cancelled,
                _ => status,
            };
        }

        return new MediaDuplicateCleanupStatusResponse(
            run.Id,
            status,
            run.DuplicateGroupCount,
            run.FilesRemovedCount,
            run.FilesRetainedCount,
            run.ErrorSummary,
            run.CreatedAt,
            run.StartedAt,
            run.CompletedAt);
    }

    public async Task ExecuteSliceAsync(
        Guid runId,
        JobContext context,
        CancellationToken cancellationToken)
    {
        var run = await _db.MediaDuplicateCleanupRuns
            .FirstOrDefaultAsync(r => r.Id == runId, cancellationToken);
        if (run is null || MediaDuplicateCleanupStatuses.IsTerminal(run.Status)) return;

        try
        {
            await ExecuteSliceCoreAsync(run, context, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            run.ErrorSummary = SanitizeError(ex);
            run.UpdatedAt = _clock.GetUtcNow().UtcDateTime;
            await _db.SaveChangesAsync(CancellationToken.None);
            await _audit.LogAsync(
                run.OwnerUserId,
                AuditActions.MediaDuplicateCleanupFail,
                AuditEntityTypes.MediaDuplicateCleanupRun,
                run.Id,
                null,
                new
                {
                    operation = "exact_media_duplicate_cleanup",
                    duplicateGroupsProcessed = run.DuplicateGroupCount,
                    filesRemoved = run.FilesRemovedCount,
                },
                CancellationToken.None);
            throw;
        }
    }

    private async Task ExecuteSliceCoreAsync(
        MediaDuplicateCleanupRun run,
        JobContext context,
        CancellationToken cancellationToken)
    {
        if (run.Status == MediaDuplicateCleanupStatuses.Queued)
        {
            run.Status = MediaDuplicateCleanupStatuses.Running;
            run.StartedAt ??= _clock.GetUtcNow().UtcDateTime;
            run.UpdatedAt = _clock.GetUtcNow().UtcDateTime;
            await _db.SaveChangesAsync(cancellationToken);
        }

        var checkpoint = MediaDuplicateCleanupCheckpoint.TryParse(context.Checkpoint)
            ?? new MediaDuplicateCleanupCheckpoint();
        var lastCompletedBlobId = checkpoint.LastCompletedBlobId;
        var currentBlobId = checkpoint.CurrentBlobId;
        var lastRedundantFileId = checkpoint.LastRedundantFileId;
        var currentGroupQualified = checkpoint.CurrentGroupQualified;
        long processedThisSlice = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (currentBlobId is null)
            {
                currentBlobId = await NextDuplicateBlobIdAsync(
                    run.OwnerUserId, lastCompletedBlobId, cancellationToken);
                if (currentBlobId is null) break;
                lastRedundantFileId = null;
                currentGroupQualified = true;
            }

            var survivorId = await SelectSurvivorAsync(
                run.OwnerUserId, currentBlobId.Value, cancellationToken);

            if (survivorId is null)
            {
                CompleteCurrentGroup(run, currentGroupQualified, retained: false);
                lastCompletedBlobId = currentBlobId;
                currentBlobId = null;
                lastRedundantFileId = null;
                currentGroupQualified = false;
                await SaveRunProgressAsync(run, cancellationToken);
                continue;
            }

            var redundantIds = await EligibleFiles(run.OwnerUserId)
                .Where(f => f.BlobObjectId == currentBlobId.Value
                    && f.Id != survivorId.Value
                    && (lastRedundantFileId == null
                        || f.Id.CompareTo(lastRedundantFileId.Value) > 0))
                .OrderBy(f => f.Id)
                .Select(f => f.Id)
                .Take(DeletePageSize + 1)
                .ToListAsync(cancellationToken);

            var hasMore = redundantIds.Count > DeletePageSize;
            var page = hasMore ? redundantIds.Take(DeletePageSize) : redundantIds;
            foreach (var redundantId in page)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await _files.SoftDeleteExactMediaDuplicateAsync(
                    run.OwnerUserId,
                    redundantId,
                    survivorId.Value,
                    cancellationToken))
                {
                    run.FilesRemovedCount++;
                }

                lastRedundantFileId = redundantId;
                processedThisSlice++;
                await context.ReportProgressAsync(
                    run.FilesRemovedCount,
                    message: $"removing exact media duplicates ({run.DuplicateGroupCount} groups, {run.FilesRemovedCount} files)",
                    cancellationToken: cancellationToken);

                if (context.ShouldYield(processedThisSlice))
                {
                    await SaveRunProgressAsync(run, cancellationToken);
                    context.RequestContinuation(
                        context.HigherPriorityWaiting
                            ? JobYieldReasons.HigherPriority
                            : JobYieldReasons.SliceBudget,
                        new MediaDuplicateCleanupCheckpoint(
                            lastCompletedBlobId,
                            currentBlobId,
                            lastRedundantFileId,
                            currentGroupQualified).ToJson());
                    return;
                }
            }

            if (hasMore) continue;

            var retained = await EligibleFiles(run.OwnerUserId)
                .AnyAsync(
                    f => f.BlobObjectId == currentBlobId.Value && f.Id == survivorId.Value,
                    cancellationToken);
            CompleteCurrentGroup(run, currentGroupQualified, retained);
            lastCompletedBlobId = currentBlobId;
            currentBlobId = null;
            lastRedundantFileId = null;
            currentGroupQualified = false;
            await SaveRunProgressAsync(run, cancellationToken);

            if (context.ShouldYield(processedThisSlice))
            {
                context.RequestContinuation(
                    context.HigherPriorityWaiting
                        ? JobYieldReasons.HigherPriority
                        : JobYieldReasons.SliceBudget,
                    new MediaDuplicateCleanupCheckpoint(
                        lastCompletedBlobId, null, null, false).ToJson());
                return;
            }
        }

        run.Status = MediaDuplicateCleanupStatuses.Succeeded;
        run.ErrorSummary = null;
        run.CompletedAt = _clock.GetUtcNow().UtcDateTime;
        run.UpdatedAt = run.CompletedAt.Value;
        await _db.SaveChangesAsync(cancellationToken);
        await context.ReportProgressAsync(
            run.FilesRemovedCount,
            run.FilesRemovedCount,
            $"complete ({run.DuplicateGroupCount} groups, {run.FilesRemovedCount} files removed)",
            cancellationToken);
        await _audit.LogAsync(
            run.OwnerUserId,
            AuditActions.MediaDuplicateCleanupComplete,
            AuditEntityTypes.MediaDuplicateCleanupRun,
            run.Id,
            null,
            new
            {
                operation = "exact_media_duplicate_cleanup",
                duplicateGroups = run.DuplicateGroupCount,
                filesRemoved = run.FilesRemovedCount,
                filesRetained = run.FilesRetainedCount,
            },
            cancellationToken);
    }

    private IQueryable<FileItem> EligibleFiles(Guid ownerUserId)
        => from file in _db.FileItems.AsNoTracking()
           join metadata in _db.BlobMetadata
               on file.BlobObjectId equals metadata.BlobObjectId
           where file.OwnerUserId == ownerUserId
               && file.DeletedAt == null
               && !_db.PartyUploadItems.Any(p => p.FileItemId == file.Id)
               && !_db.AlbumItems.Any(item => item.FileItemId == file.Id
                   && _db.PartyAlbumLinks.Any(link => link.AlbumId == item.AlbumId))
               && metadata.DetectedContentType != null
               && ((metadata.MediaCategory == MediaCategories.Image
                       && metadata.DetectedContentType.StartsWith("image/"))
                   || (metadata.MediaCategory == MediaCategories.Video
                       && metadata.DetectedContentType.StartsWith("video/")))
           select file;

    private async Task<Guid?> NextDuplicateBlobIdAsync(
        Guid ownerUserId,
        Guid? afterBlobId,
        CancellationToken cancellationToken)
    {
        var query = EligibleFiles(ownerUserId);
        if (afterBlobId is Guid after)
        {
            query = query.Where(f => f.BlobObjectId.CompareTo(after) > 0);
        }

        // Include the canonical digest in the grouping key. BlobObject.Sha256
        // is unique, immutable full-file identity; the id is retained only as
        // the internal cursor/FK used by the deletion service.
        return await (from file in query
            join blob in _db.BlobObjects on file.BlobObjectId equals blob.Id
            group file by new { blob.Id, blob.Sha256 }
            into duplicate
            where duplicate.Count() >= 2
            orderby duplicate.Key.Id
            select (Guid?)duplicate.Key.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<Guid?> SelectSurvivorAsync(
        Guid ownerUserId,
        Guid blobObjectId,
        CancellationToken cancellationToken)
    {
        var oldest = await EligibleFiles(ownerUserId)
            .Where(f => f.BlobObjectId == blobObjectId)
            .MinAsync(f => (DateTime?)f.CreatedAt, cancellationToken);
        if (oldest is null) return null;

        Guid? afterId = null;
        Guid? winnerId = null;
        string? winnerPath = null;
        while (true)
        {
            var candidate = await EligibleFiles(ownerUserId)
                .Where(f => f.BlobObjectId == blobObjectId
                    && f.CreatedAt == oldest.Value
                    && (afterId == null || f.Id.CompareTo(afterId.Value) > 0))
                .OrderBy(f => f.Id)
                .Select(f => new { f.Id, f.ParentFolderId, f.Name })
                .FirstOrDefaultAsync(cancellationToken);
            if (candidate is null) break;

            afterId = candidate.Id;
            var path = await LogicalPathAsync(
                ownerUserId, candidate.ParentFolderId, candidate.Name, cancellationToken);
            if (winnerPath is null
                || string.CompareOrdinal(path, winnerPath) < 0
                || (path == winnerPath && candidate.Id.CompareTo(winnerId!.Value) < 0))
            {
                winnerId = candidate.Id;
                winnerPath = path;
            }
        }

        return winnerId;
    }

    private async Task<string> LogicalPathAsync(
        Guid ownerUserId,
        Guid? parentFolderId,
        string fileName,
        CancellationToken cancellationToken)
    {
        var parts = new List<string> { fileName.Normalize(NormalizationForm.FormC) };
        var current = parentFolderId;
        var guard = 0;
        while (current is Guid folderId && guard++ < 256)
        {
            var folder = await _db.Folders
                .AsNoTracking()
                .Where(f => f.Id == folderId
                    && f.OwnerUserId == ownerUserId
                    && f.DeletedAt == null)
                .Select(f => new { f.Name, f.ParentFolderId })
                .FirstOrDefaultAsync(cancellationToken);
            if (folder is null) break;
            parts.Add(folder.Name.Normalize(NormalizationForm.FormC));
            current = folder.ParentFolderId;
        }
        parts.Reverse();
        return "/" + string.Join('/', parts);
    }

    private static void CompleteCurrentGroup(
        MediaDuplicateCleanupRun run,
        bool qualified,
        bool retained)
    {
        if (!qualified) return;
        run.DuplicateGroupCount++;
        if (retained) run.FilesRetainedCount++;
    }

    private async Task SaveRunProgressAsync(
        MediaDuplicateCleanupRun run,
        CancellationToken cancellationToken)
    {
        run.UpdatedAt = _clock.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync(cancellationToken);
    }

    private static string SanitizeError(Exception ex)
    {
        var message = ex.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (message.Length > 420) message = message[..420];
        return $"{ex.GetType().Name}: {message}";
    }
}

public sealed record MediaDuplicateCleanupJobPayload(Guid RunId);

public sealed record MediaDuplicateCleanupStartResponse(Guid RunId, Guid JobId, string Status);

public sealed record MediaDuplicateCleanupStatusResponse(
    Guid RunId,
    string Status,
    int DuplicateGroupCount,
    int FilesRemovedCount,
    int FilesRetainedCount,
    string? Error,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? CompletedAt);

internal sealed record MediaDuplicateCleanupCheckpoint(
    Guid? LastCompletedBlobId = null,
    Guid? CurrentBlobId = null,
    Guid? LastRedundantFileId = null,
    bool CurrentGroupQualified = false)
{
    private const int Version = 1;

    public string ToJson() => JsonSerializer.Serialize(new CheckpointDocument(
        Version,
        LastCompletedBlobId,
        CurrentBlobId,
        LastRedundantFileId,
        CurrentGroupQualified));

    public static MediaDuplicateCleanupCheckpoint? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var document = JsonSerializer.Deserialize<CheckpointDocument>(json);
            return document?.Version == Version
                ? new MediaDuplicateCleanupCheckpoint(
                    document.LastCompletedBlobId,
                    document.CurrentBlobId,
                    document.LastRedundantFileId,
                    document.CurrentGroupQualified)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record CheckpointDocument(
        int Version,
        Guid? LastCompletedBlobId,
        Guid? CurrentBlobId,
        Guid? LastRedundantFileId,
        bool CurrentGroupQualified);
}
