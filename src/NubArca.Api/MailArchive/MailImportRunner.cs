using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NubArca.Api.Audit;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Mail;
using NubArca.Api.Files;
using NubArca.Api.Folders;
using NubArca.Api.Ingestion;
using NubArca.Api.Jobs;
using NubArca.Api.Storage;
using NubArca.Api.Uploads;

namespace NubArca.Api.MailArchive;

/// <summary>
/// Executes one MailImportRun: working copy → probe → scan (manifest) →
/// import (per message) → finalize. Every step is resumable from persisted
/// state, so a crash, a cancellation or a slice boundary never loses or
/// duplicates work. See docs/mailarchive/contract.md §3 and §6.
/// </summary>
public sealed class MailImportRunner
{
    private const int PageSize = 100;
    private const int CounterFlushEvery = 20;
    private static readonly TimeSpan AttachmentClaimLease = TimeSpan.FromMinutes(30);

    private readonly AppDbContext _db;
    private readonly IPstEngine _engine;
    private readonly MailWorkspace _workspace;
    private readonly IFileItemService _files;
    private readonly IFolderService _folders;
    private readonly IUploadIdempotencyService _idempotency;
    private readonly IBlobService _blobs;
    private readonly IPostIngestionMediaPipelineService _pipeline;
    private readonly IAuditLogger _audit;
    private readonly MailArchiveOptions _options;
    private readonly string? _pepper;
    private readonly TimeProvider _clock;
    private readonly ILogger<MailImportRunner> _logger;

    public MailImportRunner(
        AppDbContext db,
        IPstEngine engine,
        MailWorkspace workspace,
        IFileItemService files,
        IFolderService folders,
        IUploadIdempotencyService idempotency,
        IBlobService blobs,
        IPostIngestionMediaPipelineService pipeline,
        IAuditLogger audit,
        IOptions<MailArchiveOptions> options,
        IOptions<DeletedContentOptions> deletedContent,
        TimeProvider clock,
        ILogger<MailImportRunner> logger)
    {
        _db = db;
        _engine = engine;
        _workspace = workspace;
        _files = files;
        _folders = folders;
        _idempotency = idempotency;
        _blobs = blobs;
        _pipeline = pipeline;
        _audit = audit;
        _options = options.Value;
        _pepper = deletedContent.Value.Pepper;
        _clock = clock;
        _logger = logger;
    }

    public async Task ExecuteAsync(Guid runId, JobContext context, CancellationToken cancellationToken)
    {
        var run = await _db.MailImportRuns.FirstOrDefaultAsync(r => r.Id == runId, cancellationToken);
        if (run is null || !MailImportRunStatuses.IsActive(run.Status))
        {
            return; // finished or gone: a redelivered job is a no-op
        }
        var archive = await _db.MailArchives.FirstAsync(a => a.Id == run.ArchiveId, cancellationToken);

        run.Status = MailImportRunStatuses.Running;
        run.StartedAt ??= Now;
        run.UpdatedAt = Now;
        await _db.SaveChangesAsync(cancellationToken);

        try
        {
            await SetPhaseAsync(run, MailImportPhases.Preparing, cancellationToken);
            var pstPath = await EnsureWorkingCopyAsync(run, archive, cancellationToken);

            if (archive.Format is null)
            {
                var probe = await _engine.ProbeAsync(pstPath, cancellationToken);
                archive.Format = probe.Format;
                archive.UpdatedAt = Now;
                await _db.SaveChangesAsync(cancellationToken);
            }

            if (archive.ScanCompletedAt is null)
            {
                await SetPhaseAsync(run, MailImportPhases.Scanning, cancellationToken);
                await ScanAsync(run, archive, pstPath, cancellationToken);
            }

            run.TotalMessages = await _db.MailImportItems.CountAsync(i => i.ArchiveId == archive.Id, cancellationToken);
            await SetPhaseAsync(run, MailImportPhases.Importing, cancellationToken);

            var checkpoint = MailImportCheckpoint.Parse(context.Checkpoint);
            var finished = await ImportAsync(run, archive, pstPath, checkpoint, context, cancellationToken);
            if (!finished)
            {
                if (await IsCooperativelyCancelledAsync(context))
                {
                    await MarkCancelledAsync(run);
                    throw new OperationCanceledException("MailArchive import cancelled.");
                }
                context.RequestContinuation(
                    context.HigherPriorityWaiting ? JobYieldReasons.HigherPriority : JobYieldReasons.SliceBudget,
                    checkpoint.ToJson());
                return;
            }

            await FinalizeAsync(run, archive, cancellationToken);
        }
        catch (MailImportFatalException ex)
        {
            await FailRunAsync(run, ex.Code, ex.Message);
        }
        catch (PstEngineException ex) when (ex.Code is "not_pst" or "unsupported_format" or "corrupt_header" or "encrypted" or "protocol_mismatch")
        {
            await FailRunAsync(run, ex.Code, PstProcessMessage(ex));
        }
        catch (OperationCanceledException)
        {
            // A cooperative cancel ends the run; a worker shutdown or a lost
            // lease leaves it running so the job's next owner resumes it.
            if (await IsCooperativelyCancelledAsync(context))
            {
                await MarkCancelledAsync(run);
            }
            throw;
        }
    }

