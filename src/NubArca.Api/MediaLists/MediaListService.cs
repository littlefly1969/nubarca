using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.MediaLibrary;

namespace NubArca.Api.MediaLists;

// DTOs. A list never carries a blob id, a hash, a storage key, a URL it stores
// or a token: only the owner's own FileItem ids and what the gallery already
// shows the owner about them.
public sealed record MediaListSummary(
    Guid Id, string? Name, string Kind, int Version, int Count, DateTime CreatedAt, DateTime UpdatedAt);

public sealed record MediaListItemsResult(int Requested, int Succeeded, int Skipped, int Version, int Count);

public sealed record MediaListIdsPage(IReadOnlyList<Guid> Ids, string? NextCursor, int Version, int Count);

public sealed record MediaListMediaPage(IReadOnlyList<MediaItem> Items, string? NextCursor, int Version, int Count);

public enum MediaListOutcome { Ok, NotFound, Invalid, Conflict }

public sealed record MediaListResult<T>(MediaListOutcome Outcome, T? Value, string? Error = null)
{
    public static MediaListResult<T> Ok(T value) => new(MediaListOutcome.Ok, value);
    public static MediaListResult<T> NotFound() => new(MediaListOutcome.NotFound, default);
    public static MediaListResult<T> Invalid(string error) => new(MediaListOutcome.Invalid, default, error);
    public static MediaListResult<T> Conflict(string error) => new(MediaListOutcome.Conflict, default, error);
}

public interface IMediaListService
{
    Task<IReadOnlyList<MediaListSummary>> ListAsync(Guid ownerUserId, CancellationToken cancellationToken);
    Task<MediaListSummary?> GetAsync(Guid ownerUserId, Guid listId, CancellationToken cancellationToken);
    // The account's current selection, created on first use.
    Task<MediaListSummary> GetSelectionAsync(Guid ownerUserId, CancellationToken cancellationToken);
    Task<MediaListResult<MediaListSummary>> CreateAsync(
        Guid ownerUserId, string? name, IReadOnlyList<Guid>? fileItemIds, CancellationToken cancellationToken);
    Task<MediaListResult<MediaListSummary>> RenameAsync(
        Guid ownerUserId, Guid listId, string? name, int? expectedVersion, CancellationToken cancellationToken);
    Task<MediaListResult<MediaListSummary>> DuplicateAsync(
        Guid ownerUserId, Guid listId, string? name, CancellationToken cancellationToken);
    Task<MediaListResult<bool>> DeleteAsync(Guid ownerUserId, Guid listId, CancellationToken cancellationToken);
    Task<MediaListResult<MediaListItemsResult>> AddItemsAsync(
        Guid ownerUserId, Guid listId, IReadOnlyList<Guid> fileItemIds, CancellationToken cancellationToken);
    Task<MediaListResult<MediaListItemsResult>> RemoveItemsAsync(
        Guid ownerUserId, Guid listId, IReadOnlyList<Guid> fileItemIds, CancellationToken cancellationToken);
    Task<MediaListResult<MediaListItemsResult>> ClearAsync(
        Guid ownerUserId, Guid listId, CancellationToken cancellationToken);
    Task<MediaListResult<MediaListIdsPage>> ListIdsAsync(
        Guid ownerUserId, Guid listId, string? cursor, int limit, CancellationToken cancellationToken);
    Task<MediaListResult<MediaListMediaPage>> ListItemsAsync(
        Guid ownerUserId, Guid listId, string? cursor, int limit, CancellationToken cancellationToken);
}

// Owner media lists and the account's current selection.
//
// Visibility is re-derived on every read: an entry counts and shows only while
// its FileItem is the owner's, not trashed, and not in the Private Vault (the
// global query filter). Nothing is rewritten when a file goes to the trash or
// the Vault, so restoring it brings it back into every list it was in; a
// permanently deleted file takes its entries with it (FK cascade).
public sealed class MediaListService : IMediaListService
{
    public const int MaxNameLength = 120;
    public const int MaxItemsPerRequest = 3000;
    public const int MaxItemsPerList = 20000;
    public const int MaxIdsPageSize = 1000;
    public const int MaxMediaPageSize = 100;

    private readonly AppDbContext _db;
    private readonly IFileItemService _files;
    private readonly TimeProvider _time;

    public MediaListService(AppDbContext db, IFileItemService files, TimeProvider time)
    {
        _db = db;
        _files = files;
        _time = time;
    }

