using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Folders;

namespace NubArca.Api.MediaLibrary;

public interface IMediaLibraryService
{
    // ---- eligibility: the single source of truth -------------------------
    // Narrows a FileItem query to media-library-eligible rows for the given
    // kind. Composes into EF queries (one NOT EXISTS on the parent folder's
    // denormalized flag — no tree walk, no N+1). Root-level files are always
    // eligible. Used by the photo/video galleries, batch media jobs, and any
    // future map/organizer surface; NEVER by the file browser, downloads,
    // quota, import, or cleanup.
    IQueryable<FileItem> ApplyMediaLibraryVisibility(IQueryable<FileItem> query, MediaKind kind);

    // True when the (owner-scoped, active) file's folder chain does not
    // exclude it for the given kind. Missing/foreign/deleted → false.
    Task<bool> IsEligibleAsync(
        Guid ownerUserId, Guid fileItemId, MediaKind kind, CancellationToken cancellationToken = default);

    // ---- rules ------------------------------------------------------------
    Task<MediaLibraryRulesResponse> ListRulesAsync(Guid ownerUserId, CancellationToken cancellationToken = default);
    // Upserts THE rule of a folder (one per folder). Null = folder missing/foreign.
    Task<MediaLibraryRuleDto?> SetRuleAsync(
        Guid ownerUserId, MediaLibraryRuleRequest request, CancellationToken cancellationToken = default);
    // Null = rule missing/foreign.
    Task<bool?> DeleteRuleAsync(Guid ownerUserId, Guid ruleId, CancellationToken cancellationToken = default);
    Task<MediaLibraryEffectiveResponse?> GetEffectiveAsync(
        Guid ownerUserId, Guid folderId, CancellationToken cancellationToken = default);

    // ---- maintenance / diagnostics ----------------------------------------
    // Recomputes the denormalized Folder.Media*Excluded flags for the whole
    // owner from the rules table. Called on every rule change and on folder
    // move/restore (folder CREATION inherits from its parent instead — O(1)).
    Task RecomputeOwnerAsync(Guid ownerUserId, CancellationToken cancellationToken = default);
    Task<MediaLibraryStatsResponse> GetStatsAsync(Guid ownerUserId, CancellationToken cancellationToken = default);
}

// Slice 94: media-library membership. Rules live in media_library_rules; the
// EFFECTIVE per-folder state is denormalized onto Folder.Media*Excluded by
// RecomputeOwnerAsync (the EffectiveDateTaken pattern: the rules table stays
// authoritative, queries read only the projection). Membership semantics:
// include-by-default; the NEAREST applicable rule wins; a rule covers files
// directly in its folder always, and the subtree only when AppliesToChildren;
// kinds the rule does not flag keep inheriting.
public sealed class MediaLibraryService : IMediaLibraryService
{
    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;
    private readonly ILogger<MediaLibraryService>? _logger;

    public MediaLibraryService(
        AppDbContext db,
        TimeProvider clock,
        ILogger<MediaLibraryService>? logger = null)
    {
        _db = db;
        _clock = clock;
        _logger = logger;
    }

    // ---- eligibility ----------------------------------------------------------

    public IQueryable<FileItem> ApplyMediaLibraryVisibility(IQueryable<FileItem> query, MediaKind kind)
        => kind == MediaKind.Photo
            ? query.Where(f => !_db.Folders.Any(d => d.Id == f.ParentFolderId && d.MediaPhotosExcluded))
            : query.Where(f => !_db.Folders.Any(d => d.Id == f.ParentFolderId && d.MediaVideosExcluded));

    public async Task<bool> IsEligibleAsync(
        Guid ownerUserId, Guid fileItemId, MediaKind kind, CancellationToken cancellationToken = default)
    {
        var query = _db.FileItems.AsNoTracking()
            .Where(f => f.Id == fileItemId && f.OwnerUserId == ownerUserId && f.DeletedAt == null);
        return await ApplyMediaLibraryVisibility(query, kind).AnyAsync(cancellationToken);
    }

    // ---- rules ------------------------------------------------------------------

