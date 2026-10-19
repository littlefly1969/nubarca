using Microsoft.EntityFrameworkCore;
using NubArca.Api.Albums.Sharing;
using NubArca.Api.Data;
using NubArca.Api.Domain;

namespace NubArca.Api.MediaLists;

public sealed record AlbumSelectionState(int Version, int Count);

public sealed record AlbumSelectionItemsResult(int Requested, int Succeeded, int Skipped, int Version, int Count);

public sealed record AlbumSelectionIdsPage(IReadOnlyList<Guid> Ids, string? NextCursor, int Version, int Count);

public interface IAlbumSelectionService
{
    Task<AlbumSelectionState> GetAsync(AlbumAccessGrant grant, CancellationToken cancellationToken);
    // Null when the cursor is malformed.
    Task<AlbumSelectionIdsPage?> ListIdsAsync(
        AlbumAccessGrant grant, string? cursor, int limit, CancellationToken cancellationToken);
    Task<AlbumSelectionItemsResult> AddAsync(
        AlbumAccessGrant grant, IReadOnlyList<Guid> fileItemIds, CancellationToken cancellationToken);
    Task<AlbumSelectionItemsResult> RemoveAsync(
        AlbumAccessGrant grant, IReadOnlyList<Guid> fileItemIds, CancellationToken cancellationToken);
    Task<AlbumSelectionItemsResult> ClearAsync(AlbumAccessGrant grant, CancellationToken cancellationToken);
}

// A person's own selection inside ONE album they can open — personal, scoped to
// that album, and separate from their library selection.
//
// Every method takes an AlbumAccessGrant the caller has just resolved, so a
// revoked, declined or expired membership never reaches here. What the
// selection SHOWS is re-derived on every read from what the album serves this
// member right now (IAlbumAccessResolver.ServableMediaIds): an item the owner
// removed, trashed, excluded or vaulted, or a contribution whose member left,
// drops out without anybody rewriting selection rows. Rows hold the album id and
// file ids only — never a share token or a URL.
public sealed class AlbumSelectionService : IAlbumSelectionService
{
    private readonly AppDbContext _db;
    private readonly IAlbumAccessResolver _access;
    private readonly TimeProvider _time;

    public AlbumSelectionService(AppDbContext db, IAlbumAccessResolver access, TimeProvider time)
    {
        _db = db;
        _access = access;
        _time = time;
    }

    public async Task<AlbumSelectionState> GetAsync(AlbumAccessGrant grant, CancellationToken cancellationToken)
    {
        var list = await FindAsync(grant, cancellationToken);
        return list is null
            ? new AlbumSelectionState(0, 0)
            : new AlbumSelectionState(list.Version, await CountVisibleAsync(grant, list.Id, cancellationToken));
    }

    public async Task<AlbumSelectionIdsPage?> ListIdsAsync(
        AlbumAccessGrant grant, string? cursor, int limit, CancellationToken cancellationToken)
    {
        long position = 0;
        var fileItemId = Guid.Empty;
        var hasCursor = !string.IsNullOrWhiteSpace(cursor);
        if (hasCursor && !MediaListService.TryParseCursor(cursor!, out position, out fileItemId)) return null;

        var list = await FindAsync(grant, cancellationToken);
        if (list is null) return new AlbumSelectionIdsPage([], null, 0, 0);

        var query = VisibleItems(grant, list.Id);
        if (hasCursor)
        {
            query = query.Where(i => i.Position > position
                || (i.Position == position && i.FileItemId.CompareTo(fileItemId) > 0));
        }
        var rows = await query
            .OrderBy(i => i.Position).ThenBy(i => i.FileItemId)
            .Select(i => new { i.Position, i.FileItemId })
            .Take(limit + 1)
            .ToListAsync(cancellationToken);
        var hasMore = rows.Count > limit;
        if (hasMore) rows.RemoveAt(rows.Count - 1);
        var next = hasMore ? $"{rows[^1].Position}_{rows[^1].FileItemId:N}" : null;
        return new AlbumSelectionIdsPage(
            rows.Select(r => r.FileItemId).ToList(), next, list.Version,
            await CountVisibleAsync(grant, list.Id, cancellationToken));
    }

    public async Task<AlbumSelectionItemsResult> AddAsync(
        AlbumAccessGrant grant, IReadOnlyList<Guid> fileItemIds, CancellationToken cancellationToken)
    {
        var requested = fileItemIds.Count;
        var distinct = fileItemIds.Distinct().ToList();
        // Only what this album serves this member now. Anything else — another
        // album's media, the member's own library, a removed or trashed item —
        // is dropped without saying which.
        var eligible = distinct.Count == 0
            ? []
            : (await _access.ServableMediaIds(grant)
                .Where(id => distinct.Contains(id))
                .ToListAsync(cancellationToken)).ToHashSet();
        var ordered = distinct.Where(eligible.Contains).ToList();
        if (ordered.Count == 0)
        {
            var current = await FindAsync(grant, cancellationToken);
            return await ResultAsync(grant, current, requested, 0, cancellationToken);
        }

        var list = await EnsureAsync(grant, cancellationToken);
        var added = 0;
        // Two devices adding at once may insert the same id; the loser re-reads
        // and tries once more. Idempotent either way.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var already = (await _db.MediaListItems.AsNoTracking()
                    .Where(i => i.ListId == list.Id && ordered.Contains(i.FileItemId))
                    .Select(i => i.FileItemId)
                    .ToListAsync(cancellationToken))
                .ToHashSet();
            var toAdd = ordered.Where(id => !already.Contains(id)).ToList();
            var room = MediaListService.MaxItemsPerList
                - await _db.MediaListItems.CountAsync(i => i.ListId == list.Id, cancellationToken);
            if (toAdd.Count > room) toAdd = toAdd.Take(Math.Max(0, room)).ToList();
            if (toAdd.Count == 0) break;

