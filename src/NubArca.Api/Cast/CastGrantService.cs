using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NubArca.Api.Access;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Metadata;
using NubArca.Api.Security;

namespace NubArca.Api.Cast;

// Mints, resolves and revokes the short-lived capabilities an external Cast
// receiver plays with. It owns the whole secret lifecycle and is the only place
// that ever holds a raw token.
//
// Creation is gated exactly as ordinary playback is: the caller must own the
// file, it must be a server-detected video, and the installation's current
// playback contract decides the mode. Nothing here can widen what a user can
// reach — a grant is always a NARROWING of an access the caller already had.
//
// Resolution is deliberately a database read on every request. The alternative
// — trusting facts captured when the grant was minted — would make a revoked
// permission keep working for hours, which is precisely the property NubArca's
// authorization model does not have anywhere else.
public sealed class CastGrantService
{
    // 32 bytes = 256 bits of CSPRNG entropy, base64url-encoded. The same scheme
    // as share links, export sessions and vault unlock tokens.
    private const int TokenBytes = 32;

    // How long an expired or revoked row is kept before the opportunistic sweep
    // removes it. Long enough that a support question can still be answered
    // from the audit trail plus the row, short enough that the table cannot
    // grow without bound.
    private static readonly TimeSpan SpentGrantRetention = TimeSpan.FromDays(1);

    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;
    private readonly IOptions<CastOptions> _options;
    private readonly IUserPermissionService _permissions;
    private readonly VideoHlsServingService _hls;

    public CastGrantService(
        AppDbContext db,
        TimeProvider clock,
        IOptions<CastOptions> options,
        IUserPermissionService permissions,
        VideoHlsServingService hls)
    {
        _db = db;
        _clock = clock;
        _options = options;
        _permissions = permissions;
        _hls = hls;
    }

    /// <summary>Base path every grant-scoped media route hangs off.</summary>
    public static string MediaBasePath(Guid grantId) => $"/api/cast/media/{grantId}";

    // ── creation ────────────────────────────────────────────────────────────

    public async Task<CastGrantCreation> CreateAsync(
        Guid userId, Guid fileItemId, CancellationToken cancellationToken)
    {
        var video = await ResolveVideoAsync(userId, fileItemId, cancellationToken);
        if (video is null)
        {
            return CastGrantCreation.NotFound;
        }

        string mode;
        string contentType;
        if (_hls.Enabled)
        {
            // Reuses the existing pipeline — the same master resolution the
            // owner's own player drives, including its idempotent lazy enqueue.
            // A half-prepared ladder is never handed to a receiver.
            var master = await _hls.GetMasterAsync(fileItemId, userId, cancellationToken);
            switch (master.Status)
            {
                case VideoHlsMasterStatus.Preparing:
                    return CastGrantCreation.Preparing;
                case VideoHlsMasterStatus.Ready:
                    break;
                default:
                    return CastGrantCreation.NotFound;
            }

            mode = CastPlaybackModes.Hls;
            contentType = VideoHlsServingService.MasterContentType;
        }
        else
        {
            // Progressive: the ORIGINAL bytes are what plays, so the stricter
            // header-sniff gate applies — the Content-Type advertised to the
            // receiver has to be one the server itself recognised.
            if (!SafeContentType.IsTrustedVideo(video.DetectedContentType))
            {
                return CastGrantCreation.NotFound;
            }

            mode = CastPlaybackModes.Direct;
            contentType = SafeContentType.ForServingVideo(video.DetectedContentType);
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        await PurgeSpentGrantsAsync(now, cancellationToken);

        var token = GenerateToken();
        var grant = new CastMediaGrant
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            FileItemId = fileItemId,
            TokenHash = HashToken(token),
            CreatedAt = now,
            ExpiresAt = now + _options.Value.EffectiveGrantLifetime,
        };
        _db.CastMediaGrants.Add(grant);
        await _db.SaveChangesAsync(cancellationToken);

        return CastGrantCreation.Created(new CastGrantSecret(
            grant.Id, grant.ExpiresAt, token, mode, contentType));
    }

    // ── resolution (every media request) ────────────────────────────────────