    // ---- preparing ----------------------------------------------------------

    private async Task<string> EnsureWorkingCopyAsync(MailImportRun run, Domain.Mail.MailArchive archive, CancellationToken cancellationToken)
    {
        if (!_engine.IsAvailable)
        {
            throw new MailImportFatalException("unavailable", "Il motore di importazione PST non è disponibile.");
        }
        var source = archive.SourceFileItemId is Guid fileId
            ? await _db.FileItems.AsNoTracking()
                .Where(f => f.Id == fileId && f.OwnerUserId == archive.OwnerUserId && f.DeletedAt == null)
                .Select(f => new { f.Id, f.BlobObjectId, f.SizeBytes })
                .FirstOrDefaultAsync(cancellationToken)
            : null;
        var sha = source is null
            ? null
            : await _db.BlobObjects.AsNoTracking()
                .Where(b => b.Id == source.BlobObjectId)
                .Select(b => b.Sha256)
                .FirstOrDefaultAsync(cancellationToken);
        if (source is null || sha is null
            || ContentFingerprint.Compute(_pepper, sha) != archive.SourceFingerprint)
        {
            throw new MailImportFatalException("source_unavailable", "Il file PST di origine non è più disponibile o è cambiato.");
        }

        var path = _workspace.SourcePath(run.Id);
        if (File.Exists(path) && new FileInfo(path).Length == source.SizeBytes)
        {
            return path;
        }

        if (_workspace.AvailableFreeBytes() < source.SizeBytes + _options.MaxAttachmentBytes)
        {
            throw new MailImportFatalException(
                "insufficient_work_space", "Spazio insufficiente nell'area di lavoro per la copia del PST.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var partial = path + ".partial";
        await using (var content = await _files.OpenContentAsync(source.Id, archive.OwnerUserId, cancellationToken)
            ?? throw new MailImportFatalException("source_unavailable", "Il file PST di origine non è più disponibile."))
        await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
        {
            await content.Content.CopyToAsync(output, 1 << 20, cancellationToken);
        }
        File.Move(partial, path, overwrite: true);
        // The engine only ever needs to read it.
        File.SetAttributes(path, FileAttributes.ReadOnly);
        return path;
    }

    // ---- scanning -------------------------------------------------------------

    private async Task ScanAsync(MailImportRun run, Domain.Mail.MailArchive archive, string pstPath, CancellationToken cancellationToken)
    {
        var folders = await _db.MailFolders
            .Where(f => f.ArchiveId == archive.Id)
            .ToDictionaryAsync(f => f.SourceFolderKey, cancellationToken);
        var nextFolderOrdinal = folders.Count == 0 ? 1 : folders.Values.Max(f => f.Ordinal) + 1;
        var nextOrdinal = (await _db.MailImportItems
            .Where(i => i.ArchiveId == archive.Id)
            .MaxAsync(i => (int?)i.Ordinal, cancellationToken) ?? 0) + 1;

        var batch = new List<(string MessageKey, string FolderKey)>(_options.ScanBatchSize);
        var scanErrors = 0;

        await foreach (var evt in _engine.ListAsync(pstPath, cancellationToken))
        {
            switch (evt.Type)
            {
                case "folder" when evt.FolderId is not null:
                    if (!folders.ContainsKey(evt.FolderId))
                    {
                        Guid? parentId = evt.ParentFolderId is not null && folders.TryGetValue(evt.ParentFolderId, out var parent)
                            ? parent.Id
                            : null;
                        var folder = new MailFolder
                        {
                            Id = Guid.NewGuid(),
                            ArchiveId = archive.Id,
                            OwnerUserId = archive.OwnerUserId,
                            ParentId = parentId,
                            SourceFolderKey = evt.FolderId,
                            Name = Truncate(MailNames.Segment(evt.Name, "Cartella"), 255)!,
                            PathJson = JsonSerializer.Serialize(
                                (evt.Path ?? []).Select(p => MailNames.Segment(p, "Cartella")).ToList()),
                            Depth = evt.Depth ?? 0,
                            Ordinal = nextFolderOrdinal++,
                            ContainerClass = Truncate(evt.ContainerClass, 64),
                        };
                        _db.MailFolders.Add(folder);
                        folders[evt.FolderId] = folder;
                    }
                    break;
                case "message" when evt.MessageId is not null && evt.FolderId is not null:
                    batch.Add((evt.MessageId, evt.FolderId));
                    if (batch.Count >= _options.ScanBatchSize)
                    {
                        nextOrdinal = await FlushScanBatchAsync(archive, batch, nextOrdinal, cancellationToken);
                    }
                    break;
                case "error":
                    scanErrors++;
                    break;
            }
        }
        nextOrdinal = await FlushScanBatchAsync(archive, batch, nextOrdinal, cancellationToken);

        archive.FolderCount = folders.Count;
        archive.ScanCompletedAt = Now;
        archive.UpdatedAt = Now;
        run.ScanErrors = scanErrors;
        run.UpdatedAt = Now;
        await _db.SaveChangesAsync(cancellationToken);
    }

    // Inserts the batch's unseen messages (a re-scan after an interrupted scan
    // finds most of them present already; their ordinals never change).
    private async Task<int> FlushScanBatchAsync(
        Domain.Mail.MailArchive archive, List<(string MessageKey, string FolderKey)> batch, int nextOrdinal,
        CancellationToken cancellationToken)
    {
        // Folders added since the last flush are written first (FK order).
        await _db.SaveChangesAsync(cancellationToken);
        if (batch.Count == 0)
        {
            return nextOrdinal;
        }
        var keys = batch.Select(b => b.MessageKey).Distinct().ToList();
        var existing = (await _db.MailImportItems.AsNoTracking()
            .Where(i => i.ArchiveId == archive.Id && keys.Contains(i.SourceMessageKey))
            .Select(i => i.SourceMessageKey)
            .ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        var now = Now;
        foreach (var (messageKey, folderKey) in batch)
        {
            if (!existing.Add(messageKey))
            {
                continue;
            }
            _db.MailImportItems.Add(new MailImportItem
            {
                Id = Guid.NewGuid(),
                ArchiveId = archive.Id,
                OwnerUserId = archive.OwnerUserId,
                SourceMessageKey = messageKey,
                SourceFolderKey = folderKey,
                Ordinal = nextOrdinal++,
                Status = MailImportItemStatuses.Pending,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
        await _db.SaveChangesAsync(cancellationToken);
        foreach (var entry in _db.ChangeTracker.Entries<MailImportItem>().ToList())
        {
            entry.State = EntityState.Detached;
        }
        batch.Clear();
        return nextOrdinal;
    }

    // ---- importing ------------------------------------------------------------

    private async Task<bool> ImportAsync(
        MailImportRun run, Domain.Mail.MailArchive archive, string pstPath, MailImportCheckpoint checkpoint,
        JobContext context, CancellationToken cancellationToken)
    {
        var serveDir = _workspace.ServeDirectory(run.Id);
        if (Directory.Exists(serveDir))
        {
            Directory.Delete(serveDir, recursive: true); // leftovers of a crashed slice
        }
        Directory.CreateDirectory(serveDir);

        var state = new ImportState(run, archive, serveDir, pstPath,
            await _db.MailFolders.AsNoTracking()
                .Where(f => f.ArchiveId == archive.Id)
                .ToDictionaryAsync(f => f.SourceFolderKey, cancellationToken));
        try
        {
            long processedThisSlice = 0;
            while (true)
            {
                var page = await _db.MailImportItems
                    .Where(i => i.ArchiveId == archive.Id
                        && i.Ordinal > checkpoint.LastOrdinal
                        && (i.Status == MailImportItemStatuses.Pending
                            || i.Status == MailImportItemStatuses.Importing
                            || i.Status == MailImportItemStatuses.Failed))
                    .OrderBy(i => i.Ordinal)
                    .Take(PageSize)
                    .ToListAsync(cancellationToken);
                if (page.Count == 0)
                {
                    await FlushCountersAsync(state, context, cancellationToken);
                    return true;
                }

                var keys = page.Select(i => i.SourceMessageKey).ToList();
                var tombstoned = (await _db.MailDeletionTombstones.AsNoTracking()
                    .Where(t => t.OwnerUserId == archive.OwnerUserId
                        && t.SourceFingerprint == archive.SourceFingerprint
                        && keys.Contains(t.SourceMessageKey))
                    .Select(t => t.SourceMessageKey)
                    .ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);

                foreach (var item in page)
                {
                    if (context.ShouldYield(processedThisSlice))
                    {
                        await FlushCountersAsync(state, context, CancellationToken.None);
                        return false;
                    }

                    await ProcessItemAsync(state, item, tombstoned.Contains(item.SourceMessageKey), cancellationToken);
                    checkpoint.LastOrdinal = item.Ordinal;
                    processedThisSlice++;
                    if (++state.SinceFlush >= CounterFlushEvery)
                    {
                        await FlushCountersAsync(state, context, cancellationToken);
                    }
                }
                _db.ChangeTracker.Clear();
                _db.Attach(run);
                _db.Attach(archive);
            }
        }
        finally
        {
            if (state.Session is not null)
            {
                await state.Session.DisposeAsync();
            }
        }
    }

    private async Task ProcessItemAsync(ImportState state, MailImportItem item, bool tombstoned, CancellationToken cancellationToken)
    {
        var archive = state.Archive;
        if (tombstoned)
        {
            item.Status = MailImportItemStatuses.SkippedDeleted;
            Decide(item, state.Run, MailImportOutcomes.SkippedDeleted);
            await _db.SaveChangesAsync(cancellationToken);
            return;
        }

        var message = await _db.MailMessages
            .FirstOrDefaultAsync(m => m.ArchiveId == archive.Id && m.SourceMessageKey == item.SourceMessageKey, cancellationToken);
        if (message?.Status == MailMessageStatuses.Complete)
        {
            // Completed earlier (an earlier slice, or a crash after the commit).
            item.Status = MailImportItemStatuses.Imported;
            item.MessageId = message.Id;
            if (item.LastRunId != state.Run.Id)
            {
                Decide(item, state.Run, MailImportOutcomes.AlreadyPresent);
            }
            await _db.SaveChangesAsync(cancellationToken);
            return;
        }

        item.Status = MailImportItemStatuses.Importing;
        item.Attempts++;
        item.LastRunId = state.Run.Id;
        item.UpdatedAt = Now;
        await _db.SaveChangesAsync(cancellationToken);

        var requestId = $"i{item.Ordinal}";
        var requestDir = Path.Combine(state.ServeDir, requestId);
        try
        {
            PstExtractResponse response;
            try
            {
                state.Session ??= await _engine.StartSessionAsync(state.PstPath, state.ServeDir, cancellationToken);
                response = await state.Session.ExtractAsync(requestId, item.SourceMessageKey, cancellationToken);
            }
            catch (PstEngineException ex)
            {
                // The session is unusable after a protocol failure: restart it
                // for the next message, record this one as failed.
                if (state.Session is not null)
                {
                    await state.Session.DisposeAsync();
                    state.Session = null;
                }
                await FailItemAsync(state, item, ex.Code, PstProcessMessage(ex), cancellationToken);
                return;
            }

            if (response.Type != "extracted" || response.Document is null)
            {
                await FailItemAsync(state, item, response.Code ?? "engine_error",
                    ProcessPstEngine.Sanitize(response.Message), cancellationToken);
                return;
            }

            var documentPath = MailWorkspace.ResolveInside(state.ServeDir, requestId, response.Document);
            var documentInfo = new FileInfo(documentPath);
            if (!documentInfo.Exists || documentInfo.Length > _options.MaxDocumentBytes)
            {
                await FailItemAsync(state, item, "too_large", "Documento del messaggio oltre il limite.", cancellationToken);
                return;
            }

            PstMessageDocument? document;
            await using (var stream = File.OpenRead(documentPath))
            {
                document = await JsonSerializer.DeserializeAsync<PstMessageDocument>(stream, PstProtocol.Json, cancellationToken);
            }
            if (document is null
                || document.Format != MailDocumentFormats.Message
                || document.Version != MailDocumentFormats.CurrentVersion)
            {
                await FailItemAsync(state, item, "unsupported_document", "Formato del documento non riconosciuto.", cancellationToken);
                return;
            }

            var folderKey = document.Source?.FolderId ?? item.SourceFolderKey;
            if (!state.Folders.TryGetValue(folderKey, out var folder)
                && !state.Folders.TryGetValue(item.SourceFolderKey, out folder))
            {
                await FailItemAsync(state, item, "folder_missing", "Cartella di origine sconosciuta.", cancellationToken);
                return;
            }

            if (message is null)
            {
                message = new MailMessage
                {
                    Id = Guid.NewGuid(),
                    OwnerUserId = archive.OwnerUserId,
                    ArchiveId = archive.Id,
                    SourceMessageKey = item.SourceMessageKey,
                    Status = MailMessageStatuses.Importing,
                    CreatedAt = Now,
                };
                _db.MailMessages.Add(message);
            }
            message.FolderId = folder.Id;
            MailMessageProjection.Apply(message, document, documentInfo.Length, Now);
            await _db.SaveChangesAsync(cancellationToken);

            var createdFiles = new List<Guid>();
            try
            {
                await LinkAttachmentsAsync(state, message, folder, document, response, requestId, createdFiles, cancellationToken);
            }
            catch (QuotaExceededException)
            {
                await FailItemAsync(state, item, "quota_exceeded", "Quota di spazio esaurita durante gli allegati.", cancellationToken);
                await ScheduleAsync(archive.OwnerUserId, createdFiles, cancellationToken);
                return;
            }

            await CompleteMessageAsync(message, item, state.Run, documentPath, cancellationToken);
            state.Imported++;
            await ScheduleAsync(archive.OwnerUserId, createdFiles, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning("MailArchive: message extraction failed ({Error}).", ex.GetType().Name);
            await FailItemAsync(state, item, "io_error", "Errore di lettura durante l'estrazione.", cancellationToken);
        }
        finally
        {
            try
            {
                if (Directory.Exists(requestDir))
                {
                    Directory.Delete(requestDir, recursive: true);
                }
            }
            catch (IOException)
            {
                // Swept with the run directory at the end.
            }
        }
    }

    private async Task LinkAttachmentsAsync(
        ImportState state, MailMessage message, MailFolder folder, PstMessageDocument document,
        PstExtractResponse response, string requestId, List<Guid> createdFiles, CancellationToken cancellationToken)
    {
        var descriptors = MailMessageProjection.FlattenAttachments(document);
        if (descriptors.Count == 0)
        {
            return;
        }

        var links = await _db.MailAttachments
            .Where(a => a.MessageId == message.Id)
            .ToDictionaryAsync(a => a.AttachmentKey, StringComparer.Ordinal, cancellationToken);
        var files = (response.Attachments ?? [])
            .GroupBy(a => a.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var ordinal = 0;
        foreach (var descriptor in descriptors)
        {
            if (!links.TryGetValue(descriptor.Key, out var link))
            {
                link = new MailAttachment
                {
                    Id = Guid.NewGuid(),
                    OwnerUserId = message.OwnerUserId,
                    MessageId = message.Id,
                    AttachmentKey = Truncate(descriptor.Key, 64)!,
                    CreatedAt = Now,
                };
                _db.MailAttachments.Add(link);
                links[descriptor.Key] = link;
            }
            link.Ordinal = ordinal++;
            link.OriginalName = MailNames.AttachmentFileName(descriptor.FileName, descriptor.DisplayName, descriptor.Key);
            link.MimeType = Truncate(descriptor.MimeType, 255);
            link.ContentId = Truncate(descriptor.ContentId, 512);
            link.IsInline = descriptor.Inline;
            link.SizeBytes = descriptor.SizeBytes;
            link.Method = Truncate(descriptor.Method ?? "unsupported", 32)!;

            if (link.Status == MailAttachmentStatuses.Linked && link.FileItemId is Guid linkedId
                && await _db.FileItems.AnyAsync(f => f.Id == linkedId && f.DeletedAt == null, cancellationToken))
            {
                state.Run.AttachmentsReused++;
                continue;
            }

            if (!descriptor.Extracted || !files.TryGetValue(descriptor.Key, out var extracted))
            {
                link.Status = MailAttachmentStatuses.Skipped;
                link.SkipReason = Truncate(descriptor.Extracted ? "missing_from_engine" : descriptor.SkipReason ?? "not_extracted", 64);
                state.Run.AttachmentsSkipped++;
                await _db.SaveChangesAsync(cancellationToken);
                continue;
            }

            var path = MailWorkspace.ResolveInside(state.ServeDir, requestId, extracted.File);
            var targetFolderId = await AttachmentFolderAsync(state, folder, cancellationToken);
            try
            {
                var (fileItemId, created) = await EnsureAttachmentFileAsync(
                    message, link, path, targetFolderId, cancellationToken);
                link.FileItemId = fileItemId;
                link.Status = MailAttachmentStatuses.Linked;
                link.SkipReason = null;
                if (created)
                {
                    createdFiles.Add(fileItemId);
                    state.Run.AttachmentsCreated++;
                }
                else
                {
                    state.Run.AttachmentsReused++;
                }
            }
            catch (UploadTooLargeException)
            {
                link.Status = MailAttachmentStatuses.Skipped;
                link.SkipReason = "too_large";
                state.Run.AttachmentsSkipped++;
            }
            await _db.SaveChangesAsync(cancellationToken);
        }
    }

    // One attachment → one FileItem, exactly once, across crashes: the
    // UploadOperation claim is completed INSIDE the FileItem transaction
    // (FileItemService.CreateAsync), so after a crash the completed claim
    // names the file that already exists and is adopted, never duplicated.
    private async Task<(Guid FileItemId, bool Created)> EnsureAttachmentFileAsync(
        MailMessage message, MailAttachment link, string path, Guid? folderId, CancellationToken cancellationToken)
    {
        var owner = message.OwnerUserId;
        var key = $"mailarchive:{message.Id:N}:{link.AttachmentKey}";
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            var claim = await _idempotency.TryClaimAsync(owner, key, AttachmentClaimLease, cancellationToken);
            switch (claim.Outcome)
            {
                case UploadClaimOutcome.AlreadyCompleted:
                    var existing = await _idempotency.FindCompletedResultAsync(owner, key, cancellationToken);
                    if (existing is not null)
                    {
                        return (existing.Id, false);
                    }
                    continue; // its file was deleted meanwhile: the row is gone, claim again

                case UploadClaimOutcome.InFlight:
                    // Only this run (one active run per archive, under a job
                    // lease) ever claims these keys: a pending claim is one we
                    // left behind before a crash, and it produced no file.
                    await _db.UploadOperations
                        .Where(o => o.OwnerUserId == owner && o.OperationKey == key && o.Status == UploadOperationStatus.Pending)
                        .ExecuteDeleteAsync(cancellationToken);
                    continue;
            }

            var name = await FreeNameAsync(owner, folderId, link.OriginalName, cancellationToken);
            try
            {
                await using var content = File.OpenRead(path);
                var created = await _files.CreateAsync(
                    owner, folderId, name, link.MimeType, content, cancellationToken,
                    generateSmallThumbnail: false,
                    extractEmbeddedMetadata: false,
                    uploadOperationClaimToken: claim.Token);
                return (created.Id, true);
            }
            catch (DuplicateFileNameException)
            {
                await _idempotency.ReleaseAsync(claim.Token, CancellationToken.None);
            }
            catch
            {
                await _idempotency.ReleaseAsync(claim.Token, CancellationToken.None);
                throw;
            }
        }
        throw new IOException("Attachment name kept colliding.");
    }

    private async Task<string> FreeNameAsync(Guid owner, Guid? folderId, string requested, CancellationToken cancellationToken)
    {
        var dot = requested.LastIndexOf('.');
        var stem = dot > 0 ? requested[..dot] : requested;
        var prefix = stem + " (";
        var taken = await _db.FileItems.AsNoTracking()
            .Where(f => f.OwnerUserId == owner && f.ParentFolderId == folderId && f.DeletedAt == null
                && (f.Name == requested || f.Name.StartsWith(prefix)))
            .Select(f => f.Name)
            .ToListAsync(cancellationToken);
        return OwnerUploadIngestion.NextFreeName(requested, taken);
    }

    // Posta/<archive>/<PST folder path> in the owner's Files, created on
    // demand and cached per slice. A folder the owner deleted is recreated.
    private async Task<Guid?> AttachmentFolderAsync(ImportState state, MailFolder folder, CancellationToken cancellationToken)
    {
        if (state.AttachmentFolders.TryGetValue(folder.Id, out var cached)
            && cached is Guid cachedId
            && await _db.Folders.AnyAsync(f => f.Id == cachedId && f.DeletedAt == null, cancellationToken))
        {
            return cached;
        }
        var path = JsonSerializer.Deserialize<List<string>>(folder.PathJson) ?? [];
        var segments = new List<string>
        {
            MailNames.Segment(_options.AttachmentRootFolderName, "Posta"),
            MailNames.Segment(state.Archive.DisplayName, "Archivio"),
        };
        // The IPM subtree root ("Top of Outlook data file") adds nothing.
        segments.AddRange(path.Skip(1).Select(p => MailNames.Segment(p, "Cartella")));
        var id = await _folders.EnsureFolderPathAsync(state.Archive.OwnerUserId, null, segments, cancellationToken);
        state.AttachmentFolders[folder.Id] = id;
        return id;
    }

    // The document blob and the "complete" state commit together: until then
    // the message is invisible, after it the reference is owned by the row.
    private async Task CompleteMessageAsync(
        MailMessage message, MailImportItem item, MailImportRun run, string documentPath, CancellationToken cancellationToken)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);
            await using var content = File.OpenRead(documentPath);
            var blob = await _blobs.StoreAsync(content, cancellationToken);
            var now = Now;
            var completed = await _db.MailMessages
                .Where(m => m.Id == message.Id && m.Status == MailMessageStatuses.Importing && m.BlobObjectId == null)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(m => m.BlobObjectId, blob.Id)
                    .SetProperty(m => m.Status, MailMessageStatuses.Complete)
                    .SetProperty(m => m.ImportedAt, now), cancellationToken);
            if (completed != 1)
            {
                throw new InvalidOperationException("Mail message was completed concurrently.");
            }
            await _db.MailImportItems
                .Where(i => i.Id == item.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(i => i.Status, MailImportItemStatuses.Imported)
                    .SetProperty(i => i.MessageId, message.Id)
                    .SetProperty(i => i.LastRunId, run.Id)
                    .SetProperty(i => i.LastOutcome, MailImportOutcomes.Imported)
                    .SetProperty(i => i.FailureCode, (string?)null)
                    .SetProperty(i => i.FailureMessage, (string?)null)
                    .SetProperty(i => i.UpdatedAt, now), cancellationToken);
            await tx.CommitAsync(cancellationToken);
        });
        message.Status = MailMessageStatuses.Complete;
        item.Status = MailImportItemStatuses.Imported;
        item.LastOutcome = MailImportOutcomes.Imported;
    }

    private async Task ScheduleAsync(Guid owner, List<Guid> fileItemIds, CancellationToken cancellationToken)
    {
        foreach (var id in fileItemIds)
        {
            try
            {
                await _pipeline.OnFileIngestedAsync(owner, id, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning("MailArchive: post-ingest scheduling failed ({Error}); a backfill will pick the file up.", ex.GetType().Name);
            }
        }
    }

    private async Task FailItemAsync(ImportState state, MailImportItem item, string code, string message, CancellationToken cancellationToken)
    {
        item.Status = MailImportItemStatuses.Failed;
        item.FailureCode = Truncate(code, 64);
        item.FailureMessage = Truncate(message, 300);
        Decide(item, state.Run, MailImportOutcomes.Failed);
        await _db.SaveChangesAsync(cancellationToken);
        state.Failed++;
    }

    private void Decide(MailImportItem item, MailImportRun run, string outcome)
    {
        item.LastRunId = run.Id;
        item.LastOutcome = outcome;
        item.UpdatedAt = Now;
    }

    // ---- counters / finalize ----------------------------------------------------

    private async Task FlushCountersAsync(ImportState state, JobContext context, CancellationToken cancellationToken)
    {
        state.SinceFlush = 0;
        var run = state.Run;
        run.ProcessedMessages = await _db.MailImportItems.CountAsync(i => i.ArchiveId == run.ArchiveId
            && (i.LastRunId == run.Id || i.Status == MailImportItemStatuses.Imported
                || i.Status == MailImportItemStatuses.SkippedDeleted || i.Status == MailImportItemStatuses.Deleted), cancellationToken);
        run.UpdatedAt = Now;
        await _db.SaveChangesAsync(cancellationToken);
        await context.ReportProgressAsync(run.ProcessedMessages, run.TotalMessages, "importing", cancellationToken);
    }

    private async Task FinalizeAsync(MailImportRun run, Domain.Mail.MailArchive archive, CancellationToken cancellationToken)
    {
        await SetPhaseAsync(run, MailImportPhases.Finalizing, cancellationToken);

        // Counters from persisted item state — never from memory.
        var items = _db.MailImportItems.Where(i => i.ArchiveId == archive.Id);
        run.TotalMessages = await items.CountAsync(cancellationToken);
        run.ImportedMessages = await items.CountAsync(i => i.LastRunId == run.Id && i.LastOutcome == MailImportOutcomes.Imported, cancellationToken);
        run.AlreadyPresentMessages = await items.CountAsync(i => i.Status == MailImportItemStatuses.Imported, cancellationToken) - run.ImportedMessages;
        run.SkippedDeletedMessages = await items.CountAsync(i => i.Status == MailImportItemStatuses.SkippedDeleted || i.Status == MailImportItemStatuses.Deleted, cancellationToken);
        run.FailedMessages = await items.CountAsync(i => i.Status == MailImportItemStatuses.Failed, cancellationToken);
        run.ProcessedMessages = run.TotalMessages;

        await MailCounts.RecountAsync(_db, archive.Id, cancellationToken);
        await _db.Entry(archive).ReloadAsync(cancellationToken);
        archive.LastImportedAt = Now;
        archive.UpdatedAt = Now;

        run.Status = run.FailedMessages > 0 || run.ScanErrors > 0
            ? MailImportRunStatuses.Partial
            : MailImportRunStatuses.Succeeded;
        if (run.ScanErrors > 0)
        {
            run.ErrorCode = "scan_errors";
            run.ErrorMessage = $"{run.ScanErrors} elementi del PST non sono leggibili.";
        }
        run.Phase = null;
        run.CompletedAt = Now;
        run.UpdatedAt = Now;
        await _db.SaveChangesAsync(cancellationToken);
        CleanupWorkspace(run.Id);

        await _audit.LogAsync(
            actor: archive.OwnerUserId,
            action: AuditActions.MailImportComplete,
            entityType: AuditEntityTypes.MailArchive,
            entityId: archive.Id,
            ipAddress: null,
            metadata: new
            {
                status = run.Status,
                imported = run.ImportedMessages,
                alreadyPresent = run.AlreadyPresentMessages,
                skippedDeleted = run.SkippedDeletedMessages,
                failed = run.FailedMessages,
                attachmentsCreated = run.AttachmentsCreated,
            },
            cancellationToken: cancellationToken);
    }

    private async Task FailRunAsync(MailImportRun run, string code, string message)
    {
        run.Status = MailImportRunStatuses.Failed;
        run.Phase = null;
        run.ErrorCode = Truncate(code, 64);
        run.ErrorMessage = Truncate(message, 300);
        run.CompletedAt = Now;
        run.UpdatedAt = Now;
        await _db.SaveChangesAsync(CancellationToken.None);
        CleanupWorkspace(run.Id);
    }

    private async Task MarkCancelledAsync(MailImportRun run)
    {
        await _db.MailImportRuns
            .Where(r => r.Id == run.Id && (r.Status == MailImportRunStatuses.Queued || r.Status == MailImportRunStatuses.Running))
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, MailImportRunStatuses.Cancelled)
                .SetProperty(r => r.Phase, (string?)null)
                .SetProperty(r => r.CompletedAt, Now)
                .SetProperty(r => r.UpdatedAt, Now), CancellationToken.None);
        // Messages left "importing" stay invisible; items "importing" resume
        // on the next run. Nothing permanent is recorded for them.
        await MailCounts.RecountAsync(_db, run.ArchiveId, CancellationToken.None);
        CleanupWorkspace(run.Id);
    }

    private async Task<bool> IsCooperativelyCancelledAsync(JobContext context) =>
        await _db.BackgroundJobs.AsNoTracking()
            .Where(j => j.Id == context.JobId)
            .Select(j => j.CancellationRequested)
            .FirstOrDefaultAsync(CancellationToken.None);

    private async Task SetPhaseAsync(MailImportRun run, string phase, CancellationToken cancellationToken)
    {
        run.Phase = phase;
        run.UpdatedAt = Now;
        await _db.SaveChangesAsync(cancellationToken);
    }

    private void CleanupWorkspace(Guid runId)
    {
        try
        {
            _workspace.DeleteRun(runId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogWarning("MailArchive: could not remove a finished run's work directory ({Error}).", ex.GetType().Name);
        }
    }

    private static string PstProcessMessage(PstEngineException ex) => ProcessPstEngine.Sanitize(ex.Message);

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    internal static string? Truncate(string? value, int max) =>
        value is null ? null : value.Length <= max ? value : value[..max];

    private sealed class ImportState(
        MailImportRun run, Domain.Mail.MailArchive archive, string serveDir, string pstPath,
        Dictionary<string, MailFolder> folders)
    {
        public MailImportRun Run { get; } = run;
        public Domain.Mail.MailArchive Archive { get; } = archive;
        public string ServeDir { get; } = serveDir;
        public string PstPath { get; } = pstPath;
        public Dictionary<string, MailFolder> Folders { get; } = folders;
        public Dictionary<Guid, Guid?> AttachmentFolders { get; } = new();
        public IPstExtractionSession? Session { get; set; }
        public int SinceFlush { get; set; }
        public int Imported { get; set; }
        public int Failed { get; set; }
    }
}

/// <summary>A run-level failure that retrying cannot fix (source gone, no space).</summary>
public sealed class MailImportFatalException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

// Internal resume state of a sliced import: the last manifest ordinal this
// run has decided. Counts and ids only; never a path or a key.
public sealed class MailImportCheckpoint
{
    public int Version { get; set; } = 1;
    public int LastOrdinal { get; set; }

    public static MailImportCheckpoint Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new MailImportCheckpoint();
        }
        try
        {
            var parsed = JsonSerializer.Deserialize<MailImportCheckpoint>(json);
            return parsed is { Version: 1 } ? parsed : new MailImportCheckpoint();
        }
        catch (JsonException)
        {
            return new MailImportCheckpoint();
        }
    }

    public string ToJson() => JsonSerializer.Serialize(this);
}
