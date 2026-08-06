using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Jobs;

namespace NubArca.Api.PhotoExport;

// Owner-private photo-archive export. Creates a read-only session, builds a
// stable SNAPSHOT of exportable photos (PhotoExportEntry rows) via a background
// job, and serves a streamed manifest + per-file original content. Preserves the
// current logical NubArca folder tree (never reorganized by date). Never
// exposes StorageKey / BlobObjectId / SHA / physical paths / raw metadata; the
// token is stored hashed (mirrors the share-link pattern).
public sealed class PhotoExportService
{
    // 32 random bytes (256-bit) → URL-safe base64; SHA-256 hex hash is stored.
    private const int TokenBytes = 32;
    // Snapshot build batch (keyset-paged). Bounded so a huge library never loads
    // into memory at once.
    private const int BuildBatchSize = 500;
    // Manifest streaming page size.
    public const int ManifestPageSize = 1000;
    // How long a session (and its token) stays valid from creation.
    public const int DefaultRetentionDays = 7;

    private readonly AppDbContext _db;
    private readonly IJobQueue _jobs;
    private readonly TimeProvider _clock;

    public PhotoExportService(AppDbContext db, IJobQueue jobs, TimeProvider clock)
    {
        _db = db;
        _jobs = jobs;
        _clock = clock;
    }

    // ---- token helpers (same scheme as share links) ------------------------
    private static string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(TokenBytes);
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    internal static string HashToken(string token)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    // ---- create ------------------------------------------------------------

    // Create a session for the owner and enqueue the snapshot-build job. Returns
    // the raw token ONCE (only its hash is persisted).
    public async Task<PhotoExportCreatedResponse> CreateAsync(
        Guid ownerUserId, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var token = GenerateToken();

        var session = new PhotoExportSession
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerUserId,
            TokenHash = HashToken(token),
            Status = PhotoExportStatuses.Pending,
            CreatedAt = now,
            ExpiresAt = now.AddDays(DefaultRetentionDays),
            UpdatedAt = now,
        };
        _db.PhotoExportSessions.Add(session);
        await _db.SaveChangesAsync(cancellationToken);

        var job = await _jobs.EnqueueAsync(
            JobTypes.PhotoExportBuild,
            new PhotoExportJobPayload(session.Id),
            cancellationToken: cancellationToken);

        session.JobId = job.Id;
        session.UpdatedAt = _clock.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync(cancellationToken);