    /// <summary>
    /// The complete gate an anonymous Cast media request passes, or null. Null
    /// covers every failure identically — unknown grant, wrong secret, expired,
    /// revoked, disabled account, permission withdrawn, file gone — so a caller
    /// answers one indistinguishable 404 and never reveals which it was.
    /// </summary>
    public async Task<CastGrantResolution?> ResolveAsync(
        Guid grantId, string? token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        var grant = await _db.CastMediaGrants.AsNoTracking()
            .FirstOrDefaultAsync(g => g.Id == grantId, cancellationToken);
        if (grant is null)
        {
            return null;
        }

        if (!MatchesToken(grant.TokenHash, token))
        {
            return null;
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        if (grant.RevokedAt is not null || grant.ExpiresAt <= now)
        {
            return null;
        }

        // The account, read now rather than trusted from mint time.
        var active = await _db.Users.AsNoTracking()
            .AnyAsync(u => u.Id == grant.UserId && u.DisabledAt == null, cancellationToken);
        if (!active)
        {
            return null;
        }

        // The permission, read now. Losing `cast.access` stops the NEXT segment,
        // which is the same "permissions change on the next request" contract
        // every other NubArca endpoint has.
        var effective = await _permissions.GetEffectiveAsync(grant.UserId, cancellationToken);
        if (!effective.Has(Permissions.CastAccess))
        {
            return null;
        }

        // The file, read now: a soft-deleted, moved-away or no-longer-video file
        // stops playing. The serving services re-check ownership themselves; this
        // makes the poster and progressive paths agree with them up front.
        var video = await ResolveVideoAsync(grant.UserId, grant.FileItemId, cancellationToken);
        if (video is null)
        {
            return null;
        }

        return new CastGrantResolution(
            grant.Id, grant.UserId, grant.FileItemId, video.DetectedContentType);
    }

    // ── revocation ──────────────────────────────────────────────────────────

    /// <summary>
    /// Revokes one grant. Idempotent, and owner-scoped: another account's grant
    /// id is answered exactly as an unknown one is.
    /// </summary>
    public async Task<bool> RevokeAsync(
        Guid grantId, Guid userId, CancellationToken cancellationToken)
    {
        var grant = await _db.CastMediaGrants
            .FirstOrDefaultAsync(g => g.Id == grantId && g.UserId == userId, cancellationToken);
        if (grant is null)
        {
            return false;
        }

        if (grant.RevokedAt is null)
        {
            grant.RevokedAt = _clock.GetUtcNow().UtcDateTime;
            await _db.SaveChangesAsync(cancellationToken);
        }
        return true;
    }

    // ── housekeeping ────────────────────────────────────────────────────────

    // Bounded, opportunistic, and driven by the only action that adds rows —
    // no timer, no hosted service, nothing permanently awake. Both predicates
    // ride the ExpiresAt index.
    private async Task PurgeSpentGrantsAsync(DateTime now, CancellationToken cancellationToken)
    {
        var cutoff = now - SpentGrantRetention;
        await _db.CastMediaGrants
            .Where(g => g.ExpiresAt < cutoff || (g.RevokedAt != null && g.RevokedAt < cutoff))
            .ExecuteDeleteAsync(cancellationToken);
    }

    // ── token primitives ────────────────────────────────────────────────────

    private static string GenerateToken()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(TokenBytes))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    internal static string HashToken(string token)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    // Constant-time over the DIGESTS. The row was already located by primary
    // key, so this comparison is the only thing standing between a guessed
    // secret and playback; a short-circuiting string compare would leak how much
    // of a guess was right.
    private static bool MatchesToken(string storedHash, string presentedToken)
        => CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(HashToken(presentedToken)),
            Encoding.UTF8.GetBytes(storedHash));

    // ── shared file gate ────────────────────────────────────────────────────

    // Owner-scoped, soft-delete-aware, server-detected-video. The same pair of
    // checks /api/files/{id}/video makes, so Cast can never see a file ordinary
    // playback cannot.
    private async Task<VideoGate?> ResolveVideoAsync(
        Guid userId, Guid fileItemId, CancellationToken cancellationToken)
    {
        var row = await _db.FileItems.AsNoTracking()
            .Where(f => f.Id == fileItemId && f.OwnerUserId == userId && f.DeletedAt == null)
            .Join(
                _db.BlobMetadata.AsNoTracking(),
                f => f.BlobObjectId,
                m => m.BlobObjectId,
                (f, m) => new
                {
                    m.MediaCategory, m.DetectedContentType, m.VideoExtractionStatus, m.VideoCodec,
                })
            .FirstOrDefaultAsync(cancellationToken);

        if (row is null || row.MediaCategory != MediaCategories.Video)
        {
            return null;
        }

        // Everything the HLS routes serve is ffmpeg-PRODUCED, so a legacy
        // container ffprobe confirmed qualifies; the progressive branch narrows
        // to IsTrustedVideo itself, because there the ORIGINAL bytes are what
        // the receiver is handed.
        var confirmed = SafeContentType.IsServerConfirmedVideo(
            row.DetectedContentType, row.VideoExtractionStatus, row.VideoCodec);
        return confirmed ? new VideoGate(row.DetectedContentType) : null;
    }

    private sealed record VideoGate(string? DetectedContentType);
}

public static class CastPlaybackModes
{
    public const string Hls = "hls";
    public const string Direct = "direct";
}

public enum CastGrantCreationStatus
{
    /// <summary>Not a video this caller can play. Answered as 404.</summary>
    NotFound,

    /// <summary>The HLS ladder is being produced; the caller polls.</summary>
    Preparing,

    Created,
}

/// <summary>The raw secret leaves the service exactly once, here.</summary>
public sealed record CastGrantSecret(
    Guid GrantId, DateTime ExpiresAt, string Token, string Mode, string ContentType);

public sealed record CastGrantCreation(CastGrantCreationStatus Status, CastGrantSecret? Grant)
{
    public static readonly CastGrantCreation NotFound =
        new(CastGrantCreationStatus.NotFound, null);

    public static readonly CastGrantCreation Preparing =
        new(CastGrantCreationStatus.Preparing, null);

    public static CastGrantCreation Created(CastGrantSecret grant) =>
        new(CastGrantCreationStatus.Created, grant);
}

/// <summary>What a verified Cast media request is allowed to act on.</summary>
public sealed record CastGrantResolution(
    Guid GrantId,
    Guid UserId,
    Guid FileItemId,
    string? DetectedContentType);