    public async Task<IReadOnlyList<MediaListSummary>> ListAsync(Guid ownerUserId, CancellationToken cancellationToken)
    {
        var lists = await _db.MediaLists.AsNoTracking()
            .Where(l => l.OwnerUserId == ownerUserId && l.Kind == MediaListKind.Named)
            .OrderByDescending(l => l.UpdatedAt).ThenBy(l => l.Id)
            .ToListAsync(cancellationToken);
        var ids = lists.Select(l => l.Id).ToList();
        var counts = await (
                from i in _db.MediaListItems.AsNoTracking()
                where ids.Contains(i.ListId)
                join f in _db.FileItems.AsNoTracking() on i.FileItemId equals f.Id
                where f.OwnerUserId == ownerUserId && f.DeletedAt == null
                group i by i.ListId into g
                select new { ListId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.ListId, x => x.Count, cancellationToken);
        return lists.Select(l => ToSummary(l, counts.GetValueOrDefault(l.Id))).ToList();
    }

    public async Task<MediaListSummary?> GetAsync(Guid ownerUserId, Guid listId, CancellationToken cancellationToken)
    {
        var list = await FindAsync(ownerUserId, listId, cancellationToken);
        return list is null ? null : ToSummary(list, await CountVisibleAsync(ownerUserId, listId, cancellationToken));
    }

    public async Task<MediaListSummary> GetSelectionAsync(Guid ownerUserId, CancellationToken cancellationToken)
    {
        var list = await EnsureSelectionAsync(ownerUserId, cancellationToken);
        return ToSummary(list, await CountVisibleAsync(ownerUserId, list.Id, cancellationToken));
    }

    public async Task<MediaListResult<MediaListSummary>> CreateAsync(
        Guid ownerUserId, string? name, IReadOnlyList<Guid>? fileItemIds, CancellationToken cancellationToken)
    {
        if (!TryNormalizeName(name, out var normalized, out var error))
            return MediaListResult<MediaListSummary>.Invalid(error!);
        if (fileItemIds is { Count: > MaxItemsPerRequest })
            return MediaListResult<MediaListSummary>.Invalid($"At most {MaxItemsPerRequest} items per request.");

        var now = _time.GetUtcNow().UtcDateTime;
        var list = new MediaList
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerUserId,
            Name = normalized,
            Kind = MediaListKind.Named,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.MediaLists.Add(list);
        await _db.SaveChangesAsync(cancellationToken);
        _db.Entry(list).State = EntityState.Detached;

        if (fileItemIds is { Count: > 0 })
            await AddItemsCoreAsync(ownerUserId, list.Id, fileItemIds, cancellationToken);
        return MediaListResult<MediaListSummary>.Ok((await GetAsync(ownerUserId, list.Id, cancellationToken))!);
    }

    public async Task<MediaListResult<MediaListSummary>> RenameAsync(
        Guid ownerUserId, Guid listId, string? name, int? expectedVersion, CancellationToken cancellationToken)
    {
        var list = await FindAsync(ownerUserId, listId, cancellationToken);
        if (list is null) return MediaListResult<MediaListSummary>.NotFound();
        if (list.Kind != MediaListKind.Named)
            return MediaListResult<MediaListSummary>.Invalid("The current selection has no name.");
        if (!TryNormalizeName(name, out var normalized, out var error))
            return MediaListResult<MediaListSummary>.Invalid(error!);

        // Optimistic: the rename lands only on the version the caller saw. A
        // rename without a version is last-writer-wins, as for an album.
        var now = _time.GetUtcNow().UtcDateTime;
        var updated = await _db.MediaLists
            .Where(l => l.Id == listId && l.OwnerUserId == ownerUserId
                && (expectedVersion == null || l.Version == expectedVersion))
            .ExecuteUpdateAsync(s => s
                .SetProperty(l => l.Name, normalized)
                .SetProperty(l => l.Version, l => l.Version + 1)
                .SetProperty(l => l.UpdatedAt, now), cancellationToken);
        if (updated == 0)
            return MediaListResult<MediaListSummary>.Conflict("The list changed on another device.");
        return MediaListResult<MediaListSummary>.Ok((await GetAsync(ownerUserId, listId, cancellationToken))!);
    }

    public async Task<MediaListResult<MediaListSummary>> DuplicateAsync(
        Guid ownerUserId, Guid listId, string? name, CancellationToken cancellationToken)
    {
        var source = await FindAsync(ownerUserId, listId, cancellationToken);
        if (source is null) return MediaListResult<MediaListSummary>.NotFound();
        // No name given: "<name> (2)" for a named list. The selection has no
        // name to derive one from, so saving it as a list needs one.
        var requested = name ?? (source.Name is null ? null : $"{source.Name} (2)");
        if (requested is { Length: > MaxNameLength }) requested = requested[..MaxNameLength];
        if (!TryNormalizeName(requested, out var normalized, out var error))
            return MediaListResult<MediaListSummary>.Invalid(error!);

        var now = _time.GetUtcNow().UtcDateTime;
        var copy = new MediaList
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerUserId,
            Name = normalized,
            Kind = MediaListKind.Named,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.MediaLists.Add(copy);
        // References only, copied in the source's order; entries the owner can
        // no longer see are not carried over.
        var entries = await VisibleItems(ownerUserId, listId)
            .OrderBy(i => i.Position).ThenBy(i => i.FileItemId)
            .Select(i => i.FileItemId)
            .ToListAsync(cancellationToken);
        long position = 0;
        foreach (var fileItemId in entries)
        {
            _db.MediaListItems.Add(new MediaListItem
            {
                ListId = copy.Id, FileItemId = fileItemId, Position = ++position, AddedAt = now,
            });
        }
        await _db.SaveChangesAsync(cancellationToken);
        _db.ChangeTracker.Clear();
        return MediaListResult<MediaListSummary>.Ok((await GetAsync(ownerUserId, copy.Id, cancellationToken))!);
    }

    public async Task<MediaListResult<bool>> DeleteAsync(Guid ownerUserId, Guid listId, CancellationToken cancellationToken)
    {
        var list = await FindAsync(ownerUserId, listId, cancellationToken);
        if (list is null) return MediaListResult<bool>.NotFound();
        if (list.Kind != MediaListKind.Named)
            return MediaListResult<bool>.Invalid("The current selection is cleared, not deleted.");
        // Entries first (explicit, so the result does not depend on the database
        // enforcing the cascade), then the list. Files are never touched.
        await _db.MediaListItems.Where(i => i.ListId == listId).ExecuteDeleteAsync(cancellationToken);
        await _db.MediaLists.Where(l => l.Id == listId && l.OwnerUserId == ownerUserId)
            .ExecuteDeleteAsync(cancellationToken);
        return MediaListResult<bool>.Ok(true);
    }

    public async Task<MediaListResult<MediaListItemsResult>> AddItemsAsync(
        Guid ownerUserId, Guid listId, IReadOnlyList<Guid> fileItemIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fileItemIds);
        if (fileItemIds.Count > MaxItemsPerRequest)
            return MediaListResult<MediaListItemsResult>.Invalid($"At most {MaxItemsPerRequest} items per request.");
        if (await FindAsync(ownerUserId, listId, cancellationToken) is null)
            return MediaListResult<MediaListItemsResult>.NotFound();
        return MediaListResult<MediaListItemsResult>.Ok(
            await AddItemsCoreAsync(ownerUserId, listId, fileItemIds, cancellationToken));
    }

