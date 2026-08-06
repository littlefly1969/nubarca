using Microsoft.EntityFrameworkCore;
using NubArca.Api.Audit;
using NubArca.Api.Data;
using NubArca.Api.Domain;

namespace NubArca.Api.Albums.Sharing;

// SHARE-ALBUM-03. See IAlbumEditingService for the contract; this file is where
// the concurrency discipline actually lives.
//
// Every mutation follows the same shape, and the ORDER matters:
//
//   grant → validate → conditional version bump → mutate → audit (by the caller)
//
// The version bump is the FIRST write and it is conditional
// (`Where(a => a.Version == expected)`), so a losing writer performs no mutation
// at all: it never reaches the reorder or the delete. That is what makes a
// conflict clean — no partial state, nothing to compensate, nothing audited.
// Doing the mutation first and checking the version afterwards would need a
// rollback and would still race.
public sealed class AlbumEditingService : IAlbumEditingService
{
    private readonly AppDbContext _db;
    private readonly TimeProvider _time;
    private readonly IAlbumAccessResolver _access;
    private readonly IAuditLogger _audit;

    public AlbumEditingService(
        AppDbContext db, TimeProvider time, IAlbumAccessResolver access, IAuditLogger audit)
    {
        _db = db;
        _time = time;
        _access = access;
        _audit = audit;
    }

    public async Task<AlbumEditResult> UpdateDetailsAsync(
        Guid actorUserId, Guid albumId, int expectedVersion,
        string? name, string? description, string? ipAddress,
        CancellationToken cancellationToken = default)
    {
        var gate = await AuthorizeAsync(albumId, actorUserId, cancellationToken);
        if (gate is not null)
        {
            return gate;
        }

        var trimmedName = name?.Trim();
        if (trimmedName is not null && (trimmedName.Length == 0 || trimmedName.Length > 255))
        {
            return Invalid("Album name must be between 1 and 255 characters.");
        }
        if (description is not null && description.Length > 1000)
        {
            return Invalid("Description must be 1000 characters or fewer.");
        }

        await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);

        if (!await TryClaimVersionAsync(albumId, expectedVersion, cancellationToken))
        {
            return await ConflictAsync(albumId, cancellationToken);
        }

