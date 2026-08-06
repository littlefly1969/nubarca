using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using NubArca.Api.Data;
using NubArca.Api.Domain;

namespace NubArca.Api.Party;

// SECURITY MODEL
// --------------
// A party link's public token is a PUBLIC capability (printed as a QR at a
// party for anyone to scan) — not a secret credential. Two requirements are in
// tension: (a) "store only the token hash, never persist the raw token", and
// (b) "a paired TV re-renders the QR on every browse", which needs the server
// to reproduce the URL. We reconcile both by DERIVING the token deterministically
// from the row's random Id via HMAC-SHA256(serverSecret, Id): the raw token is
// never written to the DB (only its SHA-256 hash is), yet any owner-authorized
// surface can reproduce it on demand. The Id is a random GUID that is never
// exposed in any DTO/log, so the derived token stays unguessable. Re-enabling
// party mode creates a NEW row (new Id → new token), so an old QR/link dies.
public sealed class PartyLinkService : IPartyLinkService
{
    // Fallback used when Party:TokenSecret is not configured. Deriving from the
    // never-exposed row Id keeps tokens unguessable even with a known default;
    // production should still set Party__TokenSecret for defence in depth.
    // This literal is token key material: changing it invalidates every already
    // issued party QR/link signed WITHOUT a configured secret. Production
    // configures Party__TokenSecret, so this fallback is a dev/test convenience
    // there rather than live key material.
    private const string DefaultSecret = "nubarca-party-token-secret-v1";

    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;
    private readonly byte[] _secret;

    public PartyLinkService(AppDbContext db, TimeProvider clock, IConfiguration config)
    {
        _db = db;
        _clock = clock;
        var configured = config["Party:TokenSecret"];
        _secret = Encoding.UTF8.GetBytes(
            string.IsNullOrWhiteSpace(configured) ? DefaultSecret : configured);
    }

