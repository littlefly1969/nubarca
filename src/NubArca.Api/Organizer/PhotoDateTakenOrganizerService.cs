using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NubArca.Api.Audit;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Folders;
using NubArca.Api.Jobs;

namespace NubArca.Api.Organizer;

// Phase 2: "Organize photos by date". Plans + executes purely-logical (DB-only)
// FileItem moves into DateTaken-based folders. Blobs, FileThumbnail rows, and
// user metadata are never touched — moving a file only reparents/renames the
// FileItem row. Dry-run is read-only; execution runs as a cooperative,
// checkpointed, owner-scoped background job and is idempotent (already-organized
// files are detected and skipped, never re-moved).
public sealed class PhotoDateTakenOrganizerService
{
    private const int DryRunSampleLimit = 20;
    private const int DryRunPageSize = 500;
    private const int ExecuteBatchSize = 200;

    private readonly AppDbContext _db;
    private readonly IFolderService _folders;
    private readonly IFileItemService _files;
    private readonly IAuditLogger _audit;
    private readonly IJobQueue _jobs;
    private readonly TimeProvider _clock;

    public PhotoDateTakenOrganizerService(
        AppDbContext db,
        IFolderService folders,
        IFileItemService files,
        IAuditLogger audit,
        IJobQueue jobs,
        TimeProvider clock)
    {
        _db = db;
        _folders = folders;
        _files = files;
        _audit = audit;
        _jobs = jobs;
        _clock = clock;
    }

    // Internal candidate projection: enough to resolve the effective date and
    // plan the move, never any storage internals. BlobObjectId is used only for
    // exact-duplicate detection within this service and never surfaced externally.
    private sealed record Candidate(
        Guid Id,
        string Name,
        Guid? ParentFolderId,
        DateTime CreatedAt,
        Guid BlobObjectId,
        DateTime? EmbeddedDate,
        string? EmbeddedSource,
        DateTime? UserOverride);

    // ------------------------------------------------------------------ dry-run

    public async Task<PhotoOrganizerDryRunResponse> DryRunAsync(
        Guid ownerUserId, OrganizerOptions options, CancellationToken cancellationToken)
    {
        var descendantIds = await ResolveRecursiveScopeAsync(ownerUserId, options, cancellationToken);

        var childCache = new Dictionary<(Guid?, string), Guid?>();
        var namesCache = new Dictionary<string, HashSet<string>>();
        var plannedNewFolderKeys = new HashSet<string>();
        var folderInfoCache = new Dictionary<Guid, (string Name, Guid? Parent)>();
        // In-memory claims for dry-run dedup simulation (pathKey + blob).
        var dryRunBlobClaims = new HashSet<(string, Guid)>();

        var rootKey = options.TargetRootFolderId?.ToString("N") ?? "root";
        var rootPath = await FolderLogicalPathAsync(options.TargetRootFolderId, folderInfoCache, cancellationToken);

        int candidate = 0, withDate = 0, missing = 0, toMove = 0, already = 0, skipMissing = 0, skipConflict = 0, exactDuplicate = 0;
        int srcUser = 0, srcOrig = 0, srcFallback = 0, srcFileCreated = 0, srcMissing = 0;
        var samples = new List<OrganizerSample>(DryRunSampleLimit);

        Guid? after = null;
        while (true)
        {
            var page = await ScopedCandidatesQuery(ownerUserId, options, descendantIds, after)
                .Take(DryRunPageSize)
                .ToListAsync(cancellationToken);
            if (page.Count == 0) break;
            after = page[^1].Id;

            foreach (var c in page)
            {
                candidate++;
                var resolution = PhotoDateTakenPlanner.Resolve(
                    c.UserOverride, c.EmbeddedDate, c.EmbeddedSource, c.CreatedAt, options.MissingBehavior);

                CountSource(resolution.Source, ref srcUser, ref srcOrig, ref srcFallback, ref srcFileCreated, ref srcMissing);
                if (HasCaptureDate(resolution.Source)) withDate++; else missing++;

                if (resolution.SkipMissing)
                {
                    skipMissing++;
                    AddSample(samples, c, resolution, currentPath: null, targetPath: null, OrganizerActions.SkipMissing);
                    continue;
                }

                var fullSegments = BuildFullSegments(options, PhotoDateTakenPlanner.TargetSegments(resolution, options.Template));
                var pathKey = rootKey + "/" + string.Join('/', fullSegments);
                var leafId = await ResolveExistingLeafForDryRunAsync(
                    ownerUserId, options.TargetRootFolderId, fullSegments, childCache, plannedNewFolderKeys, rootKey, cancellationToken);

                // Already organized: target folder exists and the file is in it.
                if (leafId is Guid existingLeaf && c.ParentFolderId == existingLeaf)
                {
                    if (dryRunBlobClaims.Contains((pathKey, c.BlobObjectId)))
                    {
                        // Another file with the same blob already claimed this slot — this copy is redundant.
                        exactDuplicate++;
                        AddSample(samples, c, resolution,
                            currentPath: await FileLogicalPathAsync(c, folderInfoCache, cancellationToken),
                            targetPath: CombinePath(rootPath, fullSegments),
                            OrganizerActions.ExactDuplicate);
                    }
                    else
                    {
                        dryRunBlobClaims.Add((pathKey, c.BlobObjectId));
                        already++;
                        AddSample(samples, c, resolution,
                            currentPath: await FileLogicalPathAsync(c, folderInfoCache, cancellationToken),
                            targetPath: CombinePath(rootPath, fullSegments),
                            OrganizerActions.Already);
                    }
                    continue;
                }

                // Exact-duplicate check: same blob already planned for this target path.
                if (dryRunBlobClaims.Contains((pathKey, c.BlobObjectId)))
                {
                    exactDuplicate++;
                    AddSample(samples, c, resolution,
                        currentPath: await FileLogicalPathAsync(c, folderInfoCache, cancellationToken),
                        targetPath: CombinePath(rootPath, fullSegments),
                        OrganizerActions.ExactDuplicate);
                    continue;
                }

                var names = await NamesForDryRunAsync(leafId, pathKey, namesCache, ownerUserId, cancellationToken);
                var finalName = PhotoDateTakenPlanner.PickName(c.Name, names, options.Conflict);
                if (finalName is null)
                {
                    skipConflict++;
                    AddSample(samples, c, resolution,
                        currentPath: await FileLogicalPathAsync(c, folderInfoCache, cancellationToken),
                        targetPath: CombinePath(rootPath, fullSegments),
                        OrganizerActions.SkipConflict);
                    continue;
                }

                dryRunBlobClaims.Add((pathKey, c.BlobObjectId)); // claim the slot for this blob+path
                names.Add(finalName); // reserve for later candidates in the plan
                toMove++;
                AddSample(samples, c, resolution,
                    currentPath: await FileLogicalPathAsync(c, folderInfoCache, cancellationToken),
                    targetPath: CombinePath(rootPath, fullSegments.Append(finalName)),
                    OrganizerActions.Move);
            }

            if (page.Count < DryRunPageSize) break;
        }

        var summary = new OrganizerSummary(
            CandidateCount: candidate,
            WithDateCount: withDate,
            MissingDateCount: missing,
            ToMoveCount: toMove,
            AlreadyOrganizedCount: already,
            SkippedMissingCount: skipMissing,
            SkippedConflictCount: skipConflict,
            ExactDuplicateRemovedCount: exactDuplicate,
            FoldersToCreateCount: plannedNewFolderKeys.Count,
            EstimatedOperations: toMove + plannedNewFolderKeys.Count,
            BySource: new OrganizerSourceCounts(srcUser, srcOrig, srcFallback, srcFileCreated, srcMissing));

        return new PhotoOrganizerDryRunResponse(summary, samples);
    }

