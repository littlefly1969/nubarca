using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NubArca.Api.Ai;
using NubArca.Api.Ai.Jobs;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Folders;
using NubArca.Api.Jobs;
using NubArca.Api.Metadata;
using NubArca.Api.Storage;
using NubArca.Api.Uploads;
using SixLabors.ImageSharp;

namespace NubArca.Api.Admin;

public interface IAdminImportService
{
    AdminImportRootsResponse GetRoots();
    Task<AdminImportBrowseResponse> BrowseAsync(string rootId, string? relativePath, CancellationToken cancellationToken);
    Task<IReadOnlyList<AdminImportUserDto>> GetSelectableUsersAsync(CancellationToken cancellationToken);
    Task<AdminImportFoldersResponse> GetDestinationFoldersAsync(Guid targetUserId, Guid? parentFolderId, CancellationToken cancellationToken);
    Task<AdminImportPreviewResponse> PreviewAsync(AdminImportPreviewRequest request, CancellationToken cancellationToken);
    Task<AdminImportRunResponse> StartRunAsync(Guid adminUserId, AdminImportRunRequest request, CancellationToken cancellationToken);
    Task<AdminImportRunStatusResponse?> GetRunStatusAsync(Guid importRunId, CancellationToken cancellationToken);
    Task<AdminImportRunListResponse> ListRunsAsync(int limit, int offset, CancellationToken cancellationToken);
    Task<AdminImportCancelResponse?> RequestCancelAsync(Guid importRunId, CancellationToken cancellationToken);
    // Slice 92: paginated, safe view over the run's persisted import items
    // (relative paths + categories only). Null when the run does not exist.
    Task<AdminImportItemListResponse?> GetRunItemsAsync(
        Guid importRunId, string? status, int page, int pageSize, CancellationToken cancellationToken);
    // Slice 92: enqueue a media-derivatives backfill job (idempotent per run)
    // so a completed/partial import's missing thumbnails/previews/posters are
    // generated in the background. Null when the run does not exist.
    Task<AdminImportEnqueueDerivativesResponse?> EnqueueMissingDerivativesAsync(
        Guid importRunId, CancellationToken cancellationToken);
    // Slice 91: executes via Background Jobs v2 — the JobContext supplies the
    // safe log sink, cooperative cancellation, and generic progress reporting.
    Task ExecuteRunAsync(Guid importRunId, JobContext jobContext, CancellationToken cancellationToken);
}

// Slice 81: admin-only, opt-in, whitelist-bounded server-side directory import.
// Imports files that already live on the NubArca server filesystem (under a
// configured root) into a selected target user's library, reusing the normal
// file-creation pipeline (dedup, quota, metadata, thumbnails, audit) and
// recreating the directory structure as logical folders.
//
// Safety: every path is canonicalized and verified to stay inside the
// configured root; symlinks are never followed; internal storage roots are
// rejected; the feature is disabled by default with no unrestricted-filesystem
// fallback. No absolute physical path ever crosses the API boundary.
public sealed class AdminImportService : IAdminImportService
{
    // Defensive caps so a pathological tree can't exhaust the preview/import.
    private const int MaxDepth = 64;
    private const int MaxScanEntries = 200_000;
    private const int MaxSegmentLength = 255;
    // Slice 92: manifest items store the source-relative path; entries beyond
    // this are recorded as skipped (never truncated — that would break resume
    // identity). Matches the admin_import_items column bound.
    private const int MaxRelativePathLength = 2048;
    // Slice 84: cap the safe conflict samples surfaced on the run detail.
    private const int MaxConflictSamples = 20;

    private readonly AppDbContext _db;
    private readonly IFileItemService _files;
    private readonly IFolderService _folders;
    private readonly IJobQueue _jobs;
    private readonly TimeProvider _clock;
    private readonly IOptions<AdminImportOptions> _options;
    private readonly IOptions<BlobStorageOptions> _storage;
    // Slice 93: staging-sourced runs read from the remote-staging root.
    private readonly IOptions<StagingOptions> _staging;
    private readonly IOptions<AiOptions> _ai;
    private readonly IOptions<MediaOptions> _media;
    private readonly ILogger<AdminImportService>? _logger;
    // Slice 98: DB batch pipeline dependencies (null = per-file path only).
    private readonly IBlobStorage? _blobStorage;
    private readonly IVideoSignatureDetector? _videoDetector;
    // deleted-content-import-skip: evaluates the two import skip options. Null
    // for direct-construction test sites that don't exercise skipping.
    private readonly IImportSkipEvaluator? _skipEvaluator;

    // Slice 98 test seam — see PersistStagedBatchCoreAsync.
    internal Func<Task>? AfterBatchLookupForTests;

    public AdminImportService(
        AppDbContext db,
        IFileItemService files,
        IFolderService folders,
        IJobQueue jobs,
        TimeProvider clock,
        IOptions<AdminImportOptions> options,
        IOptions<BlobStorageOptions> storage,
        IOptions<StagingOptions> staging,
        ILogger<AdminImportService>? logger = null,
        // Slice 98: the DB batch pipeline writes physical blobs and sniffs
        // media facts itself (page-staged, before any DB work). Optional for
        // direct-construction test sites; when null, imports use the original
        // per-file path.
        IBlobStorage? blobStorage = null,
        IVideoSignatureDetector? videoDetector = null,
        IOptions<AiOptions>? ai = null,
        IImportSkipEvaluator? skipEvaluator = null,
        // Video metadata probe config (ffprobe). Optional; null = disabled, so
        // imports enqueue no video-probe job (matches the default provider).
        IOptions<MediaOptions>? media = null)
    {
        _db = db;
        _files = files;
        _folders = folders;
        _jobs = jobs;
        _clock = clock;
        _options = options;
        _storage = storage;
        _staging = staging;
        _ai = ai ?? Options.Create(new AiOptions());
        _media = media ?? Options.Create(new MediaOptions());
        _logger = logger;
        _blobStorage = blobStorage;
        _videoDetector = videoDetector;
        _skipEvaluator = skipEvaluator;
    }

    // ---- roots -----------------------------------------------------------

    public AdminImportRootsResponse GetRoots()
    {
        var opts = _options.Value;
        var roots = CanonicalRoots()
            .Select(r => new AdminImportRootDto(r.RootId, r.Label))
            .ToList();
        return new AdminImportRootsResponse(
            Enabled: opts.Enabled,
            Configured: roots.Count > 0,
            Roots: roots,
            Throttle: new AdminImportThrottleConfig(
                opts.DelayBetweenFilesMs, opts.MaxBytesPerSecond, opts.MaxRunMinutes, opts.YieldEveryFiles));
    }

    // ---- browse ----------------------------------------------------------

    public Task<AdminImportBrowseResponse> BrowseAsync(string rootId, string? relativePath, CancellationToken cancellationToken)
    {
        var root = ResolveRoot(rootId);
        var (dir, normalizedRelative) = ResolveSourceDir(root.CanonicalPath, relativePath);

        var entries = new List<AdminImportDirectoryEntry>();
        foreach (var child in SafeEnumerate(dir))
        {
            if (!TryGetAttributes(child, out var attrs)) continue;
            if (attrs.HasFlag(FileAttributes.ReparsePoint)) continue; // never follow symlinks
            if (!attrs.HasFlag(FileAttributes.Directory)) continue;   // directories only

            var name = Path.GetFileName(child);
            var childRel = string.IsNullOrEmpty(normalizedRelative) ? name : $"{normalizedRelative}/{name}";
            CountImmediate(child, out var childDirs, out var childFiles);
            entries.Add(new AdminImportDirectoryEntry(name, childRel, childDirs, childFiles));
        }
        entries.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        var parentRelative = ParentRelative(normalizedRelative);
        return Task.FromResult(new AdminImportBrowseResponse(rootId, normalizedRelative, parentRelative, entries));
    }

    // ---- users / destination folders ------------------------------------

    public async Task<IReadOnlyList<AdminImportUserDto>> GetSelectableUsersAsync(CancellationToken cancellationToken)
    {
        return await _db.Users
            .AsNoTracking()
            .OrderBy(u => u.Email)
            .Select(u => new AdminImportUserDto(u.Id, u.Email, u.DisplayName, u.IsAdmin, u.DisabledAt == null))
            .ToListAsync(cancellationToken);
    }

    public async Task<AdminImportFoldersResponse> GetDestinationFoldersAsync(Guid targetUserId, Guid? parentFolderId, CancellationToken cancellationToken)
    {
        await EnsureTargetUserAsync(targetUserId, cancellationToken);
        if (parentFolderId is { } parent)
        {
            var folder = await _folders.GetByIdAsync(parent, targetUserId, cancellationToken);
            if (folder is null)
            {
                throw new AdminImportValidationException("Destination folder not found.");
            }
        }

        var children = await _folders.ListChildrenAsync(targetUserId, parentFolderId, cancellationToken);
        var folders = children.Select(f => new AdminImportFolderDto(f.Id, f.Name)).ToList();
        return new AdminImportFoldersResponse(targetUserId, parentFolderId, folders);
    }

    // ---- preview ---------------------------------------------------------

    public async Task<AdminImportPreviewResponse> PreviewAsync(AdminImportPreviewRequest request, CancellationToken cancellationToken)
    {
        var root = ResolveRoot(request.RootId);
        var (dir, _) = ResolveSourceDir(root.CanonicalPath, request.RelativePath);
        await EnsureTargetUserAsync(request.TargetUserId, cancellationToken);
        if (request.DestinationFolderId is { } dest)
        {
            var folder = await _folders.GetByIdAsync(dest, request.TargetUserId, cancellationToken);
            if (folder is null)
            {
                throw new AdminImportValidationException("Destination folder not found.");
            }
        }

        var scan = new ScanResult();
        ScanDirectory(dir, depth: 0, scan, cancellationToken);

        var warnings = new List<string>();
        if (scan.SkippedSymlinks > 0) warnings.Add($"{scan.SkippedSymlinks} symbolic link(s) will be skipped (not followed).");
        if (scan.SkippedUnsupported > 0) warnings.Add($"{scan.SkippedUnsupported} special file(s) (devices/sockets) will be skipped.");
        if (scan.Unreadable > 0) warnings.Add($"{scan.Unreadable} item(s) could not be read and will be skipped.");
        if (scan.Truncated) warnings.Add($"The directory is very large; preview counts are capped at {MaxScanEntries} entries.");
        if (scan.TotalFiles == 0) warnings.Add("No importable files were found in this directory.");

        return new AdminImportPreviewResponse(
            TotalFiles: scan.TotalFiles,
            TotalDirectories: scan.TotalDirectories,
            TotalBytes: scan.TotalBytes,
            SkippedSymlinks: scan.SkippedSymlinks,
            SkippedUnsupported: scan.SkippedUnsupported,
            UnreadableCount: scan.Unreadable,
            Truncated: scan.Truncated,
            Warnings: warnings);
    }

    // ---- start run (enqueue) --------------------------------------------

