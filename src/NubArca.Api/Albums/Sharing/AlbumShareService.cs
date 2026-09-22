using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Domain;

namespace NubArca.Api.Albums.Sharing;

/// <inheritdoc />
public sealed class AlbumShareService : IAlbumShareService
{
    private readonly AppDbContext _db;
    private readonly AlbumShareTokens _tokens;
    private readonly TimeProvider _clock;

    public AlbumShareService(AppDbContext db, AlbumShareTokens tokens, TimeProvider clock)
    {
        _db = db;
        _tokens = tokens;
        _clock = clock;
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    // ── Owner ───────────────────────────────────────────────────────────────

    public async Task<AlbumShareLinkDto?> GetAsync(
        Guid ownerUserId, Guid albumId, CancellationToken cancellationToken = default)
    {
        if (!await OwnsAsync(ownerUserId, albumId, cancellationToken)) return null;
        var link = await ActiveAsync(albumId, cancellationToken);
        return link is null ? null : await ProjectAsync(link, cancellationToken);
    }

    public async Task<AlbumShareLinkDto?> CreateAsync(
        Guid ownerUserId, Guid albumId, Guid createdByUserId,
        CancellationToken cancellationToken = default)
    {
        if (!await OwnsAsync(ownerUserId, albumId, cancellationToken)) return null;

        // REUSE, never re-mint. An owner opening the share panel a second time
        // must not invalidate the address they have already sent out.
        var existing = await ActiveAsync(albumId, cancellationToken);
        if (existing is not null) return await ProjectAsync(existing, cancellationToken);

        return await ProjectAsync(await MintAsync(ownerUserId, albumId, createdByUserId, cancellationToken),
            cancellationToken);
    }

    public async Task<AlbumShareLinkDto?> RotateAsync(
        Guid ownerUserId, Guid albumId, Guid createdByUserId,
        CancellationToken cancellationToken = default)
    {
        if (!await OwnsAsync(ownerUserId, albumId, cancellationToken)) return null;

        var now = Now;
        // The settings survive the rotation and the LIST does not: the owner
        // meant to change the address, not to rebuild their configuration.
        // Guests belong to the link row, so they are copied across explicitly.
        var previous = await ActiveAsync(albumId, cancellationToken);
        var carried = previous is null
            ? []
            : await _db.AlbumShareGuests.AsNoTracking()
                .Where(g => g.AlbumShareLinkId == previous.Id && g.RevokedAt == null)
                .ToListAsync(cancellationToken);

        if (previous is not null)
        {
            previous.Enabled = false;
            previous.RevokedAt = now;
            previous.UpdatedAt = now;
        }

        var link = await MintAsync(ownerUserId, albumId, createdByUserId, cancellationToken, previous);
        foreach (var guest in carried)
        {
            _db.AlbumShareGuests.Add(new AlbumShareGuest
            {
                Id = Guid.NewGuid(),
                AlbumShareLinkId = link.Id,
                Email = guest.Email,
                DisplayName = guest.DisplayName,
                CreatedAt = now,
            });
        }
        await _db.SaveChangesAsync(cancellationToken);
        return await ProjectAsync(link, cancellationToken);
    }

    public async Task<AlbumShareLinkDto?> UpdateAsync(
        Guid ownerUserId, Guid albumId, AlbumShareUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!await OwnsAsync(ownerUserId, albumId, cancellationToken)) return null;
        var link = await _db.AlbumShareLinks
            .FirstOrDefaultAsync(x => x.AlbumId == albumId && x.Enabled && x.RevokedAt == null,
                cancellationToken);
        if (link is null) return null;
        if (request.MaxUploads is int max && !AlbumShareLimits.IsValidMaxUploads(max)) return null;
        if (request.Label is { Length: > AlbumShareLimits.MaxLabelLength }) return null;

        // Omitted means unchanged, one field at a time — so two surfaces
        // configuring one link do not silently undo each other.
        link.UploadEnabled = request.UploadEnabled ?? link.UploadEnabled;
        link.AllowOriginalDownload = request.AllowOriginalDownload ?? link.AllowOriginalDownload;
        link.RequireSecondFactor = request.RequireSecondFactor ?? link.RequireSecondFactor;
        link.MaxUploads = request.MaxUploads ?? link.MaxUploads;
        if (request.Label is not null)
        {
            link.Label = string.IsNullOrWhiteSpace(request.Label) ? null : request.Label.Trim();
        }
        if (request.ExpiresAt is not null) link.ExpiresAt = request.ExpiresAt;
        link.UpdatedAt = Now;
        await _db.SaveChangesAsync(cancellationToken);
        return await ProjectAsync(link, cancellationToken);
    }