            var next = (await _db.MediaListItems.Where(i => i.ListId == list.Id)
                .MaxAsync(i => (long?)i.Position, cancellationToken) ?? 0) + 1;
            var now = _time.GetUtcNow().UtcDateTime;
            foreach (var id in toAdd)
            {
                _db.MediaListItems.Add(new MediaListItem
                {
                    ListId = list.Id, FileItemId = id, Position = next++, AddedAt = now,
                });
            }
            try
            {
                await _db.SaveChangesAsync(cancellationToken);
                added = toAdd.Count;
                break;
            }
            catch (DbUpdateException) when (attempt == 0)
            {
                _db.ChangeTracker.Clear();
            }
            finally
            {
                foreach (var entry in _db.ChangeTracker.Entries<MediaListItem>().ToList())
                    entry.State = EntityState.Detached;
            }
        }
        if (added > 0) await BumpVersionAsync(list.Id, cancellationToken);
        return await ResultAsync(grant, list, requested, added, cancellationToken);
    }

    public async Task<AlbumSelectionItemsResult> RemoveAsync(
        AlbumAccessGrant grant, IReadOnlyList<Guid> fileItemIds, CancellationToken cancellationToken)
    {
        var list = await FindAsync(grant, cancellationToken);
        var distinct = fileItemIds.Distinct().ToList();
        var removed = 0;
        if (list is not null && distinct.Count > 0)
        {
            removed = await _db.MediaListItems
                .Where(i => i.ListId == list.Id && distinct.Contains(i.FileItemId))
                .ExecuteDeleteAsync(cancellationToken);
            if (removed > 0) await BumpVersionAsync(list.Id, cancellationToken);
        }
        return await ResultAsync(grant, list, fileItemIds.Count, removed, cancellationToken);
    }

    public async Task<AlbumSelectionItemsResult> ClearAsync(AlbumAccessGrant grant, CancellationToken cancellationToken)
    {
        var list = await FindAsync(grant, cancellationToken);
        var removed = 0;
        if (list is not null)
        {
            removed = await _db.MediaListItems.Where(i => i.ListId == list.Id).ExecuteDeleteAsync(cancellationToken);
            if (removed > 0) await BumpVersionAsync(list.Id, cancellationToken);
        }
        return await ResultAsync(grant, list, removed, removed, cancellationToken);
    }

    private async Task<AlbumSelectionItemsResult> ResultAsync(
        AlbumAccessGrant grant, MediaList? list, int requested, int succeeded, CancellationToken cancellationToken)
    {
        if (list is null) return new AlbumSelectionItemsResult(requested, succeeded, requested - succeeded, 0, 0);
        var version = await _db.MediaLists.AsNoTracking()
            .Where(l => l.Id == list.Id).Select(l => l.Version).FirstAsync(cancellationToken);
        return new AlbumSelectionItemsResult(requested, succeeded, requested - succeeded, version,
            await CountVisibleAsync(grant, list.Id, cancellationToken));
    }

    private IQueryable<MediaListItem> VisibleItems(AlbumAccessGrant grant, Guid listId)
    {
        var servable = _access.ServableMediaIds(grant);
        return _db.MediaListItems.AsNoTracking()
            .Where(i => i.ListId == listId && servable.Contains(i.FileItemId));
    }

    private Task<int> CountVisibleAsync(AlbumAccessGrant grant, Guid listId, CancellationToken cancellationToken) =>
        VisibleItems(grant, listId).CountAsync(cancellationToken);

    private Task<MediaList?> FindAsync(AlbumAccessGrant grant, CancellationToken cancellationToken) =>
        _db.MediaLists.AsNoTracking()
            .FirstOrDefaultAsync(l => l.OwnerUserId == grant.ActorUserId
                && l.Kind == MediaListKind.AlbumSelection
                && l.AlbumId == grant.AlbumId, cancellationToken);

    private async Task<MediaList> EnsureAsync(AlbumAccessGrant grant, CancellationToken cancellationToken)
    {
        var existing = await FindAsync(grant, cancellationToken);
        if (existing is not null) return existing;

        var now = _time.GetUtcNow().UtcDateTime;
        var created = new MediaList
        {
            Id = Guid.NewGuid(),
            OwnerUserId = grant.ActorUserId,
            Kind = MediaListKind.AlbumSelection,
            AlbumId = grant.AlbumId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.MediaLists.Add(created);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            _db.Entry(created).State = EntityState.Detached;
            return created;
        }
        catch (DbUpdateException)
        {
            // Another device created it first (ux_media_lists_owner_album_selection).
            _db.ChangeTracker.Clear();
            return (await FindAsync(grant, cancellationToken))!;
        }
    }

    private Task BumpVersionAsync(Guid listId, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        return _db.MediaLists
            .Where(l => l.Id == listId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(l => l.Version, l => l.Version + 1)
                .SetProperty(l => l.UpdatedAt, now), cancellationToken);
    }
}