    public async Task<AdminImportRunResponse> StartRunAsync(Guid adminUserId, AdminImportRunRequest request, CancellationToken cancellationToken)
    {
        var root = ResolveRoot(request.RootId);
        var (_, normalizedRelative) = ResolveSourceDir(root.CanonicalPath, request.RelativePath);
        await EnsureTargetUserAsync(request.TargetUserId, cancellationToken);
        if (request.DestinationFolderId is { } dest)
        {
            var folder = await _folders.GetByIdAsync(dest, request.TargetUserId, cancellationToken);
            if (folder is null)
            {
                throw new AdminImportValidationException("Destination folder not found.");
            }
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        var run = new AdminImportRun
        {
            Id = Guid.NewGuid(),
            AdminUserId = adminUserId,
            TargetUserId = request.TargetUserId,
            DestinationFolderId = request.DestinationFolderId,
            SkipPreviouslyDeleted = request.SkipPreviouslyDeleted,
            SkipExistingContent = request.SkipExistingContent,
            RootId = root.RootId,
            SourceRelativePath = normalizedRelative,
            Status = AdminImportStatuses.Queued,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.AdminImportRuns.Add(run);
        await _db.SaveChangesAsync(cancellationToken);

        var job = await _jobs.EnqueueAsync(
            JobTypes.AdminImport,
            new AdminImportJobPayload(run.Id),
            idempotencyKey: $"admin-import:{run.Id:N}",
            cancellationToken: cancellationToken);

        run.JobId = job.Id;
        await _db.SaveChangesAsync(cancellationToken);

        return new AdminImportRunResponse(run.Id, job.Id, run.Status);
    }

    public async Task<AdminImportRunStatusResponse?> GetRunStatusAsync(Guid importRunId, CancellationToken cancellationToken)
    {
        var r = await _db.AdminImportRuns.AsNoTracking().FirstOrDefaultAsync(x => x.Id == importRunId, cancellationToken);
        if (r is null) return null;
        var email = await _db.Users.AsNoTracking()
            .Where(u => u.Id == r.TargetUserId)
            .Select(u => (string?)u.Email)
            .FirstOrDefaultAsync(cancellationToken);
        var job = r.JobId is Guid jid
            ? await _db.BackgroundJobs.AsNoTracking()
                .Where(j => j.Id == jid)
                .Select(j => new { j.Status, j.CancellationRequested })
                .FirstOrDefaultAsync(cancellationToken)
            : null;
        // Slice 92: conflict/resume samples derive from the persisted manifest
        // (detail view only — the runs list stays one-query cheap). Bounded,
        // discovery-ordered, safe relative paths only.
        var samples = await _db.AdminImportItems.AsNoTracking()
            .Where(i => i.ImportRunId == r.Id && i.ConflictCategory != null)
            .OrderBy(i => i.Ordinal)
            .Take(MaxConflictSamples)
            .Select(i => new AdminImportConflictSample(
                i.RelativePath.Length > 300 ? i.RelativePath.Substring(0, 300) : i.RelativePath,
                i.ConflictCategory!))
            .ToListAsync(cancellationToken);
        return ToStatus(r, email, job?.Status, job?.CancellationRequested ?? false, samples);
    }

    public async Task<AdminImportRunListResponse> ListRunsAsync(int limit, int offset, CancellationToken cancellationToken)
    {
        limit = Math.Clamp(limit, 1, 100);
        offset = Math.Max(offset, 0);
        var total = await _db.AdminImportRuns.CountAsync(cancellationToken);
        var rows = await _db.AdminImportRuns
            .AsNoTracking()
            .OrderByDescending(r => r.CreatedAt)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(cancellationToken);

        var userIds = rows.Select(r => r.TargetUserId).Distinct().ToList();
        var emails = await _db.Users.AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.Email, cancellationToken);

        // Batch-load the linked jobs for status reconciliation.
        var jobIds = rows.Where(r => r.JobId != null).Select(r => r.JobId!.Value).Distinct().ToList();
        var jobs = await _db.BackgroundJobs.AsNoTracking()
            .Where(j => jobIds.Contains(j.Id))
            .Select(j => new { j.Id, j.Status, j.CancellationRequested })
            .ToDictionaryAsync(j => j.Id, cancellationToken);

        var items = rows.Select(r =>
        {
            var job = r.JobId is Guid jid && jobs.TryGetValue(jid, out var j) ? j : null;
            // Samples are a DETAIL-view concern (one bounded item query per run
            // would defeat the batch shape here) — the list returns them empty.
            return ToStatus(
                r, emails.GetValueOrDefault(r.TargetUserId), job?.Status,
                job?.CancellationRequested ?? false, Array.Empty<AdminImportConflictSample>());
        }).ToList();
        return new AdminImportRunListResponse(items, total, limit, offset);
    }

    // ---- slice 92: per-item manifest visibility + derivative backfill ----

    public async Task<AdminImportItemListResponse?> GetRunItemsAsync(
        Guid importRunId, string? status, int page, int pageSize, CancellationToken cancellationToken)
    {
        var exists = await _db.AdminImportRuns.AsNoTracking()
            .AnyAsync(r => r.Id == importRunId, cancellationToken);
        if (!exists) return null;
        if (!string.IsNullOrWhiteSpace(status) && !AdminImportItemStatuses.IsKnown(status))
        {
            throw new AdminImportValidationException("Unknown import item status filter.");
        }

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = _db.AdminImportItems.AsNoTracking()
            .Where(i => i.ImportRunId == importRunId);
        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(i => i.Status == status);
        }

        var total = await query.CountAsync(cancellationToken);
        // Safe projection at the SQL layer: relative path + categories only —
        // never FileItemId, absolute paths, or storage internals.
        var items = await query
            .OrderBy(i => i.Ordinal)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(i => new AdminImportItemDto(
                i.RelativePath, i.Kind, i.SizeBytes, i.Status,
                i.FailureCategory, i.FailureMessage, i.ConflictCategory,
                i.Attempts, i.SourceModifiedAt, i.CompletedAt))
            .ToListAsync(cancellationToken);

        return new AdminImportItemListResponse(importRunId, items, total, page, pageSize);
    }

    public async Task<AdminImportEnqueueDerivativesResponse?> EnqueueMissingDerivativesAsync(
        Guid importRunId, CancellationToken cancellationToken)
    {
        var exists = await _db.AdminImportRuns.AsNoTracking()
            .AnyAsync(r => r.Id == importRunId, cancellationToken);
        if (!exists) return null;

        // Same idempotency key as the automatic end-of-run enqueue: a pending
        // job is reused; a finished one allows a fresh backfill. The job itself
        // is idempotent (existing derivatives are skipped).
        var job = await _jobs.EnqueueAsync(
            JobTypes.MediaDerivativesBackfill,
            new MediaDerivativesBackfillJobPayload(),
            idempotencyKey: $"media-derivatives:import:{importRunId:N}",
            cancellationToken: cancellationToken);
        return new AdminImportEnqueueDerivativesResponse(importRunId, job.Id, job.Status);
    }

    public async Task<AdminImportCancelResponse?> RequestCancelAsync(Guid importRunId, CancellationToken cancellationToken)
    {
        var run = await _db.AdminImportRuns
            .AsNoTracking()
            .Where(r => r.Id == importRunId)
            .Select(r => new { r.JobId, r.Status })
            .FirstOrDefaultAsync(cancellationToken);
        if (run is null) return null;

        // Slice 91: the import cancel endpoint delegates to the SAME job
        // cancellation path the generic Admin Jobs dashboard uses, so there is
        // one source of truth. RequestCancellationAsync is idempotent and a
        // no-op for terminal jobs.
        var requested = false;
        if (run.JobId is Guid jobId)
        {
            requested = await _jobs.RequestCancellationAsync(jobId, cancellationToken);

            // Slice 92: a flagged QUEUED job never runs (the engine finishes it
            // as cancelled at claim time without invoking the handler), so the
            // handler-side finalization can't happen — freeze the run + its
            // unprocessed manifest items here. A RUNNING job is left to its
            // handler, which observes the flag and finalizes cooperatively.
            var job = await _db.BackgroundJobs.AsNoTracking()
                .Where(j => j.Id == jobId)
                .Select(j => new { j.Status, j.CancellationRequested })
                .FirstOrDefaultAsync(cancellationToken);
            var willNeverRun = job is not null && job.CancellationRequested
                && (job.Status == JobStatuses.Queued || job.Status == JobStatuses.Cancelled);
            if (willNeverRun
                && run.Status is not (AdminImportStatuses.Succeeded
                    or AdminImportStatuses.Partial
                    or AdminImportStatuses.Failed
                    or AdminImportStatuses.Cancelled))
            {
                await FreezeCancelledRunAsync(importRunId, cancellationToken);
            }
        }

        // Report the effective (job-reconciled) status so the UI stays coherent.
        var effective = await GetRunStatusAsync(importRunId, cancellationToken);
        return new AdminImportCancelResponse(
            requested || (effective?.CancelRequested ?? false),
            effective?.Status ?? run.Status);
    }