    // ------------------------------------------------------------------ run creation

    public async Task<PhotoOrganizerRunResponse> StartRunAsync(
        Guid ownerUserId, OrganizerOptions options, CancellationToken cancellationToken)
    {
        var descendantIds = await ResolveRecursiveScopeAsync(ownerUserId, options, cancellationToken);
        var candidateCount = await ScopedCandidatesQuery(ownerUserId, options, descendantIds, null)
            .CountAsync(cancellationToken);

        var now = _clock.GetUtcNow().UtcDateTime;
        var run = new PhotoOrganizerRun
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerUserId,
            Kind = PhotoOrganizerKinds.DateTaken,
            Status = PhotoOrganizerStatuses.Queued,
            OptionsJson = JsonSerializer.Serialize(options),
            CandidateCount = candidateCount,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.PhotoOrganizerRuns.Add(run);
        await _db.SaveChangesAsync(cancellationToken);

        var job = await _jobs.EnqueueAsync(
            JobTypes.PhotoOrganizerDateTaken,
            new PhotoOrganizerJobPayload(run.Id),
            cancellationToken: cancellationToken);

        run.JobId = job.Id;
        await _db.SaveChangesAsync(cancellationToken);

        await _audit.LogAsync(
            ownerUserId, AuditActions.OrganizerRunStart, AuditEntityTypes.OrganizerRun, run.Id, null,
            new
            {
                candidateCount,
                template = OrganizerTemplateNames.ToWire(options.Template),
                scope = OrganizerScopeNames.ToWire(options.Scope),
            },
            cancellationToken);

        return new PhotoOrganizerRunResponse(run.Id, job.Id, run.Status);
    }

    public async Task<PhotoOrganizerRunStatusResponse?> GetRunStatusAsync(
        Guid ownerUserId, Guid runId, CancellationToken cancellationToken)
    {
        var run = await _db.PhotoOrganizerRuns.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == runId && r.OwnerUserId == ownerUserId, cancellationToken);
        if (run is null) return null;