    public async Task<PartyEnableResult?> EnableAsync(
        Guid ownerUserId, Guid albumId, Guid createdByUserId,
        bool? uploadEnabled = null,
        bool? requireApproval = null,
        CancellationToken cancellationToken = default)
    {
        var album = await _db.Albums
            .Where(a => a.Id == albumId && a.OwnerUserId == ownerUserId)
            .FirstOrDefaultAsync(cancellationToken);
        if (album is null)
        {
            return null;
        }

        var now = _clock.GetUtcNow().UtcDateTime;

        // Party implies TV visibility.
        if (!album.ShowOnTv)
        {
            album.ShowOnTv = true;
            album.UpdatedAt = now;
        }

        // Reuse the current active link so the view token (and any printed QR)
        // stays stable when the owner only toggles the upload sub-switch. A fresh
        // link (new tokens) is minted only when none is active — e.g. after a
        // disable, which is how re-enabling rotates the token.
        var link = await _db.PartyAlbumLinks
            .Where(p => p.AlbumId == albumId && p.OwnerUserId == ownerUserId
                && p.Enabled && p.RevokedAt == null
                && (p.ExpiresAt == null || p.ExpiresAt > now))
            .OrderByDescending(p => p.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (link is not null)
        {
            if (uploadEnabled.HasValue)
            {
                link.UploadEnabled = uploadEnabled.Value;
            }
            if (requireApproval.HasValue)
            {
                link.RequireUploadApproval = requireApproval.Value;
            }
            // Heal an older link that predates the upload token (defensive).
            link.UploadTokenHash ??= HashToken(DeriveUploadToken(link.Id));
            link.UpdatedAt = now;
        }
        else
        {
            link = new PartyAlbumLink
            {
                Id = Guid.NewGuid(),
                OwnerUserId = ownerUserId,
                AlbumId = albumId,
                Enabled = true,
                UploadEnabled = uploadEnabled ?? true,
                RequireUploadApproval = requireApproval ?? false,
                CreatedAt = now,
                UpdatedAt = now,
                RevokedAt = null,
                ExpiresAt = null,
                CreatedByUserId = createdByUserId,
            };
            link.TokenHash = HashToken(DeriveToken(link.Id));
            link.UploadTokenHash = HashToken(DeriveUploadToken(link.Id));
            _db.PartyAlbumLinks.Add(link);
        }

        await _db.SaveChangesAsync(cancellationToken);
        return new PartyEnableResult(albumId, link.Id, BuildPartyUrl(DeriveToken(link.Id)));
    }

    public async Task<bool> DisableAsync(
        Guid ownerUserId, Guid albumId,
        CancellationToken cancellationToken = default)
    {
        var owns = await _db.Albums
            .AsNoTracking()
            .AnyAsync(a => a.Id == albumId && a.OwnerUserId == ownerUserId, cancellationToken);
        if (!owns)
        {
            return false;
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        await _db.PartyAlbumLinks
            .Where(p => p.AlbumId == albumId && p.OwnerUserId == ownerUserId
                && p.Enabled && p.RevokedAt == null)
            .ExecuteUpdateAsync(
                s => s.SetProperty(p => p.Enabled, false)
                      .SetProperty(p => p.RevokedAt, _ => now)
                      .SetProperty(p => p.UpdatedAt, _ => now),
                cancellationToken);
        return true;
    }

    public async Task<AlbumPartyStatusDto?> GetOwnerStatusAsync(
        Guid ownerUserId, Guid albumId,
        CancellationToken cancellationToken = default)
    {
        var album = await _db.Albums
            .AsNoTracking()
            .Where(a => a.Id == albumId && a.OwnerUserId == ownerUserId)
            .Select(a => new { a.ShowOnTv })
            .FirstOrDefaultAsync(cancellationToken);
        if (album is null)
        {
            return null;
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        var active = await _db.PartyAlbumLinks
            .AsNoTracking()
            .Where(p => p.AlbumId == albumId && p.OwnerUserId == ownerUserId
                && p.Enabled && p.RevokedAt == null
                && (p.ExpiresAt == null || p.ExpiresAt > now))
            .OrderByDescending(p => p.CreatedAt)
            .Select(p => new { p.Id, p.UploadEnabled, p.UploadTokenHash, p.RequireUploadApproval })
            .FirstOrDefaultAsync(cancellationToken);

        // Party requires ShowOnTv; a link on a no-longer-ShowOnTv album is inert.
        var partyMode = active is not null && album.ShowOnTv;
        var viewUrl = partyMode ? BuildPartyUrl(DeriveToken(active!.Id)) : null;
        var uploadOn = partyMode && active!.UploadEnabled && active.UploadTokenHash is not null;
        var uploadUrl = uploadOn ? BuildUploadUrl(DeriveUploadToken(active!.Id)) : null;
        var requireApproval = partyMode && active!.RequireUploadApproval;
        return new AlbumPartyStatusDto(
            albumId, album.ShowOnTv, partyMode, viewUrl, uploadOn, uploadUrl, requireApproval);
    }

    public async Task<IReadOnlyDictionary<Guid, PartyLinkUrls>> GetActivePartyUrlsAsync(
        Guid ownerUserId, IReadOnlyCollection<Guid> albumIds,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<Guid, PartyLinkUrls>();
        if (albumIds.Count == 0)
        {
            return result;
        }

        var now = _clock.GetUtcNow().UtcDateTime;

        // Only ShowOnTv albums with an active link. The album join keeps a link
        // on a since-disabled album from producing a URL.
        var rows = await _db.PartyAlbumLinks
            .AsNoTracking()
            .Where(p => p.OwnerUserId == ownerUserId
                && albumIds.Contains(p.AlbumId)
                && p.Enabled && p.RevokedAt == null
                && (p.ExpiresAt == null || p.ExpiresAt > now))
            .Join(_db.Albums.AsNoTracking().Where(a => a.OwnerUserId == ownerUserId && a.ShowOnTv),
                p => p.AlbumId, a => a.Id,
                (p, a) => new { p.AlbumId, p.Id, p.CreatedAt, p.UploadEnabled, p.UploadTokenHash })
            .ToListAsync(cancellationToken);

        foreach (var group in rows.GroupBy(r => r.AlbumId))
        {
            var latest = group.OrderByDescending(r => r.CreatedAt).First();
            var uploadUrl = latest.UploadEnabled && latest.UploadTokenHash is not null
                ? BuildUploadUrl(DeriveUploadToken(latest.Id))
                : null;
            result[group.Key] = new PartyLinkUrls(BuildPartyUrl(DeriveToken(latest.Id)), uploadUrl);
        }
        return result;
    }

    public async Task<PartyAccess?> ResolvePublicAsync(
        string token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var hash = HashToken(token);
        var now = _clock.GetUtcNow().UtcDateTime;

        var link = await _db.PartyAlbumLinks
            .AsNoTracking()
            .Where(p => p.TokenHash == hash
                && p.Enabled && p.RevokedAt == null
                && (p.ExpiresAt == null || p.ExpiresAt > now))
            .Select(p => new { p.OwnerUserId, p.AlbumId })
            .FirstOrDefaultAsync(cancellationToken);
        if (link is null)
        {
            return null;
        }

        // The album must still belong to the owner AND still be ShowOnTv — so
        // turning off "Show on TV" (or moving the album) severs public access
        // even if a stale link row lingered.
        var albumOk = await _db.Albums
            .AsNoTracking()
            .AnyAsync(a => a.Id == link.AlbumId && a.OwnerUserId == link.OwnerUserId && a.ShowOnTv,
                cancellationToken);
        return albumOk ? new PartyAccess(link.OwnerUserId, link.AlbumId) : null;
    }

    public async Task<PartyAccess?> ResolveUploadAsync(
        string uploadToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(uploadToken))
        {
            return null;
        }

        var hash = HashToken(uploadToken);
        var now = _clock.GetUtcNow().UtcDateTime;

        // Match the SEPARATE upload-token hash + require the upload sub-switch on.
        // A view token hashes to TokenHash, never UploadTokenHash, so it can never
        // authorize an upload here (and vice-versa).
        var link = await _db.PartyAlbumLinks
            .AsNoTracking()
            .Where(p => p.UploadTokenHash == hash
                && p.Enabled && p.UploadEnabled && p.RevokedAt == null
                && (p.ExpiresAt == null || p.ExpiresAt > now))
            .Select(p => new { p.Id, p.OwnerUserId, p.AlbumId, p.RequireUploadApproval })
            .FirstOrDefaultAsync(cancellationToken);
        if (link is null)
        {
            return null;
        }

        var albumOk = await _db.Albums
            .AsNoTracking()
            .AnyAsync(a => a.Id == link.AlbumId && a.OwnerUserId == link.OwnerUserId && a.ShowOnTv,
                cancellationToken);
        return albumOk
            ? new PartyAccess(link.OwnerUserId, link.AlbumId, link.Id, link.RequireUploadApproval)
            : null;
    }

    // view token = URL-safe base64 of HMAC-SHA256(secret, linkId). ~43 chars, 256-bit.
    private string DeriveToken(Guid linkId) => Derive(linkId.ToByteArray());

    // upload token = HMAC over linkId ++ "upload" — a DISTINCT high-entropy value
    // from the view token for the same link, so the two are independently
    // matchable and revocable.
    private string DeriveUploadToken(Guid linkId)
        => Derive([.. linkId.ToByteArray(), .. UploadContext]);

    private static readonly byte[] UploadContext = Encoding.UTF8.GetBytes("upload");

    private string Derive(byte[] input)
    {
        using var hmac = new HMACSHA256(_secret);
        var mac = hmac.ComputeHash(input);
        return Convert.ToBase64String(mac)
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

    private static string BuildPartyUrl(string token) => $"/party/{token}";
    private static string BuildUploadUrl(string uploadToken) => $"/party/{uploadToken}/upload";
}