        var album = await _db.Albums.FirstAsync(a => a.Id == albumId, cancellationToken);
        if (trimmedName is not null) album.Name = trimmedName;
        // A null description CLEARS it; omitting the field entirely is how a
        // caller leaves it alone, which the request record expresses by leaving
        // Description null — so "clear the description" is deliberately not
        // expressible here and is done by sending an empty string.
        if (description is not null) album.Description = description.Length == 0 ? null : description;

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Album names are unique per owner. Roll back rather than leave the
            // version incremented for a mutation that did not happen.
            await tx.RollbackAsync(cancellationToken);
            return Invalid("An album with this name already exists.");
        }

        await AuditAsync(actorUserId, albumId, AuditActions.AlbumEditDetails, ipAddress,
            new { version = album.Version }, cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return Ok(album);
    }

    public async Task<AlbumEditResult> SetCoverAsync(
        Guid actorUserId, Guid albumId, int expectedVersion, Guid? fileItemId, string? ipAddress,
        CancellationToken cancellationToken = default)
    {
        var gate = await AuthorizeAsync(albumId, actorUserId, cancellationToken);
        if (gate is not null)
        {
            return gate;
        }

        // A cover must be a CURRENT, SERVABLE member. Checked against the same
        // predicate the media routes use, so a cover can never point at
        // something the album's own members cannot open.
        if (fileItemId is not null)
        {
            var servable = await ServableMemberIdsAsync(albumId, cancellationToken);
            if (!servable.Contains(fileItemId.Value))
            {
                return Invalid("That item is not currently a visible member of this album.");
            }
        }

        await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);

        if (!await TryClaimVersionAsync(albumId, expectedVersion, cancellationToken))
        {
            return await ConflictAsync(albumId, cancellationToken);
        }

        var album = await _db.Albums.FirstAsync(a => a.Id == albumId, cancellationToken);
        album.CoverFileItemId = fileItemId;
        await _db.SaveChangesAsync(cancellationToken);

        await AuditAsync(actorUserId, albumId, AuditActions.AlbumEditCover, ipAddress,
            new { version = album.Version, cleared = fileItemId is null }, cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return Ok(album);
    }

    public async Task<AlbumEditResult> ReorderAsync(
        Guid actorUserId, Guid albumId, int expectedVersion,
        IReadOnlyList<Guid> orderedAlbumItemIds, string? ipAddress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(orderedAlbumItemIds);

        var gate = await AuthorizeAsync(albumId, actorUserId, cancellationToken);
        if (gate is not null)
        {
            return gate;
        }

        await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);

        if (!await TryClaimVersionAsync(albumId, expectedVersion, cancellationToken))
        {
            return await ConflictAsync(albumId, cancellationToken);
        }

        // Read the current membership INSIDE the claimed version, so the set
        // being validated is the set being reordered.
        var current = await _db.AlbumItems
            .Where(ai => ai.AlbumId == albumId)
            .ToListAsync(cancellationToken);

        // Exact set equality: no duplicates, no omissions, nothing foreign. A
        // partial list is refused rather than interpreted — see the interface.
        var requested = orderedAlbumItemIds.ToList();
        if (requested.Count != current.Count
            || requested.Distinct().Count() != requested.Count
            || !requested.ToHashSet().SetEquals(current.Select(ai => ai.Id)))
        {
            await tx.RollbackAsync(cancellationToken);
            return Invalid("The order must list exactly the album's current items, once each.");
        }

        // Normalized to a contiguous 1..n: the stored order never depends on
        // what the client sent as indices, only on the sequence it sent.
        var byId = current.ToDictionary(ai => ai.Id);
        for (var i = 0; i < requested.Count; i++)
        {
            byId[requested[i]].SortOrder = i + 1;
        }
        await _db.SaveChangesAsync(cancellationToken);

        var album = await _db.Albums.AsNoTracking()
            .FirstAsync(a => a.Id == albumId, cancellationToken);
        // The COUNT, never the id sequence: the order is recoverable from the
        // album, and a list of ids in the trail grows without bound.
        await AuditAsync(actorUserId, albumId, AuditActions.AlbumEditReorder, ipAddress,
            new { version = album.Version, items = requested.Count }, cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return Ok(album);
    }

    public async Task<AlbumEditResult> RemoveItemAsync(
        Guid actorUserId, Guid albumId, int expectedVersion, Guid albumItemId, string? ipAddress,
        CancellationToken cancellationToken = default)
    {
        var gate = await AuthorizeAsync(albumId, actorUserId, cancellationToken);
        if (gate is not null)
        {
            return gate;
        }

        await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);

        if (!await TryClaimVersionAsync(albumId, expectedVersion, cancellationToken))
        {
            return await ConflictAsync(albumId, cancellationToken);
        }

        var item = await _db.AlbumItems
            .FirstOrDefaultAsync(ai => ai.AlbumId == albumId && ai.Id == albumItemId, cancellationToken);
        if (item is null)
        {
            await tx.RollbackAsync(cancellationToken);
            return new AlbumEditResult(AlbumEditOutcome.ItemNotFound);
        }

        var fileItemId = item.FileItemId;
        _db.AlbumItems.Remove(item);
        await _db.SaveChangesAsync(cancellationToken);

        // The chosen cover cannot outlive the row it pointed at, and the order
        // is recompacted so no hole survives the removal. Same transaction.
        await _db.Albums
            .Where(a => a.Id == albumId && a.CoverFileItemId == fileItemId)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.CoverFileItemId, (Guid?)null),
                cancellationToken);
        await CompactAsync(albumId, cancellationToken);

        var album = await _db.Albums.AsNoTracking()
            .FirstAsync(a => a.Id == albumId, cancellationToken);
        await AuditAsync(actorUserId, albumId, AuditActions.AlbumEditRemoveItem, ipAddress,
            new { version = album.Version, albumItemId, reason = "removed_by_curator" },
            cancellationToken);
        await tx.CommitAsync(cancellationToken);

        return Ok(album) with { CoverFileItemId = album.CoverFileItemId };
    }

    // ── Internals ───────────────────────────────────────────────────────────

    // Returns a failure result when the caller may NOT edit, or null when they
    // may. The grant is resolved fresh every time: an editor whose role was
    // downgraded, or whose membership was revoked, after opening a form is
    // stopped here rather than by a stale client-side check.
    private async Task<AlbumEditResult?> AuthorizeAsync(
        Guid albumId, Guid actorUserId, CancellationToken cancellationToken)
    {
        var grant = await _access.ResolveAsync(albumId, actorUserId, cancellationToken);
        if (grant is null)
        {
            return new AlbumEditResult(AlbumEditOutcome.NotAccessible);
        }
        // The OWNER edits through this same path — no separate implementation,
        // and therefore no way for the two to diverge.
        if (!grant.IsOwner && !AlbumRoles.CanEdit(grant.Role))
        {
            return new AlbumEditResult(AlbumEditOutcome.RoleNotPermitted);
        }
        return null;
    }

    // The conditional version bump. Returns false when somebody else moved the
    // album first — and because this is the first write of every mutation, a
    // false here means nothing was changed at all.
    private async Task<bool> TryClaimVersionAsync(
        Guid albumId, int expectedVersion, CancellationToken cancellationToken)
    {
        var rows = await _db.Albums
            .Where(a => a.Id == albumId && a.Version == expectedVersion)
            .ExecuteUpdateAsync(
                s => s.SetProperty(a => a.Version, a => a.Version + 1)
                      .SetProperty(a => a.UpdatedAt, _ => _time.GetUtcNow().UtcDateTime),
                cancellationToken);
        return rows == 1;
    }

    private async Task<AlbumEditResult> ConflictAsync(
        Guid albumId, CancellationToken cancellationToken)
    {
        // Carry the CURRENT state so the client can refresh and explain the
        // collision, rather than blindly retrying a destructive command.
        var album = await _db.Albums.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == albumId, cancellationToken);
        return new AlbumEditResult(
            AlbumEditOutcome.VersionConflict,
            album?.Version, album?.Name, album?.Description, album?.CoverFileItemId,
            "This album changed while you were editing it.");
    }

    // Servable members, by the same rules the media routes enforce: the album
    // owner's own media, or a contribution whose source owner is still an
    // active member. Used to validate a cover choice.
    private async Task<HashSet<Guid>> ServableMemberIdsAsync(
        Guid albumId, CancellationToken cancellationToken)
    {
        var ids = await _db.AlbumItems
            .AsNoTracking()
            .Where(ai => ai.AlbumId == albumId)
            .Join(_db.Albums.AsNoTracking(),
                ai => ai.AlbumId, a => a.Id,
                (ai, a) => new { ai.FileItemId, ai.AddedByUserId, AlbumOwnerUserId = a.OwnerUserId })
            .Join(_db.FileItems.AsNoTracking(),
                x => x.FileItemId, f => f.Id,
                (x, f) => new
                {
                    x.FileItemId, x.AddedByUserId, x.AlbumOwnerUserId,
                    FileOwnerUserId = f.OwnerUserId, f.DeletedAt, f.MediaLibraryState,
                })
            .Where(x => x.DeletedAt == null && x.MediaLibraryState == MediaLibraryState.Active)
            .Where(x => x.FileOwnerUserId == x.AlbumOwnerUserId
                || (x.AddedByUserId == x.FileOwnerUserId
                    && _db.Users.Any(u => u.Id == x.FileOwnerUserId && u.DisabledAt == null)
                    && _db.AlbumMemberships.Any(m =>
                        m.AlbumId == albumId
                        && m.MemberUserId == x.FileOwnerUserId
                        && m.State == AlbumMembershipStates.Accepted
                        && m.RevokedAt == null)))
            .Select(x => x.FileItemId)
            .ToListAsync(cancellationToken);
        return ids.ToHashSet();
    }

    private async Task CompactAsync(Guid albumId, CancellationToken cancellationToken)
    {
        var ordered = await _db.AlbumItems
            .Where(ai => ai.AlbumId == albumId)
            .OrderBy(ai => ai.SortOrder)
            .ThenBy(ai => ai.FileItemId)
            .ToListAsync(cancellationToken);
        var position = 1;
        var changed = false;
        foreach (var item in ordered)
        {
            if (item.SortOrder != position) { item.SortOrder = position; changed = true; }
            position += 1;
        }
        if (changed) await _db.SaveChangesAsync(cancellationToken);
    }

    // WriteAsync, not LogAsync: inside the open transaction, and NOT swallowing.
    // A curation change that committed without the entry explaining it is
    // exactly the gap this audit exists to close, so an audit failure aborts
    // the whole mutation.
    private Task AuditAsync(
        Guid actorUserId, Guid albumId, string action, string? ipAddress,
        object metadata, CancellationToken cancellationToken) =>
        _audit.WriteAsync(actorUserId, action, AuditEntityTypes.Album, albumId, ipAddress,
            Flatten(albumId, metadata), cancellationToken);

    private static Dictionary<string, object?> Flatten(Guid albumId, object metadata)
    {
        var map = new Dictionary<string, object?> { ["albumId"] = albumId };
        foreach (var p in metadata.GetType().GetProperties())
        {
            map[p.Name] = p.GetValue(metadata);
        }
        return map;
    }

    private static AlbumEditResult Ok(Album album) => new(
        AlbumEditOutcome.Ok, album.Version, album.Name, album.Description, album.CoverFileItemId);

    private static AlbumEditResult Invalid(string message) =>
        new(AlbumEditOutcome.InvalidCommand, Message: message);

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException?.Message.Contains("23505") == true
        || ex.InnerException?.Message.Contains("UNIQUE") == true;
}
