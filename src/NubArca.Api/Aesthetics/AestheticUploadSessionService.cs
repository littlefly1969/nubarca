using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NubArca.Api.Data;
using NubArca.Api.Domain;

namespace NubArca.Api.Aesthetics;

// Owner-private lifecycle of the TV "Beauty Lab" QR mobile-upload capability.
// Token model mirrors the Party upload token: a cryptographically random 256-bit
// value, shown ONCE inside the QR URL, of which only a SHA-256 hash is ever
// persisted. Purpose is scoped by construction — this table is consumed only by
// the Aesthetics Lab direct-upload path, never by any list/read/delete/analyze.
public sealed class AestheticUploadSessionService : IAestheticUploadSessionService
{
    // The PUBLIC mobile page route (SPA). The raw token is a PATH segment, never
    // a query string, so it doesn't leak via Referer or server query logs.
    public const string MobilePagePathPrefix = "/beauty-lab-upload/";

    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;
    private readonly AestheticsOptions _options;

    public AestheticUploadSessionService(
        AppDbContext db, TimeProvider clock, IOptions<AestheticsOptions> options)
    {
        _db = db;
        _clock = clock;
        _options = options.Value;
    }

    public async Task<AestheticUploadSessionCreatedDto> CreateAsync(
        Guid ownerUserId, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var ttl = TimeSpan.FromMinutes(Math.Max(1, _options.UploadSessionTtlMinutes));

        // 256-bit CSPRNG token; only its hash is stored.
        var rawToken = GenerateToken();
        var session = new AestheticUploadSession
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerUserId,
            TokenHash = HashToken(rawToken),
            MaxFiles = Math.Max(1, _options.UploadSessionMaxFiles),
            MaxTotalBytes = Math.Max(1, _options.UploadSessionMaxTotalBytes),
            AcceptedCount = 0,
            RejectedCount = 0,
            UsedBytes = 0,
            CreatedAt = now,
            ExpiresAt = now + ttl,
            RevokedAt = null,
        };
        _db.AestheticUploadSessions.Add(session);
        await _db.SaveChangesAsync(cancellationToken);

        return new AestheticUploadSessionCreatedDto(
            session.Id,
            MobilePagePathPrefix + rawToken,
            session.ExpiresAt,
            session.MaxFiles,
            session.MaxTotalBytes,
            session.AcceptedCount,
            session.RejectedCount,
            DeriveState(session, now));
    }

    public async Task<AestheticUploadSessionStatusDto?> GetStatusAsync(
        Guid ownerUserId, Guid id, CancellationToken cancellationToken = default)
    {
        var session = await _db.AestheticUploadSessions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id && s.OwnerUserId == ownerUserId, cancellationToken);
        if (session is null)
        {
            return null;
        }
        var now = _clock.GetUtcNow().UtcDateTime;
        return new AestheticUploadSessionStatusDto(
            session.Id, session.ExpiresAt, session.MaxFiles, session.MaxTotalBytes,
            session.AcceptedCount, session.RejectedCount, DeriveState(session, now));
    }

    public async Task<bool> RevokeAsync(
        Guid ownerUserId, Guid id, CancellationToken cancellationToken = default)
    {
        var session = await _db.AestheticUploadSessions
            .FirstOrDefaultAsync(s => s.Id == id && s.OwnerUserId == ownerUserId, cancellationToken);
        if (session is null)
        {
            return false;
        }
        // Idempotent: revoking an already-revoked session is a no-op success.
        if (session.RevokedAt is null)
        {
            session.RevokedAt = _clock.GetUtcNow().UtcDateTime;
            await _db.SaveChangesAsync(cancellationToken);
        }
        return true;
    }

    public async Task<AestheticUploadPublicStateDto?> GetPublicStateByTokenAsync(
        string rawToken, CancellationToken cancellationToken = default)
    {
        var session = await FindByTokenAsync(rawToken, cancellationToken);
        if (session is null)
        {
            return null;
        }
        var now = _clock.GetUtcNow().UtcDateTime;
        return new AestheticUploadPublicStateDto(
            DeriveState(session, now), session.ExpiresAt, session.MaxFiles,
            session.MaxTotalBytes, session.AcceptedCount, session.RejectedCount);
    }

    public async Task<AestheticUploadSessionResolution?> ResolveActiveByTokenAsync(
        string rawToken, CancellationToken cancellationToken = default)
    {
        var session = await FindByTokenAsync(rawToken, cancellationToken);
        if (session is null)
        {
            return null;
        }
        var now = _clock.GetUtcNow().UtcDateTime;
        if (DeriveState(session, now) != AestheticUploadSessionStates.Active)
        {
            return null;
        }
        return new AestheticUploadSessionResolution(
            session.Id,
            session.OwnerUserId,
            Math.Max(0, session.MaxFiles - session.AcceptedCount),
            Math.Max(0, session.MaxTotalBytes - session.UsedBytes));
    }

    public async Task RecordResultAsync(
        Guid sessionId, bool accepted, long bytes, CancellationToken cancellationToken = default)
    {
        var session = await _db.AestheticUploadSessions
            .FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);
        if (session is null)
        {
            return;
        }
        if (accepted)
        {
            session.AcceptedCount++;
            session.UsedBytes += Math.Max(0, bytes);
        }
        else
        {
            session.RejectedCount++;
        }
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<int> CleanupExpiredAsync(CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var retention = TimeSpan.FromMinutes(Math.Max(1, _options.UploadSessionRetentionMinutes));
        var cutoff = now - retention;

        // Hard-delete sessions that expired past the retention window OR were
        // revoked past it. Rows still refuse uploads before deletion (resolve
        // re-checks expiry/revocation), so this is pure reclamation.
        var stale = await _db.AestheticUploadSessions
            .Where(s => s.ExpiresAt < cutoff
                || (s.RevokedAt != null && s.RevokedAt < cutoff))
            .ToListAsync(cancellationToken);
        if (stale.Count == 0)
        {
            return 0;
        }
        _db.AestheticUploadSessions.RemoveRange(stale);
        await _db.SaveChangesAsync(cancellationToken);
        return stale.Count;
    }

    private async Task<AestheticUploadSession?> FindByTokenAsync(
        string rawToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(rawToken))
        {
            return null;
        }
        var hash = HashToken(rawToken);
        return await _db.AestheticUploadSessions
            .FirstOrDefaultAsync(s => s.TokenHash == hash, cancellationToken);
    }

    // active only while not revoked, not past expiry, and under both caps.
    private static string DeriveState(AestheticUploadSession s, DateTime now)
    {
        if (s.RevokedAt is not null)
        {
            return AestheticUploadSessionStates.Revoked;
        }
        if (s.ExpiresAt <= now)
        {
            return AestheticUploadSessionStates.Expired;
        }
        if (s.AcceptedCount >= s.MaxFiles || s.UsedBytes >= s.MaxTotalBytes)
        {
            return AestheticUploadSessionStates.Full;
        }
        return AestheticUploadSessionStates.Active;
    }

    // URL-safe base64 of 32 random bytes (256-bit), matching the TV session token.
    private static string GenerateToken()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

    internal static string HashToken(string token)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