    public async Task<MediaLibraryRulesResponse> ListRulesAsync(
        Guid ownerUserId, CancellationToken cancellationToken = default)
    {
        var rules = await _db.MediaLibraryRules.AsNoTracking()
            .Where(r => r.OwnerUserId == ownerUserId)
            .Join(_db.Folders, r => r.FolderId, f => f.Id, (r, f) => new { Rule = r, FolderName = f.Name })
            .OrderBy(x => x.FolderName)
            .Select(x => new MediaLibraryRuleDto(
                x.Rule.Id, x.Rule.FolderId, x.FolderName, x.Rule.RuleType,
                x.Rule.AppliesToPhotos, x.Rule.AppliesToVideos, x.Rule.AppliesToChildren,
                x.Rule.CreatedAt, x.Rule.UpdatedAt))
            .ToListAsync(cancellationToken);
        return new MediaLibraryRulesResponse(rules);
    }

    public async Task<MediaLibraryRuleDto?> SetRuleAsync(
        Guid ownerUserId, MediaLibraryRuleRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!MediaLibraryRuleTypes.IsKnown(request.RuleType))
        {
            throw new MediaLibraryValidationException("Unknown rule type.");
        }
        if (!request.AppliesToPhotos && !request.AppliesToVideos)
        {
            throw new MediaLibraryValidationException("A rule must apply to photos, videos, or both.");
        }

        // Ownership gate: the folder must be the caller's own ACTIVE folder.
        // Missing / foreign / trashed all collapse to null (no-leak 404).
        var folder = await _db.Folders.AsNoTracking()
            .Where(f => f.Id == request.FolderId && f.OwnerUserId == ownerUserId && f.DeletedAt == null)
            .Select(f => new { f.Id, f.Name })
            .FirstOrDefaultAsync(cancellationToken);
        if (folder is null)
        {
            return null;
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        var rule = await _db.MediaLibraryRules
            .FirstOrDefaultAsync(
                r => r.OwnerUserId == ownerUserId && r.FolderId == request.FolderId,
                cancellationToken);
        if (rule is null)
        {
            rule = new MediaLibraryRule
            {
                Id = Guid.NewGuid(),
                OwnerUserId = ownerUserId,
                FolderId = request.FolderId,
                CreatedAt = now,
            };
            _db.MediaLibraryRules.Add(rule);
        }
        rule.RuleType = request.RuleType;
        rule.AppliesToPhotos = request.AppliesToPhotos;
        rule.AppliesToVideos = request.AppliesToVideos;
        rule.AppliesToChildren = request.AppliesToChildren;
        rule.UpdatedAt = now;
        await _db.SaveChangesAsync(cancellationToken);

        await RecomputeOwnerAsync(ownerUserId, cancellationToken);

        return new MediaLibraryRuleDto(
            rule.Id, rule.FolderId, folder.Name, rule.RuleType,
            rule.AppliesToPhotos, rule.AppliesToVideos, rule.AppliesToChildren,
            rule.CreatedAt, rule.UpdatedAt);
    }

    public async Task<bool?> DeleteRuleAsync(
        Guid ownerUserId, Guid ruleId, CancellationToken cancellationToken = default)
    {
        var deleted = await _db.MediaLibraryRules
            .Where(r => r.Id == ruleId && r.OwnerUserId == ownerUserId)
            .ExecuteDeleteAsync(cancellationToken);
        if (deleted == 0)
        {
            return null;
        }
        await RecomputeOwnerAsync(ownerUserId, cancellationToken);
        return true;
    }

