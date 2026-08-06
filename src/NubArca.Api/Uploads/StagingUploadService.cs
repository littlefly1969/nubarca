using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NubArca.Api.Admin;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Jobs;
using NubArca.Api.Storage;

namespace NubArca.Api.Uploads;

public interface IStagingUploadService
{
    StagingConfigResponse GetConfig();
    Task<StagingSessionResponse> CreateSessionAsync(
        Guid userId, bool isAdmin, StagingSessionCreateRequest request, CancellationToken cancellationToken);
    Task<StagingSessionListResponse> ListSessionsAsync(Guid userId, int limit, CancellationToken cancellationToken);
    Task<StagingSessionResponse?> GetSessionAsync(Guid userId, Guid sessionId, CancellationToken cancellationToken);
    Task<StagingManifestResponse?> SubmitManifestAsync(
        Guid userId, Guid sessionId, StagingManifestRequest request, CancellationToken cancellationToken);
    Task<StagingItemListResponse?> GetItemsAsync(
        Guid userId, Guid sessionId, string? status, int page, int pageSize, CancellationToken cancellationToken);
    Task<StagingMissingResponse?> GetMissingAsync(
        Guid userId, Guid sessionId, int afterOrdinal, int limit, CancellationToken cancellationToken);
    Task<StagingChunkResponse?> ReceiveChunkAsync(
        Guid userId, Guid sessionId, Guid itemId, int chunkIndex,
        Stream body, long? contentLength, CancellationToken cancellationToken);
    Task<StagingVerifyResponse?> VerifyAsync(Guid userId, Guid sessionId, CancellationToken cancellationToken);
    Task<StagingImportStartResponse?> StartImportAsync(Guid userId, Guid sessionId, CancellationToken cancellationToken);
    Task<StagingCancelResponse?> CancelAsync(Guid userId, Guid sessionId, CancellationToken cancellationToken);
    // null = not found / foreign; false = not deletable right now (importing).
    Task<bool?> DeleteAsync(Guid userId, Guid sessionId, CancellationToken cancellationToken);
}

// Slice 93: web remote-staging upload — resumable browser chunk uploads into
// temporary, isolated per-session staging directories, verified against the
// persisted manifest/chunk state and then handed off to the EXISTING
// admin-import pipeline (admin_import_runs / admin_import_items / Jobs v2).
//
// Safety model: every relative path is validated (no `..`, no absolute or
// drive-prefixed paths, bounded segments) and every resolved file path is
// re-checked to stay inside the session's staging directory; the staging root
// must not overlap blob storage; client size/mtime/type hints never become
// metadata — server-side detection at import stays authoritative. No absolute
// path, storage key, hash, or payload ever crosses the API boundary.
public sealed class StagingUploadService : IStagingUploadService
{
    private const int MaxRelativePathLength = 1024;
    private const int MaxSegmentLength = 255;
    private const int MaxNameLength = 200;
    private const int ManifestErrorSamples = 10;
    private const int InsertBatchSize = 500;
    private const int VerifyPageSize = 500;

    private readonly AppDbContext _db;
    private readonly IJobQueue _jobs;
    private readonly IAdminImportService _import;
    private readonly TimeProvider _clock;
    private readonly IOptions<StagingOptions> _options;
    private readonly IOptions<BlobStorageOptions> _storage;
    private readonly ILogger<StagingUploadService>? _logger;

    public StagingUploadService(
        AppDbContext db,
        IJobQueue jobs,
        IAdminImportService import,
        TimeProvider clock,
        IOptions<StagingOptions> options,
        IOptions<BlobStorageOptions> storage,
        ILogger<StagingUploadService>? logger = null)
    {
        _db = db;
        _jobs = jobs;
        _import = import;
        _clock = clock;
        _options = options;
        _storage = storage;
        _logger = logger;
    }

    private DateTime UtcNow => _clock.GetUtcNow().UtcDateTime;

    // ---- config ------------------------------------------------------------

    public StagingConfigResponse GetConfig()
    {
        var opts = _options.Value;
        var enabled = opts.Enabled && !string.IsNullOrWhiteSpace(opts.RootPath);
        return new StagingConfigResponse(
            enabled,
            opts.MaxSessionBytes,
            EffectiveMaxFileBytes(),
            opts.MaxFilesPerSession,
            opts.EffectiveChunkSizeBytes,
            opts.SessionTtlHours);
    }

    // Staging must never accept a file the import would later reject as too
    // large — align with the app-level Storage:MaxUploadBytes ceiling.
    private long EffectiveMaxFileBytes()
    {
        var opts = _options.Value;
        var storageMax = _storage.Value.MaxUploadBytes;
        return storageMax > 0 ? Math.Min(opts.MaxFileBytes, storageMax) : opts.MaxFileBytes;
    }

    // ---- sessions ------------------------------------------------------------

    public async Task<StagingSessionResponse> CreateSessionAsync(
        Guid userId, bool isAdmin, StagingSessionCreateRequest request, CancellationToken cancellationToken)
    {
        ResolveStagingRoot(); // throws StagingUnavailableException when off/unconfigured

        var targetUserId = request.TargetUserId ?? userId;
        if (targetUserId != userId && !isAdmin)
        {
            throw new StagingForbiddenException("Only an admin can stage an upload for another user.");
        }
        var targetExists = await _db.Users.AsNoTracking()
            .AnyAsync(u => u.Id == targetUserId, cancellationToken);
        if (!targetExists)
        {
            throw new StagingValidationException("Target user not found.");
        }
        if (request.DestinationFolderId is { } destId)
        {
            var folderOk = await _db.Folders.AsNoTracking().AnyAsync(
                f => f.Id == destId && f.OwnerUserId == targetUserId && f.DeletedAt == null,
                cancellationToken);
            if (!folderOk)
            {
                throw new StagingValidationException("Destination folder not found.");
            }
        }

        var name = (request.Name ?? string.Empty).Trim();
        if (name.Length > MaxNameLength)
        {
            throw new StagingValidationException($"Name must be at most {MaxNameLength} characters.");
        }

        var now = UtcNow;
        var session = new RemoteUploadSession
        {
            Id = Guid.NewGuid(),
            CreatedByUserId = userId,
            TargetUserId = targetUserId,
            DestinationFolderId = request.DestinationFolderId,
            SkipPreviouslyDeleted = request.SkipPreviouslyDeleted,
            SkipExistingContent = request.SkipExistingContent,
            Name = name.Length > 0 ? name : $"Upload {now:yyyy-MM-dd HH:mm}",
            Status = RemoteUploadSessionStatuses.Draft,
            CreatedAt = now,
            UpdatedAt = now,
            ExpiresAt = now.AddHours(Math.Max(1, _options.Value.SessionTtlHours)),
        };
        session.StagingRelativeRoot = session.Id.ToString("N");
        _db.RemoteUploadSessions.Add(session);
        await _db.SaveChangesAsync(cancellationToken);

        return await ToResponseAsync(session, cancellationToken);
    }

