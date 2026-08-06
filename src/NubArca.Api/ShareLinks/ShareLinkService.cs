using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Domain;

namespace NubArca.Api.ShareLinks;

public sealed class ShareLinkService : IShareLinkService
{
    private const int TokenBytes = 32; // 256 bits

    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;

    public ShareLinkService(AppDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<ShareLinkCreationResult?> CreateAsync(
        Guid ownerUserId,
        Guid fileItemId,
        DateTime? expiresAt,
        int? maxDownloads,
        CancellationToken cancellationToken = default)
    {
        // Owner-scoped + soft-delete-aware file lookup. Missing / foreign /
        // soft-deleted all collapse to null (caller maps to 404).
        var ownsFile = await _db.FileItems
            .AsNoTracking()
            .AnyAsync(
                f => f.Id == fileItemId
                    && f.OwnerUserId == ownerUserId
                    && f.DeletedAt == null,
                cancellationToken);
        if (!ownsFile)
        {
            return null;
        }

        var token = GenerateToken();
        var link = new ShareLink
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerUserId,
            FileItemId = fileItemId,
            TokenHash = HashToken(token),
            CreatedAt = _clock.GetUtcNow().UtcDateTime,
            ExpiresAt = expiresAt,
            RevokedAt = null,
            DownloadCount = 0,
            MaxDownloads = maxDownloads,
            LastAccessedAt = null,
        };

        _db.ShareLinks.Add(link);
        await _db.SaveChangesAsync(cancellationToken);

        return new ShareLinkCreationResult(link.Id, token, link.ExpiresAt, link.MaxDownloads);
    }

    public async Task<bool> RevokeAsync(
        Guid ownerUserId,
        Guid shareLinkId,
        CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;

        // Atomic owner-scoped UPDATE. Affects 0 rows when the link does not
        // exist or belongs to a different owner — both indistinguishable to
        // the caller (404 in the HTTP layer).
        var affected = await _db.ShareLinks
            .Where(s => s.Id == shareLinkId && s.OwnerUserId == ownerUserId)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(s => s.RevokedAt, _ => now),
                cancellationToken);