    public async Task<MediaLibraryEffectiveResponse?> GetEffectiveAsync(
        Guid ownerUserId, Guid folderId, CancellationToken cancellationToken = default)
    {
        var folder = await _db.Folders.AsNoTracking()
            .Where(f => f.Id == folderId && f.OwnerUserId == ownerUserId && f.DeletedAt == null)
            .Select(f => new { f.Id, f.Name, f.ParentFolderId })
            .FirstOrDefaultAsync(cancellationToken);
        if (folder is null)
        {
            return null;
        }

        // Walk the ancestor chain (bounded by the tree's depth cap) collecting
        // each folder's rule; the nearest applicable rule per kind decides.
        var chain = new List<(Guid Id, string Name, MediaLibraryRule? Rule)>();
        Guid? currentId = folder.Id;
        var guard = 0;
        while (currentId is Guid id && guard++ < 128)
        {
            var node = await _db.Folders.AsNoTracking()
                .Where(f => f.Id == id && f.OwnerUserId == ownerUserId)
                .Select(f => new { f.Id, f.Name, f.ParentFolderId })
                .FirstOrDefaultAsync(cancellationToken);
            if (node is null) break;
            var rule = await _db.MediaLibraryRules.AsNoTracking()
                .FirstOrDefaultAsync(
                    r => r.OwnerUserId == ownerUserId && r.FolderId == id, cancellationToken);
            chain.Add((node.Id, node.Name, rule));
            currentId = node.ParentFolderId;
        }

        MediaLibraryEffectiveKind Resolve(bool photos)
        {
            for (var i = 0; i < chain.Count; i++)
            {
                var (id, name, rule) = chain[i];
                if (rule is null) continue;
                var appliesToKind = photos ? rule.AppliesToPhotos : rule.AppliesToVideos;
                if (!appliesToKind) continue;
                // Rules on the folder itself always apply to its files; rules
                // on an ancestor apply only when they cover children.
                if (i > 0 && !rule.AppliesToChildren) continue;
                var excluded = rule.RuleType == MediaLibraryRuleTypes.Exclude;
                return i == 0
                    ? new MediaLibraryEffectiveKind(excluded, MediaLibraryEffectiveSources.Rule, id, name)
                    : new MediaLibraryEffectiveKind(excluded, MediaLibraryEffectiveSources.Inherited, id, name);
            }
            return new MediaLibraryEffectiveKind(false, MediaLibraryEffectiveSources.Default, null, null);
        }

        var ownRule = chain.Count > 0 ? chain[0].Rule : null;
        return new MediaLibraryEffectiveResponse(
            folder.Id,
            Resolve(photos: true),
            Resolve(photos: false),
            ownRule is null
                ? null
                : new MediaLibraryRuleDto(
                    ownRule.Id, ownRule.FolderId, folder.Name, ownRule.RuleType,
                    ownRule.AppliesToPhotos, ownRule.AppliesToVideos, ownRule.AppliesToChildren,
                    ownRule.CreatedAt, ownRule.UpdatedAt));
    }

    // ---- recompute ------------------------------------------------------------

    public async Task RecomputeOwnerAsync(Guid ownerUserId, CancellationToken cancellationToken = default)
    {
        // Per-owner advisory lock (the tree-mutation lock) so a concurrent
        // folder move/create can't interleave with the flag walk.
        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);
            await TreeMutationLock.AcquireAsync(_db, ownerUserId, cancellationToken);

            // The whole owner's folder skeleton + rules fit comfortably in
            // memory (ids + flags only); files are never loaded.
            var folders = await _db.Folders
                .Where(f => f.OwnerUserId == ownerUserId)
                .ToListAsync(cancellationToken);
            var rules = await _db.MediaLibraryRules.AsNoTracking()
                .Where(r => r.OwnerUserId == ownerUserId)
                .ToDictionaryAsync(r => r.FolderId, cancellationToken);

            var childrenByParent = folders
                .Where(f => f.ParentFolderId != null)
                .GroupBy(f => f.ParentFolderId!.Value)
                .ToDictionary(g => g.Key, g => g.ToList());

            var visited = new HashSet<Guid>();
            var stack = new Stack<(Folder Folder, bool InheritP, bool InheritV)>();
            foreach (var root in folders.Where(f => f.ParentFolderId == null))
            {
                stack.Push((root, false, false));
            }

            var changed = 0;
            while (stack.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (folder, inheritP, inheritV) = stack.Pop();
                if (!visited.Add(folder.Id)) continue;

                rules.TryGetValue(folder.Id, out var rule);
                var excludeBit = rule?.RuleType == MediaLibraryRuleTypes.Exclude;
                var directP = rule is { AppliesToPhotos: true } ? excludeBit : inheritP;
                var directV = rule is { AppliesToVideos: true } ? excludeBit : inheritV;
                var childP = rule is { AppliesToPhotos: true, AppliesToChildren: true } ? excludeBit : inheritP;
                var childV = rule is { AppliesToVideos: true, AppliesToChildren: true } ? excludeBit : inheritV;

                if (folder.MediaPhotosExcluded != directP
                    || folder.MediaVideosExcluded != directV
                    || folder.MediaPhotosExcludedForChildren != childP
                    || folder.MediaVideosExcludedForChildren != childV)
                {
                    folder.MediaPhotosExcluded = directP;
                    folder.MediaVideosExcluded = directV;
                    folder.MediaPhotosExcludedForChildren = childP;
                    folder.MediaVideosExcludedForChildren = childV;
                    changed++;
                }

                if (childrenByParent.TryGetValue(folder.Id, out var children))
                {
                    foreach (var child in children)
                    {
                        stack.Push((child, childP, childV));
                    }
                }
            }

