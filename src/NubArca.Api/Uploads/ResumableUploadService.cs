using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Storage;

namespace NubArca.Api.Uploads;

public sealed record ResumableUploadConfigResponse(bool Enabled, int ChunkSizeBytes, long MaxFileBytes);

public sealed record ResumableUploadCreateRequest(string? FileName, long SizeBytes, string? ContentType = null);

public sealed record ResumableUploadResponse(
    Guid UploadId,
    string Status,
    long SizeBytes,
    int ChunkSizeBytes,
    int ExpectedChunkCount,
    IReadOnlyList<int> ReceivedChunks,
    DateTime ExpiresAt);

public sealed record ResumableChunkResponse(
    int ChunkIndex,
    bool AlreadyReceived,
    int ReceivedChunkCount,
    int ExpectedChunkCount);

// Either a folder destination (ParentFolderId/RelativePath/Conflict) or an
// album (AlbumId), which files the upload under "Album/<name>" itself.
public sealed record ResumableUploadCompleteRequest(
    Guid? ParentFolderId,
    string? RelativePath,
    string? Conflict,
    Guid? AlbumId = null);

public sealed class ResumableUploadUnavailableException(string message) : Exception(message);
public sealed class ResumableUploadValidationException(string message) : Exception(message);
public sealed class ResumableUploadConflictException(string message, string code) : Exception(message)
{
    public string Code { get; } = code;
}

public interface IResumableUploadService
{
    ResumableUploadConfigResponse GetConfig();
    Task<ResumableUploadResponse> CreateAsync(
        Guid ownerUserId, ResumableUploadCreateRequest request, CancellationToken cancellationToken);
    Task<ResumableUploadResponse?> GetAsync(Guid ownerUserId, Guid uploadId, CancellationToken cancellationToken);
    Task<ResumableChunkResponse?> ReceiveChunkAsync(
        Guid ownerUserId, Guid uploadId, int chunkIndex, Stream body, long? contentLength,
        CancellationToken cancellationToken);

    // The upload, ready to ingest, or why not. Null = not found / foreign.
    Task<ResumableUpload?> GetForCompletionAsync(Guid ownerUserId, Guid uploadId, CancellationToken cancellationToken);
    Stream OpenAssembled(ResumableUpload upload);
    Task MarkCompletedAsync(ResumableUpload upload, Guid fileItemId, CancellationToken cancellationToken);

    Task<bool> DeleteAsync(Guid ownerUserId, Guid uploadId, CancellationToken cancellationToken);
    Task<int> SweepExpiredAsync(CancellationToken cancellationToken);

    // The replay key that makes a completion a single logical operation.
    static string OperationKey(Guid uploadId) => $"resumable-{uploadId:N}";
}

// Chunked owner uploads. The server is the source of truth for what has
// arrived: a client that lost its place asks, and sends only what is missing.
//
// Safety model: the temporary file is named by the upload id only (never by
// the client's name or path), lives under one server-chosen directory, and is
// pre-sized so every chunk is written at offset index × chunkSize. A chunk row
// is recorded only after its exact byte range was written and flushed, so an
// interrupted request leaves the chunk "missing" and a retry overwrites it.
// The client's name and size are hints; the ingest content-addresses and
// detects the bytes like any other upload.
public sealed class ResumableUploadService : IResumableUploadService
{
    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;
    private readonly ResumableUploadOptions _options;
    private readonly BlobStorageOptions _storage;
    private readonly UploadOptions _uploads;
    private readonly ILogger<ResumableUploadService> _logger;

    public ResumableUploadService(
        AppDbContext db,
        TimeProvider clock,
        IOptions<ResumableUploadOptions> options,
        IOptions<BlobStorageOptions> storage,
        IOptions<UploadOptions> uploads,
        ILogger<ResumableUploadService> logger)
    {
        _db = db;
        _clock = clock;
        _options = options.Value;
        _storage = storage.Value;
        _uploads = uploads.Value;
        _logger = logger;
    }

    private DateTime UtcNow => _clock.GetUtcNow().UtcDateTime;

    private TimeSpan Ttl => TimeSpan.FromHours(Math.Max(1, _options.TtlHours));

    // The same ceilings a single multipart upload meets: the app-level
    // Storage:MaxUploadBytes, and the per-part limit operators configured.
    private long MaxFileBytes
    {
        get
        {
            var limits = new[] { _storage.MaxUploadBytes, _uploads.MaxFileSizeBytes }.Where(v => v > 0).ToArray();
            return limits.Length == 0 ? long.MaxValue : limits.Min();
        }
    }

    public ResumableUploadConfigResponse GetConfig()
    {
        var enabled = _options.Enabled && TryResolveRoot(out _);
        return new ResumableUploadConfigResponse(enabled, _options.EffectiveChunkSizeBytes, MaxFileBytes);
    }