        return new PhotoExportCreatedResponse(
            session.Id, token, session.Status, session.ExpiresAt);
    }

    // ---- status / revoke (owner cookie) ------------------------------------

    public async Task<PhotoExportStatusResponse?> GetStatusForOwnerAsync(
        Guid sessionId, Guid ownerUserId, CancellationToken cancellationToken = default)
    {
        var session = await _db.PhotoExportSessions.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == sessionId && s.OwnerUserId == ownerUserId, cancellationToken);
        return session is null ? null : ToStatus(session);
    }

    // Revoke (idempotent). Returns false when the session is missing or not the
    // caller's — mapped to 404 (no foreign-session distinction).
    public async Task<bool> RevokeAsync(
        Guid sessionId, Guid ownerUserId, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var affected = await _db.PhotoExportSessions
            .Where(s => s.Id == sessionId && s.OwnerUserId == ownerUserId && s.RevokedAt == null)
            .ExecuteUpdateAsync(set => set
                .SetProperty(s => s.Status, PhotoExportStatuses.Revoked)
                .SetProperty(s => s.RevokedAt, _ => (DateTime?)now)
                .SetProperty(s => s.UpdatedAt, _ => now), cancellationToken);
        if (affected > 0)
        {
            return true;
        }
        // Idempotent: already-revoked but owned still counts as success.
        return await _db.PhotoExportSessions.AsNoTracking()
            .AnyAsync(s => s.Id == sessionId && s.OwnerUserId == ownerUserId, cancellationToken);
    }

    private PhotoExportStatusResponse ToStatus(PhotoExportSession s)
    {
        var effective = EffectiveStatus(s, _clock.GetUtcNow().UtcDateTime);
        return new PhotoExportStatusResponse(
            s.Id, effective, s.FileCount, s.TotalBytes, s.ErrorSummary,
            s.CreatedAt, s.CompletedAt, s.ExpiresAt,
            ManifestReady: effective == PhotoExportStatuses.Ready);
    }

    public static string EffectiveStatus(PhotoExportSession s, DateTime now)
    {
        if (s.RevokedAt is not null) return PhotoExportStatuses.Revoked;
        if (now >= s.ExpiresAt) return PhotoExportStatuses.Expired;
        return s.Status;
    }

    // ---- access resolution (manifest + file streaming) ---------------------

    // Returns the session iff it is USABLE for download by this caller: either
    // the owner cookie matches, or a valid (matching, not-revoked, not-expired)
    // token is supplied. Null otherwise → caller returns 404. A foreign or
    // missing session is indistinguishable from a bad token.
    public async Task<PhotoExportSession?> ResolveUsableSessionAsync(
        Guid sessionId, Guid? cookieOwnerUserId, string? rawToken, CancellationToken cancellationToken = default)
    {
        var session = await _db.PhotoExportSessions.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);
        if (session is null)
        {
            return null;
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        if (session.RevokedAt is not null || now >= session.ExpiresAt)
        {
            return null; // revoked/expired → unusable
        }

        // Owner cookie path.
        if (cookieOwnerUserId is Guid owner && owner == session.OwnerUserId)
        {
            return session;
        }

        // Token path (constant work; compare hashes).
        if (!string.IsNullOrEmpty(rawToken)
            && CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(session.TokenHash),
                Encoding.ASCII.GetBytes(HashToken(rawToken))))
        {
            return session;
        }

        return null;
    }

    // One manifest page (keyset by entry id). The manifest is built dynamically
    // from the persisted snapshot rows — never the live tree — so it is stable.
    public async Task<IReadOnlyList<PhotoExportManifestEntry>> GetManifestPageAsync(
        Guid sessionId, Guid? afterEntryId, int limit, string downloadBasePath,
        CancellationToken cancellationToken = default)
    {
        var q = _db.PhotoExportEntries.AsNoTracking()
            .Where(e => e.SessionId == sessionId);
        if (afterEntryId is Guid after)
        {
            q = q.Where(e => e.Id.CompareTo(after) > 0);
        }
        var rows = await q
            .OrderBy(e => e.Id)
            .Take(Math.Clamp(limit, 1, ManifestPageSize))
            .Select(e => new { e.Id, e.RelativePath, e.Name, e.SizeBytes, e.ContentType, e.LastModified })
            .ToListAsync(cancellationToken);

        return rows.Select(e => new PhotoExportManifestEntry(
            e.Id.ToString("N"),
            e.RelativePath,
            e.Name,
            e.SizeBytes,
            e.ContentType,
            $"{downloadBasePath}/{e.Id:N}",
            e.LastModified)).ToList();
    }

    // Resolve an entry to the internal FileItemId used to stream content. Returns
    // null when the entry does not belong to this session.
    public async Task<Guid?> ResolveEntryFileItemAsync(
        Guid sessionId, Guid entryId, CancellationToken cancellationToken = default)
    {
        return await _db.PhotoExportEntries.AsNoTracking()
            .Where(e => e.SessionId == sessionId && e.Id == entryId)
            .Select(e => (Guid?)e.FileItemId)
            .FirstOrDefaultAsync(cancellationToken);
    }

    // ---- snapshot build (background job slice) -----------------------------

    public async Task ExecuteSliceAsync(Guid sessionId, JobContext context, CancellationToken cancellationToken)
    {
        var session = await _db.PhotoExportSessions.FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);
        if (session is null || PhotoExportStatuses.IsJobTerminal(session.Status))
        {
            return; // missing/finished/revoked → idempotent no-op
        }

        try
        {
            await ExecuteSliceCoreAsync(session, context, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw; // owned by the job engine (cooperative cancel / shutdown)
        }
        catch (Exception ex)
        {
            // Sanitized summary only — never a stack trace/path/key.
            session.Status = PhotoExportStatuses.Failed;
            session.ErrorSummary = $"{ex.GetType().Name}: build failed";
            session.CompletedAt ??= _clock.GetUtcNow().UtcDateTime;
            session.UpdatedAt = _clock.GetUtcNow().UtcDateTime;
            try { await _db.SaveChangesAsync(CancellationToken.None); } catch { /* best effort */ }
            throw;
        }
    }

    private async Task ExecuteSliceCoreAsync(
        PhotoExportSession session, JobContext context, CancellationToken cancellationToken)
    {
        var checkpoint = PhotoExportCheckpoint.TryParse(context.Checkpoint) ?? new PhotoExportCheckpoint();

        if (session.Status == PhotoExportStatuses.Pending)
        {
            session.Status = PhotoExportStatuses.Building;
            session.StartedAt ??= _clock.GetUtcNow().UtcDateTime;
            session.UpdatedAt = _clock.GetUtcNow().UtcDateTime;
            await _db.SaveChangesAsync(cancellationToken);
        }

        // Folder path resolver: load the owner's folders once (folders ≪ files).
        var folders = await _db.Folders.AsNoTracking()
            .Where(f => f.OwnerUserId == session.OwnerUserId)
            .Select(f => new { f.Id, f.ParentFolderId, f.Name })
            .ToListAsync(cancellationToken);
        var byId = folders.ToDictionary(f => f.Id, f => (f.ParentFolderId, f.Name));

        var entriesBuilt = checkpoint.EntriesBuiltTotal;
        var bytesTotal = checkpoint.BytesTotal;
        var lastId = checkpoint.LastFileId;
        var totalCandidates = session.FileCount; // best-effort; recomputed below if 0

        var processedThisSlice = 0L;
        var moreWork = false;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var query = PhotoExportEligibility.EligiblePhotos(_db, session.OwnerUserId);
            if (lastId is Guid lid)
            {
                query = query.Where(f => f.Id.CompareTo(lid) > 0);
            }
            var batch = await query
                .OrderBy(f => f.Id)
                .Take(BuildBatchSize + 1)
                .Select(f => new
                {
                    f.Id,
                    f.ParentFolderId,
                    f.Name,
                    f.MimeType,
                    f.SizeBytes,
                    f.UpdatedAt,
                    f.CreatedAt,
                    DetectedContentType = _db.BlobMetadata
                        .Where(m => m.BlobObjectId == f.BlobObjectId)
                        .Select(m => m.DetectedContentType)
                        .FirstOrDefault(),
                })
                .ToListAsync(cancellationToken);

            var more = batch.Count > BuildBatchSize;
            var page = more ? batch.Take(BuildBatchSize).ToList() : batch;
            if (page.Count == 0)
            {
                break;
            }

            foreach (var f in page)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relativePath = BuildRelativePath(byId, f.ParentFolderId, f.Name);
                _db.PhotoExportEntries.Add(new PhotoExportEntry
                {
                    Id = Guid.NewGuid(),
                    SessionId = session.Id,
                    FileItemId = f.Id,
                    RelativePath = relativePath,
                    Name = f.Name,
                    SizeBytes = f.SizeBytes,
                    // Prefer the server-detected type; fall back to the client MIME.
                    ContentType = f.DetectedContentType ?? f.MimeType,
                    LastModified = f.UpdatedAt ?? f.CreatedAt,
                });
                entriesBuilt++;
                bytesTotal += f.SizeBytes;
                lastId = f.Id;
                processedThisSlice++;
            }

            await _db.SaveChangesAsync(cancellationToken);
            await context.ReportProgressAsync(entriesBuilt, null, $"building snapshot ({entriesBuilt} photos)", cancellationToken);

            if (!more)
            {
                moreWork = false;
                break;
            }
            if (context.ShouldYield(processedThisSlice))
            {
                moreWork = true;
                break;
            }
        }

        session.FileCount = entriesBuilt;
        session.TotalBytes = bytesTotal;
        session.UpdatedAt = _clock.GetUtcNow().UtcDateTime;
        _ = totalCandidates;

        if (context.IsCancellationRequested && moreWork)
        {
            // Cooperative cancel mid-build: leave session Building; a later run or
            // expiry handles it. Do not mark a permanent failure.
            await _db.SaveChangesAsync(cancellationToken);
            return;
        }

        if (moreWork)
        {
            await _db.SaveChangesAsync(cancellationToken);
            var next = new PhotoExportCheckpoint
            {
                EntriesBuiltTotal = entriesBuilt,
                BytesTotal = bytesTotal,
                LastFileId = lastId,
            };
            var reason = context.HigherPriorityWaiting ? JobYieldReasons.HigherPriority : JobYieldReasons.SliceBudget;
            context.RequestContinuation(reason, next.Serialize());
            return;
        }

        session.Status = PhotoExportStatuses.Ready;
        session.CompletedAt ??= _clock.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync(cancellationToken);
    }

    // Build the logical export path: parent-folder chain (root → leaf) + name,
    // relative (no leading slash). Each segment is defensively stripped of path
    // separators (NubArca names cannot contain them, but the manifest must
    // never enable traversal in a downloader).
    internal static string BuildRelativePath(
        IReadOnlyDictionary<Guid, (Guid? ParentFolderId, string Name)> byId, Guid? parentFolderId, string name)
    {
        var segments = new List<string>();
        var current = parentFolderId;
        var guard = 0;
        while (current is Guid id && byId.TryGetValue(id, out var node) && guard++ < 1000)
        {
            segments.Add(SafeSegment(node.Name));
            current = node.ParentFolderId;
        }
        segments.Reverse();
        segments.Add(SafeSegment(name));
        return string.Join("/", segments);
    }

    private static string SafeSegment(string raw)
    {
        var s = raw.Replace('\\', '_').Replace('/', '_').Trim();
        if (s is "" or "." or "..")
        {
            s = "_";
        }
        return s;
    }
}