        // Reconcile a non-terminal run with its job (the job engine may have
        // cancelled/failed the row before the handler could finalize the run —
        // e.g. cancelled before its first slice). The job is the tie-breaker.
        var effectiveStatus = run.Status;
        var cancellationPending = false;
        if (run.JobId is Guid jobId && !PhotoOrganizerStatuses.IsTerminal(run.Status))
        {
            var job = await _db.BackgroundJobs.AsNoTracking()
                .Where(j => j.Id == jobId)
                .Select(j => new { j.CancellationRequested, j.Status })
                .FirstOrDefaultAsync(cancellationToken);
            if (job is not null)
            {
                cancellationPending = job.CancellationRequested && !JobStatuses.IsTerminal(job.Status);
                effectiveStatus = job.Status switch
                {
                    JobStatuses.Cancelled => PhotoOrganizerStatuses.Cancelled,
                    JobStatuses.Failed => PhotoOrganizerStatuses.Failed,
                    _ => run.Status,
                };
            }
        }

        var options = ParseOptions(run.OptionsJson);
        return new PhotoOrganizerRunStatusResponse(
            run.Id, run.Kind, effectiveStatus, cancellationPending,
            options is null ? "yyyy/yyyy-MM-dd" : OrganizerTemplateNames.ToWire(options.Template),
            options?.TargetRootName,
            options is null ? "all" : OrganizerScopeNames.ToWire(options.Scope),
            run.CandidateCount, run.MovedCount, run.AlreadyOrganizedCount,
            run.SkippedMissingDateCount, run.SkippedConflictCount, run.ExactDuplicateRemovedCount,
            run.FailedCount, run.FoldersCreatedCount,
            run.ErrorSummary, run.CreatedAt, run.StartedAt, run.CompletedAt);
    }

    // ------------------------------------------------------------------ execution (job)

    public async Task ExecuteSliceAsync(Guid runId, JobContext context, CancellationToken cancellationToken)
    {
        var run = await _db.PhotoOrganizerRuns.FirstOrDefaultAsync(r => r.Id == runId, cancellationToken);
        if (run is null || PhotoOrganizerStatuses.IsTerminal(run.Status))
        {
            return; // run vanished or already finished — treat as success (no retry storm)
        }

        try
        {
            await ExecuteSliceCoreAsync(run, context, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw; // owned by the job engine (cooperative cancel / shutdown)
        }
        catch (Exception ex)
        {
            await TryMarkRunFailedAsync(run, context, ex);
            throw;
        }
    }

    private async Task ExecuteSliceCoreAsync(PhotoOrganizerRun run, JobContext context, CancellationToken cancellationToken)
    {
        var options = ParseOptions(run.OptionsJson)
            ?? throw new InvalidOperationException("Organizer run options are unreadable.");

        var checkpoint = PhotoOrganizerCheckpoint.TryParse(context.Checkpoint) ?? new PhotoOrganizerCheckpoint();

        if (run.Status == PhotoOrganizerStatuses.Queued)
        {
            run.Status = PhotoOrganizerStatuses.Running;
            run.StartedAt ??= _clock.GetUtcNow().UtcDateTime;
            run.UpdatedAt = _clock.GetUtcNow().UtcDateTime;
            await _db.SaveChangesAsync(cancellationToken);
        }

        var descendantIds = await ResolveRecursiveScopeAsync(run.OwnerUserId, options, cancellationToken);

        var moved = checkpoint.MovedTotal;
        var already = checkpoint.AlreadyTotal;
        var skipMissing = checkpoint.SkippedMissingTotal;
        var skipConflict = checkpoint.SkippedConflictTotal;
        var exactDuplicateRemoved = checkpoint.ExactDuplicateRemovedTotal;
        var failed = checkpoint.FailedTotal;
        var foldersCreated = checkpoint.FoldersCreatedTotal;
        var processedTotal = checkpoint.ProcessedTotal;
        var lastId = checkpoint.LastFileId;

        // Per-slice caches. DB state across slices reflects prior moves, so
        // re-querying a folder's names on first touch in a slice is correct.
        // namesCache is keyed by target path string (a not-yet-created folder
        // has no id) so files sharing a new folder share one reservation set.
        var childCache = new Dictionary<(Guid?, string), Guid?>();
        var namesCache = new Dictionary<string, HashSet<string>>();
        // (targetFolderId, blobObjectId) pairs claimed in this slice — prevents
        // N+1 DB duplicate checks for the common case of many duplicates together.
        var blobFolderClaims = new HashSet<(Guid, Guid)>();
        var rootKey = options.TargetRootFolderId?.ToString("N") ?? "root";

        long processedThisSlice = 0;
        var yielded = false;
        var moreWork = false;

        while (true)
        {
            var batch = await ScopedCandidatesQuery(run.OwnerUserId, options, descendantIds, lastId)
                .Take(ExecuteBatchSize + 1)
                .ToListAsync(cancellationToken);
            var more = batch.Count > ExecuteBatchSize;
            var page = more ? batch.Take(ExecuteBatchSize).ToList() : batch;
            if (page.Count == 0) break;

            foreach (var c in page)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var resolution = PhotoDateTakenPlanner.Resolve(
                    c.UserOverride, c.EmbeddedDate, c.EmbeddedSource, c.CreatedAt, options.MissingBehavior);

                if (resolution.SkipMissing)
                {
                    skipMissing++;
                }
                else
                {
                    var outcome = await ProcessFileAsync(
                        run.Id, run.OwnerUserId, c, resolution, options, rootKey, childCache, namesCache, blobFolderClaims, cancellationToken);
                    moved += outcome.Moved;
                    already += outcome.Already;
                    skipConflict += outcome.SkipConflict;
                    exactDuplicateRemoved += outcome.ExactDuplicateRemoved;
                    failed += outcome.Failed;
                    foldersCreated += outcome.FoldersCreated;
                    if (outcome.MovedRecord is PhotoOrganizerMove record)
                    {
                        // Defer the INSERT to the batch-end SaveChanges below.
                        // MoveToFolderAsync already committed the FileItem update;
                        // the audit record is secondary and does not need its own
                        // round-trip. The batch-end SaveChanges (or the next
                        // MoveToFolderAsync's own SaveChanges) will flush them.
                        _db.PhotoOrganizerMoves.Add(record);
                    }
                }

                processedTotal++;
                processedThisSlice++;
                lastId = c.Id;

                await context.ReportProgressAsync(
                    processedTotal, run.CandidateCount > 0 ? run.CandidateCount : null,
                    $"organizing ({moved} moved, {already} already, {exactDuplicateRemoved} deduped, {skipMissing + skipConflict} skipped, {failed} failed)",
                    cancellationToken);

                if (context.ShouldYield(processedThisSlice))
                {
                    yielded = true;
                    break;
                }
            }

            // Flush any deferred PhotoOrganizerMove audit rows accumulated this
            // batch (one round-trip per batch instead of one per moved file).
            await _db.SaveChangesAsync(cancellationToken);

            if (yielded)
            {
                moreWork = true;
                break;
            }
            if (!more)
            {
                moreWork = false;
                break;
            }
        }

        run.MovedCount = moved;
        run.AlreadyOrganizedCount = already;
        run.SkippedMissingDateCount = skipMissing;
        run.SkippedConflictCount = skipConflict;
        run.ExactDuplicateRemovedCount = exactDuplicateRemoved;
        run.FailedCount = failed;
        run.FoldersCreatedCount = foldersCreated;
        run.UpdatedAt = _clock.GetUtcNow().UtcDateTime;

        if (context.IsCancellationRequested)
        {
            run.Status = PhotoOrganizerStatuses.Cancelled;
            run.CompletedAt ??= _clock.GetUtcNow().UtcDateTime;
            await _db.SaveChangesAsync(cancellationToken);
            await _audit.LogAsync(
                run.OwnerUserId, AuditActions.OrganizerRunCancel, AuditEntityTypes.OrganizerRun, run.Id, null,
                Counts(run), CancellationToken.None);
            return;
        }

        if (moreWork)
        {
            await _db.SaveChangesAsync(cancellationToken);
            var nextCheckpoint = new PhotoOrganizerCheckpoint
            {
                ProcessedTotal = processedTotal,
                MovedTotal = moved,
                AlreadyTotal = already,
                SkippedMissingTotal = skipMissing,
                SkippedConflictTotal = skipConflict,
                ExactDuplicateRemovedTotal = exactDuplicateRemoved,
                FailedTotal = failed,
                FoldersCreatedTotal = foldersCreated,
                LastFileId = lastId,
            };
            var reason = context.HigherPriorityWaiting ? JobYieldReasons.HigherPriority : JobYieldReasons.SliceBudget;
            context.RequestContinuation(reason, nextCheckpoint.Serialize());
            return;
        }

        run.Status = failed > 0 ? PhotoOrganizerStatuses.Partial : PhotoOrganizerStatuses.Succeeded;
        run.CompletedAt ??= _clock.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync(cancellationToken);
        await _audit.LogAsync(
            run.OwnerUserId, AuditActions.OrganizerRunComplete, AuditEntityTypes.OrganizerRun, run.Id, null,
            Counts(run), cancellationToken);
    }

    private readonly record struct FileOutcome(
        int Moved, int Already, int SkipConflict, int Failed, int FoldersCreated, int ExactDuplicateRemoved, PhotoOrganizerMove? MovedRecord);

    // Process one candidate during execution. Folders are only created when a
    // move will actually happen (already-organized + conflict-skip + exact-duplicate
    // are decided first against existing state) so no empty target folders are created.
    private async Task<FileOutcome> ProcessFileAsync(
        Guid runId,
        Guid ownerUserId,
        Candidate c,
        DateResolution resolution,
        OrganizerOptions options,
        string rootKey,
        Dictionary<(Guid?, string), Guid?> childCache,
        Dictionary<string, HashSet<string>> namesCache,
        HashSet<(Guid, Guid)> blobFolderClaims,
        CancellationToken cancellationToken)
    {
        var fullSegments = BuildFullSegments(options, PhotoDateTakenPlanner.TargetSegments(resolution, options.Template));
        var pathKey = rootKey + "/" + string.Join('/', fullSegments);

        var leafId = await ResolveExistingLeafAsync(ownerUserId, options.TargetRootFolderId, fullSegments, childCache, cancellationToken);

        // ── Already organised ─────────────────────────────────────────────────
        if (leafId is Guid existingLeaf && c.ParentFolderId == existingLeaf)
        {
            // c is physically in its correct target folder, so it is a valid
            // survivor. It is redundant ONLY if ANOTHER active copy of the same
            // blob is really present in this folder. The in-memory claim alone is
            // ambiguous: an out-of-target duplicate that removed *itself* also
            // records a claim (see the exact-duplicate branch below), and a claim
            // added that way must NOT cause the last in-target copy to delete
            // itself — that would drop BOTH copies (data loss). Confirm against
            // active rows before deleting; the DB check runs only when a claim
            // exists (the rare duplicate case), preserving the N+1 avoidance.
            if (blobFolderClaims.Contains((existingLeaf, c.BlobObjectId)))
            {
                var anotherActiveInTarget = await _db.FileItems.AsNoTracking()
                    .AnyAsync(f => f.OwnerUserId == ownerUserId
                        && f.BlobObjectId == c.BlobObjectId
                        && f.ParentFolderId == existingLeaf
                        && f.DeletedAt == null
                        && f.Id != c.Id, cancellationToken);
                if (anotherActiveInTarget)
                {
                    // Automatic exact-duplicate cleanup — explicitly NOT a
                    // user-intent delete, so it never writes a tombstone.
                    await _files.SoftDeleteAsync(
                        ownerUserId, c.Id, cancellationToken, FileDeleteReason.OrganizerExactDedupe);
                    return new FileOutcome(0, 0, 0, 0, 0, 1, null);
                }
            }
            blobFolderClaims.Add((existingLeaf, c.BlobObjectId));
            return new FileOutcome(0, 1, 0, 0, 0, 0, null); // already organised (idempotent)
        }

        // ── Exact-duplicate check ─────────────────────────────────────────────
        // If the target folder already exists, check whether another active FileItem
        // with the same blob is already there (in-memory cache first, then DB for
        // idempotency across slice boundaries or survivors outside the current scope).
        if (leafId is Guid targetLeaf)
        {
            bool isDuplicate;
            if (blobFolderClaims.Contains((targetLeaf, c.BlobObjectId)))
            {
                isDuplicate = true;
            }
            else
            {
                isDuplicate = await _db.FileItems.AsNoTracking()
                    .AnyAsync(f => f.OwnerUserId == ownerUserId
                        && f.BlobObjectId == c.BlobObjectId
                        && f.ParentFolderId == targetLeaf
                        && f.DeletedAt == null
                        && f.Id != c.Id, cancellationToken);
                if (isDuplicate)
                    blobFolderClaims.Add((targetLeaf, c.BlobObjectId)); // cache to prevent N+1
            }
            if (isDuplicate)
            {
                // Automatic exact-duplicate cleanup — never a tombstone.
                await _files.SoftDeleteAsync(
                    ownerUserId, c.Id, cancellationToken, FileDeleteReason.OrganizerExactDedupe);
                return new FileOutcome(0, 0, 0, 0, 0, 1, null);
            }
        }

        // ── Name resolution ───────────────────────────────────────────────────
        var names = await NamesForExecuteAsync(ownerUserId, leafId, pathKey, namesCache, cancellationToken);
        var finalName = PhotoDateTakenPlanner.PickName(c.Name, names, options.Conflict);
        if (finalName is null)
        {
            return new FileOutcome(0, 0, 1, 0, 0, 0, null); // conflict, policy=skip
        }

        // ── Folder creation ───────────────────────────────────────────────────
        // If ResolveExistingLeafAsync already confirmed every segment exists (and
        // populated childCache), skip the redundant EnsureFolderPath re-validation
        // (which would do 1 root check + N segment queries for folders we know
        // are there). Only call it when the path is genuinely missing so folders
        // can be created.
        Guid targetFolderId;
        int created;
        if (leafId is Guid knownLeaf)
        {
            targetFolderId = knownLeaf;
            created = 0;
        }
        else
        {
            var (ensuredLeaf, foldersCreated) = await _folders.EnsureFolderPathWithCountAsync(
                ownerUserId, options.TargetRootFolderId, fullSegments, cancellationToken);
            if (ensuredLeaf is not Guid resolved)
            {
                return new FileOutcome(0, 0, 0, 1, foldersCreated, 0, null); // target root vanished
            }
            targetFolderId = resolved;
            created = foldersCreated;
        }

        // ── Move ──────────────────────────────────────────────────────────────
        try
        {
            var result = await _files.MoveToFolderAsync(ownerUserId, c.Id, targetFolderId, finalName, cancellationToken);
            if (result is null)
            {
                return new FileOutcome(0, 0, 0, 1, created, 0, null); // file vanished mid-run
            }
            names.Add(finalName);
            blobFolderClaims.Add((targetFolderId, c.BlobObjectId)); // C is now the survivor for this blob+folder
            return new FileOutcome(1, 0, 0, 0, created, 0, MoveRecord(runId, c, targetFolderId, finalName, resolution));
        }
        catch (DuplicateFileNameException)
        {
            // A concurrent change took the name — re-pick from fresh DB state once.
            var fresh = await ExistingFileNamesAsync(ownerUserId, targetFolderId, cancellationToken);
            var retryName = PhotoDateTakenPlanner.PickName(c.Name, fresh, options.Conflict);
            if (retryName is null)
            {
                return new FileOutcome(0, 0, 1, 0, created, 0, null);
            }
            try
            {
                var result = await _files.MoveToFolderAsync(ownerUserId, c.Id, targetFolderId, retryName, cancellationToken);
                if (result is null) return new FileOutcome(0, 0, 0, 1, created, 0, null);
                blobFolderClaims.Add((targetFolderId, c.BlobObjectId));
                return new FileOutcome(1, 0, 0, 0, created, 0, MoveRecord(runId, c, targetFolderId, retryName, resolution));
            }
            catch (Exception)
            {
                return new FileOutcome(0, 0, 0, 1, created, 0, null);
            }
        }
        catch (Exception)
        {
            return new FileOutcome(0, 0, 0, 1, created, 0, null); // a failed item never blocks the run
        }
    }

    private PhotoOrganizerMove MoveRecord(Guid runId, Candidate c, Guid targetFolderId, string finalName, DateResolution resolution)
        => new()
        {
            Id = Guid.NewGuid(),
            RunId = runId,
            FileItemId = c.Id,
            SourceParentFolderId = c.ParentFolderId,
            SourceName = c.Name,
            TargetParentFolderId = targetFolderId,
            TargetName = finalName,
            EffectiveDateTaken = resolution.BucketDate ?? c.CreatedAt,
            DateTakenSource = resolution.Source,
            CreatedAt = _clock.GetUtcNow().UtcDateTime,
        };

    // -------------------------------------------------------------- shared helpers

    // The full segment chain BELOW the target root folder = [targetRootName?] + date segments.
    private static IReadOnlyList<string> BuildFullSegments(OrganizerOptions options, IReadOnlyList<string> dateSegments)
    {
        if (options.TargetRootName is { Length: > 0 } root)
        {
            var list = new List<string>(dateSegments.Count + 1) { root };
            list.AddRange(dateSegments);
            return list;
        }
        return dateSegments;
    }

    private async Task<HashSet<Guid>> ResolveRecursiveScopeAsync(
        Guid ownerUserId, OrganizerOptions options, CancellationToken cancellationToken)
    {
        if (options.Scope == OrganizerScopeKind.FolderRecursive && options.FolderId is Guid root)
        {
            return await DescendantFolderIdsAsync(ownerUserId, root, cancellationToken);
        }
        return new HashSet<Guid>();
    }

    private async Task<HashSet<Guid>> DescendantFolderIdsAsync(
        Guid ownerUserId, Guid rootId, CancellationToken cancellationToken)
    {
        var all = new HashSet<Guid> { rootId };
        var frontier = new List<Guid> { rootId };
        while (frontier.Count > 0)
        {
            var children = await _db.Folders.AsNoTracking()
                .Where(f => f.OwnerUserId == ownerUserId
                    && f.DeletedAt == null
                    && f.ParentFolderId != null
                    && frontier.Contains(f.ParentFolderId.Value))
                .Select(f => f.Id)
                .ToListAsync(cancellationToken);
            frontier = children.Where(all.Add).ToList();
        }
        return all;
    }

    private IQueryable<Candidate> ScopedCandidatesQuery(
        Guid ownerUserId, OrganizerOptions options, HashSet<Guid> descendantIds, Guid? afterId)
    {
        var q = _db.FileItems.AsNoTracking()
            .Where(f => f.OwnerUserId == ownerUserId && f.DeletedAt == null);

        switch (options.Scope)
        {
            case OrganizerScopeKind.Selected:
                var ids = options.FileIds;
                q = q.Where(f => ids.Contains(f.Id));
                break;
            case OrganizerScopeKind.Folder:
                q = q.Where(f => f.ParentFolderId == options.FolderId);
                break;
            case OrganizerScopeKind.FolderRecursive:
                if (options.FolderId is Guid)
                {
                    q = q.Where(f => f.ParentFolderId != null && descendantIds.Contains(f.ParentFolderId.Value));
                }
                break; // null folder = whole tree → no parent filter (acts like All)
            case OrganizerScopeKind.MediaLibrary:
                // Slice 3: the media-library organizer scope skips both
                // folder-excluded photos AND per-file Excluded photos. Explicit
                // Selected/Folder/All scopes stay pure file-system operations
                // (an Excluded file is still a normal, organizable file there).
                q = q.Where(f => !_db.Folders.Any(d => d.Id == f.ParentFolderId && d.MediaPhotosExcluded)
                    && f.MediaLibraryState == Domain.MediaLibraryState.Active);
                break;
            case OrganizerScopeKind.All:
            default:
                break;
        }

        if (afterId is Guid after)
        {
            q = q.Where(f => f.Id.CompareTo(after) > 0);
        }

        // Use LEFT JOINs instead of three correlated subqueries per row.
        // BlobMetadata has a unique constraint on BlobObjectId (1:1 with
        // BlobObject). FileItemUserMetadata has a unique constraint on FileItemId.
        // Both JOINs produce at most one extra row per FileItem so ORDER BY +
        // keyset pagination remain correct. The MediaCategory filter is folded
        // into the WHERE so the old correlated ANY subquery is also eliminated.
        return from f in q
               join bm in _db.BlobMetadata
                   on f.BlobObjectId equals bm.BlobObjectId into bms
               from bm in bms.DefaultIfEmpty()
               where f.MimeType.StartsWith("image/") || bm.MediaCategory == "image"
               join um in _db.FileItemUserMetadata
                   on f.Id equals um.FileItemId into ums
               from um in ums.DefaultIfEmpty()
               orderby f.Id
               select new Candidate(
                   f.Id,
                   f.Name,
                   f.ParentFolderId,
                   f.CreatedAt,
                   f.BlobObjectId,
                   bm != null ? bm.DateTaken : null,
                   bm != null ? bm.DateTakenSource : null,
                   um != null ? um.DateTakenOverride : null);
    }

    // Read-only walk of the target folder chain; returns the leaf id if every
    // segment already exists, else null. Caches positive (found) lookups only;
    // missing segments are NOT cached so that a folder created mid-batch is
    // visible on the next file's lookup without poisoning the cache.
    private async Task<Guid?> ResolveExistingLeafAsync(
        Guid ownerUserId, Guid? rootId, IReadOnlyList<string> segments,
        Dictionary<(Guid?, string), Guid?> childCache, CancellationToken cancellationToken)
    {
        Guid? current = rootId;
        foreach (var seg in segments)
        {
            var key = (current, seg);
            if (!childCache.TryGetValue(key, out var childId))
            {
                childId = await _db.Folders.AsNoTracking()
                    .Where(f => f.OwnerUserId == ownerUserId && f.ParentFolderId == current && f.DeletedAt == null && f.Name == seg)
                    .Select(f => (Guid?)f.Id)
                    .FirstOrDefaultAsync(cancellationToken);
                if (childId is not null)
                    childCache[key] = childId; // only cache positive results
            }
            if (childId is null) return null;
            current = childId;
        }
        return current;
    }

    // Dry-run leaf resolution that ALSO records which folder prefixes don't yet
    // exist (counted once via plannedNewFolderKeys).
    private async Task<Guid?> ResolveExistingLeafForDryRunAsync(
        Guid ownerUserId, Guid? rootId, IReadOnlyList<string> segments,
        Dictionary<(Guid?, string), Guid?> childCache, HashSet<string> plannedNewFolderKeys, string rootKey,
        CancellationToken cancellationToken)
    {
        Guid? current = rootId;
        var existing = true;
        var prefix = rootKey;
        foreach (var seg in segments)
        {
            prefix += "/" + seg;
            if (existing)
            {
                var key = (current, seg);
                if (!childCache.TryGetValue(key, out var childId))
                {
                    childId = await _db.Folders.AsNoTracking()
                        .Where(f => f.OwnerUserId == ownerUserId && f.ParentFolderId == current && f.DeletedAt == null && f.Name == seg)
                        .Select(f => (Guid?)f.Id)
                        .FirstOrDefaultAsync(cancellationToken);
                    childCache[key] = childId;
                }
                if (childId is Guid found) { current = found; continue; }
                existing = false;
            }
            plannedNewFolderKeys.Add(prefix);
        }
        return existing ? current : null;
    }

    private async Task<HashSet<string>> NamesForExecuteAsync(
        Guid ownerUserId, Guid? leafId, string pathKey, Dictionary<string, HashSet<string>> namesCache, CancellationToken cancellationToken)
    {
        if (namesCache.TryGetValue(pathKey, out var cached)) return cached;
        var names = leafId is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : await ExistingFileNamesAsync(ownerUserId, leafId.Value, cancellationToken);
        namesCache[pathKey] = names;
        return names;
    }

    private async Task<HashSet<string>> NamesForDryRunAsync(
        Guid? leafId, string pathKey, Dictionary<string, HashSet<string>> namesCache,
        Guid ownerUserId, CancellationToken cancellationToken)
    {
        if (namesCache.TryGetValue(pathKey, out var cached)) return cached;
        var names = leafId is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : await ExistingFileNamesAsync(ownerUserId, leafId.Value, cancellationToken);
        namesCache[pathKey] = names;
        return names;
    }

    private async Task<HashSet<string>> ExistingFileNamesAsync(
        Guid ownerUserId, Guid folderId, CancellationToken cancellationToken)
    {
        var names = await _db.FileItems.AsNoTracking()
            .Where(f => f.OwnerUserId == ownerUserId && f.ParentFolderId == folderId && f.DeletedAt == null)
            .Select(f => f.Name)
            .ToListAsync(cancellationToken);
        return new HashSet<string>(names, StringComparer.Ordinal);
    }

    private async Task<string> FileLogicalPathAsync(
        Candidate c, Dictionary<Guid, (string Name, Guid? Parent)> cache, CancellationToken cancellationToken)
    {
        var folderPath = await FolderLogicalPathAsync(c.ParentFolderId, cache, cancellationToken);
        return folderPath.Length > 0 ? CombinePath(folderPath, new[] { c.Name }) : "/" + c.Name;
    }

    private async Task<string> FolderLogicalPathAsync(
        Guid? folderId, Dictionary<Guid, (string Name, Guid? Parent)> cache, CancellationToken cancellationToken)
    {
        if (folderId is null) return string.Empty;
        var parts = new List<string>();
        var current = folderId;
        var guard = 0;
        while (current is Guid id && guard++ < 256)
        {
            if (!cache.TryGetValue(id, out var info))
            {
                var row = await _db.Folders.AsNoTracking()
                    .Where(f => f.Id == id)
                    .Select(f => new { f.Name, f.ParentFolderId })
                    .FirstOrDefaultAsync(cancellationToken);
                if (row is null) break;
                info = (row.Name, row.ParentFolderId);
                cache[id] = info;
            }
            parts.Add(info.Name);
            current = info.Parent;
        }
        parts.Reverse();
        return "/" + string.Join('/', parts);
    }

    private void AddSample(
        List<OrganizerSample> samples, Candidate c, DateResolution resolution,
        string? currentPath, string? targetPath, string action)
    {
        if (samples.Count >= DryRunSampleLimit) return;
        samples.Add(new OrganizerSample(
            c.Name,
            currentPath ?? "/" + c.Name,
            targetPath ?? string.Empty,
            resolution.BucketDate,
            resolution.Source,
            action));
    }

    private static string CombinePath(string prefix, IEnumerable<string> parts)
    {
        var head = prefix.Trim('/');
        var all = (head.Length > 0 ? head.Split('/') : Array.Empty<string>()).Concat(parts);
        return "/" + string.Join('/', all);
    }

    private static bool HasCaptureDate(string source) => source is
        PhotoOrganizerDateSources.UserOverride
        or PhotoOrganizerDateSources.MetadataOriginal
        or PhotoOrganizerDateSources.MetadataFallback;

    private static void CountSource(
        string source, ref int user, ref int orig, ref int fallback, ref int fileCreated, ref int missing)
    {
        switch (source)
        {
            case PhotoOrganizerDateSources.UserOverride: user++; break;
            case PhotoOrganizerDateSources.MetadataOriginal: orig++; break;
            case PhotoOrganizerDateSources.MetadataFallback: fallback++; break;
            case PhotoOrganizerDateSources.FileCreatedFallback: fileCreated++; break;
            default: missing++; break;
        }
    }

    private static object Counts(PhotoOrganizerRun run) => new
    {
        moved = run.MovedCount,
        alreadyOrganized = run.AlreadyOrganizedCount,
        skippedMissingDate = run.SkippedMissingDateCount,
        skippedConflict = run.SkippedConflictCount,
        exactDuplicateRemoved = run.ExactDuplicateRemovedCount,
        failed = run.FailedCount,
        foldersCreated = run.FoldersCreatedCount,
    };

    private static OrganizerOptions? ParseOptions(string json)
    {
        try { return JsonSerializer.Deserialize<OrganizerOptions>(json); }
        catch (JsonException) { return null; }
    }

    private async Task TryMarkRunFailedAsync(PhotoOrganizerRun run, JobContext context, Exception failure)
    {
        try
        {
            var job = await _db.BackgroundJobs.AsNoTracking()
                .Where(j => j.Id == context.JobId)
                .Select(j => new { j.Attempts, j.MaxAttempts })
                .FirstOrDefaultAsync(CancellationToken.None);
            var permanent = job is null || job.Attempts >= job.MaxAttempts;
            if (!permanent) return;

            var summary = $"Unexpected failure ({failure.GetType().Name}).";
            var now = _clock.GetUtcNow().UtcDateTime;
            await _db.PhotoOrganizerRuns
                .Where(r => r.Id == run.Id && !PhotoOrganizerStatuses.IsTerminal(r.Status))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.Status, PhotoOrganizerStatuses.Failed)
                    .SetProperty(r => r.ErrorSummary, summary)
                    .SetProperty(r => r.CompletedAt, r => r.CompletedAt ?? now)
                    .SetProperty(r => r.UpdatedAt, now), CancellationToken.None);

            await _audit.LogAsync(
                run.OwnerUserId, AuditActions.OrganizerRunFail, AuditEntityTypes.OrganizerRun, run.Id, null,
                new { error = summary }, CancellationToken.None);
        }
        catch
        {
            // best-effort terminal bookkeeping
        }
    }
}