    public async Task<ResumableUploadResponse> CreateAsync(
        Guid ownerUserId, ResumableUploadCreateRequest request, CancellationToken cancellationToken)
    {
        var root = ResolveRoot();

        var fileName = Path.GetFileName((request.FileName ?? string.Empty).Replace('\\', '/')).Trim();
        if (fileName.Length == 0)
        {
            throw new ResumableUploadValidationException("Missing 'fileName'.");
        }
        // Validated now with the exact rule the ingest applies later, so a bad
        // name is refused before a single chunk is sent.
        try { FileItemService.ValidateAndTrimName(fileName); }
        catch (ArgumentException ex) { throw new ResumableUploadValidationException(ex.Message); }

        if (request.SizeBytes <= 0)
        {
            throw new ResumableUploadValidationException("'sizeBytes' must be positive.");
        }
        if (request.SizeBytes > MaxFileBytes)
        {
            throw new UploadTooLargeException(MaxFileBytes);
        }

        // An early, honest refusal: the authoritative quota check still runs
        // inside the ingest transaction.
        if (_storage.DefaultUserQuotaBytes > 0)
        {
            var used = await _db.FileItems
                .Where(f => f.OwnerUserId == ownerUserId)
                .SumAsync(f => (long?)f.SizeBytes, cancellationToken) ?? 0L;
            if (used + request.SizeBytes > _storage.DefaultUserQuotaBytes)
            {
                throw new QuotaExceededException(_storage.DefaultUserQuotaBytes, used, request.SizeBytes);
            }
        }

        var now = UtcNow;
        var active = await _db.ResumableUploads.CountAsync(
            u => u.OwnerUserId == ownerUserId
                && u.Status == ResumableUploadStatuses.Uploading
                && u.ExpiresAt > now,
            cancellationToken);
        if (active >= Math.Max(1, _options.MaxActiveUploadsPerUser))
        {
            throw new ResumableUploadConflictException(
                "Too many unfinished uploads. Finish or cancel some first.", "too_many_uploads");
        }

        var chunkSize = _options.EffectiveChunkSizeBytes;
        var upload = new ResumableUpload
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerUserId,
            FileName = fileName,
            ContentType = string.IsNullOrWhiteSpace(request.ContentType) || request.ContentType.Length > 255
                ? null
                : request.ContentType.Trim(),
            SizeBytes = request.SizeBytes,
            ChunkSizeBytes = chunkSize,
            ExpectedChunkCount = (int)((request.SizeBytes + chunkSize - 1) / chunkSize),
            Status = ResumableUploadStatuses.Uploading,
            CreatedAt = now,
            UpdatedAt = now,
            ExpiresAt = now + Ttl,
        };

        // Reserve the full size up front (sparse where the filesystem allows)
        // so chunk writes at any offset and the completion size check agree.
        Directory.CreateDirectory(root);
        await using (var fs = new FileStream(PartPath(root, upload.Id), FileMode.CreateNew, FileAccess.Write))
        {
            fs.SetLength(upload.SizeBytes);
        }

        _db.ResumableUploads.Add(upload);
        await _db.SaveChangesAsync(cancellationToken);
        _db.Entry(upload).State = EntityState.Detached;