    public async Task<MediaListResult<MediaListItemsResult>> RemoveItemsAsync(
        Guid ownerUserId, Guid listId, IReadOnlyList<Guid> fileItemIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fileItemIds);
        if (fileItemIds.Count > MaxItemsPerRequest)
            return MediaListResult<MediaListItemsResult>.Invalid($"At most {MaxItemsPerRequest} items per request.");
        if (await FindAsync(ownerUserId, listId, cancellationToken) is null)
            return MediaListResult<MediaListItemsResult>.NotFound();

        var distinct = fileItemIds.Distinct().ToList();
        var removed = distinct.Count == 0
            ? 0
            : await _db.MediaListItems
                .Where(i => i.ListId == listId && distinct.Contains(i.FileItemId))
                .ExecuteDeleteAsync(cancellationToken);
        if (removed > 0) await BumpVersionAsync(listId, cancellationToken);
        return MediaListResult<MediaListItemsResult>.Ok(
            await ItemsResultAsync(ownerUserId, listId, fileItemIds.Count, removed, cancellationToken));
    }

    public async Task<MediaListResult<MediaListItemsResult>> ClearAsync(
        Guid ownerUserId, Guid listId, CancellationToken cancellationToken)
    {
        if (await FindAsync(ownerUserId, listId, cancellationToken) is null)
            return MediaListResult<MediaListItemsResult>.NotFound();
        var removed = await _db.MediaListItems.Where(i => i.ListId == listId).ExecuteDeleteAsync(cancellationToken);
        if (removed > 0) await BumpVersionAsync(listId, cancellationToken);
        return MediaListResult<MediaListItemsResult>.Ok(
            await ItemsResultAsync(ownerUserId, listId, removed, removed, cancellationToken));
    }

    public async Task<MediaListResult<MediaListIdsPage>> ListIdsAsync(
        Guid ownerUserId, Guid listId, string? cursor, int limit, CancellationToken cancellationToken)
    {
        if (limit < 1 || limit > MaxIdsPageSize)
            return MediaListResult<MediaListIdsPage>.Invalid($"'limit' must be between 1 and {MaxIdsPageSize}.");
        var list = await FindAsync(ownerUserId, listId, cancellationToken);
        if (list is null) return MediaListResult<MediaListIdsPage>.NotFound();
        var page = await PageAsync(ownerUserId, listId, cursor, limit, cancellationToken);
        if (page is null) return MediaListResult<MediaListIdsPage>.Invalid("'cursor' is malformed.");
        return MediaListResult<MediaListIdsPage>.Ok(new MediaListIdsPage(
            page.Value.Ids, page.Value.Next, list.Version, await CountVisibleAsync(ownerUserId, listId, cancellationToken)));
    }

    public async Task<MediaListResult<MediaListMediaPage>> ListItemsAsync(
        Guid ownerUserId, Guid listId, string? cursor, int limit, CancellationToken cancellationToken)
    {
        if (limit < 1 || limit > MaxMediaPageSize)
            return MediaListResult<MediaListMediaPage>.Invalid($"'limit' must be between 1 and {MaxMediaPageSize}.");
        var list = await FindAsync(ownerUserId, listId, cancellationToken);
        if (list is null) return MediaListResult<MediaListMediaPage>.NotFound();
        var page = await PageAsync(ownerUserId, listId, cursor, limit, cancellationToken);
        if (page is null) return MediaListResult<MediaListMediaPage>.Invalid("'cursor' is malformed.");
        // Active AND Excluded: a photo picked from "Trascurate" stays visible in
        // the list it was put in. Trash and Vault stay out (gallery gate).
        var items = await _files.ListMediaByIdsAsync(ownerUserId, page.Value.Ids, MediaLibraryScope.All, cancellationToken);
        return MediaListResult<MediaListMediaPage>.Ok(new MediaListMediaPage(
            items, page.Value.Next, list.Version, await CountVisibleAsync(ownerUserId, listId, cancellationToken)));
    }

    // ---- internals ----

    private async Task<MediaListItemsResult> AddItemsCoreAsync(
        Guid ownerUserId, Guid listId, IReadOnlyList<Guid> fileItemIds, CancellationToken cancellationToken)
    {
        var requested = fileItemIds.Count;
        var distinct = fileItemIds.Distinct().ToList();
        var added = 0;
        if (distinct.Count > 0)
        {
            // Only the owner's own, untrashed media. Foreign, missing, trashed,
            // Vault (global filter) and non-media ids are dropped without saying
            // which — no existence leak.
            var eligible = await _db.FileItems.AsNoTracking()
                .Where(f => distinct.Contains(f.Id) && f.OwnerUserId == ownerUserId && f.DeletedAt == null
                    && (f.MimeType.StartsWith("image/") || f.MimeType.StartsWith("video/")))
                .Select(f => f.Id)
                .ToListAsync(cancellationToken);
            var eligibleSet = eligible.ToHashSet();
            // Keep the caller's order (pick order), not the database's.
            var ordered = distinct.Where(eligibleSet.Contains).ToList();

            // Two devices adding at once may both insert the same id; the loser
            // re-reads what is already there and tries once more. The add is
            // idempotent either way.
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var already = (await _db.MediaListItems.AsNoTracking()
                        .Where(i => i.ListId == listId && ordered.Contains(i.FileItemId))
                        .Select(i => i.FileItemId)
                        .ToListAsync(cancellationToken))
                    .ToHashSet();
                var toAdd = ordered.Where(id => !already.Contains(id)).ToList();
                var room = MaxItemsPerList - await _db.MediaListItems.CountAsync(i => i.ListId == listId, cancellationToken);
                if (toAdd.Count > room) toAdd = toAdd.Take(Math.Max(0, room)).ToList();
                if (toAdd.Count == 0) break;

                var next = (await _db.MediaListItems.Where(i => i.ListId == listId)
                    .MaxAsync(i => (long?)i.Position, cancellationToken) ?? 0) + 1;
                var now = _time.GetUtcNow().UtcDateTime;
                foreach (var id in toAdd)
                {
                    _db.MediaListItems.Add(new MediaListItem
                    {
                        ListId = listId, FileItemId = id, Position = next++, AddedAt = now,
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
            if (added > 0) await BumpVersionAsync(listId, cancellationToken);
        }
        return await ItemsResultAsync(ownerUserId, listId, requested, added, cancellationToken);
    }

    private async Task<MediaListItemsResult> ItemsResultAsync(
        Guid ownerUserId, Guid listId, int requested, int succeeded, CancellationToken cancellationToken)
    {
        var version = await _db.MediaLists.AsNoTracking()
            .Where(l => l.Id == listId).Select(l => l.Version).FirstAsync(cancellationToken);
        return new MediaListItemsResult(requested, succeeded, requested - succeeded, version,
            await CountVisibleAsync(ownerUserId, listId, cancellationToken));
    }

    private async Task<(IReadOnlyList<Guid> Ids, string? Next)?> PageAsync(
        Guid ownerUserId, Guid listId, string? cursor, int limit, CancellationToken cancellationToken)
    {
        var query = VisibleItems(ownerUserId, listId);
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            if (!TryParseCursor(cursor, out var position, out var fileItemId)) return null;
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
        return (rows.Select(r => r.FileItemId).ToList(), next);
    }

    private static bool TryParseCursor(string cursor, out long position, out Guid fileItemId)
    {
        position = 0;
        fileItemId = Guid.Empty;
        var parts = cursor.Split('_');
        return parts.Length == 2
            && long.TryParse(parts[0], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out position)
            && Guid.TryParseExact(parts[1], "N", out fileItemId);
    }

    private IQueryable<MediaListItem> VisibleItems(Guid ownerUserId, Guid listId) =>
        from i in _db.MediaListItems.AsNoTracking()
        where i.ListId == listId
        join f in _db.FileItems.AsNoTracking() on i.FileItemId equals f.Id
        where f.OwnerUserId == ownerUserId && f.DeletedAt == null
        select i;

    private Task<int> CountVisibleAsync(Guid ownerUserId, Guid listId, CancellationToken cancellationToken) =>
        VisibleItems(ownerUserId, listId).CountAsync(cancellationToken);

    private Task<MediaList?> FindAsync(Guid ownerUserId, Guid listId, CancellationToken cancellationToken) =>
        _db.MediaLists.AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == listId && l.OwnerUserId == ownerUserId, cancellationToken);

    private async Task<MediaList> EnsureSelectionAsync(Guid ownerUserId, CancellationToken cancellationToken)
    {
        var existing = await _db.MediaLists.AsNoTracking()
            .FirstOrDefaultAsync(l => l.OwnerUserId == ownerUserId && l.Kind == MediaListKind.CurrentSelection,
                cancellationToken);
        if (existing is not null) return existing;

        var now = _time.GetUtcNow().UtcDateTime;
        var created = new MediaList
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerUserId,
            Kind = MediaListKind.CurrentSelection,
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
            // Another device created it first (ux_media_lists_owner_selection).
            _db.ChangeTracker.Clear();
            return await _db.MediaLists.AsNoTracking()
                .FirstAsync(l => l.OwnerUserId == ownerUserId && l.Kind == MediaListKind.CurrentSelection,
                    cancellationToken);
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

    private static bool TryNormalizeName(string? name, out string? normalized, out string? error)
    {
        normalized = name?.Trim();
        error = null;
        if (string.IsNullOrEmpty(normalized))
        {
            error = "'name' is required.";
            return false;
        }
        if (normalized.Length > MaxNameLength || normalized.Any(char.IsControl))
        {
            error = $"'name' must be at most {MaxNameLength} printable characters.";
            return false;
        }
        return true;
    }

    private static MediaListSummary ToSummary(MediaList list, int count) => new(
        list.Id, list.Name, list.Kind == MediaListKind.CurrentSelection ? "selection" : "named",
        list.Version, count, list.CreatedAt, list.UpdatedAt);
}