        return affected > 0;
    }

    public async Task<IReadOnlyList<ShareLinkSummary>?> ListByFileAsync(
        Guid ownerUserId,
        Guid fileItemId,
        CancellationToken cancellationToken = default)
    {
        // Owner-scoped + soft-delete-aware file existence check first. Missing /
        // foreign / soft-deleted all collapse to null (caller maps to 404).
        var ownsFile = await _db.FileItems
            .AsNoTracking()
            .AnyAsync(
                f => f.Id == fileItemId
                    && f.OwnerUserId == ownerUserId
                    && f.DeletedAt == null,
                cancellationToken);
        if (!ownsFile)
        {
            return null;
        }

        var now = _clock.GetUtcNow().UtcDateTime;

        var summaries = await _db.ShareLinks
            .AsNoTracking()
            .Where(s => s.FileItemId == fileItemId && s.OwnerUserId == ownerUserId)
            .OrderByDescending(s => s.CreatedAt)
            .Select(s => new ShareLinkSummary(
                s.Id,
                s.CreatedAt,
                s.ExpiresAt,
                s.RevokedAt,
                s.MaxDownloads,
                s.DownloadCount,
                s.LastAccessedAt,
                s.RevokedAt != null,
                s.ExpiresAt != null && s.ExpiresAt <= now,
                s.MaxDownloads != null && s.DownloadCount >= s.MaxDownloads))
            .ToListAsync(cancellationToken);

        return summaries;
    }

    public async Task<ShareLinkListResponse> ListForOwnerAsync(
        Guid ownerUserId,
        ShareLinkStatusFilter status,
        int limit,
        int offset,
        CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;

        var query = _db.ShareLinks
            .AsNoTracking()
            .Where(s => s.OwnerUserId == ownerUserId);

        query = status switch
        {
            ShareLinkStatusFilter.Active => query.Where(s =>
                s.RevokedAt == null
                && (s.ExpiresAt == null || s.ExpiresAt > now)
                && (s.MaxDownloads == null || s.DownloadCount < s.MaxDownloads)),
            ShareLinkStatusFilter.Revoked => query.Where(s => s.RevokedAt != null),
            ShareLinkStatusFilter.Expired => query.Where(s =>
                s.RevokedAt == null && s.ExpiresAt != null && s.ExpiresAt <= now),
            _ => query, // All
        };

        var total = await query.CountAsync(cancellationToken);

        // Join to FileItem for the file's name + parent folder. A ShareLink
        // always references an existing FileItem row (FK Restrict) even when
        // the file is soft-deleted, so the inner join never silently drops a
        // link from the listing.
        var page = await query
            .OrderByDescending(s => s.CreatedAt)
            .ThenByDescending(s => s.Id)
            .Skip(offset)
            .Take(limit)
            .Join(
                _db.FileItems.AsNoTracking(),
                s => s.FileItemId,
                f => f.Id,
                (s, f) => new
                {
                    s.Id,
                    FileName = f.Name,
                    f.ParentFolderId,
                    s.CreatedAt,
                    s.ExpiresAt,
                    s.RevokedAt,
                    s.MaxDownloads,
                    s.DownloadCount,
                    s.LastAccessedAt,
                    IsRevoked = s.RevokedAt != null,
                    IsExpired = s.ExpiresAt != null && s.ExpiresAt <= now,
                    IsExhausted = s.MaxDownloads != null && s.DownloadCount >= s.MaxDownloads,
                })
            .ToListAsync(cancellationToken);

        var resolvePath = await BuildFolderPathResolverAsync(ownerUserId, cancellationToken);

        var items = page
            .Select(r => new ShareLinkListItem(
                r.Id,
                r.FileName,
                resolvePath(r.ParentFolderId),
                r.CreatedAt,
                r.ExpiresAt,
                r.RevokedAt,
                r.MaxDownloads,
                r.DownloadCount,
                r.LastAccessedAt,
                r.IsRevoked,
                r.IsExpired,
                r.IsExhausted))
            .ToList();

        return new ShareLinkListResponse(items, limit, offset, total);
    }

    // Loads the owner's folders once and returns a resolver that builds a
    // human-readable logical path ("/" for root, "/Photos/Holidays" for a
    // nested file) from a file's ParentFolderId. Personal-cloud scale: one
    // owner-scoped query, walked in memory — no recursive CTE, so SQLite and
    // PostgreSQL behave identically.
    private async Task<Func<Guid?, string?>> BuildFolderPathResolverAsync(
        Guid ownerUserId,
        CancellationToken cancellationToken)
    {
        var folders = await _db.Folders
            .AsNoTracking()
            .Where(f => f.OwnerUserId == ownerUserId)
            .Select(f => new { f.Id, f.ParentFolderId, f.Name })
            .ToListAsync(cancellationToken);

        var byId = folders.ToDictionary(f => f.Id, f => (f.ParentFolderId, f.Name));

        return parentFolderId =>
        {
            if (parentFolderId is null)
            {
                return "/";
            }

            var segments = new List<string>();
            var current = parentFolderId;
            var guard = 0;
            while (current is Guid id && byId.TryGetValue(id, out var node) && guard++ < 1000)
            {
                segments.Add(node.Name);
                current = node.ParentFolderId;
            }
            segments.Reverse();
            return "/" + string.Join("/", segments);
        };
    }

    public async Task<ShareLinkConsumeResult?> ConsumeAsync(
        string token,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var hash = HashToken(token);
        var now = _clock.GetUtcNow().UtcDateTime;

        // Atomic validate + increment. The WHERE clause encodes every
        // not-revoked / not-expired / not-exhausted predicate so concurrent
        // consumers cannot push DownloadCount past MaxDownloads.
        var affected = await _db.ShareLinks
            .Where(s => s.TokenHash == hash
                && s.RevokedAt == null
                && (s.ExpiresAt == null || s.ExpiresAt > now)
                && (s.MaxDownloads == null || s.DownloadCount < s.MaxDownloads))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(s => s.DownloadCount, s => s.DownloadCount + 1)
                    .SetProperty(s => s.LastAccessedAt, _ => (DateTime?)now),
                cancellationToken);

        if (affected == 0)
        {
            return null;
        }

        var link = await _db.ShareLinks
            .AsNoTracking()
            .Where(s => s.TokenHash == hash)
            .Select(s => new { s.FileItemId, s.OwnerUserId })
            .FirstOrDefaultAsync(cancellationToken);

        return link is null ? null : new ShareLinkConsumeResult(link.FileItemId, link.OwnerUserId);
    }

    private static string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(TokenBytes);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    internal static string HashToken(string token)
    {
        var bytes = Encoding.UTF8.GetBytes(token);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexStringLower(hash);
    }
}