        return ToResponse(upload, []);
    }

    public async Task<ResumableUploadResponse?> GetAsync(
        Guid ownerUserId, Guid uploadId, CancellationToken cancellationToken)
    {
        var upload = await FindOwnedAsync(ownerUserId, uploadId, cancellationToken);
        if (upload is null) return null;
        var received = await _db.ResumableUploadChunks.AsNoTracking()
            .Where(c => c.UploadId == uploadId)
            .OrderBy(c => c.ChunkIndex)
            .Select(c => c.ChunkIndex)
            .ToListAsync(cancellationToken);
        return ToResponse(upload, received);
    }

    public async Task<ResumableChunkResponse?> ReceiveChunkAsync(
        Guid ownerUserId, Guid uploadId, int chunkIndex, Stream body, long? contentLength,
        CancellationToken cancellationToken)
    {
        var root = ResolveRoot();
        var upload = await FindOwnedAsync(ownerUserId, uploadId, cancellationToken);
        if (upload is null) return null;
        if (upload.Status != ResumableUploadStatuses.Uploading)
        {
            throw new ResumableUploadConflictException("This upload is already complete.", "upload_completed");
        }
        if (chunkIndex < 0 || chunkIndex >= upload.ExpectedChunkCount)
        {
            throw new ResumableUploadValidationException("Chunk index is out of range for this file.");
        }

        // A chunk sent twice (a retry after a lost response) is a safe no-op.
        var alreadyReceived = await _db.ResumableUploadChunks.AsNoTracking()
            .AnyAsync(c => c.UploadId == uploadId && c.ChunkIndex == chunkIndex, cancellationToken);
        if (alreadyReceived)
        {
            return new ResumableChunkResponse(chunkIndex, true, upload.ReceivedChunkCount, upload.ExpectedChunkCount);
        }

        var expectedSize = ChunkLength(upload, chunkIndex);
        if (contentLength is { } declared && declared != expectedSize)
        {
            throw new ResumableUploadValidationException(
                $"Chunk {chunkIndex} must be exactly {expectedSize} bytes.");
        }

        var path = PartPath(root, upload.Id);
        if (!File.Exists(path))
        {
            // The temporary file went away (an operator cleaned the volume).
            // The upload cannot be finished; the client starts a new one.
            throw new ResumableUploadConflictException("This upload can no longer be resumed.", "upload_lost");
        }

        long written = 0;
        try
        {
            await using var fs = new FileStream(
                path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite,
                bufferSize: 81920, options: FileOptions.Asynchronous);
            fs.Seek((long)chunkIndex * upload.ChunkSizeBytes, SeekOrigin.Begin);
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
                throw new ResumableUploadValidationException(
                    $"Chunk {chunkIndex} was shorter than the expected {expectedSize} bytes.");
            }
            if (await body.ReadAsync(buffer.AsMemory(0, 1), cancellationToken) > 0)
            {
                throw new ResumableUploadValidationException(
                    $"Chunk {chunkIndex} was longer than the expected {expectedSize} bytes.");
            }
            await fs.FlushAsync(cancellationToken);
        }
        catch (IOException)
        {
            throw new ResumableUploadValidationException("The chunk could not be written.");
        }

        var now = UtcNow;
        _db.ResumableUploadChunks.Add(new ResumableUploadChunk
        {
            UploadId = uploadId,
            ChunkIndex = chunkIndex,
            SizeBytes = (int)expectedSize,
            ReceivedAt = now,
        });
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // A concurrent twin recorded the same chunk first: same bytes, same
            // range, so its receipt stands for both.
            _db.ChangeTracker.Clear();
            return new ResumableChunkResponse(chunkIndex, true, upload.ReceivedChunkCount, upload.ExpectedChunkCount);
        }

        await _db.ResumableUploads.Where(u => u.Id == uploadId).ExecuteUpdateAsync(s => s
            .SetProperty(u => u.ReceivedChunkCount, u => u.ReceivedChunkCount + 1)
            .SetProperty(u => u.UpdatedAt, now)
            .SetProperty(u => u.ExpiresAt, now + Ttl), cancellationToken);

        var count = await _db.ResumableUploads.AsNoTracking()
            .Where(u => u.Id == uploadId)
            .Select(u => u.ReceivedChunkCount)
            .FirstAsync(cancellationToken);
        return new ResumableChunkResponse(chunkIndex, false, count, upload.ExpectedChunkCount);
    }

    public async Task<ResumableUpload?> GetForCompletionAsync(
        Guid ownerUserId, Guid uploadId, CancellationToken cancellationToken)
    {
        var upload = await FindOwnedAsync(ownerUserId, uploadId, cancellationToken);
        if (upload is null || upload.Status == ResumableUploadStatuses.Completed) return upload;

        var root = ResolveRoot();
        // The chunk rows, not the counter, are authoritative.
        var received = await _db.ResumableUploadChunks
            .CountAsync(c => c.UploadId == uploadId, cancellationToken);
        if (received < upload.ExpectedChunkCount)
        {
            throw new ResumableUploadConflictException(
                $"The upload is missing {upload.ExpectedChunkCount - received} chunk(s).", "upload_incomplete");
        }
        var path = PartPath(root, upload.Id);
        if (!File.Exists(path) || new FileInfo(path).Length != upload.SizeBytes)
        {
            throw new ResumableUploadConflictException("This upload can no longer be resumed.", "upload_lost");
        }
        return upload;
    }

    public Stream OpenAssembled(ResumableUpload upload) => new FileStream(
        PartPath(ResolveRoot(), upload.Id), FileMode.Open, FileAccess.Read, FileShare.Read,
        bufferSize: 81920, options: FileOptions.Asynchronous | FileOptions.SequentialScan);

    public async Task MarkCompletedAsync(ResumableUpload upload, Guid fileItemId, CancellationToken cancellationToken)
    {
        var now = UtcNow;
        // The row outlives the bytes for a short replay window, so a completion
        // whose response was lost still answers with the same file.
        await _db.ResumableUploads.Where(u => u.Id == upload.Id).ExecuteUpdateAsync(s => s
            .SetProperty(u => u.Status, ResumableUploadStatuses.Completed)
            .SetProperty(u => u.FileItemId, fileItemId)
            .SetProperty(u => u.CompletedAt, now)
            .SetProperty(u => u.UpdatedAt, now)
            .SetProperty(u => u.ExpiresAt, now + Ttl), CancellationToken.None);
        await _db.ResumableUploadChunks.Where(c => c.UploadId == upload.Id)
            .ExecuteDeleteAsync(CancellationToken.None);
        TryDeletePart(upload.Id);
    }

    public async Task<bool> DeleteAsync(Guid ownerUserId, Guid uploadId, CancellationToken cancellationToken)
    {
        var upload = await FindOwnedAsync(ownerUserId, uploadId, cancellationToken);
        if (upload is null) return false;
        await DeleteRowsAsync(uploadId, cancellationToken);
        TryDeletePart(uploadId);
        return true;
    }

    public async Task<int> SweepExpiredAsync(CancellationToken cancellationToken)
    {
        var now = UtcNow;
        var expired = await _db.ResumableUploads.AsNoTracking()
            .Where(u => u.ExpiresAt < now)
            .OrderBy(u => u.ExpiresAt)
            .Select(u => u.Id)
            .Take(500)
            .ToListAsync(cancellationToken);
        foreach (var id in expired)
        {
            await DeleteRowsAsync(id, cancellationToken);
            TryDeletePart(id);
        }

        // Temporary files whose row is gone (a crash between the two deletes,
        // or rows removed with their user) are reclaimed once they are older
        // than any live upload could be.
        if (TryResolveRoot(out var root) && Directory.Exists(root))
        {
            var cutoff = now - Ttl - TimeSpan.FromHours(1);
            foreach (var path in Directory.EnumerateFiles(root, "*.part"))
            {
                if (File.GetLastWriteTimeUtc(path) >= cutoff) continue;
                if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out var id)) continue;
                if (await _db.ResumableUploads.AnyAsync(u => u.Id == id, cancellationToken)) continue;
                TryDeletePart(id);
            }
        }
        return expired.Count;
    }

    // ---- helpers ------------------------------------------------------------

    private async Task<ResumableUpload?> FindOwnedAsync(
        Guid ownerUserId, Guid uploadId, CancellationToken cancellationToken)
    {
        var upload = await _db.ResumableUploads.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == uploadId && u.OwnerUserId == ownerUserId, cancellationToken);
        // An expired upload is gone as far as the client is concerned, even
        // before the sweeper reaches it.
        return upload is null || upload.ExpiresAt < UtcNow ? null : upload;
    }

    private async Task DeleteRowsAsync(Guid uploadId, CancellationToken cancellationToken)
    {
        await _db.ResumableUploadChunks.Where(c => c.UploadId == uploadId).ExecuteDeleteAsync(cancellationToken);
        await _db.ResumableUploads.Where(u => u.Id == uploadId).ExecuteDeleteAsync(cancellationToken);
    }

    private static long ChunkLength(ResumableUpload upload, int chunkIndex) =>
        chunkIndex < upload.ExpectedChunkCount - 1
            ? upload.ChunkSizeBytes
            : upload.SizeBytes - (long)(upload.ExpectedChunkCount - 1) * upload.ChunkSizeBytes;

    private static ResumableUploadResponse ToResponse(ResumableUpload upload, IReadOnlyList<int> received) => new(
        upload.Id, upload.Status, upload.SizeBytes, upload.ChunkSizeBytes,
        upload.ExpectedChunkCount, received, upload.ExpiresAt);

    private static string PartPath(string root, Guid uploadId) => Path.Combine(root, $"{uploadId:N}.part");

    private void TryDeletePart(Guid uploadId)
    {
        if (!TryResolveRoot(out var root)) return;
        try
        {
            File.Delete(PartPath(root, uploadId));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The sweeper's orphan pass retries. Never logs a path.
            _logger.LogWarning("resumable upload: could not delete a temporary file ({Type}).", ex.GetType().Name);
        }
    }

    private string ResolveRoot() => TryResolveRoot(out var root)
        ? root
        : throw new ResumableUploadUnavailableException("Resumable uploads are not available on this server.");

    private bool TryResolveRoot(out string root)
    {
        root = string.Empty;
        if (!_options.Enabled) return false;
        var configured = !string.IsNullOrWhiteSpace(_options.RootPath)
            ? _options.RootPath
            : string.IsNullOrWhiteSpace(_storage.RootPath) ? null : Path.Combine(_storage.RootPath, "resumable");
        if (configured is null) return false;
        try
        {
            root = Path.GetFullPath(configured);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