            // Defensive: a folder whose parent chain was unreachable (should
            // not happen in a consistent tree) keeps default-include flags.
            foreach (var orphan in folders.Where(f => !visited.Contains(f.Id)))
            {
                if (orphan.MediaPhotosExcluded || orphan.MediaVideosExcluded
                    || orphan.MediaPhotosExcludedForChildren || orphan.MediaVideosExcludedForChildren)
                {
                    orphan.MediaPhotosExcluded = false;
                    orphan.MediaVideosExcluded = false;
                    orphan.MediaPhotosExcludedForChildren = false;
                    orphan.MediaVideosExcludedForChildren = false;
                    changed++;
                }
            }

            await _db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            if (changed > 0)
            {
                _logger?.LogInformation(
                    "media-library: recomputed eligibility for owner {OwnerUserId} ({Changed} folder(s) changed).",
                    ownerUserId, changed);
            }
        });
    }

    // ---- diagnostics ------------------------------------------------------------

    public async Task<MediaLibraryStatsResponse> GetStatsAsync(
        Guid ownerUserId, CancellationToken cancellationToken = default)
    {
        // Media membership for stats mirrors the gallery's server-detected
        // category; counts only — no ids, paths, or coordinates.
        var activeFiles = _db.FileItems.AsNoTracking()
            .Where(f => f.OwnerUserId == ownerUserId && f.DeletedAt == null);

        IQueryable<FileItem> OfCategory(string category) => activeFiles
            .Where(f => _db.BlobMetadata.Any(m => m.BlobObjectId == f.BlobObjectId
                && m.MediaCategory == category
                && m.DetectedContentType != null));

        var photos = OfCategory(MediaCategories.Image);
        var videos = OfCategory(MediaCategories.Video);

        var photosEligible = await ApplyMediaLibraryVisibility(photos, MediaKind.Photo)
            .CountAsync(cancellationToken);
        var photosTotal = await photos.CountAsync(cancellationToken);
        var videosEligible = await ApplyMediaLibraryVisibility(videos, MediaKind.Video)
            .CountAsync(cancellationToken);
        var videosTotal = await videos.CountAsync(cancellationToken);
        var ruleCount = await _db.MediaLibraryRules
            .CountAsync(r => r.OwnerUserId == ownerUserId, cancellationToken);

        // Extraction coverage over the DISTINCT blobs the owner's active files
        // reference (blob metadata is global; the owner-scoped view is "my
        // files' blobs").
        var ownerBlobMetadata = _db.BlobMetadata.AsNoTracking()
            .Where(m => activeFiles.Any(f => f.BlobObjectId == m.BlobObjectId));
        var blobsTotal = await ownerBlobMetadata.CountAsync(cancellationToken);
        var blobsExtracted = await ownerBlobMetadata.CountAsync(
            m => m.ExtractionStatus == MetadataStatuses.Completed, cancellationToken);
        var blobsPending = await ownerBlobMetadata.CountAsync(
            m => m.ExtractionStatus == MetadataStatuses.Pending, cancellationToken);
        var blobsFailed = await ownerBlobMetadata.CountAsync(
            m => m.ExtractionStatus == MetadataStatuses.Failed, cancellationToken);
        var blobsWithDateTaken = await ownerBlobMetadata.CountAsync(
            m => m.DateTaken != null, cancellationToken);
        var blobsWithGps = await ownerBlobMetadata.CountAsync(
            m => m.GpsLatitude != null && m.GpsLongitude != null, cancellationToken);

        return new MediaLibraryStatsResponse(
            photosEligible, photosTotal - photosEligible,
            videosEligible, videosTotal - videosEligible,
            ruleCount,
            blobsTotal, blobsExtracted, blobsPending, blobsFailed,
            blobsWithDateTaken, blobsWithGps);
    }
}
