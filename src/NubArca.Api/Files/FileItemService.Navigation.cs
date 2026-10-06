using System.Globalization;
using Microsoft.EntityFrameworkCore;
using NubArca.Api.Media;

namespace NubArca.Api.Files;

public sealed partial class FileItemService
{
    public async Task<MediaNavigationIndex> MediaNavigationAsync(
        Guid ownerUserId, ImageFilters filters, MediaKindScope kind, ImageSortField sort,
        ImageSortDirection direction, CancellationToken cancellationToken)
    {
        var query = BuildGalleryQuery(ownerUserId, filters, kind);
        List<MediaNavigationBucket> buckets;
        if (sort == ImageSortField.Name)
        {
            // Group and order in the DATABASE's collation, exactly like the wall.
            // No complete list of media ids/names is transferred or held in memory.
            var groups = query.Select(DisplaySortKeyExpression())
                .GroupBy(name => name.Substring(0, 1))
                .Select(g => new { Key = g.Key, Count = g.Count(), First = g.Min() });
            var ordered = direction == ImageSortDirection.Asc
                ? groups.OrderBy(g => g.First) : groups.OrderByDescending(g => g.First);
            buckets = (await ordered.ToListAsync(cancellationToken))
                .Select(g => new MediaNavigationBucket("n:" + g.Key, g.Count)).ToList();
        }
        else
        {
            var dates = sort == ImageSortField.DateTaken
                ? query.Select(f => f.EffectiveDateTaken) : query.Select(f => f.CreatedAt);
            var groups = dates.GroupBy(d => d.Year * 100 + d.Month)
                .Select(g => new { Key = g.Key, Count = g.Count() });
            var ordered = direction == ImageSortDirection.Asc
                ? groups.OrderBy(g => g.Key) : groups.OrderByDescending(g => g.Key);
            buckets = (await ordered.ToListAsync(cancellationToken)).Select(g =>
                new MediaNavigationBucket($"{g.Key / 100:D4}-{g.Key % 100:D2}", g.Count)).ToList();
        }
        return new(buckets);
    }

    public static bool IsNavigationTarget(ImageSortField sort, string? target)
    {
        if (target is null) return false;
        if (sort == ImageSortField.Name)
            return target.StartsWith("n:", StringComparison.Ordinal) && target.Length is >= 2 and <= 4;
        return sort is ImageSortField.Created or ImageSortField.DateTaken
            && DateTime.TryParseExact(target, "yyyy-MM", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out _);
    }

    public async Task<MediaNavigationWindow?> MediaWindowAsync(
        Guid ownerUserId, ImageFilters filters, MediaKindScope kind, ImageSortField sort,
        ImageSortDirection direction, int limit, string? target, ImageCursor? cursor,
        bool before, CancellationToken cancellationToken)
    {
        var fingerprint = kind.MediaCursorFingerprint(filters);
        var query = BuildGalleryQuery(ownerUserId, filters, kind);
        var inclusive = cursor is null;
        if (inclusive)
        {
            if (!IsNavigationTarget(sort, target)) return null;
            var group = query;
            if (sort == ImageSortField.Name)
            {
                var initial = target![2..];
                group = group.Where(f => (_db.FileItemUserMetadata
                    .Where(u => u.FileItemId == f.Id).Select(u => u.Title).FirstOrDefault()
                    ?? f.Name).ToLower().Substring(0, 1) == initial);
            }
            else
            {
                var start = DateTime.SpecifyKind(DateTime.ParseExact(target!, "yyyy-MM", CultureInfo.InvariantCulture), DateTimeKind.Utc);
                // DateTime.MaxValue's last month has no representable next month.
                var end = start.Year == 9999 && start.Month == 12 ? DateTime.MaxValue : start.AddMonths(1);
                group = sort == ImageSortField.DateTaken
                    ? group.Where(f => f.EffectiveDateTaken >= start && f.EffectiveDateTaken < end)
                    : group.Where(f => f.CreatedAt >= start && f.CreatedAt < end);
            }
            var first = await ApplyOrdering(group, sort, direction).Select(f => new GalleryRow
            {
                Id = f.Id, Name = f.Name, CreatedAt = f.CreatedAt, EffectiveDateTaken = f.EffectiveDateTaken,
                Title = _db.FileItemUserMetadata.Where(u => u.FileItemId == f.Id).Select(u => u.Title).FirstOrDefault(),
            }).FirstOrDefaultAsync(cancellationToken);
            if (first is null) return null;
            cursor = BuildCursor(sort, direction, first, fingerprint);
        }

        var fetchDirection = before ? Reverse(direction) : direction;
        var (rows, _, _, _) = await ListMediaRowsAsync(ownerUserId, limit,
            cursor! with { Direction = fetchDirection }, filters, sort, fetchDirection, kind,
            cancellationToken, fingerprint, computeTotalCount: false, includeBoundary: inclusive);
        var orderedRows = before ? rows.Reverse().ToList() : rows.ToList();
        if (orderedRows.Count == 0) return new([], null, null);
        var firstCursor = BuildCursor(sort, direction, orderedRows[0], fingerprint);
        var lastCursor = BuildCursor(sort, direction, orderedRows[^1], fingerprint);
        var hasPrevious = await ApplyCursorSeek(query, firstCursor with { Direction = Reverse(direction) }).AnyAsync(cancellationToken);
        var hasNext = await ApplyCursorSeek(query, lastCursor).AnyAsync(cancellationToken);
        return new(await ProjectMediaItemsAsync(orderedRows, cancellationToken),
            hasNext ? lastCursor.Encode() : null, hasPrevious ? firstCursor.Encode() : null);
    }

    private static ImageSortDirection Reverse(ImageSortDirection direction)
        => direction == ImageSortDirection.Asc ? ImageSortDirection.Desc : ImageSortDirection.Asc;
}