    // Slice 92: terminal-cancel a run whose job will never (re)execute —
    // freezes unprocessed manifest items as `cancelled` and finalizes the run
    // row from item-derived counters. Idempotent.
    private async Task FreezeCancelledRunAsync(Guid runId, CancellationToken cancellationToken)
    {
        // Slice 93: a staging-sourced run syncs its session here too (the
        // handler never runs for a pre-flagged queued job).
        var stagingSessionId = await _db.AdminImportRuns.AsNoTracking()
            .Where(r => r.Id == runId)
            .Select(r => r.StagingSessionId)
            .FirstOrDefaultAsync(cancellationToken);
        if (stagingSessionId is Guid sid)
        {
            await SyncStagingSessionAsync(sid, AdminImportStatuses.Cancelled, cancellationToken);
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        await _db.AdminImportItems
            .Where(i => i.ImportRunId == runId
                && (i.Status == AdminImportItemStatuses.Pending
                    || i.Status == AdminImportItemStatuses.Importing))
            .ExecuteUpdateAsync(s => s
                .SetProperty(i => i.Status, AdminImportItemStatuses.Cancelled)
                .SetProperty(i => i.UpdatedAt, now)
                .SetProperty(i => i.CompletedAt, now), cancellationToken);

        var counters = await ComputeCountersAsync(runId, cancellationToken);
        await _db.AdminImportRuns.Where(r => r.Id == runId).ExecuteUpdateAsync(s => s
            .SetProperty(r => r.Status, AdminImportStatuses.Cancelled)
            .SetProperty(r => r.Phase, (string?)null)
            .SetProperty(r => r.ImportedFiles, counters.Imported)
            .SetProperty(r => r.AlreadyImportedFiles, counters.AlreadyImported)
            .SetProperty(r => r.SkippedFiles, counters.Skipped)
            .SetProperty(r => r.FailedFiles, counters.Failed)
            .SetProperty(r => r.ConflictFiles, counters.Conflicts)
            .SetProperty(r => r.CancelledFiles, counters.Cancelled)
            .SetProperty(r => r.SkippedPreviouslyDeletedFiles, counters.SkippedPreviouslyDeleted)
            .SetProperty(r => r.SkippedAlreadyPresentFiles, counters.SkippedAlreadyPresent)
            .SetProperty(r => r.ImportedBytes, counters.ImportedBytes)
            .SetProperty(r => r.CurrentRelativePath, (string?)null)
            .SetProperty(r => r.CompletedAt, r => r.CompletedAt ?? now)
            .SetProperty(r => r.UpdatedAt, now), cancellationToken);
    }

    // Maps a run row to the safe status DTO, computing L1 aggregate metrics.
    // Slice 91: the effective status + "cancellation pending" flag are
    // reconciled against the linked background job so the two can never
    // contradict (e.g. job cancelled but run still "running"). `jobStatus` is
    // the linked job's status (null only if the job row is gone).
    private static AdminImportRunStatusResponse ToStatus(
        AdminImportRun r, string? targetUserEmail, string? jobStatus, bool jobCancellationRequested,
        IReadOnlyList<AdminImportConflictSample> samples)
    {
        // Job terminal states win over the domain status; otherwise the domain
        // status (which carries the richer partial/paused detail) is shown.
        var effectiveStatus = jobStatus switch
        {
            JobStatuses.Cancelled => AdminImportStatuses.Cancelled,
            JobStatuses.Failed => AdminImportStatuses.Failed,
            _ => r.Status,
        };
        // "Cancelling…": requested on a not-yet-terminal job.
        var cancelPending = jobCancellationRequested && !JobStatuses.IsTerminal(jobStatus);
        long? durationMs = r.StartedAt is { } s && r.CompletedAt is { } c
            ? (long)(c - s).TotalMilliseconds
            : null;
        double? seconds = durationMs is { } d && d > 0 ? d / 1000.0 : null;
        double? filesPerSec = seconds is { } sec ? r.ImportedFiles / sec : null;
        double? bytesPerSec = seconds is { } sec2 ? r.ImportedBytes / sec2 : null;
        long touched = (long)r.ImportedFiles + r.ConflictFiles + r.FailedFiles + r.SkippedFiles;
        double? conflictPct = touched > 0 ? r.ConflictFiles * 100.0 / touched : null;
        double? skippedPct = touched > 0 ? r.SkippedFiles * 100.0 / touched : null;
        double? failedPct = touched > 0 ? r.FailedFiles * 100.0 / touched : null;
        long? avgBytes = r.ImportedFiles > 0 ? r.ImportedBytes / r.ImportedFiles : null;

        // Slice 92: not-yet-processed manifest files. ScannedFiles is the
        // manifest's file total; AlreadyImportedFiles is a SUBSET of
        // ImportedFiles, so it does not subtract.
        var pending = Math.Max(0, r.ScannedFiles
            - r.ImportedFiles - r.SkippedFiles - r.FailedFiles - r.ConflictFiles - r.CancelledFiles);

        var metrics = new AdminImportRunMetrics(
            durationMs, filesPerSec, bytesPerSec, conflictPct, skippedPct, failedPct, avgBytes);
        var phases = new AdminImportPhaseTimings(
            r.ReadMillis, r.HashMillis, r.WriteMillis, r.BlobDbMillis,
            r.DetectMillis, r.MetadataMillis, r.FileItemMillis, r.ThumbnailMillis,
            r.FolderMillis, r.ItemDbMillis);

        return new AdminImportRunStatusResponse(
            r.Id, r.JobId, effectiveStatus, cancelPending, r.Phase,
            r.RootId, r.SourceRelativePath, r.TargetUserId, targetUserEmail, r.DestinationFolderId,
            r.ScannedFiles, pending, r.ImportedFiles, r.SkippedFiles,
            r.SkippedPreviouslyDeletedFiles, r.SkippedAlreadyPresentFiles,
            r.FailedFiles, r.ConflictFiles,
            r.AlreadyImportedFiles, r.CancelledFiles,
            r.ImportedBytes, r.TotalBytes, r.TotalDirectories,
            r.CurrentRelativePath, r.ErrorSummary, r.CreatedAt, r.StartedAt, r.CompletedAt,
            r.ScanCompletedAt, metrics, phases, samples);
    }

    // ---- execute run (job handler) --------------------------------------
    //
    // Slice 92: the run executes in two phases, both resumable:
    //   1. SCAN — stream-walk the source tree and persist one admin_import_items
    //      row per entry (the manifest), in batches. Never holds the tree in
    //      memory. Marked complete via run.ScanCompletedAt.
    //   2. IMPORT — drain the manifest's `pending` items in bounded pages.
    //      Item state is the source of truth: a resumed/retried run skips
    //      `imported` items by status instead of re-walking the source.
    // Run counters/progress derive from item statuses (RefreshRunCountersAsync).

    public async Task ExecuteRunAsync(Guid importRunId, JobContext jobContext, CancellationToken cancellationToken)
    {
        var run = await _db.AdminImportRuns.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == importRunId, cancellationToken);
        if (run is null)
        {
            // Nothing to do — the run row was removed; treat as success so the
            // job does not retry forever.
            return;
        }
        if (run.Status is AdminImportStatuses.Succeeded
            or AdminImportStatuses.Partial
            or AdminImportStatuses.Cancelled
            or AdminImportStatuses.Failed)
        {
            // A stale duplicate job (e.g. a pause-requeue that raced a cancel)
            // must never reopen a finished run.
            return;
        }

        // Slice 97 (bug 2): a thrown slice used to leave the run row `running`
        // and a staging-sourced session `importing` forever — only the JOB row
        // went failed, so the session could never be discarded and never
        // expired. On the FINAL attempt, persist the failure on the run and
        // sync the session; a retryable attempt leaves the run `running` so
        // the retry re-enters normally. OperationCanceledException stays owned
        // by the job engine (cooperative cancel / worker shutdown — neither is
        // a failure).
        try
        {
            await ExecuteRunCoreAsync(run, jobContext, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            await TryMarkRunFailedAsync(run, jobContext, ex);
            throw;
        }
    }

    // Slice 97: best-effort terminal-failure bookkeeping for the run + its
    // staging session. Uses the same "last attempt" predicate as the job
    // engine (the claim already incremented Attempts). Never throws; writes
    // with CancellationToken.None so a cancelled job token cannot skip it.
    private async Task TryMarkRunFailedAsync(AdminImportRun run, JobContext jobContext, Exception failure)
    {
        try
        {
            var job = await _db.BackgroundJobs.AsNoTracking()
                .Where(j => j.Id == jobContext.JobId)
                .Select(j => new { j.Attempts, j.MaxAttempts })
                .FirstOrDefaultAsync(CancellationToken.None);
            var permanent = job is null || job.Attempts >= job.MaxAttempts;
            if (!permanent)
            {
                return;
            }

            // Validation messages are crafted operator-safe; anything else is
            // reduced to its type name (raw messages can echo paths).
            var summary = failure is AdminImportValidationException
                ? failure.Message
                : $"Unexpected failure ({failure.GetType().Name}).";
            var now = _clock.GetUtcNow().UtcDateTime;
            await _db.AdminImportRuns
                .Where(r => r.Id == run.Id
                    && r.Status != AdminImportStatuses.Succeeded
                    && r.Status != AdminImportStatuses.Partial
                    && r.Status != AdminImportStatuses.Cancelled)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.Status, AdminImportStatuses.Failed)
                    .SetProperty(r => r.Phase, (string?)null)
                    .SetProperty(r => r.ErrorSummary, summary)
                    .SetProperty(r => r.CompletedAt, r => r.CompletedAt ?? now)
                    .SetProperty(r => r.UpdatedAt, now), CancellationToken.None);

            if (run.StagingSessionId is Guid sessionId)
            {
                await SyncStagingSessionAsync(sessionId, AdminImportStatuses.Failed, CancellationToken.None);
            }
        }
        catch
        {
            // Best-effort: the resilient staging discard (slice 97) still
            // converges from the job row alone if this write fails.
        }
    }

    private async Task ExecuteRunCoreAsync(
        AdminImportRun run, JobContext jobContext, CancellationToken cancellationToken)
    {
        // The JobContext is the single source of the log sink + cooperative
        // cancellation flag + generic progress. `log` is just its safe sink.
        Action<string> log = jobContext.Log;

        // Re-validate against current config (it may have changed since enqueue)
        // and re-resolve the source — never trust a cached absolute path.
        // Slice 93: a staging-sourced run reads from the remote-staging session
        // directory instead of a configured AdminImport root (and therefore
        // does NOT require AdminImport:Enabled — normal users stage uploads).
        string sourceDir;
        if (run.StagingSessionId is Guid stagingSessionId)
        {
            sourceDir = ResolveStagingSourceDir(stagingSessionId);
        }
        else
        {
            var root = ResolveRoot(run.RootId);
            (sourceDir, _) = ResolveSourceDir(root.CanonicalPath, run.SourceRelativePath);
        }
        await EnsureTargetUserAsync(run.TargetUserId, cancellationToken);
        if (run.DestinationFolderId is { } destId)
        {
            var folder = await _folders.GetByIdAsync(destId, run.TargetUserId, cancellationToken);
            if (folder is null)
            {
                throw new AdminImportValidationException("Destination folder no longer exists.");
            }
        }

        var state = new ImportState
        {
            RunId = run.Id,
            JobId = jobContext.JobId,
            Job = jobContext,
            TargetUserId = run.TargetUserId,
            DestinationFolderId = run.DestinationFolderId,
            SkipPreviouslyDeleted = run.SkipPreviouslyDeleted,
            SkipExistingContent = run.SkipExistingContent,
            SourceDir = sourceDir,
            // Files created at/after this mark were imported by THIS run (used
            // to tell a resumed item apart from a true pre-existing conflict).
            RunCreatedAt = run.CreatedAt,
            // Slice 83: throttle config + per-job wall-clock budget.
            MaxRunMinutes = _options.Value.MaxRunMinutes,
            RunStartTimestamp = _clock.GetTimestamp(),
        };
        var timings = new FileCreateTimings();
        var throttle = new ImportThrottle(_options.Value, _clock);

        var startNow = _clock.GetUtcNow().UtcDateTime;
        await _db.AdminImportRuns.Where(r => r.Id == run.Id).ExecuteUpdateAsync(s => s
            .SetProperty(r => r.Status, AdminImportStatuses.Running)
            .SetProperty(r => r.ErrorSummary, (string?)null)
            // First slice stamps StartedAt; resume slices keep the original so
            // the run duration spans the whole (possibly paused) run.
            .SetProperty(r => r.StartedAt, r => r.StartedAt ?? startNow)
            .SetProperty(r => r.UpdatedAt, startNow), cancellationToken);

        // ---- phase 1: scan → persisted manifest --------------------------
        if (run.ScanCompletedAt is null)
        {
            await SetPhaseAsync(state.RunId, AdminImportPhases.Scanning, CancellationToken.None);
            // Crash-safe restart: a previous slice may have persisted a partial
            // manifest (no imports can have happened — the import phase only
            // starts after ScanCompletedAt). Rebuild it from scratch.
            await _db.AdminImportItems
                .Where(i => i.ImportRunId == state.RunId)
                .ExecuteDeleteAsync(CancellationToken.None);
            await ScanIntoManifestAsync(state, log, cancellationToken);
        }
        else
        {
            // Resume: items a dead worker left `importing` are retryable —
            // FileItemService.CreateAsync is atomic, so no partial FileItem can
            // exist for them. (A completed-then-crashed item re-detects as
            // `already-imported-this-run` through the duplicate-name path.)
            var reset = await _db.AdminImportItems
                .Where(i => i.ImportRunId == state.RunId && i.Status == AdminImportItemStatuses.Importing)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(i => i.Status, AdminImportItemStatuses.Pending)
                    .SetProperty(i => i.UpdatedAt, startNow), CancellationToken.None);
            if (reset > 0)
            {
                log($"admin-import: resume reset {reset} in-flight item(s) to pending");
            }
        }

        // ---- phase 2: import → drain pending items ------------------------
        if (!state.Cancelled)
        {
            await SetPhaseAsync(state.RunId, AdminImportPhases.Importing, CancellationToken.None);
            await ImportPendingItemsAsync(state, timings, throttle, log, cancellationToken);
        }

        DateTime? completedAt = _clock.GetUtcNow().UtcDateTime;
        string status;
        if (state.Cancelled)
        {
            status = AdminImportStatuses.Cancelled;
            // Freeze the unprocessed remainder so the manifest is coherent
            // ("what never ran" is visible, not stuck pending forever).
            await _db.AdminImportItems
                .Where(i => i.ImportRunId == state.RunId
                    && (i.Status == AdminImportItemStatuses.Pending
                        || i.Status == AdminImportItemStatuses.Importing))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(i => i.Status, AdminImportItemStatuses.Cancelled)
                    .SetProperty(i => i.UpdatedAt, completedAt.Value)
                    .SetProperty(i => i.CompletedAt, completedAt), CancellationToken.None);
        }
        else if (state.TimedOut)
        {
            // Pause: persist progress, leave items pending, and re-queue a fresh
            // job; the resume slice skips completed items by STATE (no re-walk).
            status = AdminImportStatuses.Paused;
            completedAt = null;
        }
        else
        {
            var anyFailed = await _db.AdminImportItems.AsNoTracking().AnyAsync(
                i => i.ImportRunId == state.RunId && i.Status == AdminImportItemStatuses.Failed,
                CancellationToken.None);
            status = anyFailed ? AdminImportStatuses.Partial : AdminImportStatuses.Succeeded;
        }

        // Terminal/pause state MUST persist even when the cancellation token is
        // already cancelled (cooperative cancel), so use a non-cancellable token.
        var counters = await FinalizeAsync(state, timings, status, completedAt, CancellationToken.None);

        // Surface the final counts as generic job progress too (so the Admin
        // Jobs dashboard shows a meaningful end state even for tiny imports).
        await jobContext.ReportProgressAsync(
            counters.Processed, counters.Scanned == 0 ? null : counters.Scanned,
            $"{status}: imported {counters.Imported}", CancellationToken.None);

        if (state.TimedOut)
        {
            // Null idempotency key: the CURRENT job is still 'running' under the
            // per-run key, so reusing it would dedup to this job and the run
            // would never resume. A keyless enqueue always creates a new job.
            var resumeJob = await _jobs.EnqueueAsync(
                JobTypes.AdminImport, new AdminImportJobPayload(state.RunId),
                idempotencyKey: null, cancellationToken: CancellationToken.None);
            // Re-point the run at the live job so cancellation/status
            // reconciliation target the slice that will actually execute
            // (cancelling a paused run must flag the RESUME job, not the
            // finished one).
            await _db.AdminImportRuns.Where(r => r.Id == state.RunId).ExecuteUpdateAsync(s => s
                .SetProperty(r => r.JobId, resumeJob.Id)
                .SetProperty(r => r.UpdatedAt, _clock.GetUtcNow().UtcDateTime), CancellationToken.None);
        }

        // Slice 92, part B/C: derivatives are no longer generated inline (by
        // default) — hand the finished run to the existing idempotent
        // media-derivatives backfill job, which generates any missing image
        // thumbnails/previews and video posters in the background.
        if (!_options.Value.GenerateDerivativesInline
            && counters.Imported > 0
            && status is AdminImportStatuses.Succeeded or AdminImportStatuses.Partial)
        {
            var derivativesJob = await _jobs.EnqueueAsync(
                JobTypes.MediaDerivativesBackfill,
                new MediaDerivativesBackfillJobPayload(),
                idempotencyKey: $"media-derivatives:import:{state.RunId:N}",
                cancellationToken: CancellationToken.None);
            log($"admin-import: enqueued derivatives job {derivativesJob.Id}");
        }

        // Slice 94 (metadata pipeline V2): full embedded extraction is also
        // off the critical path by default — the idempotent, version-aware
        // metadata backfill job enriches the imported blobs asynchronously
        // (EXIF/IPTC/XMP/GPS/dates), recomputing EffectiveDateTaken and the
        // GPS projection as it goes.
        if (!_options.Value.ExtractMetadataInline
            && counters.Imported > 0
            && status is AdminImportStatuses.Succeeded or AdminImportStatuses.Partial)
        {
            var metadataJob = await _jobs.EnqueueAsync(
                JobTypes.MetadataEmbeddedBackfill,
                new MetadataBackfillJobPayload(),
                idempotencyKey: $"metadata-backfill:import:{state.RunId:N}",
                cancellationToken: CancellationToken.None);
            log($"admin-import: enqueued metadata extraction job {metadataJob.Id}");
        }

        // Video metadata probing (ffprobe) is likewise idempotent + version-aware.
        // Only enqueued when a provider is configured; the global backfill picks
        // up every imported video blob still needing a probe.
        if (_media.Value.VideoMetadataProbeEnabled
            && counters.Imported > 0
            && status is AdminImportStatuses.Succeeded or AdminImportStatuses.Partial)
        {
            var videoMetaJob = await _jobs.EnqueueAsync(
                JobTypes.MetadataVideoBackfill,
                new VideoMetadataBackfillJobPayload(),
                idempotencyKey: $"metadata-video-backfill:import:{state.RunId:N}",
                cancellationToken: CancellationToken.None);
            log($"admin-import: enqueued video metadata job {videoMetaJob.Id}");
        }

        // Photo embeddings are profile-keyed and idempotent. A completed import
        // is the natural hand-off point: cheap media detection has already made
        // image blobs eligible, while the import critical path stays free of
        // model inference. The handler still performs the authoritative backend
        // readiness check, so a temporarily unavailable model remains a clean
        // operational state rather than corrupting per-blob status.
        var ai = _ai.Value;
        if (ai.Enabled
            && ai.ImageEmbeddingsEnabled
            && counters.Imported > 0
            && status is AdminImportStatuses.Succeeded or AdminImportStatuses.Partial)
        {
            var embeddingsJob = await _jobs.EnqueueAsync(
                JobTypes.AiPhotosEmbeddingsBackfill,
                new AiBackfillJobPayload(ProfileKey: ai.PhotoSimilarityProfileKey),
                idempotencyKey: $"ai-photo-embeddings:import:{state.RunId:N}",
                cancellationToken: CancellationToken.None);
            log($"admin-import: enqueued photo embeddings job {embeddingsJob.Id}");
        }

        // Slice 93: a staging-sourced run reports its outcome back to the
        // remote-upload session (and reclaims the staging directory after a
        // FULLY successful import when cleanup is enabled).
        if (run.StagingSessionId is Guid sessionToSync)
        {
            await SyncStagingSessionAsync(sessionToSync, status, CancellationToken.None);
        }

        log($"admin-import: {status} imported={counters.Imported} conflicts={counters.Conflicts} skipped={counters.Skipped} failed={counters.Failed}");

        // Slice 98: DB batch pipeline diagnostics — counts + milliseconds
        // only, never an identifier. Distinguishes the NEXT bottleneck
        // (lookup vs refcount vs save/commit vs conflict pre-check).
        if (state.DbBatches > 0)
        {
            log($"admin-import: db-batch size={_options.Value.DbBatchSize} batches={state.DbBatches} "
                + $"items={state.DbBatchItems} fallbacks={state.DbBatchFallbacks} "
                + $"new_blobs={state.NewBlobs} dup_blob_refs={state.DuplicateBlobRefs} "
                + $"lookup_ms={state.BlobDbLookupMillis} refcount_ms={state.BlobDbRefcountMillis} "
                + $"conflict_ms={state.ConflictCheckMillis} save_ms={state.SaveChangesMillis} "
                + $"saves={state.SaveChangesCount} commits={state.CommitCount}");
        }

        // Cooperative cancellation: tell the engine to mark the JOB cancelled
        // (it re-checks the persistent flag) so job + run never disagree.
        if (state.Cancelled)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    // Slice 93: where a staging-sourced run reads its files from. The session
    // directory is derived from the validated staging root — never persisted.
    private string ResolveStagingSourceDir(Guid stagingSessionId)
    {
        var opts = _staging.Value;
        if (string.IsNullOrWhiteSpace(opts.RootPath))
        {
            throw new AdminImportValidationException("Staging storage is not configured.");
        }
        var root = Path.GetFullPath(opts.RootPath.Trim())
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var dir = Path.Combine(root, stagingSessionId.ToString("N"), "files");
        if (!Directory.Exists(dir))
        {
            throw new AdminImportValidationException("The staging session files are missing.");
        }
        return dir;
    }

    // Maps a finished staging-sourced run back onto its session. Paused runs
    // stay `importing` (the resume slice continues); partial imports keep the
    // staging directory so failed files can be retried or discarded.
    // Slice 97 (bug 2): failed runs map to a FAILED session — without that
    // mapping a permanently-failed import left the session `importing`
    // forever, which blocked delete/discard and exempted it from expiry.
    private async Task SyncStagingSessionAsync(
        Guid sessionId, string runStatus, CancellationToken cancellationToken)
    {
        string? sessionStatus = runStatus switch
        {
            AdminImportStatuses.Succeeded => RemoteUploadSessionStatuses.Imported,
            AdminImportStatuses.Partial => RemoteUploadSessionStatuses.Imported,
            AdminImportStatuses.Cancelled => RemoteUploadSessionStatuses.Cancelled,
            AdminImportStatuses.Failed => RemoteUploadSessionStatuses.Failed,
            _ => null,
        };
        if (sessionStatus is null) return;

        var (errorCode, errorMessage) = runStatus switch
        {
            AdminImportStatuses.Partial => ("partial_import",
                "Some files failed to import; staging was kept for retry or discard."),
            AdminImportStatuses.Failed => ("import_failed",
                "The import failed; staged files were kept for retry or discard."),
            _ => ((string?)null, (string?)null),
        };
        var now = _clock.GetUtcNow().UtcDateTime;
        await _db.RemoteUploadSessions
            .Where(s => s.Id == sessionId && s.Status == RemoteUploadSessionStatuses.Importing)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, sessionStatus)
                .SetProperty(x => x.CompletedAt, now)
                .SetProperty(x => x.LastErrorCode, errorCode)
                .SetProperty(x => x.LastErrorMessage, errorMessage)
                .SetProperty(x => x.UpdatedAt, now), cancellationToken);

        if (runStatus == AdminImportStatuses.Succeeded && _staging.Value.CleanupEnabled)
        {
            try
            {
                var root = Path.GetFullPath(_staging.Value.RootPath.Trim())
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var dir = Path.Combine(root, sessionId.ToString("N"));
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best effort; the staging sweeper reclaims it at expiry.
                _logger?.LogWarning("staging: post-import cleanup failed ({Type}).", ex.GetType().Name);
            }
        }
    }

    // Wall-clock budget via the monotonic timestamp (so it is unaffected by
    // system-clock changes; _clock.GetTimestamp is only called here).
    private bool BudgetExpired(ImportState state)
        => state.MaxRunMinutes > 0
            && _clock.GetElapsedTime(state.RunStartTimestamp).TotalMinutes >= state.MaxRunMinutes;

    // ---- phase 1: scan ----------------------------------------------------

    // Stream-walks the source tree (iterative, explicit stack — bounded memory
    // regardless of tree size) and persists manifest items in batches. Records
    // files and directories as `pending`, and symlinks / special files /
    // unreadable entries / over-long paths as `skipped` with a stable category,
    // so every discovered entry is accounted for. Sets ScanCompletedAt + totals
    // on success; observes cooperative cancellation between batches.
    private async Task ScanIntoManifestAsync(ImportState state, Action<string> log, CancellationToken cancellationToken)
    {
        var batchSize = Math.Max(1, _options.Value.ScanBatchSize);
        var batch = new List<AdminImportItem>(batchSize);
        var ordinal = 0;
        var fileCount = 0;
        long totalBytes = 0;
        var dirCount = 0;
        string? lastRelative = null;

        var stack = new Stack<(string Dir, string Relative, int Depth)>();
        stack.Push((state.SourceDir, string.Empty, 0));

        AdminImportItem NewItem(string kind, string relative, string status) => new()
        {
            Id = Guid.NewGuid(),
            ImportRunId = state.RunId,
            Ordinal = ++ordinal,
            Kind = kind,
            RelativePath = relative,
            Status = status,
            CreatedAt = _clock.GetUtcNow().UtcDateTime,
            UpdatedAt = _clock.GetUtcNow().UtcDateTime,
        };

        AdminImportItem Skip(string relative, string category)
        {
            var item = NewItem(AdminImportItemKinds.File, relative, AdminImportItemStatuses.Skipped);
            item.FailureCategory = category;
            item.CompletedAt = item.CreatedAt;
            return item;
        }

        async Task FlushAsync()
        {
            if (batch.Count == 0) return;
            _db.AdminImportItems.AddRange(batch);
            await _db.SaveChangesAsync(CancellationToken.None);
            // Detach so a million-item scan doesn't accumulate tracked entities.
            foreach (var item in batch) _db.Entry(item).State = EntityState.Detached;
            batch.Clear();

            var now = _clock.GetUtcNow().UtcDateTime;
            await _db.AdminImportRuns.Where(r => r.Id == state.RunId).ExecuteUpdateAsync(s => s
                .SetProperty(r => r.ScannedFiles, fileCount)
                .SetProperty(r => r.TotalBytes, totalBytes)
                .SetProperty(r => r.TotalDirectories, dirCount)
                .SetProperty(r => r.CurrentRelativePath, lastRelative)
                .SetProperty(r => r.UpdatedAt, now), CancellationToken.None);
            await state.Job.ReportProgressAsync(
                fileCount, null, Truncate($"scanning: {lastRelative}", 200), CancellationToken.None);
        }

        while (stack.Count > 0)
        {
            // Cooperative cancellation between directories (cheap PK read).
            if (await IsJobCancelRequestedAsync(state.JobId)) { state.Cancelled = true; break; }
            cancellationToken.ThrowIfCancellationRequested();

            var (dir, relative, depth) = stack.Pop();
            foreach (var child in SafeEnumerate(dir))
            {
                var name = Path.GetFileName(child);
                var childRel = relative.Length == 0 ? name : $"{relative}/{name}";
                lastRelative = childRel;

                if (childRel.Length > MaxRelativePathLength)
                {
                    batch.Add(Skip(TruncatePath(childRel), AdminImportFailureCategories.PathTooLong));
                }
                else if (!TryGetAttributes(child, out var attrs))
                {
                    batch.Add(Skip(childRel, AdminImportFailureCategories.Unreadable));
                }
                else if (attrs.HasFlag(FileAttributes.ReparsePoint))
                {
                    // Never followed — neither symlinked dirs nor files.
                    batch.Add(Skip(childRel, AdminImportFailureCategories.SymbolicLink));
                }
                else if (attrs.HasFlag(FileAttributes.Directory))
                {
                    if (depth >= MaxDepth)
                    {
                        batch.Add(Skip(childRel, AdminImportFailureCategories.PathTooLong));
                    }
                    else
                    {
                        dirCount++;
                        batch.Add(NewItem(AdminImportItemKinds.Directory, childRel, AdminImportItemStatuses.Pending));
                        stack.Push((child, childRel, depth + 1));
                    }
                }
                else if (IsSpecialFile(attrs))
                {
                    batch.Add(Skip(childRel, AdminImportFailureCategories.SpecialFile));
                }
                else
                {
                    var item = NewItem(AdminImportItemKinds.File, childRel, AdminImportItemStatuses.Pending);
                    try
                    {
                        var info = new FileInfo(child);
                        item.SizeBytes = info.Length;
                        item.SourceModifiedAt = info.LastWriteTimeUtc;
                        totalBytes += info.Length;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        item.Status = AdminImportItemStatuses.Skipped;
                        item.FailureCategory = AdminImportFailureCategories.Unreadable;
                        item.CompletedAt = item.CreatedAt;
                    }
                    batch.Add(item);
                    fileCount++;
                }

                if (batch.Count >= batchSize)
                {
                    await FlushAsync();
                }
            }
        }

        await FlushAsync();

        if (!state.Cancelled)
        {
            var now = _clock.GetUtcNow().UtcDateTime;
            await _db.AdminImportRuns.Where(r => r.Id == state.RunId).ExecuteUpdateAsync(s => s
                .SetProperty(r => r.ScanCompletedAt, now)
                .SetProperty(r => r.ScannedFiles, fileCount)
                .SetProperty(r => r.TotalBytes, totalBytes)
                .SetProperty(r => r.TotalDirectories, dirCount)
                .SetProperty(r => r.CurrentRelativePath, (string?)null)
                .SetProperty(r => r.UpdatedAt, now), CancellationToken.None);
            log($"admin-import: scan complete files={fileCount} dirs={dirCount}");
        }
    }

    // ---- phase 2: import ----------------------------------------------------

    private async Task ImportPendingItemsAsync(
        ImportState state, FileCreateTimings timings, ImportThrottle throttle,
        Action<string> log, CancellationToken cancellationToken)
    {
        var batchSize = Math.Max(1, _options.Value.ItemBatchSize);
        // Destination folder cache: source dir relative path → logical folder id.
        // Bounded by the number of distinct directories in the page stream.
        var folderCache = new Dictionary<string, Guid?>(StringComparer.Ordinal)
        {
            [string.Empty] = state.DestinationFolderId,
        };
        var lastOrdinal = 0;

        while (true)
        {
            if (await IsJobCancelRequestedAsync(state.JobId)) { state.Cancelled = true; return; }
            cancellationToken.ThrowIfCancellationRequested();

            // Keyset page of pending manifest items (memory stays bounded for
            // arbitrarily large runs). AsNoTracking: items are updated via
            // ExecuteUpdate, never through the change tracker.
            var page = await _db.AdminImportItems.AsNoTracking()
                .Where(i => i.ImportRunId == state.RunId
                    && i.Status == AdminImportItemStatuses.Pending
                    && i.Ordinal > lastOrdinal)
                .OrderBy(i => i.Ordinal)
                .Take(batchSize)
                .ToListAsync(CancellationToken.None);
            if (page.Count == 0)
            {
                break;
            }

            // Slice 95: claim the WHOLE page in one statement (pending →
            // importing, attempts burned) instead of one round trip + commit
            // per file. Crash recovery is identical to per-file claiming: a
            // dead worker leaves the page `importing` and resume resets it to
            // pending (FileItem creation stays atomic, so a completed-but-
            // unmarked file re-detects as already-imported).
            var pageIds = page.Select(i => i.Id).ToList();
            var claimNow = _clock.GetUtcNow().UtcDateTime;
            var claimStart = Stopwatch.GetTimestamp();
            await _db.AdminImportItems
                .Where(i => pageIds.Contains(i.Id) && i.Status == AdminImportItemStatuses.Pending)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(i => i.Status, AdminImportItemStatuses.Importing)
                    .SetProperty(i => i.Attempts, i => i.Attempts + 1)
                    .SetProperty(i => i.UpdatedAt, claimNow), CancellationToken.None);
            state.ItemDbMillis += (long)Stopwatch.GetElapsedTime(claimStart).TotalMilliseconds;

            // Slice 98: with DB batching, files are STAGED (read + hash +
            // physical write + media sniff — no DB work) and persisted in
            // sub-batches of DbBatchSize with ONE transaction/commit each. The
            // staged list is always flushed before any graceful exit, so
            // pause/cancel keep the "at least one file of progress" and
            // "current work finishes atomically" guarantees — the atomic unit
            // simply grows from one file to one bounded batch.
            var batching = DbBatchingEnabled;
            var dbBatchSize = Math.Max(1, _options.Value.DbBatchSize);
            var staged = new List<StagedFile>(batching ? dbBatchSize : 0);

            async Task FlushStagedAsync()
            {
                if (staged.Count == 0)
                {
                    return;
                }
                var batch = new List<StagedFile>(staged);
                staged.Clear();
                await PersistStagedBatchAsync(state, batch, timings, throttle, folderCache, log);
            }

            foreach (var item in page)
            {
                lastOrdinal = item.Ordinal;

                // BEFORE each file: cooperative cancellation (the job's
                // persistent flag) → stop gracefully so ExecuteRunAsync
                // finalises the run as cancelled. A TOKEN cancellation without
                // the flag means worker shutdown / lost lease — let it
                // propagate so the job is reclaimed (no terminal write; staged
                // items stay `importing` and resume resets them to pending —
                // their physical bytes dedup by content on the retry, and no
                // refcount was committed, so nothing leaks). Both are checked
                // only BETWEEN files. Graceful exits flush the staged batch
                // and UNCLAIM the rest of the page (status + burned attempt),
                // so pause/cancel leaves exactly the state per-file claiming
                // left; a CRASH skips this and resume's importing→pending
                // reset recovers, with the extra burned attempt remaining the
                // conservative crash signal it always was.
                if (await IsJobCancelRequestedAsync(state.JobId))
                {
                    await FlushStagedAsync();
                    state.Cancelled = true;
                    await UnclaimAfterAsync(state, pageIds, item.Ordinal - 1);
                    return;
                }
                cancellationToken.ThrowIfCancellationRequested();

                if (item.Kind == AdminImportItemKinds.Directory)
                {
                    await ProcessDirectoryItemAsync(item, state, folderCache);
                    continue;
                }

                if (batching)
                {
                    var stagedFile = await TryStageFileAsync(item, state, timings, throttle, folderCache);
                    if (stagedFile is not null)
                    {
                        staged.Add(stagedFile);
                        if (staged.Count >= dbBatchSize)
                        {
                            await FlushStagedAsync();
                        }
                    }
                }
                else
                {
                    await ProcessFileItemAsync(item, state, timings, throttle, folderCache);
                }
                state.FilesProcessed++;

                // AFTER each file: cancellation + wall-clock budget. Checking
                // the budget here (rather than before) guarantees at least one
                // file of forward progress per slice — no zero-progress
                // requeue loop.
                if (await IsJobCancelRequestedAsync(state.JobId))
                {
                    await FlushStagedAsync();
                    state.Cancelled = true;
                    await UnclaimAfterAsync(state, pageIds, item.Ordinal);
                    return;
                }
                if (BudgetExpired(state))
                {
                    await FlushStagedAsync();
                    state.TimedOut = true;
                    await UnclaimAfterAsync(state, pageIds, item.Ordinal);
                    return;
                }

                // Inter-file delay so the import doesn't monopolise the box.
                // None: cooperative cancel is observed by the between-files
                // flag check, so a cancel during the delay must NOT throw and
                // bypass finalisation.
                await throttle.BetweenFilesAsync(CancellationToken.None);

                // Persist progress BEFORE yielding, then yield to the scheduler
                // so the API/web stays responsive. Staged work flushes first so
                // the surfaced counters only ever reflect committed truth.
                if (throttle.ShouldYield(state.FilesProcessed))
                {
                    await FlushStagedAsync();
                    var counters = await RefreshRunCountersAsync(state, item.RelativePath, CancellationToken.None);
                    log($"admin-import: imported={counters.Imported} conflicts={counters.Conflicts} skipped={counters.Skipped} failed={counters.Failed}");
                    await Task.Yield();
                }
            }

            // Page boundary: nothing staged may outlive its claimed page.
            await FlushStagedAsync();
        }
    }

    // Materialises the logical folder for a scanned source directory. This is
    // what preserves EMPTY directories; folders for files are (re)created on
    // demand via the same cache. The page-level claim (slice 95) already
    // burned the attempt.
    private async Task ProcessDirectoryItemAsync(
        AdminImportItem item, ImportState state, Dictionary<string, Guid?> folderCache)
    {
        var ok = await TryEnsureFolderAsync(item.RelativePath, state, folderCache);
        await MarkItemAsync(state, item.Id,
            ok ? AdminImportItemStatuses.Imported : AdminImportItemStatuses.Failed,
            failureCategory: ok ? null : AdminImportFailureCategories.FolderError,
            failureMessage: ok ? null : "The logical folder could not be created.");
    }

    private async Task ProcessFileItemAsync(
        AdminImportItem item, ImportState state, FileCreateTimings timings,
        ImportThrottle throttle, Dictionary<string, Guid?> folderCache, bool checkSkips = true)
    {
        // The item was already claimed (pending → importing, attempt burned)
        // by the page-level batch claim, so a crash mid-file is visible and
        // retried on resume without a per-file round trip here (slice 95).
        var prepared = await TryPrepareFileAsync(item, state, folderCache);
        if (prepared is null)
        {
            return;
        }
        var (absolute, folderId, name) = prepared.Value;

        // deleted-content-import-skip: the batched pipeline evaluates skips
        // up-front (FilterImportSkipsAsync), so `checkSkips` is only true on the
        // pure per-file path (no staging). There the content hash isn't known
        // yet, so hash the file first (streamed, no copy) and skip before any
        // CreateAsync / post-ingestion work.
        if (checkSkips && _skipEvaluator is not null
            && (state.SkipPreviouslyDeleted || state.SkipExistingContent))
        {
            var sha = await ComputeFileSha256Async(absolute);
            if (sha is not null)
            {
                var decisions = await _skipEvaluator.EvaluateBatchAsync(
                    state.TargetUserId, new[] { sha },
                    state.SkipPreviouslyDeleted, state.SkipExistingContent, CancellationToken.None);
                if (decisions.TryGetValue(sha, out var reason) && reason != ImportSkipReason.None)
                {
                    await MarkItemAsync(state, item.Id, reason == ImportSkipReason.PreviouslyDeleted
                        ? AdminImportItemStatuses.SkippedPreviouslyDeleted
                        : AdminImportItemStatuses.SkippedAlreadyPresent);
                    return;
                }
            }
        }
        try
        {
            var fs = new FileStream(
                absolute, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 81920, options: FileOptions.Asynchronous | FileOptions.SequentialScan);
            try
            {
                // Byte-rate limit large reads when configured (pass-through otherwise).
                var source = throttle.Wrap(fs);
                // Slice 91: a file in flight finishes ATOMICALLY — cooperative
                // cancellation (and worker shutdown) is observed only BETWEEN
                // files, never mid-CreateAsync. Passing CancellationToken.None
                // guarantees the item status and the committed FileItem stay
                // consistent on a mid-run cancel; the next between-files check
                // stops the loop.
                // Slice 92: derivatives are OFF the critical path by default —
                // GenerateDerivativesInline=false skips the inline thumbnail;
                // the backfill job (enqueued at run end) generates it instead.
                // Slice 94: likewise full embedded metadata extraction — by
                // default only the cheap detection facts are written inline
                // and the metadata.embedded.backfill job enriches async.
                var created = await _files.CreateAsync(
                    state.TargetUserId, folderId, name, GuessMimeType(name),
                    source, CancellationToken.None, timings,
                    generateSmallThumbnail: _options.Value.GenerateDerivativesInline,
                    extractEmbeddedMetadata: _options.Value.ExtractMetadataInline);
                await MarkItemAsync(state, item.Id, AdminImportItemStatuses.Imported, fileItemId: created.Id);
            }
            finally
            {
                await fs.DisposeAsync();
            }
        }
        catch (DuplicateFileNameException)
        {
            // No silent overwrite. Classify the collision so a benign resume
            // retry isn't reported as a true conflict: an active sibling whose
            // CreatedAt >= this run's CreatedAt was imported by THIS run
            // (resume); anything older pre-existed.
            var existing = await _db.FileItems
                .AsNoTracking()
                .Where(f => f.OwnerUserId == state.TargetUserId
                    && f.ParentFolderId == folderId
                    && f.DeletedAt == null
                    && f.Name == name)
                .Select(f => new { f.Id, f.CreatedAt })
                .FirstOrDefaultAsync(CancellationToken.None);

            if (existing is not null && existing.CreatedAt >= state.RunCreatedAt)
            {
                // The FileItem exists and belongs to this run → the item IS
                // imported; the category records that it was resume-detected.
                await MarkItemAsync(state, item.Id, AdminImportItemStatuses.Imported,
                    conflictCategory: AdminImportConflictCategories.AlreadyImportedThisRun,
                    fileItemId: existing.Id);
            }
            else
            {
                await MarkItemAsync(state, item.Id, AdminImportItemStatuses.Conflict,
                    conflictCategory: AdminImportConflictCategories.Preexisting);
            }
        }
        catch (QuotaExceededException)
        {
            await MarkItemAsync(state, item.Id, AdminImportItemStatuses.Failed,
                AdminImportFailureCategories.QuotaExceeded, "The target user's storage quota is exceeded.");
        }
        catch (UploadTooLargeException)
        {
            await MarkItemAsync(state, item.Id, AdminImportItemStatuses.Failed,
                AdminImportFailureCategories.TooLarge, "The file exceeds the maximum upload size.");
        }
        catch (ArgumentException)
        {
            await MarkItemAsync(state, item.Id, AdminImportItemStatuses.Failed,
                AdminImportFailureCategories.InvalidName, "The file name is not valid for import.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Canned message only — exception text can embed absolute paths.
            _logger?.LogWarning("admin-import: file import failed ({Type}).", ex.GetType().Name);
            await MarkItemAsync(state, item.Id, AdminImportItemStatuses.Failed,
                AdminImportFailureCategories.IoError, "The file could not be read or stored.");
        }
    }

    // Shared verification prologue of the per-file and batched import paths:
    // path re-resolution + containment, source verification (vanished /
    // type-swap / drift), and destination-folder materialisation. Marks the
    // item itself and returns null when the file must not be ingested.
    private async Task<(string Absolute, Guid? FolderId, string Name)?> TryPrepareFileAsync(
        AdminImportItem item, ImportState state, Dictionary<string, Guid?> folderCache)
    {
        // Re-resolve + re-verify the absolute path from the stored relative
        // path (defence in depth — the manifest was produced by our own scan,
        // but the filesystem may have changed since).
        string absolute;
        try
        {
            absolute = Path.GetFullPath(Path.Combine(
                state.SourceDir, item.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch (Exception)
        {
            await MarkItemAsync(state, item.Id, AdminImportItemStatuses.Failed,
                AdminImportFailureCategories.InvalidName, "The source path is not valid.");
            return null;
        }
        if (!IsWithin(absolute, state.SourceDir))
        {
            await MarkItemAsync(state, item.Id, AdminImportItemStatuses.Failed,
                AdminImportFailureCategories.InvalidName, "The source path escapes the import root.");
            return null;
        }

        // Source verification: vanished → skipped; swapped for a symlink /
        // special file, or size/mtime drifted since scan → failed (the admin
        // re-scans by starting a new run). 2s mtime tolerance covers coarse
        // filesystem timestamp granularity (e.g. FAT).
        if (!File.Exists(absolute))
        {
            await MarkItemAsync(state, item.Id, AdminImportItemStatuses.Skipped,
                AdminImportFailureCategories.SourceMissing, "The source file no longer exists.");
            return null;
        }
        if (!TryGetAttributes(absolute, out var attrs)
            || attrs.HasFlag(FileAttributes.ReparsePoint)
            || IsSpecialFile(attrs))
        {
            await MarkItemAsync(state, item.Id, AdminImportItemStatuses.Failed,
                AdminImportFailureCategories.SourceChanged, "The source entry changed type after the scan.");
            return null;
        }
        try
        {
            var info = new FileInfo(absolute);
            var mtimeDrift = item.SourceModifiedAt is { } scanned
                ? Math.Abs((info.LastWriteTimeUtc - scanned).TotalSeconds)
                : 0;
            if (info.Length != item.SizeBytes || mtimeDrift > 2)
            {
                await MarkItemAsync(state, item.Id, AdminImportItemStatuses.Failed,
                    AdminImportFailureCategories.SourceChanged,
                    "The source file changed after the scan; start a new run to rescan.");
                return null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await MarkItemAsync(state, item.Id, AdminImportItemStatuses.Failed,
                AdminImportFailureCategories.Unreadable, "The source file could not be read.");
            return null;
        }

        // Destination logical folder (cached per source directory).
        var directoryRelative = ParentRelative(item.RelativePath) ?? string.Empty;
        Guid? folderId;
        if (!folderCache.TryGetValue(directoryRelative, out folderId))
        {
            if (!await TryEnsureFolderAsync(directoryRelative, state, folderCache))
            {
                await MarkItemAsync(state, item.Id, AdminImportItemStatuses.Failed,
                    AdminImportFailureCategories.FolderError, "The destination folder could not be created.");
                return null;
            }
            folderId = folderCache[directoryRelative];
        }

        var name = item.RelativePath[(item.RelativePath.LastIndexOf('/') + 1)..];
        return (absolute, folderId, name);
    }

    // ---- slice 98: DB batch pipeline ---------------------------------------
    //
    // The per-file path costs 3-4 commits and ~10 round trips per file (blob
    // upsert, FileItem+metadata transaction, item mark) — ~332 ms/file on the
    // field baseline, ≈90% of the import wall clock. The batch pipeline stages
    // a sub-batch of files (read + hash + physical write + media sniff, no DB)
    // and then persists the WHOLE batch with one SHA lookup, one metadata
    // lookup, one sibling pre-check, set-based refcount increments, AddRange'd
    // inserts, batched item-status updates, and ONE commit. Unique indexes
    // remain the final authority: any batch-level DbUpdateException rolls the
    // transaction back (no refcount/row survives) and the batch retries
    // through the unchanged per-file path, which resolves dedup/adopt/conflict
    // per item exactly as before.

    private bool DbBatchingEnabled =>
        _blobStorage is not null
        && _options.Value.DbBatchSize > 1
        // Inline derivative/extraction configs flow through CreateAsync so the
        // batch path never needs to replicate them.
        && !_options.Value.GenerateDerivativesInline
        && !_options.Value.ExtractMetadataInline;

    // A file that passed verification and whose bytes are already in the blob
    // store, waiting for the batched DB phase. Never logged.
    // Streams a file through SHA-256 (lower-hex) without copying it. Used only
    // by the pure per-file import path's skip pre-check (the batched path
    // already has the hash from the physical write). Returns null on I/O error
    // so the caller falls through to the normal import (never skips on failure).
    private static async Task<string?> ComputeFileSha256Async(string absolutePath)
    {
        try
        {
            await using var fs = new FileStream(
                absolutePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 81920, options: FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = await SHA256.HashDataAsync(fs, CancellationToken.None);
            return Convert.ToHexStringLower(hash);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private sealed record StagedFile(
        AdminImportItem Item,
        Guid? FolderId,
        string Name,
        string MimeType,
        string Sha256,
        string StorageKey,
        long SizeBytes,
        int? Width,
        int? Height,
        string? DetectedFormat,
        string? DetectedContentType,
        bool IsImage,
        bool IsVideo);

    private sealed record ItemMark(
        Guid ItemId,
        string Status,
        string? FailureCategory = null,
        string? FailureMessage = null,
        string? ConflictCategory = null,
        Guid? FileItemId = null);

    private sealed record MetaFacts(
        Guid Id, Guid BlobObjectId, DateTime? DateTaken,
        double? GpsLatitude, double? GpsLongitude, double? GpsAltitude);

    // Verification + physical write + media sniff for one item. Marks the item
    // itself (skip/fail) and returns null when there is nothing to persist.
    private async Task<StagedFile?> TryStageFileAsync(
        AdminImportItem item, ImportState state, FileCreateTimings timings,
        ImportThrottle throttle, Dictionary<string, Guid?> folderCache)
    {
        var prepared = await TryPrepareFileAsync(item, state, folderCache);
        if (prepared is null)
        {
            return null;
        }
        var (absolute, folderId, rawName) = prepared.Value;

        try
        {
            // EXACTLY the per-file path's validation/normalisation.
            var name = FileItemService.ValidateAndTrimName(rawName);
            var mime = FileItemService.NormalizeMimeType(GuessMimeType(rawName));

            BlobWriteResult write;
            var fs = new FileStream(
                absolute, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 81920, options: FileOptions.Asynchronous | FileOptions.SequentialScan);
            try
            {
                // Physical, content-addressed, byte-idempotent write — no DB.
                // MaxUploadBytes is enforced while streaming, like uploads.
                write = await _blobStorage!.WriteAsync(throttle.Wrap(fs), CancellationToken.None);
            }
            finally
            {
                await fs.DisposeAsync();
            }
            timings.ReadMillis += write.ReadMillis;
            timings.HashMillis += write.HashMillis;
            timings.WriteMillis += write.WriteMillis;

            var detectStart = Stopwatch.GetTimestamp();
            var facts = await DetectStagedFactsAsync(write.StorageKey);
            timings.DetectMillis += (long)Stopwatch.GetElapsedTime(detectStart).TotalMilliseconds;

            return new StagedFile(
                item, folderId, name, mime,
                write.Sha256, write.StorageKey, write.SizeBytes,
                facts.Width, facts.Height, facts.Format, facts.ContentType,
                facts.IsImage, facts.IsVideo);
        }
        catch (UploadTooLargeException)
        {
            await MarkItemAsync(state, item.Id, AdminImportItemStatuses.Failed,
                AdminImportFailureCategories.TooLarge, "The file exceeds the maximum upload size.");
        }
        catch (ArgumentException)
        {
            await MarkItemAsync(state, item.Id, AdminImportItemStatuses.Failed,
                AdminImportFailureCategories.InvalidName, "The file name is not valid for import.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Canned message only — exception text can embed absolute paths.
            _logger?.LogWarning("admin-import: file staging failed ({Type}).", ex.GetType().Name);
            await MarkItemAsync(state, item.Id, AdminImportItemStatuses.Failed,
                AdminImportFailureCategories.IoError, "The file could not be read or stored.");
        }
        return null;
    }

    // Mirror of FileItemService.TryDetectImageFactsAsync reading straight from
    // the blob store by key (the BlobObject row does not exist yet at staging
    // time). Best-effort; any failure resolves to "no media facts".
    private async Task<(int? Width, int? Height, string? Format, string? ContentType, bool IsImage, bool IsVideo)>
        DetectStagedFactsAsync(string storageKey)
    {
        try
        {
            await using var stream = await _blobStorage!.OpenReadAsync(storageKey, CancellationToken.None);
            var info = await Image.IdentifyAsync(stream, CancellationToken.None);
            if (info is not null)
            {
                var format = info.Metadata.DecodedImageFormat;
                return (info.Width, info.Height, format?.Name, format?.DefaultMimeType, true, false);
            }
        }
        catch
        {
            // fall through to video detection
        }

        if (_videoDetector is not null)
        {
            try
            {
                await using var stream = await _blobStorage!.OpenReadAsync(storageKey, CancellationToken.None);
                var sig = await _videoDetector.InspectAsync(stream, CancellationToken.None);
                if (sig is not null)
                {
                    return (null, null, sig.Container, sig.ContentType, false, true);
                }
            }
            catch
            {
                // best-effort
            }
        }

        return (null, null, null, null, false, false);
    }

    // Persists one staged batch; on ANY batch-level DB failure, rolls back and
    // re-runs exactly this batch through the per-file path (the proven dedup/
    // adopt/conflict semantics), so only truly failing items fail.
    private async Task PersistStagedBatchAsync(
        ImportState state, List<StagedFile> batch, FileCreateTimings timings,
        ImportThrottle throttle, Dictionary<string, Guid?> folderCache, Action<string> log)
    {
        // deleted-content-import-skip: drop (and mark) skipped items BEFORE any
        // persistence — batched, so no N+1. Runs here (not in the core tx) so
        // the skip marks are committed independently of the batch write, and so
        // BOTH the batch path and its per-file fallback only ever see survivors.
        batch = await FilterImportSkipsAsync(state, batch);
        if (batch.Count == 0)
        {
            return;
        }

        state.DbBatches++;
        state.DbBatchItems += batch.Count;
        try
        {
            await PersistStagedBatchCoreAsync(state, batch, timings);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _db.ChangeTracker.Clear();
            state.DbBatchFallbacks++;
            // Counts + exception type only.
            _logger?.LogWarning(
                "admin-import: batch persist failed ({Type}); retrying {Count} item(s) per-file.",
                ex.GetType().Name, batch.Count);
            log($"admin-import: db-batch fallback engaged for {batch.Count} item(s)");
            foreach (var stagedFile in batch)
            {
                // Physical bytes are already present (byte-idempotent store);
                // CreateAsync re-runs the full per-item pipeline on them. Skips
                // were already applied above, so don't re-check here.
                await ProcessFileItemAsync(
                    stagedFile.Item, state, timings, throttle, folderCache, checkSkips: false);
            }
        }
    }

    // Batched evaluation of the two import skip options for a staged batch.
    // Marks each skipped item with its user-facing skip status and returns the
    // survivors to persist. A no-op (returns the batch unchanged) when neither
    // option is enabled or no evaluator is wired.
    private async Task<List<StagedFile>> FilterImportSkipsAsync(ImportState state, List<StagedFile> batch)
    {
        if (_skipEvaluator is null
            || (!state.SkipPreviouslyDeleted && !state.SkipExistingContent)
            || batch.Count == 0)
        {
            return batch;
        }

        var shas = batch.Select(s => s.Sha256).ToList();
        var decisions = await _skipEvaluator.EvaluateBatchAsync(
            state.TargetUserId, shas, state.SkipPreviouslyDeleted, state.SkipExistingContent,
            CancellationToken.None);
        if (decisions.Count == 0)
        {
            return batch;
        }

        var survivors = new List<StagedFile>(batch.Count);
        foreach (var staged in batch)
        {
            if (decisions.TryGetValue(staged.Sha256, out var reason) && reason != ImportSkipReason.None)
            {
                var status = reason == ImportSkipReason.PreviouslyDeleted
                    ? AdminImportItemStatuses.SkippedPreviouslyDeleted
                    : AdminImportItemStatuses.SkippedAlreadyPresent;
                await MarkItemAsync(state, staged.Item.Id, status);
            }
            else
            {
                survivors.Add(staged);
            }
        }
        return survivors;
    }

    private async Task PersistStagedBatchCoreAsync(
        ImportState state, List<StagedFile> batch, FileCreateTimings timings)
    {
        var owner = state.TargetUserId;
        var now = _clock.GetUtcNow().UtcDateTime;

        // 1) ONE round trip: which of the batch's SHAs already exist?
        var lookupStart = Stopwatch.GetTimestamp();
        var shas = batch.Select(s => s.Sha256).Distinct().ToList();
        var existingBlobs = await _db.BlobObjects.AsNoTracking()
            .Where(b => shas.Contains(b.Sha256))
            .Select(b => new { b.Id, b.Sha256 })
            .ToListAsync(CancellationToken.None);
        var blobIdBySha = existingBlobs.ToDictionary(b => b.Sha256, b => b.Id, StringComparer.Ordinal);

        // 2) ONE round trip: dedup facts for the existing blobs (effective
        // dates + GPS projection seeds), which also reveals pre-metadata-era
        // blobs that still need their row.
        var metaByBlob = new Dictionary<Guid, MetaFacts>();
        if (existingBlobs.Count > 0)
        {
            var existingIds = existingBlobs.Select(b => b.Id).ToList();
            var metaRows = await _db.BlobMetadata.AsNoTracking()
                .Where(m => existingIds.Contains(m.BlobObjectId))
                .Select(m => new MetaFacts(
                    m.Id, m.BlobObjectId, m.DateTaken, m.GpsLatitude, m.GpsLongitude, m.GpsAltitude))
                .ToListAsync(CancellationToken.None);
            foreach (var m in metaRows)
            {
                metaByBlob[m.BlobObjectId] = m;
            }
        }
        var lookupMillis = (long)Stopwatch.GetElapsedTime(lookupStart).TotalMilliseconds;
        state.BlobDbLookupMillis += lookupMillis;
        timings.BlobDbMillis += lookupMillis;

        // Test seam: lets a test inject a concurrent same-SHA writer into the
        // window between the batch's lookup and its inserts — the unique-index
        // collision that must trigger the per-file fallback. Never set in
        // production code.
        if (AfterBatchLookupForTests is not null)
        {
            await AfterBatchLookupForTests();
        }

        // 3) ONE round trip: sibling pre-check. Resolves the EXPECTED
        // conflicts (pre-existing names, resume re-encounters) up front; the
        // unique index still arbitrates anything this misses via the fallback.
        var conflictStart = Stopwatch.GetTimestamp();
        var names = batch.Select(s => s.Name).Distinct().ToList();
        var siblings = await _db.FileItems.AsNoTracking()
            .Where(f => f.OwnerUserId == owner && f.DeletedAt == null && names.Contains(f.Name))
            .Select(f => new { f.Id, f.Name, f.ParentFolderId, f.CreatedAt })
            .ToListAsync(CancellationToken.None);
        var siblingByKey = new Dictionary<(Guid? FolderId, string Name), (Guid Id, DateTime CreatedAt)>();
        foreach (var s in siblings)
        {
            siblingByKey[(s.ParentFolderId, s.Name)] = (s.Id, s.CreatedAt);
        }
        var conflictMillis = (long)Stopwatch.GetElapsedTime(conflictStart).TotalMilliseconds;
        state.ConflictCheckMillis += conflictMillis;
        timings.FileItemMillis += conflictMillis;

        var marks = new List<ItemMark>(batch.Count);
        var toInsert = new List<StagedFile>(batch.Count);
        var seenPairs = new HashSet<(Guid?, string)>();
        foreach (var sf in batch)
        {
            var key = (sf.FolderId, sf.Name);
            if (siblingByKey.TryGetValue(key, out var hit))
            {
                // Same classification as the per-file DuplicateFileNameException
                // handler: created by THIS run → resume re-encounter; older →
                // a true pre-existing conflict.
                marks.Add(hit.CreatedAt >= state.RunCreatedAt
                    ? new ItemMark(sf.Item.Id, AdminImportItemStatuses.Imported,
                        ConflictCategory: AdminImportConflictCategories.AlreadyImportedThisRun,
                        FileItemId: hit.Id)
                    : new ItemMark(sf.Item.Id, AdminImportItemStatuses.Conflict,
                        ConflictCategory: AdminImportConflictCategories.Preexisting));
                continue;
            }
            if (!seenPairs.Add(key))
            {
                // Two items resolving to the same destination cannot come from
                // our own scan (relative paths are unique) — defensive: first
                // wins, the rest are conflicts.
                marks.Add(new ItemMark(sf.Item.Id, AdminImportItemStatuses.Conflict,
                    ConflictCategory: AdminImportConflictCategories.Preexisting));
                continue;
            }
            toInsert.Add(sf);
        }

        // 4) ONE transaction: quota gate, folder liveness, set-based refcount
        // increments, AddRange'd inserts, batched item-status updates, ONE
        // commit. Mirrors CreateAsync's transactional invariants (advisory
        // owner lock first, quota inside the lock).
        var txStart = Stopwatch.GetTimestamp();
        long refcountMillis = 0, saveMillis = 0;
        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(CancellationToken.None);
            await TreeMutationLock.AcquireAsync(_db, owner, CancellationToken.None);

            // Quota cutoff in staged order — outcome identical to processing
            // the files one by one under the per-file quota check.
            var allowed = toInsert;
            var quota = _storage.Value.DefaultUserQuotaBytes;
            if (quota > 0)
            {
                var used = await _db.FileItems
                    .Where(f => f.OwnerUserId == owner)
                    .SumAsync(f => (long?)f.SizeBytes, CancellationToken.None) ?? 0L;
                allowed = new List<StagedFile>(toInsert.Count);
                foreach (var sf in toInsert)
                {
                    if (used + sf.SizeBytes > quota)
                    {
                        marks.Add(new ItemMark(sf.Item.Id, AdminImportItemStatuses.Failed,
                            AdminImportFailureCategories.QuotaExceeded,
                            "The target user's storage quota is exceeded."));
                    }
                    else
                    {
                        used += sf.SizeBytes;
                        allowed.Add(sf);
                    }
                }
            }

            // Destination folders must still be alive (per-file path verifies
            // per item inside its transaction).
            var folderIds = allowed
                .Where(s => s.FolderId is not null)
                .Select(s => s.FolderId!.Value)
                .Distinct()
                .ToList();
            if (folderIds.Count > 0)
            {
                var alive = await _db.Folders.AsNoTracking()
                    .Where(f => folderIds.Contains(f.Id) && f.OwnerUserId == owner && f.DeletedAt == null)
                    .Select(f => f.Id)
                    .ToListAsync(CancellationToken.None);
                if (alive.Count != folderIds.Count)
                {
                    var dead = folderIds.Except(alive).ToHashSet();
                    var survivors = new List<StagedFile>(allowed.Count);
                    foreach (var sf in allowed)
                    {
                        if (sf.FolderId is Guid fid && dead.Contains(fid))
                        {
                            marks.Add(new ItemMark(sf.Item.Id, AdminImportItemStatuses.Failed,
                                AdminImportFailureCategories.FolderError,
                                "The destination folder no longer exists."));
                        }
                        else
                        {
                            survivors.Add(sf);
                        }
                    }
                    allowed = survivors;
                }
            }

            // Set-based refcount increments for deduped blobs, grouped by
            // increment size (typically a single UPDATE for the whole batch).
            var refStart = Stopwatch.GetTimestamp();
            var duplicateGroups = allowed
                .Where(s => blobIdBySha.ContainsKey(s.Sha256))
                .GroupBy(s => s.Sha256)
                .GroupBy(g => g.Count(), g => blobIdBySha[g.Key]);
            foreach (var group in duplicateGroups)
            {
                var increment = group.Key;
                var ids = group.ToList();
                await _db.BlobObjects
                    .Where(b => ids.Contains(b.Id))
                    .ExecuteUpdateAsync(
                        s => s
                            .SetProperty(b => b.ReferenceCount, b => b.ReferenceCount + increment)
                            .SetProperty(b => b.PurgeEligibleAt, _ => null),
                        CancellationToken.None);
                state.DuplicateBlobRefs += increment * ids.Count;
            }
            refcountMillis = (long)Stopwatch.GetElapsedTime(refStart).TotalMilliseconds;

            // New blob rows — one per DISTINCT missing SHA (page-internal
            // dedup), refcount = number of batch files sharing the content —
            // plus the co-committed metadata row (deferred-extraction shape).
            foreach (var group in allowed
                .Where(s => !blobIdBySha.ContainsKey(s.Sha256))
                .GroupBy(s => s.Sha256, StringComparer.Ordinal))
            {
                var first = group.First();
                var blob = new BlobObject
                {
                    Id = Guid.NewGuid(),
                    Sha256 = group.Key,
                    SizeBytes = first.SizeBytes,
                    StorageKey = first.StorageKey,
                    ReferenceCount = group.Count(),
                    CreatedAt = now,
                };
                _db.BlobObjects.Add(blob);
                blobIdBySha[group.Key] = blob.Id;
                state.NewBlobs++;

                var meta = BuildStagedMetadata(blob.Id, first, now);
                _db.BlobMetadata.Add(meta);
                metaByBlob[blob.Id] = new MetaFacts(meta.Id, blob.Id, null, null, null, null);
            }

            // Pre-metadata-era existing blobs (rare): create their row now,
            // exactly like the per-file path would.
            foreach (var group in allowed
                .Where(s => !metaByBlob.ContainsKey(blobIdBySha[s.Sha256]))
                .GroupBy(s => blobIdBySha[s.Sha256]))
            {
                var first = group.First();
                var meta = BuildStagedMetadata(group.Key, first, now);
                _db.BlobMetadata.Add(meta);
                metaByBlob[group.Key] = new MetaFacts(meta.Id, group.Key, null, null, null, null);
            }

            // FileItems + GPS projections (dedup of already-extracted blobs).
            foreach (var sf in allowed)
            {
                var blobId = blobIdBySha[sf.Sha256];
                metaByBlob.TryGetValue(blobId, out var meta);
                var (effectiveDate, effectiveSource) =
                    EffectiveDateTakenSources.Compute(null, meta?.DateTaken, now);
                var fileItem = new FileItem
                {
                    Id = Guid.NewGuid(),
                    OwnerUserId = owner,
                    ParentFolderId = sf.FolderId,
                    BlobObjectId = blobId,
                    Name = sf.Name,
                    MimeType = sf.MimeType,
                    SizeBytes = sf.SizeBytes,
                    CreatedAt = now,
                    UpdatedAt = null,
                    DeletedAt = null,
                    Width = sf.Width,
                    Height = sf.Height,
                    EffectiveDateTaken = effectiveDate,
                    EffectiveDateTakenSource = effectiveSource,
                };
                _db.FileItems.Add(fileItem);
                if (meta is { GpsLatitude: double lat, GpsLongitude: double lon })
                {
                    _db.FileItemLocations.Add(new FileItemLocation
                    {
                        FileItemId = fileItem.Id,
                        OwnerUserId = owner,
                        Latitude = lat,
                        Longitude = lon,
                        Altitude = meta.GpsAltitude,
                        TakenAt = effectiveDate,
                        SourceBlobMetadataId = meta.Id,
                        CreatedAt = now,
                        UpdatedAt = now,
                    });
                }
                marks.Add(new ItemMark(sf.Item.Id, AdminImportItemStatuses.Imported,
                    FileItemId: fileItem.Id));
            }

            // Item statuses ride the SAME commit (attached stubs; EF batches
            // the UPDATEs), so "FileItem committed" and "item imported" can no
            // longer disagree even across a crash.
            foreach (var mark in marks)
            {
                var stub = new AdminImportItem { Id = mark.ItemId };
                _db.AdminImportItems.Attach(stub);
                stub.Status = mark.Status;
                stub.FailureCategory = mark.FailureCategory;
                stub.FailureMessage = mark.FailureMessage;
                stub.ConflictCategory = mark.ConflictCategory;
                stub.FileItemId = mark.FileItemId;
                stub.CompletedAt = now;
                stub.UpdatedAt = now;
            }

            var saveStart = Stopwatch.GetTimestamp();
            await _db.SaveChangesAsync(CancellationToken.None);
            await tx.CommitAsync(CancellationToken.None);
            saveMillis = (long)Stopwatch.GetElapsedTime(saveStart).TotalMilliseconds;
        });
        _db.ChangeTracker.Clear();

        state.BlobDbRefcountMillis += refcountMillis;
        state.SaveChangesMillis += saveMillis;
        state.SaveChangesCount++;
        state.CommitCount++;
        timings.BlobDbMillis += refcountMillis;
        timings.FileItemMillis +=
            (long)Stopwatch.GetElapsedTime(txStart).TotalMilliseconds - refcountMillis;
    }

    // Mirrors FileItemService.BuildBlobMetadataAsync's DEFERRED branch — the
    // batch pipeline never extracts inline (inline configs disable batching),
    // so the row carries detection facts only and stays `pending` for the
    // async metadata backfill. Keep the two in sync.
    private BlobMetadata BuildStagedMetadata(Guid blobObjectId, StagedFile sf, DateTime now)
    {
        // Malformed images can report a non-positive dimension; coerce to NULL
        // so we never violate ck_blob_metadata_{width,height}_positive (the file
        // still imports, just without dimensions). Shared with the per-file path.
        var (width, height, pixelCount) = BlobDimensions.Normalize(sf.Width, sf.Height);
        return new BlobMetadata
        {
            Id = Guid.NewGuid(),
            BlobObjectId = blobObjectId,
            SizeBytes = sf.SizeBytes,
            DetectedContentType = sf.DetectedContentType,
            MediaCategory = sf.IsImage
                ? MediaCategories.Image
                : sf.IsVideo
                    ? MediaCategories.Video
                    : MediaCategories.FromMimeType(sf.MimeType),
            DetectedFormat = sf.DetectedFormat,
            Width = width,
            Height = height,
            PixelCount = pixelCount,
            ThumbnailStatus = sf.IsImage ? MetadataStatuses.Pending : MetadataStatuses.Skipped,
            ExtractionStatus = sf.IsImage ? MetadataStatuses.Pending : MetadataStatuses.Skipped,
            CreatedAt = now,
        };
    }

    // Ensures the logical folder chain for a source-relative directory path,
    // caching every prefix. Returns false (and caches nothing) on failure.
    private async Task<bool> TryEnsureFolderAsync(
        string directoryRelative, ImportState state, Dictionary<string, Guid?> folderCache)
    {
        if (folderCache.ContainsKey(directoryRelative))
        {
            return true;
        }
        var segments = directoryRelative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        try
        {
            var folderStart = Stopwatch.GetTimestamp();
            // None: folder materialisation completes atomically; cooperative
            // cancel is observed between files, not mid-operation.
            var folderId = await _folders.EnsureFolderPathAsync(
                state.TargetUserId, state.DestinationFolderId, segments, CancellationToken.None);
            state.FolderMillis += (long)Stopwatch.GetElapsedTime(folderStart).TotalMilliseconds;
            folderCache[directoryRelative] = folderId;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or DuplicateFolderNameException or FolderNotFoundException)
        {
            _logger?.LogWarning("admin-import: folder creation failed ({Type}).", ex.GetType().Name);
            return false;
        }
    }

    // Slice 95: graceful-exit companion of the page-level batch claim —
    // returns the not-yet-processed remainder of the page (ordinals AFTER the
    // last handled item) to `pending` and un-burns the attempt the claim
    // charged, in one statement. Only items still `importing` match, so
    // already-terminal items are never touched.
    private async Task UnclaimAfterAsync(ImportState state, List<Guid> pageIds, int afterOrdinal)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var unclaimStart = Stopwatch.GetTimestamp();
        await _db.AdminImportItems
            .Where(i => pageIds.Contains(i.Id)
                && i.Ordinal > afterOrdinal
                && i.Status == AdminImportItemStatuses.Importing)
            .ExecuteUpdateAsync(s => s
                .SetProperty(i => i.Status, AdminImportItemStatuses.Pending)
                .SetProperty(i => i.Attempts, i => i.Attempts - 1)
                .SetProperty(i => i.UpdatedAt, now), CancellationToken.None);
        state.ItemDbMillis += (long)Stopwatch.GetElapsedTime(unclaimStart).TotalMilliseconds;
    }

    // Single chokepoint for item terminal writes (the durable per-file resume
    // record — deliberately kept per file). Attempts are burned by the
    // page-level claim. Timed into ItemDbMillis (slice 95).
    private async Task MarkItemAsync(
        ImportState state, Guid itemId, string status,
        string? failureCategory = null, string? failureMessage = null,
        string? conflictCategory = null, Guid? fileItemId = null)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var markStart = Stopwatch.GetTimestamp();
        await _db.AdminImportItems
            .Where(i => i.Id == itemId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(i => i.Status, status)
                .SetProperty(i => i.FailureCategory, failureCategory)
                .SetProperty(i => i.FailureMessage, failureMessage)
                .SetProperty(i => i.ConflictCategory, conflictCategory)
                .SetProperty(i => i.FileItemId, fileItemId)
                .SetProperty(i => i.CompletedAt, now)
                .SetProperty(i => i.UpdatedAt, now), CancellationToken.None);
        state.ItemDbMillis += (long)Stopwatch.GetElapsedTime(markStart).TotalMilliseconds;
    }

    // Authoritative cooperative-cancellation signal: the linked job's persistent
    // CancellationRequested flag (set by the generic Admin Jobs cancel endpoint
    // OR the import cancel endpoint, which delegates to it). Checked BETWEEN
    // files — cheap indexed PK lookup, negligible next to reading a file. Read
    // directly (not via JobContext) so it reflects ONLY cooperative cancel, never
    // worker shutdown (which is handled separately by the loop's token check).
    private Task<bool> IsJobCancelRequestedAsync(Guid jobId)
        => _db.BackgroundJobs.AsNoTracking()
            .Where(j => j.Id == jobId)
            .Select(j => j.CancellationRequested)
            .FirstOrDefaultAsync();

    private sealed record RunCounters(
        int Scanned, int Imported, int AlreadyImported, int Skipped, int Failed,
        int Conflicts, int Cancelled, long ImportedBytes,
        int SkippedPreviouslyDeleted, int SkippedAlreadyPresent)
    {
        public int Processed => Imported + Skipped + Failed + Conflicts + Cancelled
            + SkippedPreviouslyDeleted + SkippedAlreadyPresent;
    }

    // Slice 92: the manifest is authoritative — recompute the run row's
    // denormalized counters from item statuses (one indexed GROUP BY), flush
    // them, and report generic job progress. Returns the fresh counters.
    private async Task<RunCounters> RefreshRunCountersAsync(
        ImportState state, string? currentRelative, CancellationToken cancellationToken)
    {
        var counters = await ComputeCountersAsync(state.RunId, cancellationToken);
        var now = _clock.GetUtcNow().UtcDateTime;
        await _db.AdminImportRuns.Where(r => r.Id == state.RunId).ExecuteUpdateAsync(s => s
            .SetProperty(r => r.ImportedFiles, counters.Imported)
            .SetProperty(r => r.AlreadyImportedFiles, counters.AlreadyImported)
            .SetProperty(r => r.SkippedFiles, counters.Skipped)
            .SetProperty(r => r.FailedFiles, counters.Failed)
            .SetProperty(r => r.ConflictFiles, counters.Conflicts)
            .SetProperty(r => r.CancelledFiles, counters.Cancelled)
            .SetProperty(r => r.SkippedPreviouslyDeletedFiles, counters.SkippedPreviouslyDeleted)
            .SetProperty(r => r.SkippedAlreadyPresentFiles, counters.SkippedAlreadyPresent)
            .SetProperty(r => r.ImportedBytes, counters.ImportedBytes)
            .SetProperty(r => r.CurrentRelativePath, currentRelative)
            .SetProperty(r => r.UpdatedAt, now), cancellationToken);

        await state.Job.ReportProgressAsync(
            counters.Processed,
            counters.Scanned == 0 ? null : counters.Scanned,
            currentRelative is null ? null : Truncate(currentRelative, 200),
            cancellationToken);
        return counters;
    }

    private async Task<RunCounters> ComputeCountersAsync(Guid runId, CancellationToken cancellationToken)
    {
        var groups = await _db.AdminImportItems.AsNoTracking()
            .Where(i => i.ImportRunId == runId && i.Kind == AdminImportItemKinds.File)
            .GroupBy(i => i.Status)
            .Select(g => new { Status = g.Key, Count = g.Count(), Bytes = g.Sum(x => x.SizeBytes) })
            .ToListAsync(cancellationToken);
        int CountFor(string s) => groups.FirstOrDefault(g => g.Status == s)?.Count ?? 0;

        var alreadyImported = await _db.AdminImportItems.AsNoTracking().CountAsync(
            i => i.ImportRunId == runId
                && i.Kind == AdminImportItemKinds.File
                && i.ConflictCategory == AdminImportConflictCategories.AlreadyImportedThisRun,
            cancellationToken);

        return new RunCounters(
            Scanned: groups.Sum(g => g.Count),
            Imported: CountFor(AdminImportItemStatuses.Imported),
            AlreadyImported: alreadyImported,
            Skipped: CountFor(AdminImportItemStatuses.Skipped),
            Failed: CountFor(AdminImportItemStatuses.Failed),
            Conflicts: CountFor(AdminImportItemStatuses.Conflict),
            Cancelled: CountFor(AdminImportItemStatuses.Cancelled),
            ImportedBytes: groups
                .Where(g => g.Status == AdminImportItemStatuses.Imported)
                .Sum(g => g.Bytes),
            SkippedPreviouslyDeleted: CountFor(AdminImportItemStatuses.SkippedPreviouslyDeleted),
            SkippedAlreadyPresent: CountFor(AdminImportItemStatuses.SkippedAlreadyPresent));
    }

    private async Task SetPhaseAsync(Guid runId, string? phase, CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        await _db.AdminImportRuns.Where(r => r.Id == runId).ExecuteUpdateAsync(s => s
            .SetProperty(r => r.Phase, phase)
            .SetProperty(r => r.UpdatedAt, now), cancellationToken);
    }

    // Terminal/pause flush — item-derived counters + L2 timing totals +
    // CompletedAt (null when paused, so the run reads back as in progress).
    private async Task<RunCounters> FinalizeAsync(
        ImportState state, FileCreateTimings timings, string status, DateTime? completedAt,
        CancellationToken cancellationToken)
    {
        var counters = await ComputeCountersAsync(state.RunId, cancellationToken);
        var now = _clock.GetUtcNow().UtcDateTime;
        await _db.AdminImportRuns.Where(r => r.Id == state.RunId).ExecuteUpdateAsync(s => s
            .SetProperty(r => r.Status, status)
            .SetProperty(r => r.Phase, (string?)null)
            .SetProperty(r => r.ImportedFiles, counters.Imported)
            .SetProperty(r => r.AlreadyImportedFiles, counters.AlreadyImported)
            .SetProperty(r => r.SkippedFiles, counters.Skipped)
            .SetProperty(r => r.FailedFiles, counters.Failed)
            .SetProperty(r => r.ConflictFiles, counters.Conflicts)
            .SetProperty(r => r.CancelledFiles, counters.Cancelled)
            .SetProperty(r => r.SkippedPreviouslyDeletedFiles, counters.SkippedPreviouslyDeleted)
            .SetProperty(r => r.SkippedAlreadyPresentFiles, counters.SkippedAlreadyPresent)
            .SetProperty(r => r.ImportedBytes, counters.ImportedBytes)
            .SetProperty(r => r.CurrentRelativePath, (string?)null)
            .SetProperty(r => r.ReadMillis, (long?)timings.ReadMillis)
            .SetProperty(r => r.HashMillis, (long?)timings.HashMillis)
            .SetProperty(r => r.WriteMillis, (long?)timings.WriteMillis)
            .SetProperty(r => r.BlobDbMillis, (long?)timings.BlobDbMillis)
            .SetProperty(r => r.DetectMillis, (long?)timings.DetectMillis)
            .SetProperty(r => r.MetadataMillis, (long?)timings.MetadataMillis)
            .SetProperty(r => r.FileItemMillis, (long?)timings.FileItemMillis)
            .SetProperty(r => r.ThumbnailMillis, (long?)timings.ThumbnailMillis)
            .SetProperty(r => r.FolderMillis, (long?)state.FolderMillis)
            .SetProperty(r => r.ItemDbMillis, (long?)state.ItemDbMillis)
            .SetProperty(r => r.CompletedAt, completedAt)
            .SetProperty(r => r.UpdatedAt, now), cancellationToken);
        return counters;
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max];

    private static string TruncatePath(string relativePath)
        => relativePath.Length <= MaxRelativePathLength
            ? relativePath
            : relativePath[..MaxRelativePathLength];

    // ---- scan (preview) --------------------------------------------------

    private void ScanDirectory(string dir, int depth, ScanResult scan, CancellationToken cancellationToken)
    {
        if (depth > MaxDepth || scan.Total >= MaxScanEntries) { scan.Truncated = scan.Total >= MaxScanEntries; return; }

        foreach (var child in SafeEnumerate(dir, scan))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (scan.Total >= MaxScanEntries) { scan.Truncated = true; return; }
            if (!TryGetAttributes(child, out var attrs)) { scan.Unreadable++; continue; }
            if (attrs.HasFlag(FileAttributes.ReparsePoint)) { scan.SkippedSymlinks++; continue; }

            if (attrs.HasFlag(FileAttributes.Directory))
            {
                scan.TotalDirectories++;
                scan.Total++;
                ScanDirectory(child, depth + 1, scan, cancellationToken);
                continue;
            }

            if (IsSpecialFile(attrs)) { scan.SkippedUnsupported++; continue; }

            scan.Total++;
            scan.TotalFiles++;
            try { scan.TotalBytes += new FileInfo(child).Length; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { scan.Unreadable++; }
        }
    }

    // ---- path safety -----------------------------------------------------

    private sealed record ResolvedRoot(string RootId, string Label, string CanonicalPath);

    private List<ResolvedRoot> CanonicalRoots()
    {
        var opts = _options.Value;
        var result = new List<ResolvedRoot>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in opts.Roots)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            string canonical;
            try { canonical = Path.GetFullPath(raw.Trim()); }
            catch { continue; }
            canonical = canonical.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (canonical.Length == 0) continue;
            if (!seen.Add(canonical)) continue;
            // A root that overlaps an internal storage location is never usable.
            if (OverlapsInternalStorage(canonical)) continue;
            var label = Path.GetFileName(canonical);
            if (string.IsNullOrEmpty(label)) label = $"root-{seen.Count}";
            result.Add(new ResolvedRoot(RootIdOf(canonical), label, canonical));
        }
        return result;
    }

    private static string RootIdOf(string canonicalPath)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalPath));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    private ResolvedRoot ResolveRoot(string rootId)
    {
        var opts = _options.Value;
        if (!opts.Enabled)
        {
            throw new AdminImportUnavailableException(
                "Server-side import is disabled. Set AdminImport__Enabled=true and configure AdminImport__Roots__0.");
        }
        var roots = CanonicalRoots();
        if (roots.Count == 0)
        {
            throw new AdminImportUnavailableException(
                "Server-side import is enabled but no import roots are configured.");
        }
        var match = roots.FirstOrDefault(r => string.Equals(r.RootId, rootId, StringComparison.Ordinal));
        if (match is null)
        {
            throw new AdminImportValidationException("Unknown import root.");
        }
        return match;
    }

    // Resolve a safe relative subpath under a canonical root. Validates each
    // segment, rejects traversal/escape, refuses to traverse symbolic links,
    // and rejects internal storage locations. Returns (absoluteDir, normalizedRelative).
    private (string Dir, string NormalizedRelative) ResolveSourceDir(string canonicalRoot, string? relativePath)
    {
        var segments = SplitRelative(relativePath);
        var current = canonicalRoot;
        foreach (var seg in segments)
        {
            if (seg.Length == 0 || seg == "." || seg == ".." || seg.Length > MaxSegmentLength)
            {
                throw new AdminImportValidationException("Invalid path.");
            }
            current = Path.GetFullPath(Path.Combine(current, seg));
            if (!IsWithin(current, canonicalRoot))
            {
                throw new AdminImportValidationException("Path escapes the configured import root.");
            }
            if (!Directory.Exists(current))
            {
                throw new AdminImportValidationException("Directory not found.");
            }
            if (TryGetAttributes(current, out var attrs) && attrs.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new AdminImportValidationException("Symbolic links are not allowed.");
            }
        }

        if (!Directory.Exists(current))
        {
            throw new AdminImportValidationException("Directory not found.");
        }
        if (OverlapsInternalStorage(current))
        {
            throw new AdminImportValidationException("This location is part of NubArca internal storage and cannot be imported.");
        }

        var normalized = string.Join('/', segments);
        return (current, normalized);
    }

    private bool OverlapsInternalStorage(string candidate)
    {
        var storage = _storage.Value;
        foreach (var internalRoot in new[] { storage.RootPath, storage.EffectiveDerivedRootPath })
        {
            if (string.IsNullOrWhiteSpace(internalRoot)) continue;
            string canonical;
            try { canonical = Path.GetFullPath(internalRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
            catch { continue; }
            // Reject if the candidate is inside the storage root OR contains it.
            if (IsWithin(candidate, canonical) || IsWithin(canonical, candidate))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsWithin(string child, string parent)
    {
        if (string.Equals(child, parent, StringComparison.Ordinal)) return true;
        var prefix = parent.EndsWith(Path.DirectorySeparatorChar) ? parent : parent + Path.DirectorySeparatorChar;
        return child.StartsWith(prefix, StringComparison.Ordinal);
    }

    private static string[] SplitRelative(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return Array.Empty<string>();
        return relativePath
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string? ParentRelative(string normalizedRelative)
    {
        if (string.IsNullOrEmpty(normalizedRelative)) return null;
        var idx = normalizedRelative.LastIndexOf('/');
        return idx < 0 ? string.Empty : normalizedRelative[..idx];
    }

    // ---- helpers ---------------------------------------------------------

    private async Task EnsureTargetUserAsync(Guid targetUserId, CancellationToken cancellationToken)
    {
        var exists = await _db.Users.AsNoTracking().AnyAsync(u => u.Id == targetUserId, cancellationToken);
        if (!exists)
        {
            throw new AdminImportValidationException("Target user not found.");
        }
    }

    private void CountImmediate(string dir, out int dirCount, out int fileCount)
    {
        dirCount = 0;
        fileCount = 0;
        foreach (var child in SafeEnumerate(dir))
        {
            if (!TryGetAttributes(child, out var attrs)) continue;
            if (attrs.HasFlag(FileAttributes.ReparsePoint)) continue;
            if (attrs.HasFlag(FileAttributes.Directory)) dirCount++;
            else if (!IsSpecialFile(attrs)) fileCount++;
        }
    }

    private IEnumerable<string> SafeEnumerate(string dir, ScanResult? scan = null)
    {
        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (scan is not null) scan.Unreadable++;
            return Array.Empty<string>();
        }
        return entries;
    }

    private static bool TryGetAttributes(string path, out FileAttributes attributes)
    {
        try { attributes = File.GetAttributes(path); return true; }
        catch { attributes = default; return false; }
    }

    // Device/socket/FIFO etc. The Device flag is the only special-file marker
    // reliably surfaced by FileAttributes; the symlink case is handled
    // separately via ReparsePoint.
    private static bool IsSpecialFile(FileAttributes attrs)
        => attrs.HasFlag(FileAttributes.Device);

    private static string GuessMimeType(string name)
    {
        var ext = Path.GetExtension(name).ToLowerInvariant();
        return ext switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            ".tif" or ".tiff" => "image/tiff",
            ".heic" or ".heif" => "image/heif",
            ".mp4" or ".m4v" => "video/mp4",
            ".mov" => "video/quicktime",
            ".webm" => "video/webm",
            ".mkv" => "video/x-matroska",
            ".avi" => "video/x-msvideo",
            ".pdf" => "application/pdf",
            ".txt" => "text/plain",
            ".zip" => "application/zip",
            _ => "application/octet-stream",
        };
    }

    private sealed class ScanResult
    {
        public int TotalFiles;
        public int TotalDirectories;
        public long TotalBytes;
        public int SkippedSymlinks;
        public int SkippedUnsupported;
        public int Unreadable;
        public bool Truncated;
        public int Total;
    }

    // Slice 92: per-execution working state only — every COUNTER now derives
    // from admin_import_items (the manifest is the source of truth).
    private sealed class ImportState
    {
        public Guid RunId;
        // The executing job: Id for the cooperative-cancel flag read; Job for
        // generic progress reporting.
        public Guid JobId;
        public JobContext Job = null!;
        public Guid TargetUserId;
        public Guid? DestinationFolderId;
        // Validated absolute source dir for THIS execution (re-resolved from
        // config at job start; never persisted).
        public string SourceDir = string.Empty;
        public DateTime RunCreatedAt;
        // deleted-content-import-skip: import options for this run.
        public bool SkipPreviouslyDeleted;
        public bool SkipExistingContent;
        public long FolderMillis;
        // Slice 95: import-item bookkeeping (page claims + per-file terminal
        // marks) — the resume machinery's own DB cost, measured separately.
        public long ItemDbMillis;
        public bool Cancelled;
        // Slice 83: cooperative throttling + wall-clock budget. FilesProcessed
        // counts THIS slice only (yield cadence), not run totals.
        public int FilesProcessed;
        public bool TimedOut;
        public int MaxRunMinutes;
        public long RunStartTimestamp;

        // Slice 98: DB batch pipeline diagnostics (counts + milliseconds only;
        // logged at run end — never persisted, never any identifier).
        public int DbBatches;
        public int DbBatchFallbacks;
        public int DbBatchItems;
        public int NewBlobs;
        public int DuplicateBlobRefs;
        public long BlobDbLookupMillis;
        public long BlobDbRefcountMillis;
        public long ConflictCheckMillis;
        public long SaveChangesMillis;
        public int SaveChangesCount;
        public int CommitCount;
    }
}