    public async Task<StagingSessionListResponse> ListSessionsAsync(
        Guid userId, int limit, CancellationToken cancellationToken)
    {
        limit = Math.Clamp(limit, 1, 100);
        var total = await _db.RemoteUploadSessions
            .CountAsync(s => s.CreatedByUserId == userId, cancellationToken);
        var rows = await _db.RemoteUploadSessions.AsNoTracking()
            .Where(s => s.CreatedByUserId == userId)
            .OrderByDescending(s => s.CreatedAt)
            .Take(limit)
            .ToListAsync(cancellationToken);
        var sessions = new List<StagingSessionResponse>(rows.Count);
        foreach (var row in rows)
        {
            sessions.Add(await ToResponseAsync(row, cancellationToken));
        }
        return new StagingSessionListResponse(sessions, total);
    }

    public async Task<StagingSessionResponse?> GetSessionAsync(
        Guid userId, Guid sessionId, CancellationToken cancellationToken)
    {
        var session = await GetOwnedSessionAsync(userId, sessionId, cancellationToken);
        return session is null ? null : await ToResponseAsync(session, cancellationToken);
    }

    // Owner-scoped lookup (missing/foreign collapse to null → 404) with lazy
    // expiry: an overdue non-terminal session flips to `expired` on access, so
    // "expired sessions cannot be resumed" needs no background race.
    private async Task<RemoteUploadSession?> GetOwnedSessionAsync(
        Guid userId, Guid sessionId, CancellationToken cancellationToken)
    {
        var session = await _db.RemoteUploadSessions.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == sessionId && s.CreatedByUserId == userId, cancellationToken);
        if (session is null) return null;