    public async Task<bool> RevokeAsync(
        Guid ownerUserId, Guid albumId, CancellationToken cancellationToken = default)
    {
        if (!await OwnsAsync(ownerUserId, albumId, cancellationToken)) return false;
        var now = Now;
        // Idempotent by construction: a set-based update over whatever is still
        // open, which is zero rows when the owner revokes twice.
        await _db.AlbumShareLinks
            .Where(x => x.AlbumId == albumId && x.Enabled)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Enabled, false)
                .SetProperty(x => x.RevokedAt, now)
                .SetProperty(x => x.UpdatedAt, now), cancellationToken);
        return true;
    }

    public async Task<AlbumShareGuestDto?> AddGuestAsync(
        Guid ownerUserId, Guid albumId, AlbumShareGuestRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!await OwnsAsync(ownerUserId, albumId, cancellationToken)) return null;
        if (!AlbumShareTokens.IsPlausibleEmail(request.Email)) return null;
        var link = await ActiveAsync(albumId, cancellationToken);
        if (link is null) return null;

        var email = AlbumShareTokens.NormalizeEmail(request.Email);
        var name = string.IsNullOrWhiteSpace(request.DisplayName)
            ? null
            : request.DisplayName.Trim()[..Math.Min(
                request.DisplayName.Trim().Length, AlbumShareLimits.MaxDisplayNameLength)];

        var existing = await _db.AlbumShareGuests
            .FirstOrDefaultAsync(g => g.AlbumShareLinkId == link.Id && g.Email == email,
                cancellationToken);
        if (existing is not null)
        {
            // Re-adding somebody the owner removed REUSES their row rather than
            // leaving two with different verdicts about the same address.
            existing.RevokedAt = null;
            existing.DisplayName = name ?? existing.DisplayName;
            await _db.SaveChangesAsync(cancellationToken);
            return new AlbumShareGuestDto(
                existing.Id, existing.Email, existing.DisplayName, existing.CreatedAt);
        }

        var live = await _db.AlbumShareGuests.CountAsync(
            g => g.AlbumShareLinkId == link.Id && g.RevokedAt == null, cancellationToken);
        if (live >= AlbumShareLimits.MaxGuests) return null;

        var guest = new AlbumShareGuest
        {
            Id = Guid.NewGuid(),
            AlbumShareLinkId = link.Id,
            Email = email,
            DisplayName = name,
            CreatedAt = Now,
        };
        _db.AlbumShareGuests.Add(guest);
        await _db.SaveChangesAsync(cancellationToken);
        return new AlbumShareGuestDto(guest.Id, guest.Email, guest.DisplayName, guest.CreatedAt);
    }

    public async Task<bool> RemoveGuestAsync(
        Guid ownerUserId, Guid albumId, Guid guestId,
        CancellationToken cancellationToken = default)
    {
        if (!await OwnsAsync(ownerUserId, albumId, cancellationToken)) return false;
        var link = await ActiveAsync(albumId, cancellationToken);
        if (link is null) return false;

        var now = Now;
        var closed = await _db.AlbumShareGuests
            .Where(g => g.Id == guestId && g.AlbumShareLinkId == link.Id && g.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(g => g.RevokedAt, now), cancellationToken);
        if (closed == 0) return false;

        // A DEVICE IS ONLY AS GOOD AS THE ADDRESS BEHIND IT. Removing somebody
        // has to close the phone they already verified, or the removal would
        // take effect only when that phone's cookie happened to expire.
        await _db.AlbumShareDevices
            .Where(d => d.AlbumShareGuestId == guestId && d.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.RevokedAt, now), cancellationToken);
        return true;
    }

    // ── The seam ────────────────────────────────────────────────────────────

    public async Task<AlbumShareResolved> ResolveAsync(
        string? token, string? deviceToken, CancellationToken cancellationToken = default)
    {
        if (!AlbumShareTokens.LooksLikeToken(token)) return AlbumShareResolved.NotFound;

        var hash = AlbumShareTokens.Hash(token!);
        var now = Now;
        var row = await _db.AlbumShareLinks.AsNoTracking()
            .Where(x => x.TokenHash == hash)
            .Select(x => new
            {
                x.Id, x.AlbumId, x.OwnerUserId, x.Enabled, x.UploadEnabled,
                x.AllowOriginalDownload, x.RequireSecondFactor,
                x.MaxUploads, x.UploadCount, x.RevokedAt, x.ExpiresAt,
            })
            .FirstOrDefaultAsync(cancellationToken);

        // One generic nothing for every reason the link does not open: unknown,
        // revoked, expired. A caller learns only that this address is not one.
        if (row is null || !row.Enabled || row.RevokedAt is not null
            || (row.ExpiresAt is DateTime expiry && expiry <= now))
        {
            return AlbumShareResolved.NotFound;
        }

        // The album must still exist AND still be the owner's. A link is a
        // capability the owner granted over their own album; it does not
        // survive the album changing hands.
        var albumOk = await _db.Albums.AsNoTracking().AnyAsync(
            a => a.Id == row.AlbumId && a.OwnerUserId == row.OwnerUserId, cancellationToken);
        if (!albumOk) return AlbumShareResolved.NotFound;

        Guid? guestId = null;
        if (row.RequireSecondFactor)
        {
            guestId = await VerifiedGuestAsync(row.Id, deviceToken, now, cancellationToken);
            if (guestId is null) return AlbumShareResolved.NeedsSecondFactor();
        }

        return AlbumShareResolved.Granted(new AlbumShareAccess(
            row.Id, row.AlbumId, row.OwnerUserId, row.UploadEnabled, row.AllowOriginalDownload,
            row.MaxUploads, row.UploadCount, row.RequireSecondFactor, guestId));
    }

    public async Task<AlbumShareUploadResult> TryClaimUploadSlotAsync(
        Guid linkId, CancellationToken cancellationToken = default)
    {
        var now = Now;
        // ONE STATEMENT claims the slot and counts it, so two phones finishing
        // together cannot both take the last one. `MaxUploads = 0` is no
        // ceiling, and is the only branch that never fails on a live link.
        var claimed = await _db.AlbumShareLinks
            .Where(x => x.Id == linkId && x.Enabled && x.UploadEnabled
                && (x.MaxUploads == 0 || x.UploadCount < x.MaxUploads))
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.UploadCount, x => x.UploadCount + 1)
                .SetProperty(x => x.UpdatedAt, now), cancellationToken);
        if (claimed > 0) return new AlbumShareUploadResult(AlbumShareUploadOutcome.Accepted);

        // It failed; saying WHY is the whole point, because the two reasons ask
        // the owner for different things — one is a switch, the other a number.
        var state = await _db.AlbumShareLinks.AsNoTracking()
            .Where(x => x.Id == linkId)
            .Select(x => new { x.Enabled, x.UploadEnabled, x.MaxUploads, x.UploadCount })
            .FirstOrDefaultAsync(cancellationToken);
        if (state is null || !state.Enabled)
            return new AlbumShareUploadResult(AlbumShareUploadOutcome.Refused);
        if (!state.UploadEnabled)
            return new AlbumShareUploadResult(AlbumShareUploadOutcome.UploadsDisabled);
        return new AlbumShareUploadResult(AlbumShareUploadOutcome.LimitReached);
    }

    public Task ReleaseUploadSlotAsync(Guid linkId, CancellationToken cancellationToken = default) =>
        _db.AlbumShareLinks
            .Where(x => x.Id == linkId && x.UploadCount > 0)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.UploadCount, x => x.UploadCount - 1),
                cancellationToken);

    // ── Internals ───────────────────────────────────────────────────────────

    private Task<bool> OwnsAsync(Guid ownerUserId, Guid albumId, CancellationToken ct) =>
        _db.Albums.AsNoTracking().AnyAsync(a => a.Id == albumId && a.OwnerUserId == ownerUserId, ct);

    private Task<AlbumShareLink?> ActiveAsync(Guid albumId, CancellationToken ct) =>
        _db.AlbumShareLinks.FirstOrDefaultAsync(
            x => x.AlbumId == albumId && x.Enabled && x.RevokedAt == null, ct);

    private async Task<AlbumShareLink> MintAsync(
        Guid ownerUserId, Guid albumId, Guid createdByUserId, CancellationToken ct,
        AlbumShareLink? carrySettingsFrom = null)
    {
        var now = Now;
        var link = new AlbumShareLink
        {
            Id = Guid.NewGuid(),
            AlbumId = albumId,
            OwnerUserId = ownerUserId,
            Enabled = true,
            UploadEnabled = carrySettingsFrom?.UploadEnabled ?? true,
            AllowOriginalDownload = carrySettingsFrom?.AllowOriginalDownload ?? false,
            RequireSecondFactor = carrySettingsFrom?.RequireSecondFactor ?? false,
            Label = carrySettingsFrom?.Label,
            MaxUploads = carrySettingsFrom?.MaxUploads ?? AlbumShareLimits.DefaultMaxUploads,
            // A rotation is a new address, not a new allowance: what the old
            // link already accepted still sits in the album.
            UploadCount = carrySettingsFrom?.UploadCount ?? 0,
            CreatedAt = now,
            UpdatedAt = now,
            CreatedByUserId = createdByUserId,
        };
        // The token is derived from the id and stored only as a digest, so this
        // is the one moment the raw value exists — and it is not kept.
        link.TokenHash = AlbumShareTokens.Hash(_tokens.DeriveToken(link.Id));
        _db.AlbumShareLinks.Add(link);
        await _db.SaveChangesAsync(ct);
        return link;
    }

    private async Task<Guid?> VerifiedGuestAsync(
        Guid linkId, string? deviceToken, DateTime now, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(deviceToken)) return null;
        var hash = AlbumShareTokens.Hash(deviceToken);
        var device = await _db.AlbumShareDevices
            .Where(d => d.TokenHash == hash && d.AlbumShareLinkId == linkId
                && d.RevokedAt == null && d.ExpiresAt > now)
            .Select(d => new { d.Id, d.AlbumShareGuestId })
            .FirstOrDefaultAsync(ct);
        if (device is null) return null;

        // The address behind the device is re-read on every request, which is
        // what makes removing somebody take effect on their next tap.
        var stillListed = await _db.AlbumShareGuests.AsNoTracking().AnyAsync(
            g => g.Id == device.AlbumShareGuestId && g.RevokedAt == null, ct);
        if (!stillListed) return null;

        await _db.AlbumShareDevices.Where(d => d.Id == device.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.LastSeenAt, now), ct);
        return device.AlbumShareGuestId;
    }

    private async Task<AlbumShareLinkDto> ProjectAsync(AlbumShareLink link, CancellationToken ct)
    {
        var guests = await _db.AlbumShareGuests.AsNoTracking()
            .Where(g => g.AlbumShareLinkId == link.Id && g.RevokedAt == null)
            .OrderBy(g => g.Email)
            .Select(g => new AlbumShareGuestDto(g.Id, g.Email, g.DisplayName, g.CreatedAt))
            .ToListAsync(ct);
        return new AlbumShareLinkDto(
            link.Id,
            AlbumShareTokens.SharePath(_tokens.DeriveToken(link.Id)),
            link.Enabled, link.UploadEnabled, link.AllowOriginalDownload,
            link.RequireSecondFactor, link.Label,
            link.MaxUploads, link.UploadCount, link.CreatedAt, link.ExpiresAt, guests);
    }
}