        if (!RemoteUploadSessionStatuses.IsTerminal(session.Status)
            && session.Status != RemoteUploadSessionStatuses.Importing
            && session.ExpiresAt < UtcNow)
        {
            var now = UtcNow;
            await _db.RemoteUploadSessions
                .Where(s => s.Id == session.Id && s.Status == session.Status)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Status, RemoteUploadSessionStatuses.Expired)
                    .SetProperty(x => x.CompletedAt, now)
                    .SetProperty(x => x.UpdatedAt, now), cancellationToken);
            session.Status = RemoteUploadSessionStatuses.Expired;
            session.CompletedAt = now;
        }
        return session;
    }

    private async Task<StagingSessionResponse> ToResponseAsync(
        RemoteUploadSession s, CancellationToken cancellationToken)
    {
        StagingImportProgress? import = null;
        if (s.AdminImportRunId is Guid runId)
        {
            // Safe counters mirrored from the linked admin-import run so the
            // uploader can follow import progress without admin endpoints.
            var run = await _db.AdminImportRuns.AsNoTracking()
                .Where(r => r.Id == runId)
                .Select(r => new
                {
                    r.Status, r.Phase, r.ImportedFiles, r.SkippedFiles, r.FailedFiles,
                    r.ConflictFiles, r.CancelledFiles, r.ScannedFiles, r.ImportedBytes,
                    r.SkippedPreviouslyDeletedFiles, r.SkippedAlreadyPresentFiles,
                })
                .FirstOrDefaultAsync(cancellationToken);
            if (run is not null)
            {
                var pending = Math.Max(0, run.ScannedFiles
                    - run.ImportedFiles - run.SkippedFiles - run.FailedFiles
                    - run.ConflictFiles - run.CancelledFiles
                    - run.SkippedPreviouslyDeletedFiles - run.SkippedAlreadyPresentFiles);
                import = new StagingImportProgress(
                    run.Status, run.Phase, run.ImportedFiles, pending,
                    run.FailedFiles, run.ConflictFiles, run.SkippedFiles,
                    run.SkippedPreviouslyDeletedFiles, run.SkippedAlreadyPresentFiles,
                    run.ImportedBytes);
            }
        }

        return new StagingSessionResponse(
            s.Id, s.Name, s.Status, s.TargetUserId, s.DestinationFolderId,
            s.TotalFiles, s.TotalBytes, s.ReceivedFiles, s.ReceivedBytes,
            s.VerifiedFiles, s.FailedFiles,
            _options.Value.EffectiveChunkSizeBytes,
            s.CreatedAt, s.ExpiresAt, s.CompletedAt,
            s.LastErrorCode, s.LastErrorMessage,
            s.AdminImportRunId, import);
    }

    // ---- manifest ------------------------------------------------------------

    public async Task<StagingManifestResponse?> SubmitManifestAsync(
        Guid userId, Guid sessionId, StagingManifestRequest request, CancellationToken cancellationToken)
    {
        ResolveStagingRoot();
        var session = await GetOwnedSessionAsync(userId, sessionId, cancellationToken);
        if (session is null) return null;
        if (session.Status != RemoteUploadSessionStatuses.Draft)
        {
            throw new StagingConflictException("This session already has a manifest.");
        }

        var files = request.Files ?? Array.Empty<StagingManifestFile>();
        var opts = _options.Value;
        if (files.Count == 0)
        {
            throw new StagingValidationException("The manifest contains no files.");
        }
        if (files.Count > opts.MaxFilesPerSession)
        {
            throw new StagingValidationException(
                $"The manifest exceeds the {opts.MaxFilesPerSession}-file session limit.");
        }

        // Validate every path + size up front; reject the whole manifest on any
        // invalid entry (clear, atomic semantics) with a bounded sample list.
        var maxFile = EffectiveMaxFileBytes();
        var chunkSize = opts.EffectiveChunkSizeBytes;
        var errors = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var normalized = new List<(string Path, long Size, DateTime? Modified)>(files.Count);
        long totalBytes = 0;
        foreach (var file in files)
        {
            if (!TryNormalizeRelativePath(file.RelativePath, out var path, out var reason))
            {
                AddError(errors, $"{Truncate(file.RelativePath ?? "(null)", 120)}: {reason}");
                continue;
            }
            if (!seen.Add(path))
            {
                AddError(errors, $"{Truncate(path, 120)}: duplicate path");
                continue;
            }
            if (file.SizeBytes < 0)
            {
                AddError(errors, $"{Truncate(path, 120)}: negative size");
                continue;
            }
            if (file.SizeBytes > maxFile)
            {
                AddError(errors, $"{Truncate(path, 120)}: exceeds the per-file limit");
                continue;
            }
            totalBytes += file.SizeBytes;
            normalized.Add((path, file.SizeBytes, file.LastModifiedAt));
        }
        if (errors.Count > 0)
        {
            throw new StagingValidationException(
                $"The manifest was rejected: {string.Join("; ", errors)}");
        }
        if (totalBytes > opts.MaxSessionBytes)
        {
            throw new StagingValidationException(
                "The manifest exceeds the per-session byte limit.");
        }

        // Batch-insert items in manifest order. 0-byte files have no chunks and
        // are complete immediately.
        var now = UtcNow;
        var ordinal = 0;
        var alreadyComplete = 0;
        var batch = new List<RemoteUploadItem>(InsertBatchSize);
        foreach (var (path, size, modified) in normalized)
        {
            var expectedChunks = size == 0
                ? 0
                : (int)((size + chunkSize - 1) / chunkSize);
            var item = new RemoteUploadItem
            {
                Id = Guid.NewGuid(),
                SessionId = session.Id,
                Ordinal = ++ordinal,
                RelativePath = path,
                SizeBytes = size,
                LastModifiedAt = modified,
                Status = expectedChunks == 0
                    ? RemoteUploadItemStatuses.Uploaded
                    : RemoteUploadItemStatuses.Pending,
                ChunkSizeBytes = chunkSize,
                ExpectedChunkCount = expectedChunks,
                CreatedAt = now,
                UpdatedAt = now,
            };
            if (expectedChunks == 0) alreadyComplete++;
            batch.Add(item);
            if (batch.Count >= InsertBatchSize)
            {
                await FlushItemsAsync(batch, cancellationToken);
            }
        }
        await FlushItemsAsync(batch, cancellationToken);

        await _db.RemoteUploadSessions.Where(s => s.Id == session.Id).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.Status, RemoteUploadSessionStatuses.ManifestReceived)
            .SetProperty(x => x.TotalFiles, normalized.Count)
            .SetProperty(x => x.TotalBytes, totalBytes)
            .SetProperty(x => x.ReceivedFiles, alreadyComplete)
            .SetProperty(x => x.UpdatedAt, now), cancellationToken);

        return new StagingManifestResponse(
            session.Id, RemoteUploadSessionStatuses.ManifestReceived,
            normalized.Count, totalBytes, chunkSize, alreadyComplete);
    }

    private static void AddError(List<string> errors, string message)
    {
        if (errors.Count < ManifestErrorSamples) errors.Add(message);
        else if (errors.Count == ManifestErrorSamples) errors.Add("…");
    }

    private async Task FlushItemsAsync(List<RemoteUploadItem> batch, CancellationToken cancellationToken)
    {
        if (batch.Count == 0) return;
        _db.RemoteUploadItems.AddRange(batch);
        await _db.SaveChangesAsync(cancellationToken);
        foreach (var item in batch) _db.Entry(item).State = EntityState.Detached;
        batch.Clear();
    }

    // ---- items / missing -------------------------------------------------------

    public async Task<StagingItemListResponse?> GetItemsAsync(
        Guid userId, Guid sessionId, string? status, int page, int pageSize, CancellationToken cancellationToken)
    {
        var session = await GetOwnedSessionAsync(userId, sessionId, cancellationToken);
        if (session is null) return null;
        if (!string.IsNullOrWhiteSpace(status) && !RemoteUploadItemStatuses.IsKnown(status))
        {
            throw new StagingValidationException("Unknown item status filter.");
        }

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);
        var query = _db.RemoteUploadItems.AsNoTracking().Where(i => i.SessionId == sessionId);
        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(i => i.Status == status);
        }
        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderBy(i => i.Ordinal)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(i => new StagingItemDto(
                i.Id, i.Ordinal, i.RelativePath, i.SizeBytes, i.LastModifiedAt,
                i.Status, i.ReceivedBytes, i.ExpectedChunkCount, i.ReceivedChunkCount,
                i.FailureCode, i.FailureMessage))
            .ToListAsync(cancellationToken);
        return new StagingItemListResponse(sessionId, items, total, page, pageSize);
    }

    public async Task<StagingMissingResponse?> GetMissingAsync(
        Guid userId, Guid sessionId, int afterOrdinal, int limit, CancellationToken cancellationToken)
    {
        var session = await GetOwnedSessionAsync(userId, sessionId, cancellationToken);
        if (session is null) return null;

        limit = Math.Clamp(limit, 1, 200);
        // Incomplete = anything that still needs bytes: pending/uploading
        // (fresh or resumed) — uploaded/verified/skipped need nothing.
        var page = await _db.RemoteUploadItems.AsNoTracking()
            .Where(i => i.SessionId == sessionId
                && i.Ordinal > afterOrdinal
                && (i.Status == RemoteUploadItemStatuses.Pending
                    || i.Status == RemoteUploadItemStatuses.Uploading))
            .OrderBy(i => i.Ordinal)
            .Take(limit + 1)
            .Select(i => new { i.Id, i.Ordinal, i.RelativePath, i.SizeBytes, i.LastModifiedAt, i.ExpectedChunkCount })
            .ToListAsync(cancellationToken);
        var hasMore = page.Count > limit;
        if (hasMore) page.RemoveAt(page.Count - 1);

        // One grouped query for the page's received chunk indices.
        var ids = page.Select(i => i.Id).ToList();
        var received = await _db.RemoteUploadChunks.AsNoTracking()
            .Where(c => ids.Contains(c.ItemId))
            .Select(c => new { c.ItemId, c.ChunkIndex })
            .ToListAsync(cancellationToken);
        var receivedByItem = received
            .GroupBy(c => c.ItemId)
            .ToDictionary(g => g.Key, g => g.Select(c => c.ChunkIndex).ToHashSet());

        var items = page.Select(i =>
        {
            var have = receivedByItem.GetValueOrDefault(i.Id);
            var missing = new List<int>();
            for (var index = 0; index < i.ExpectedChunkCount; index++)
            {
                if (have is null || !have.Contains(index)) missing.Add(index);
            }
            return new StagingMissingItem(
                i.Id, i.Ordinal, i.RelativePath, i.SizeBytes, i.LastModifiedAt, missing);
        }).ToList();

        return new StagingMissingResponse(
            sessionId,
            _options.Value.EffectiveChunkSizeBytes,
            items,
            items.Count > 0 ? items[^1].Ordinal : null,
            hasMore);
    }

    // ---- chunk upload ----------------------------------------------------------

    public async Task<StagingChunkResponse?> ReceiveChunkAsync(
        Guid userId, Guid sessionId, Guid itemId, int chunkIndex,
        Stream body, long? contentLength, CancellationToken cancellationToken)
    {
        var root = ResolveStagingRoot();
        var session = await GetOwnedSessionAsync(userId, sessionId, cancellationToken);
        if (session is null) return null;
        if (session.Status is not (RemoteUploadSessionStatuses.ManifestReceived
            or RemoteUploadSessionStatuses.Uploading))
        {
            throw new StagingConflictException(
                $"This session does not accept uploads (status: {session.Status}).");
        }

        var item = await _db.RemoteUploadItems.AsNoTracking()
            .FirstOrDefaultAsync(i => i.Id == itemId && i.SessionId == sessionId, cancellationToken);
        if (item is null) return null;

        if (chunkIndex < 0 || chunkIndex >= item.ExpectedChunkCount)
        {
            throw new StagingValidationException("Chunk index is out of range for this file.");
        }

        // Idempotency: an already-received chunk is a safe no-op (covers the
        // client retrying after a lost response).
        var alreadyReceived = await _db.RemoteUploadChunks.AsNoTracking()
            .AnyAsync(c => c.ItemId == itemId && c.ChunkIndex == chunkIndex, cancellationToken);
        if (alreadyReceived)
        {
            return new StagingChunkResponse(
                itemId, chunkIndex, AlreadyReceived: true,
                item.Status, item.ReceivedChunkCount, item.ExpectedChunkCount);
        }
        if (item.Status is RemoteUploadItemStatuses.Uploaded
            or RemoteUploadItemStatuses.Verified
            or RemoteUploadItemStatuses.Skipped)
        {
            throw new StagingConflictException("This file is already complete.");
        }

        var expectedSize = chunkIndex < item.ExpectedChunkCount - 1
            ? (long)item.ChunkSizeBytes
            : item.SizeBytes - (long)(item.ExpectedChunkCount - 1) * item.ChunkSizeBytes;
        if (contentLength is { } declared && declared != expectedSize)
        {
            throw new StagingValidationException(
                $"Chunk {chunkIndex} must be exactly {expectedSize} bytes.");
        }

        // Resolve + re-verify the target path stays inside the session's
        // staging directory (defence in depth — the path was validated at
        // manifest time).
        var absolute = ResolveItemPath(root, session, item.RelativePath);

        // Stream the body directly into the file at the chunk offset, counting
        // bytes; the chunk row is recorded only AFTER the full expected range
        // was written and flushed, so an interrupted/short/over-long body
        // leaves the chunk "missing" and a retry simply overwrites the range.
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        long written = 0;
        try
        {
            await using var fs = new FileStream(
                absolute, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite,
                bufferSize: 81920, options: FileOptions.Asynchronous);
            if (fs.Length != item.SizeBytes)
            {
                // Fix the final size up front (sparse where supported) so
                // random-access chunk writes and the size verification agree.
                fs.SetLength(item.SizeBytes);
            }
            fs.Seek((long)chunkIndex * item.ChunkSizeBytes, SeekOrigin.Begin);

            var buffer = new byte[81920];
            while (written < expectedSize)
            {
                var toRead = (int)Math.Min(buffer.Length, expectedSize - written);
                var n = await body.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken);
                if (n == 0) break;
                await fs.WriteAsync(buffer.AsMemory(0, n), cancellationToken);
                written += n;
            }
            if (written != expectedSize)
            {
                throw new StagingValidationException(
                    $"Chunk {chunkIndex} body was shorter than the expected {expectedSize} bytes.");
            }
            // Reject a body longer than declared (one extra probe read).
            var extra = await body.ReadAsync(buffer.AsMemory(0, 1), cancellationToken);
            if (extra > 0)
            {
                throw new StagingValidationException(
                    $"Chunk {chunkIndex} body was longer than the expected {expectedSize} bytes.");
            }
            await fs.FlushAsync(cancellationToken);
        }
        catch (IOException)
        {
            throw new StagingValidationException("The chunk could not be written to staging storage.");
        }

        // Record the receipt; a concurrent duplicate insert means another
        // request won the race — treat as already received.
        _db.RemoteUploadChunks.Add(new RemoteUploadChunk
        {
            ItemId = itemId,
            ChunkIndex = chunkIndex,
            SizeBytes = (int)expectedSize,
            ReceivedAt = UtcNow,
        });
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            _db.ChangeTracker.Clear();
            return new StagingChunkResponse(
                itemId, chunkIndex, AlreadyReceived: true,
                item.Status, item.ReceivedChunkCount, item.ExpectedChunkCount);
        }

        var now = UtcNow;
        await _db.RemoteUploadItems.Where(i => i.Id == itemId).ExecuteUpdateAsync(s => s
            .SetProperty(i => i.ReceivedChunkCount, i => i.ReceivedChunkCount + 1)
            .SetProperty(i => i.ReceivedBytes, i => i.ReceivedBytes + expectedSize)
            .SetProperty(i => i.Status, RemoteUploadItemStatuses.Uploading)
            .SetProperty(i => i.FailureCode, (string?)null)
            .SetProperty(i => i.FailureMessage, (string?)null)
            .SetProperty(i => i.UpdatedAt, now), cancellationToken);

        // Completion check: exactly one request observes the final count and
        // flips the item to uploaded (and bumps the session's per-FILE
        // counters — chunk uploads never contend on the session row).
        var completed = await _db.RemoteUploadItems
            .Where(i => i.Id == itemId
                && i.ReceivedChunkCount == i.ExpectedChunkCount
                && i.Status == RemoteUploadItemStatuses.Uploading)
            .ExecuteUpdateAsync(s => s
                .SetProperty(i => i.Status, RemoteUploadItemStatuses.Uploaded)
                .SetProperty(i => i.UpdatedAt, now), cancellationToken);
        if (completed == 1)
        {
            await _db.RemoteUploadSessions.Where(s => s.Id == sessionId).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.ReceivedFiles, x => x.ReceivedFiles + 1)
                .SetProperty(x => x.ReceivedBytes, x => x.ReceivedBytes + item.SizeBytes)
                .SetProperty(x => x.UpdatedAt, now), cancellationToken);
        }
        // First chunk of the session flips it to `uploading`.
        if (session.Status == RemoteUploadSessionStatuses.ManifestReceived)
        {
            await _db.RemoteUploadSessions
                .Where(s => s.Id == sessionId && s.Status == RemoteUploadSessionStatuses.ManifestReceived)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Status, RemoteUploadSessionStatuses.Uploading)
                    .SetProperty(x => x.UpdatedAt, now), cancellationToken);
        }

        var fresh = await _db.RemoteUploadItems.AsNoTracking()
            .Where(i => i.Id == itemId)
            .Select(i => new { i.Status, i.ReceivedChunkCount, i.ExpectedChunkCount })
            .FirstAsync(cancellationToken);
        return new StagingChunkResponse(
            itemId, chunkIndex, AlreadyReceived: false,
            fresh.Status, fresh.ReceivedChunkCount, fresh.ExpectedChunkCount);
    }

    // ---- verification ------------------------------------------------------------

    public async Task<StagingVerifyResponse?> VerifyAsync(
        Guid userId, Guid sessionId, CancellationToken cancellationToken)
    {
        var root = ResolveStagingRoot();
        var session = await GetOwnedSessionAsync(userId, sessionId, cancellationToken);
        if (session is null) return null;
        if (session.Status == RemoteUploadSessionStatuses.ReadyToImport)
        {
            // Idempotent: re-verifying a verified session is a no-op.
            return new StagingVerifyResponse(
                sessionId, session.Status, session.VerifiedFiles, 0, 0, ReadyToImport: true);
        }
        if (session.Status is not (RemoteUploadSessionStatuses.ManifestReceived
            or RemoteUploadSessionStatuses.Uploading))
        {
            throw new StagingConflictException(
                $"This session cannot be verified (status: {session.Status}).");
        }

        var now = UtcNow;
        await _db.RemoteUploadSessions.Where(s => s.Id == sessionId).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.Status, RemoteUploadSessionStatuses.Verifying)
            .SetProperty(x => x.UpdatedAt, now), cancellationToken);

        // Bounded synchronous verification: per item it is one chunk-count
        // lookup (grouped per page) + one filesystem stat — no hashing, no
        // byte reads. Even tens of thousands of files stay responsive.
        var verified = 0;
        var incomplete = 0;
        var corrupt = 0;
        var skipped = 0;
        var lastOrdinal = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await _db.RemoteUploadItems.AsNoTracking()
                .Where(i => i.SessionId == sessionId && i.Ordinal > lastOrdinal)
                .OrderBy(i => i.Ordinal)
                .Take(VerifyPageSize)
                .ToListAsync(cancellationToken);
            if (page.Count == 0) break;
            lastOrdinal = page[^1].Ordinal;

            var ids = page.Select(i => i.Id).ToList();
            var chunkCounts = await _db.RemoteUploadChunks.AsNoTracking()
                .Where(c => ids.Contains(c.ItemId))
                .GroupBy(c => c.ItemId)
                .Select(g => new { ItemId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(g => g.ItemId, g => g.Count, cancellationToken);

            foreach (var item in page)
            {
                if (item.Status == RemoteUploadItemStatuses.Skipped) { skipped++; continue; }
                if (item.Status == RemoteUploadItemStatuses.Verified) { verified++; continue; }

                var chunkCount = chunkCounts.GetValueOrDefault(item.Id);
                if (chunkCount < item.ExpectedChunkCount)
                {
                    incomplete++;
                    continue;
                }

                var absolute = ResolveItemPath(root, session, item.RelativePath);
                string? failure = null;
                try
                {
                    if (item.SizeBytes == 0)
                    {
                        // 0-byte files have no chunks; materialise the empty file.
                        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
                        if (!File.Exists(absolute)) File.Create(absolute).Dispose();
                    }
                    else if (!File.Exists(absolute))
                    {
                        failure = RemoteUploadFailureCodes.FileMissing;
                    }
                    else if (new FileInfo(absolute).Length != item.SizeBytes)
                    {
                        failure = RemoteUploadFailureCodes.SizeMismatch;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failure = RemoteUploadFailureCodes.IoError;
                }

                if (failure is null)
                {
                    await _db.RemoteUploadItems.Where(i => i.Id == item.Id).ExecuteUpdateAsync(s => s
                        .SetProperty(i => i.Status, RemoteUploadItemStatuses.Verified)
                        .SetProperty(i => i.UpdatedAt, now), cancellationToken);
                    verified++;
                }
                else
                {
                    // Staged bytes are wrong/unreadable: wipe the chunk state so
                    // the client re-uploads this file from scratch.
                    await _db.RemoteUploadChunks
                        .Where(c => c.ItemId == item.Id)
                        .ExecuteDeleteAsync(cancellationToken);
                    await _db.RemoteUploadItems.Where(i => i.Id == item.Id).ExecuteUpdateAsync(s => s
                        .SetProperty(i => i.Status, RemoteUploadItemStatuses.Pending)
                        .SetProperty(i => i.ReceivedBytes, 0L)
                        .SetProperty(i => i.ReceivedChunkCount, 0)
                        .SetProperty(i => i.FailureCode, failure)
                        .SetProperty(i => i.FailureMessage, "Verification failed; please re-upload this file.")
                        .SetProperty(i => i.UpdatedAt, now), cancellationToken);
                    corrupt++;
                }
            }
        }

        var ready = incomplete == 0 && corrupt == 0;
        // Refresh session counters wholesale from item state (authoritative).
        var receivedFiles = await _db.RemoteUploadItems.CountAsync(
            i => i.SessionId == sessionId
                && (i.Status == RemoteUploadItemStatuses.Uploaded
                    || i.Status == RemoteUploadItemStatuses.Verified),
            cancellationToken);
        var receivedBytes = await _db.RemoteUploadItems
            .Where(i => i.SessionId == sessionId
                && (i.Status == RemoteUploadItemStatuses.Uploaded
                    || i.Status == RemoteUploadItemStatuses.Verified))
            .SumAsync(i => (long?)i.SizeBytes, cancellationToken) ?? 0L;
        await _db.RemoteUploadSessions.Where(s => s.Id == sessionId).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.Status, ready
                ? RemoteUploadSessionStatuses.ReadyToImport
                : RemoteUploadSessionStatuses.Uploading)
            .SetProperty(x => x.VerifiedFiles, verified)
            .SetProperty(x => x.ReceivedFiles, receivedFiles)
            .SetProperty(x => x.ReceivedBytes, receivedBytes)
            .SetProperty(x => x.UpdatedAt, UtcNow), cancellationToken);

        return new StagingVerifyResponse(
            sessionId,
            ready ? RemoteUploadSessionStatuses.ReadyToImport : RemoteUploadSessionStatuses.Uploading,
            verified, incomplete, corrupt, ready);
    }

    // ---- import handoff -----------------------------------------------------------

    public async Task<StagingImportStartResponse?> StartImportAsync(
        Guid userId, Guid sessionId, CancellationToken cancellationToken)
    {
        ResolveStagingRoot();
        var session = await GetOwnedSessionAsync(userId, sessionId, cancellationToken);
        if (session is null) return null;
        if (session.Status != RemoteUploadSessionStatuses.ReadyToImport)
        {
            throw new StagingConflictException(
                $"This session is not ready to import (status: {session.Status}).");
        }
        // Re-validate the destination right before handoff (it may have been
        // deleted since session creation).
        if (session.DestinationFolderId is { } destId)
        {
            var folderOk = await _db.Folders.AsNoTracking().AnyAsync(
                f => f.Id == destId && f.OwnerUserId == session.TargetUserId && f.DeletedAt == null,
                cancellationToken);
            if (!folderOk)
            {
                throw new StagingValidationException("The destination folder no longer exists.");
            }
        }

        // The verified staging manifest BECOMES the import manifest: the run is
        // created with ScanCompletedAt already set and its admin_import_items
        // pre-populated from the verified items, so the import job skips the
        // scan phase entirely (no second walk) and inherits everything else —
        // resume by item state, quota, dedup, conflicts, metadata extraction,
        // and the end-of-run derivatives job.
        var now = UtcNow;
        var verifiedItems = _db.RemoteUploadItems.AsNoTracking()
            .Where(i => i.SessionId == sessionId && i.Status == RemoteUploadItemStatuses.Verified)
            .OrderBy(i => i.Ordinal);
        var totalFiles = await verifiedItems.CountAsync(cancellationToken);
        var totalBytes = await verifiedItems.SumAsync(i => (long?)i.SizeBytes, cancellationToken) ?? 0L;
        if (totalFiles == 0)
        {
            throw new StagingConflictException("This session has no verified files to import.");
        }

        var run = new AdminImportRun
        {
            Id = Guid.NewGuid(),
            AdminUserId = session.CreatedByUserId,
            TargetUserId = session.TargetUserId,
            DestinationFolderId = session.DestinationFolderId,
            SkipPreviouslyDeleted = session.SkipPreviouslyDeleted,
            SkipExistingContent = session.SkipExistingContent,
            RootId = "staging",
            SourceRelativePath = string.Empty,
            StagingSessionId = session.Id,
            Status = AdminImportStatuses.Queued,
            ScanCompletedAt = now,
            ScannedFiles = totalFiles,
            TotalBytes = totalBytes,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.AdminImportRuns.Add(run);
        await _db.SaveChangesAsync(cancellationToken);
        _db.Entry(run).State = EntityState.Detached;

        var ordinal = 0;
        var dirs = new HashSet<string>(StringComparer.Ordinal);
        var batch = new List<AdminImportItem>(InsertBatchSize);
        var lastItemOrdinal = 0;
        while (true)
        {
            var page = await _db.RemoteUploadItems.AsNoTracking()
                .Where(i => i.SessionId == sessionId
                    && i.Status == RemoteUploadItemStatuses.Verified
                    && i.Ordinal > lastItemOrdinal)
                .OrderBy(i => i.Ordinal)
                .Take(InsertBatchSize)
                .Select(i => new { i.Ordinal, i.RelativePath, i.SizeBytes })
                .ToListAsync(cancellationToken);
            if (page.Count == 0) break;
            lastItemOrdinal = page[^1].Ordinal;

            foreach (var staged in page)
            {
                var slash = staged.RelativePath.LastIndexOf('/');
                if (slash > 0) dirs.Add(staged.RelativePath[..slash]);
                batch.Add(new AdminImportItem
                {
                    Id = Guid.NewGuid(),
                    ImportRunId = run.Id,
                    Ordinal = ++ordinal,
                    Kind = AdminImportItemKinds.File,
                    RelativePath = staged.RelativePath,
                    SizeBytes = staged.SizeBytes,
                    // Null: the staged file's mtime is its upload time, not the
                    // manifest mtime — the import's drift check uses size only.
                    SourceModifiedAt = null,
                    Status = AdminImportItemStatuses.Pending,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
                if (batch.Count >= InsertBatchSize)
                {
                    _db.AdminImportItems.AddRange(batch);
                    await _db.SaveChangesAsync(cancellationToken);
                    foreach (var b in batch) _db.Entry(b).State = EntityState.Detached;
                    batch.Clear();
                }
            }
        }
        if (batch.Count > 0)
        {
            _db.AdminImportItems.AddRange(batch);
            await _db.SaveChangesAsync(cancellationToken);
            foreach (var b in batch) _db.Entry(b).State = EntityState.Detached;
            batch.Clear();
        }
        await _db.AdminImportRuns.Where(r => r.Id == run.Id).ExecuteUpdateAsync(s => s
            .SetProperty(r => r.TotalDirectories, dirs.Count), cancellationToken);

        var job = await _jobs.EnqueueAsync(
            JobTypes.AdminImport,
            new AdminImportJobPayload(run.Id),
            idempotencyKey: $"admin-import:{run.Id:N}",
            cancellationToken: cancellationToken);
        await _db.AdminImportRuns.Where(r => r.Id == run.Id).ExecuteUpdateAsync(s => s
            .SetProperty(r => r.JobId, job.Id), cancellationToken);

        await _db.RemoteUploadSessions.Where(s => s.Id == sessionId).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.Status, RemoteUploadSessionStatuses.Importing)
            .SetProperty(x => x.AdminImportRunId, run.Id)
            .SetProperty(x => x.UpdatedAt, UtcNow), cancellationToken);

        _logger?.LogInformation(
            "staging: session {SessionId} handed off to import run {RunId} (job {JobId}).",
            sessionId, run.Id, job.Id);
        return new StagingImportStartResponse(
            sessionId, RemoteUploadSessionStatuses.Importing, run.Id, job.Id);
    }

    // ---- cancel / delete ------------------------------------------------------------

    public async Task<StagingCancelResponse?> CancelAsync(
        Guid userId, Guid sessionId, CancellationToken cancellationToken)
    {
        var session = await GetOwnedSessionAsync(userId, sessionId, cancellationToken);
        if (session is null) return null;

        if (session.Status == RemoteUploadSessionStatuses.Cancelled)
        {
            return new StagingCancelResponse(sessionId, session.Status, CancellationRequested: false);
        }
        if (session.Status is RemoteUploadSessionStatuses.Imported
            or RemoteUploadSessionStatuses.Failed
            or RemoteUploadSessionStatuses.Expired)
        {
            throw new StagingConflictException("This session is already finished.");
        }

        if (session.Status == RemoteUploadSessionStatuses.Importing)
        {
            // Delegate to the import run's cancellation path (the same single
            // source of truth the admin import/jobs dashboards use). It flags
            // the job, freezes a never-to-run queued job's items, and syncs
            // this session — either immediately (queued) or when the running
            // handler finalizes.
            var requested = false;
            if (session.AdminImportRunId is Guid runId)
            {
                var result = await _import.RequestCancelAsync(runId, cancellationToken);
                requested = result?.CancellationRequested ?? false;
            }
            var current = await _db.RemoteUploadSessions.AsNoTracking()
                .Where(s => s.Id == sessionId)
                .Select(s => s.Status)
                .FirstAsync(cancellationToken);
            return new StagingCancelResponse(sessionId, current, requested);
        }

        var now = UtcNow;
        await _db.RemoteUploadSessions.Where(s => s.Id == sessionId).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.Status, RemoteUploadSessionStatuses.Cancelled)
            .SetProperty(x => x.CompletedAt, now)
            .SetProperty(x => x.UpdatedAt, now), cancellationToken);
        return new StagingCancelResponse(
            sessionId, RemoteUploadSessionStatuses.Cancelled, CancellationRequested: true);
    }

    public async Task<bool?> DeleteAsync(Guid userId, Guid sessionId, CancellationToken cancellationToken)
    {
        var session = await GetOwnedSessionAsync(userId, sessionId, cancellationToken);
        if (session is null) return null;
        if (session.Status == RemoteUploadSessionStatuses.Importing
            && await LinkedImportIsActiveAsync(session, cancellationToken))
        {
            // The import job may still be reading these staged files — the
            // user must cancel it first.
            return false;
        }

        await DeleteSessionRowsAndFilesAsync(session, cancellationToken);
        return true;
    }

    // Slice 97 (bug 2): `importing` blocks discard only while the linked
    // run/job can actually still read the staged files. A session whose linked
    // run or job is terminal (failed/cancelled/succeeded/partial) — or whose
    // run/job rows are gone entirely — is STALE bookkeeping (e.g. a
    // permanently-failed import recorded before the failure-sync existed) and
    // must remain discardable, otherwise it is stuck forever: undeletable and
    // exempt from expiry.
    private async Task<bool> LinkedImportIsActiveAsync(
        RemoteUploadSession session, CancellationToken cancellationToken)
    {
        if (session.AdminImportRunId is not Guid runId)
        {
            return false; // importing with no linked run: stale
        }

        var run = await _db.AdminImportRuns.AsNoTracking()
            .Where(r => r.Id == runId)
            .Select(r => new { r.Status, r.JobId })
            .FirstOrDefaultAsync(cancellationToken);
        if (run is null)
        {
            return false; // linked run row gone: stale
        }
        if (run.Status is AdminImportStatuses.Succeeded
            or AdminImportStatuses.Partial
            or AdminImportStatuses.Failed
            or AdminImportStatuses.Cancelled)
        {
            return false; // run already terminal
        }

        // Queued/running/paused run: the linked job decides. run.JobId always
        // points at the slice that will execute next (pause re-points it), so
        // a terminal or missing job row means nothing will read the files.
        if (run.JobId is not Guid jobId)
        {
            return false;
        }
        var jobStatus = await _db.BackgroundJobs.AsNoTracking()
            .Where(j => j.Id == jobId)
            .Select(j => j.Status)
            .FirstOrDefaultAsync(cancellationToken);
        return jobStatus is not null && !JobStatuses.IsTerminal(jobStatus);
    }

    // Shared by DELETE and the cleanup sweeper. Rows first (chunks → items →
    // session, FK order), staging directory last (a crash in between leaves an
    // orphan directory the sweeper can reclaim, never dangling rows).
    internal async Task DeleteSessionRowsAndFilesAsync(
        RemoteUploadSession session, CancellationToken cancellationToken)
    {
        var itemIds = _db.RemoteUploadItems
            .Where(i => i.SessionId == session.Id)
            .Select(i => i.Id);
        await _db.RemoteUploadChunks
            .Where(c => itemIds.Contains(c.ItemId))
            .ExecuteDeleteAsync(cancellationToken);
        await _db.RemoteUploadItems
            .Where(i => i.SessionId == session.Id)
            .ExecuteDeleteAsync(cancellationToken);
        await _db.RemoteUploadSessions
            .Where(s => s.Id == session.Id)
            .ExecuteDeleteAsync(cancellationToken);
        TryDeleteSessionDirectory(session);
    }

    internal void TryDeleteSessionDirectory(RemoteUploadSession session)
    {
        try
        {
            var dir = SessionDirectory(ResolveStagingRoot(), session);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or StagingUnavailableException)
        {
            // Best effort; the sweeper retries on its next pass. Counts only.
            _logger?.LogWarning("staging: could not delete a session directory ({Type}).", ex.GetType().Name);
        }
    }

    // ---- path / root safety ------------------------------------------------------

    // Validates + canonicalizes the staging root on first use. Throws
    // StagingUnavailableException when the feature is off or unconfigured, and
    // StagingValidationException when the root overlaps NubArca's internal
    // blob storage roots.
    internal string ResolveStagingRoot()
    {
        var opts = _options.Value;
        if (!opts.Enabled || string.IsNullOrWhiteSpace(opts.RootPath))
        {
            throw new StagingUnavailableException(
                "Staged uploads are disabled. Set Staging__Enabled=true and Staging__RootPath.");
        }
        string canonical;
        try
        {
            canonical = Path.GetFullPath(opts.RootPath.Trim())
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception)
        {
            throw new StagingUnavailableException("The configured staging root is not a valid path.");
        }
        var storage = _storage.Value;
        foreach (var internalRoot in new[] { storage.RootPath, storage.EffectiveDerivedRootPath })
        {
            if (string.IsNullOrWhiteSpace(internalRoot)) continue;
            string other;
            try
            {
                other = Path.GetFullPath(internalRoot)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch { continue; }
            if (IsWithin(canonical, other) || IsWithin(other, canonical))
            {
                throw new StagingUnavailableException(
                    "The staging root must not overlap NubArca blob storage.");
            }
        }
        Directory.CreateDirectory(canonical);
        return canonical;
    }

    private static string SessionDirectory(string root, RemoteUploadSession session)
        => Path.Combine(root, session.Id.ToString("N"));

    private static string SessionFilesDirectory(string root, RemoteUploadSession session)
        => Path.Combine(SessionDirectory(root, session), "files");

    // Resolves a validated relative path inside the session's files directory
    // and re-checks containment (belt and braces).
    private static string ResolveItemPath(string root, RemoteUploadSession session, string relativePath)
    {
        var filesDir = SessionFilesDirectory(root, session);
        var absolute = Path.GetFullPath(Path.Combine(
            filesDir, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsWithin(absolute, filesDir))
        {
            throw new StagingValidationException("Invalid path.");
        }
        return absolute;
    }

    private static bool IsWithin(string child, string parent)
    {
        if (string.Equals(child, parent, StringComparison.Ordinal)) return true;
        var prefix = parent.EndsWith(Path.DirectorySeparatorChar) ? parent : parent + Path.DirectorySeparatorChar;
        return child.StartsWith(prefix, StringComparison.Ordinal);
    }

    // Manifest path policy: relative, forward-slash normalized, no traversal,
    // no absolute/drive-prefixed paths, bounded lengths, printable segments.
    internal static bool TryNormalizeRelativePath(string? raw, out string normalized, out string reason)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(raw)) { reason = "empty path"; return false; }
        var candidate = raw.Replace('\\', '/').Trim();
        if (candidate.Length > MaxRelativePathLength) { reason = "path too long"; return false; }
        if (candidate.StartsWith('/')) { reason = "absolute paths are not allowed"; return false; }
        if (candidate.Contains(':')) { reason = "drive-prefixed paths are not allowed"; return false; }

        var segments = candidate.Split('/');
        foreach (var segment in segments)
        {
            if (segment.Length == 0) { reason = "empty path segment"; return false; }
            if (segment is "." or "..") { reason = "path traversal is not allowed"; return false; }
            if (segment.Length > MaxSegmentLength) { reason = "path segment too long"; return false; }
            foreach (var ch in segment)
            {
                if (char.IsControl(ch)) { reason = "control characters are not allowed"; return false; }
            }
        }

        normalized = string.Join('/', segments);
        reason = string.Empty;
        return true;
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max];
}
