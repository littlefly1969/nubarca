using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.MediaLibrary;
using Npgsql;

namespace NubArca.Api.Folders;

public sealed class FolderService : IFolderService
{
    private const int MaxNameLength = 255;
    private const string SiblingNameUniqueIndex = "ux_folders_active_sibling_name";

    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;
    // Slice 77: needed by SoftDeleteRecursiveAsync to reuse per-file soft-
    // delete semantics (blob refcount, album memberships, share-link, metadata,
    // audit). Optional so the many direct-construction test sites keep
    // compiling; null means recursive delete is not available at that call-site.
    private readonly IFileItemService? _fileItems;
    // Slice 94: the denormalized Folder.Media*Excluded flags must follow tree
    // shape changes — moves and restores trigger a recompute (creation inherits
    // from the parent inline). Optional for the same test-construction reason.
    private readonly IMediaLibraryService? _mediaLibrary;

    public FolderService(
        AppDbContext db,
        TimeProvider clock,
        IFileItemService? fileItems = null,
        IMediaLibraryService? mediaLibrary = null)
    {
        _db = db;
        _clock = clock;
        _fileItems = fileItems;
        _mediaLibrary = mediaLibrary;
    }

    public async Task<Folder> CreateAsync(
        Guid ownerUserId,
        Guid? parentFolderId,
        string name,
        CancellationToken cancellationToken = default)
    {
        var validatedName = ValidateAndTrimName(name);

        var folder = new Folder
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerUserId,
            ParentFolderId = parentFolderId,
            Name = validatedName,
            CreatedAt = _clock.GetUtcNow().UtcDateTime,
            UpdatedAt = null,
            DeletedAt = null,
        };

        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);
            await TreeMutationLock.AcquireAsync(_db, ownerUserId, cancellationToken);

            // Parent + sibling checks INSIDE the lock so a concurrent
            // SoftDeleteAsync on the parent (or DeleteAsync on a competing
            // sibling) cannot slip between the read and the write.
            if (parentFolderId is Guid parentId)
            {
                var parent = await _db.Folders
                    .AsNoTracking()
                    .Where(f => f.Id == parentId
                        && f.OwnerUserId == ownerUserId
                        && f.DeletedAt == null)
                    .Select(f => new
                    {
                        f.MediaPhotosExcludedForChildren,
                        f.MediaVideosExcludedForChildren,
                    })
                    .FirstOrDefaultAsync(cancellationToken);
                if (parent is null)
                {
                    throw new FolderNotFoundException(parentId);
                }

                // Slice 94: a NEW folder has no rules, so its effective
                // media-library state is exactly what its parent propagates —
                // O(1) inheritance instead of an owner-wide recompute (admin
                // imports create many folders).
                folder.MediaPhotosExcluded = parent.MediaPhotosExcludedForChildren;
                folder.MediaVideosExcluded = parent.MediaVideosExcludedForChildren;
                folder.MediaPhotosExcludedForChildren = parent.MediaPhotosExcludedForChildren;
                folder.MediaVideosExcludedForChildren = parent.MediaVideosExcludedForChildren;
            }

            var siblingExists = await _db.Folders
                .AsNoTracking()
                .AnyAsync(
                    f => f.OwnerUserId == ownerUserId
                        && f.ParentFolderId == parentFolderId
                        && f.DeletedAt == null
                        && f.Name == validatedName,
                    cancellationToken);
            if (siblingExists)
            {
                throw new DuplicateFolderNameException(ownerUserId, parentFolderId, validatedName);
            }

            _db.Folders.Add(folder);
            try
            {
                await _db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex) when (IsSiblingNameUniqueViolation(ex))
            {
                _db.Entry(folder).State = EntityState.Detached;
                throw new DuplicateFolderNameException(ownerUserId, parentFolderId, validatedName);
            }

            await tx.CommitAsync(cancellationToken);
            return folder;
        });
    }

    public async Task<Guid?> EnsureFolderPathAsync(
        Guid ownerUserId,
        Guid? rootParentId,
        IReadOnlyList<string> segments,
        CancellationToken cancellationToken = default)
    {
        var (leaf, _) = await EnsureFolderPathWithCountAsync(
            ownerUserId, rootParentId, segments, cancellationToken);
        return leaf;
    }

    public async Task<(Guid? LeafFolderId, int FoldersCreated)> EnsureFolderPathWithCountAsync(
        Guid ownerUserId,
        Guid? rootParentId,
        IReadOnlyList<string> segments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var foldersCreated = 0;

        // Validate the root parent up-front (matches CreateAsync's contract).
        if (rootParentId is Guid rid)
        {
            var rootValid = await _db.Folders
                .AsNoTracking()
                .AnyAsync(
                    f => f.Id == rid && f.OwnerUserId == ownerUserId && f.DeletedAt == null,
                    cancellationToken);
            if (!rootValid)
            {
                throw new FolderNotFoundException(rid);
            }
        }

        var currentParent = rootParentId;
        foreach (var rawName in segments)
        {
            var name = ValidateAndTrimName(rawName);

            // Find an existing active folder with this name under the current
            // parent (owner-scoped). Descend into it if present.
            var existingId = await _db.Folders
                .AsNoTracking()
                .Where(f => f.OwnerUserId == ownerUserId
                    && f.ParentFolderId == currentParent
                    && f.DeletedAt == null
                    && f.Name == name)
                .Select(f => (Guid?)f.Id)
                .FirstOrDefaultAsync(cancellationToken);

            if (existingId is Guid found)
            {
                currentParent = found;
                continue;
            }

            // Not found — create it. CreateAsync acquires the per-owner tree
            // lock and re-checks siblings, so a concurrent ensure for the same
            // path races safely: the loser catches DuplicateFolderNameException
            // and re-finds the winner's folder.
            try
            {
                var created = await CreateAsync(ownerUserId, currentParent, name, cancellationToken);
                currentParent = created.Id;
                foldersCreated++;
            }
            catch (DuplicateFolderNameException)
            {
                var raced = await _db.Folders
                    .AsNoTracking()
                    .Where(f => f.OwnerUserId == ownerUserId
                        && f.ParentFolderId == currentParent
                        && f.DeletedAt == null
                        && f.Name == name)
                    .Select(f => (Guid?)f.Id)
                    .FirstOrDefaultAsync(cancellationToken);
                if (raced is null)
                {
                    // Conflict wasn't a folder we can descend into (e.g. the
                    // sibling-name index also covers a different state) —
                    // surface the original conflict rather than guessing.
                    throw;
                }
                currentParent = raced;
            }
        }

        return (currentParent, foldersCreated);
    }

    public Task<Folder?> GetByIdAsync(
        Guid folderId,
        Guid ownerUserId,
        CancellationToken cancellationToken = default)
    {
        return _db.Folders
            .AsNoTracking()
            .FirstOrDefaultAsync(
                f => f.Id == folderId
                    && f.OwnerUserId == ownerUserId
                    && f.DeletedAt == null,
                cancellationToken);
    }

    public async Task<IReadOnlyList<FolderSummary>> ListChildrenAsync(
        Guid ownerUserId,
        Guid? parentFolderId,
        CancellationToken cancellationToken = default)
    {
        return await _db.Folders
            .AsNoTracking()
            .Where(f => f.OwnerUserId == ownerUserId
                && f.ParentFolderId == parentFolderId
                && f.DeletedAt == null)
            .OrderBy(f => f.Name)
            .Select(f => new FolderSummary(f.Id, f.Name, f.CreatedAt))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<FolderSummary>> ListChildFoldersAsync(
        Guid ownerUserId,
        Guid? parentFolderId,
        DirectorySortField sort,
        DirectorySortDirection direction,
        CancellationToken cancellationToken = default)
    {
        var query = _db.Folders
            .AsNoTracking()
            .Where(f => f.OwnerUserId == ownerUserId
                && f.ParentFolderId == parentFolderId
                && f.DeletedAt == null);

        var asc = direction == DirectorySortDirection.Asc;
        // Folders have no size/type column, so those sorts fall back to name
        // ordering (still honouring direction) — they sit above the files which
        // carry the real size/type ordering.
        query = (sort, asc) switch
        {
            (DirectorySortField.Created, true) => query.OrderBy(f => f.CreatedAt).ThenBy(f => f.Id),
            (DirectorySortField.Created, false) => query.OrderByDescending(f => f.CreatedAt).ThenByDescending(f => f.Id),
            (_, false) => query.OrderByDescending(f => f.Name).ThenByDescending(f => f.Id),
            _ => query.OrderBy(f => f.Name).ThenBy(f => f.Id),
        };

        return await query
            .Select(f => new FolderSummary(f.Id, f.Name, f.CreatedAt))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<FolderTrashSummary>> ListTrashAsync(
        Guid ownerUserId,
        Guid? parentFolderId = null,
        CancellationToken cancellationToken = default)
    {
        var query = _db.Folders
            .AsNoTracking()
            .Where(f => f.OwnerUserId == ownerUserId && f.DeletedAt != null);

        if (parentFolderId is Guid parentId)
        {
            query = query.Where(f => f.ParentFolderId == parentId);
        }

        return await query
            .OrderByDescending(f => f.DeletedAt)
            .ThenBy(f => f.Name)
            .Select(f => new FolderTrashSummary(
                f.Id, f.Name, f.ParentFolderId, f.CreatedAt, f.UpdatedAt, f.DeletedAt!.Value))
            .ToListAsync(cancellationToken);
    }

    public async Task<Folder?> RenameAsync(
        Guid ownerUserId,
        Guid folderId,
        string newName,
        CancellationToken cancellationToken = default)
    {
        var validatedName = ValidateAndTrimName(newName);

        var folder = await _db.Folders.FirstOrDefaultAsync(
            f => f.Id == folderId && f.OwnerUserId == ownerUserId && f.DeletedAt == null,
            cancellationToken);
        if (folder is null)
        {
            return null;
        }

        if (folder.Name == validatedName)
        {
            return folder; // no-op
        }

        var siblingExists = await _db.Folders
            .AsNoTracking()
            .AnyAsync(
                f => f.OwnerUserId == ownerUserId
                    && f.ParentFolderId == folder.ParentFolderId
                    && f.DeletedAt == null
                    && f.Id != folderId
                    && f.Name == validatedName,
                cancellationToken);
        if (siblingExists)
        {
            throw new DuplicateFolderNameException(ownerUserId, folder.ParentFolderId, validatedName);
        }

        folder.Name = validatedName;
        folder.UpdatedAt = _clock.GetUtcNow().UtcDateTime;

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return folder;
        }
        catch (DbUpdateException ex) when (IsSiblingNameUniqueViolation(ex))
        {
            _db.Entry(folder).State = EntityState.Detached;
            throw new DuplicateFolderNameException(ownerUserId, folder.ParentFolderId, validatedName);
        }
    }

    public async Task<Folder?> MoveAsync(
        Guid ownerUserId,
        Guid folderId,
        Guid? newParentFolderId,
        CancellationToken cancellationToken = default)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        var moved = await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);
            await TreeMutationLock.AcquireAsync(_db, ownerUserId, cancellationToken);

            // All reads happen AFTER the lock, so the cycle-check ancestor
            // walk sees a stable view: a concurrent reciprocal move (A → B
            // while we move B → A) is fully serialised and the second mover
            // sees the first mover's updated parent pointer.
            var folder = await _db.Folders.FirstOrDefaultAsync(
                f => f.Id == folderId && f.OwnerUserId == ownerUserId && f.DeletedAt == null,
                cancellationToken);
            if (folder is null)
            {
                return null;
            }

            if (newParentFolderId is Guid newParentId)
            {
                var parentValid = await _db.Folders
                    .AsNoTracking()
                    .AnyAsync(
                        f => f.Id == newParentId
                            && f.OwnerUserId == ownerUserId
                            && f.DeletedAt == null,
                        cancellationToken);
                if (!parentValid)
                {
                    throw new FolderNotFoundException(newParentId);
                }

                if (await IsInAncestorChainAsync(folderId, newParentId, ownerUserId, cancellationToken))
                {
                    throw new ArgumentException(
                        "Cannot move folder into itself or one of its descendants.",
                        nameof(newParentFolderId));
                }
            }

            if (folder.ParentFolderId == newParentFolderId)
            {
                await tx.CommitAsync(cancellationToken);
                return folder; // no-op
            }

            var siblingExists = await _db.Folders
                .AsNoTracking()
                .AnyAsync(
                    f => f.OwnerUserId == ownerUserId
                        && f.ParentFolderId == newParentFolderId
                        && f.DeletedAt == null
                        && f.Id != folderId
                        && f.Name == folder.Name,
                    cancellationToken);
            if (siblingExists)
            {
                throw new DuplicateFolderNameException(ownerUserId, newParentFolderId, folder.Name);
            }

            folder.ParentFolderId = newParentFolderId;
            folder.UpdatedAt = _clock.GetUtcNow().UtcDateTime;

            try
            {
                await _db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex) when (IsSiblingNameUniqueViolation(ex))
            {
                _db.Entry(folder).State = EntityState.Detached;
                throw new DuplicateFolderNameException(ownerUserId, newParentFolderId, folder.Name);
            }

            await tx.CommitAsync(cancellationToken);
            return folder;
        });

        // Slice 94: the moved subtree's inherited media-library state may have
        // changed — recompute the owner's denormalized flags (rules stay
        // authoritative; runs outside the move transaction under the same
        // per-owner lock).
        if (moved is not null && _mediaLibrary is not null)
        {
            await _mediaLibrary.RecomputeOwnerAsync(ownerUserId, cancellationToken);
        }
        return moved;
    }

    public async Task<bool> SoftDeleteAsync(
        Guid ownerUserId,
        Guid folderId,
        CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);
            await TreeMutationLock.AcquireAsync(_db, ownerUserId, cancellationToken);

            // Existence + empty checks AFTER the lock so a concurrent
            // CreateAsync(parent=folderId) / MoveAsync(target=folderId) is
            // blocked behind the lock and either observes our committed
            // DeletedAt (and 404s) or runs after we release without changes.
            var exists = await _db.Folders
                .AsNoTracking()
                .AnyAsync(
                    f => f.Id == folderId
                        && f.OwnerUserId == ownerUserId
                        && f.DeletedAt == null,
                    cancellationToken);
            if (!exists)
            {
                await tx.CommitAsync(cancellationToken);
                return false;
            }

            var hasChildFolders = await _db.Folders.AnyAsync(
                f => f.ParentFolderId == folderId && f.DeletedAt == null,
                cancellationToken);
            var hasChildFiles = await _db.FileItems.AnyAsync(
                f => f.ParentFolderId == folderId && f.DeletedAt == null,
                cancellationToken);
            if (hasChildFolders || hasChildFiles)
            {
                throw new FolderNotEmptyException(folderId);
            }

            // Atomic gate: stamps DeletedAt only if still active and owned.
            var affected = await _db.Folders
                .Where(f => f.Id == folderId
                    && f.OwnerUserId == ownerUserId
                    && f.DeletedAt == null)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(f => f.DeletedAt, _ => (DateTime?)now),
                    cancellationToken);

            await tx.CommitAsync(cancellationToken);
            return affected > 0;
        });
    }

    public async Task<FolderDeletePreview?> GetDeletePreviewAsync(
        Guid ownerUserId,
        Guid folderId,
        CancellationToken cancellationToken = default)
    {
        // Verify ownership (same no-leak as other methods: missing/foreign = null).
        var exists = await _db.Folders.AsNoTracking()
            .AnyAsync(f => f.Id == folderId && f.OwnerUserId == ownerUserId && f.DeletedAt == null,
                cancellationToken);
        if (!exists) return null;

        // Collect all descendant folder ids (BFS).
        var folderIds = await CollectDescendantFolderIdsAsync(ownerUserId, folderId, cancellationToken);

        var fileCount = await _db.FileItems.AsNoTracking()
            .CountAsync(f => f.OwnerUserId == ownerUserId
                && folderIds.Contains(f.ParentFolderId)
                && f.DeletedAt == null, cancellationToken);

        // Exclude the root itself from the folder count (we're previewing its CHILDREN).
        var folderCount = folderIds.Count - 1;

        return new FolderDeletePreview(fileCount, folderCount);
    }

    public async Task<RecursiveDeleteResult?> SoftDeleteRecursiveAsync(
        Guid ownerUserId,
        Guid folderId,
        CancellationToken cancellationToken = default)
    {
        if (_fileItems is null)
        {
            throw new InvalidOperationException(
                "SoftDeleteRecursiveAsync requires IFileItemService to be injected into FolderService.");
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);
            await TreeMutationLock.AcquireAsync(_db, ownerUserId, cancellationToken);

            // Verify ownership after the lock (same guard as SoftDeleteAsync).
            var exists = await _db.Folders.AsNoTracking()
                .AnyAsync(f => f.Id == folderId && f.OwnerUserId == ownerUserId && f.DeletedAt == null,
                    cancellationToken);
            if (!exists)
            {
                await tx.CommitAsync(cancellationToken);
                return null;
            }

            // Collect ALL descendant folder ids (BFS, inside the lock).
            var allFolderIds = await CollectDescendantFolderIdsAsync(ownerUserId, folderId, cancellationToken);

            // Collect all active FileItem ids under any of these folders.
            var fileIds = await _db.FileItems.AsNoTracking()
                .Where(f => f.OwnerUserId == ownerUserId
                    && allFolderIds.Contains(f.ParentFolderId)
                    && f.DeletedAt == null)
                .Select(f => f.Id)
                .ToListAsync(cancellationToken);

            // Commit the transaction so the tree-mutation lock is released
            // before the per-file SoftDeleteAsync calls (each of which acquires
            // its own lock). This is the correct approach because:
            // - We already have a consistent snapshot of what needs deleting.
            // - SoftDeleteAsync is idempotent and owner-scoped — it will
            //   silently skip anything already deleted or foreign.
            // - Holding the outer tx open across many per-file calls would
            //   risk lock timeouts on large folders.
            await tx.CommitAsync(cancellationToken);

            // Soft-delete every file via IFileItemService.SoftDeleteAsync to
            // preserve blob refcounts, album memberships, share-links, metadata,
            // audit semantics, and derived-artifact cleanup.
            var deletedFileCount = 0;
            foreach (var fileId in fileIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Explicit user-intent bulk (folder) delete: each descendant may
                // record a deleted-content tombstone if it is the owner's final
                // active occurrence. Content with a copy OUTSIDE the deleted tree
                // still counts as active, so the per-file final-occurrence check
                // naturally suppresses the tombstone for it.
                var ok = await _fileItems.SoftDeleteAsync(
                    ownerUserId, fileId, cancellationToken, FileDeleteReason.UserBulkDelete);
                if (ok) deletedFileCount++;
            }

            // Batch-stamp DeletedAt on all folders (root + descendants) in one
            // ExecuteUpdateAsync call (no FK constraint blocks folder→folder
            // soft-delete since we're only updating DeletedAt, not deleting rows).
            var affected = await _db.Folders
                .Where(f => allFolderIds.Contains(f.Id)
                    && f.OwnerUserId == ownerUserId
                    && f.DeletedAt == null)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(f => f.DeletedAt, _ => (DateTime?)now),
                    cancellationToken);

            return new RecursiveDeleteResult(deletedFileCount, affected);
        });
    }

    // BFS over the owned active folder tree rooted at `rootId`.
    // Returns a set that includes `rootId` itself + all active descendants.
    // Owner-scoped: only folders owned by `ownerUserId` are traversed.
    private async Task<HashSet<Guid?>> CollectDescendantFolderIdsAsync(
        Guid ownerUserId, Guid rootId, CancellationToken cancellationToken)
    {
        var visited = new HashSet<Guid?> { rootId };
        var queue = new Queue<Guid>();
        queue.Enqueue(rootId);

        while (queue.Count > 0)
        {
            var parentId = queue.Dequeue();
            var childIds = await _db.Folders.AsNoTracking()
                .Where(f => f.ParentFolderId == parentId
                    && f.OwnerUserId == ownerUserId
                    && f.DeletedAt == null)
                .Select(f => f.Id)
                .ToListAsync(cancellationToken);

            foreach (var childId in childIds)
            {
                if (visited.Add(childId))
                {
                    queue.Enqueue(childId);
                }
            }
        }
        return visited;
    }

    public async Task<Folder?> RestoreAsync(
        Guid ownerUserId,
        Guid folderId,
        CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var strategy = _db.Database.CreateExecutionStrategy();
        var restored = await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);
            await TreeMutationLock.AcquireAsync(_db, ownerUserId, cancellationToken);

            // All reads INSIDE the lock so a concurrent SoftDeleteAsync on
            // the parent cannot slip between "parent active?" and "flip child
            // DeletedAt" — the second arriver sees the first's commit and 404s
            // or 409s as appropriate.
            var folder = await _db.Folders
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    f => f.Id == folderId && f.OwnerUserId == ownerUserId,
                    cancellationToken);
            if (folder is null)
            {
                await tx.CommitAsync(cancellationToken);
                return null;
            }

            if (folder.DeletedAt is null)
            {
                await tx.CommitAsync(cancellationToken);
                return folder;
            }

            if (folder.ParentFolderId is Guid parentId)
            {
                var parentActive = await _db.Folders
                    .AsNoTracking()
                    .AnyAsync(
                        f => f.Id == parentId
                            && f.OwnerUserId == ownerUserId
                            && f.DeletedAt == null,
                        cancellationToken);
                if (!parentActive)
                {
                    throw new RestoreParentDeletedException(parentId);
                }
            }

            var siblingExists = await _db.Folders
                .AsNoTracking()
                .AnyAsync(
                    f => f.OwnerUserId == ownerUserId
                        && f.ParentFolderId == folder.ParentFolderId
                        && f.DeletedAt == null
                        && f.Id != folderId
                        && f.Name == folder.Name,
                    cancellationToken);
            if (siblingExists)
            {
                throw new DuplicateFolderNameException(ownerUserId, folder.ParentFolderId, folder.Name);
            }

            int affected;
            try
            {
                affected = await _db.Folders
                    .Where(f => f.Id == folderId
                        && f.OwnerUserId == ownerUserId
                        && f.DeletedAt != null)
                    .ExecuteUpdateAsync(
                        setters => setters
                            .SetProperty(f => f.DeletedAt, _ => (DateTime?)null)
                            .SetProperty(f => f.UpdatedAt, _ => (DateTime?)now),
                        cancellationToken);
            }
            catch (DbUpdateException ex) when (IsSiblingNameUniqueViolation(ex))
            {
                throw new DuplicateFolderNameException(ownerUserId, folder.ParentFolderId, folder.Name);
            }

            if (affected == 0)
            {
                // Lost a race: another writer already restored / hard-deleted.
                await tx.CommitAsync(cancellationToken);
                return null;
            }

            await tx.CommitAsync(cancellationToken);

            return await _db.Folders
                .AsNoTracking()
                .FirstOrDefaultAsync(f => f.Id == folderId, cancellationToken);
        });

        // Slice 94: a restored subtree must reflect any rules that changed
        // while it was in Trash.
        if (restored is not null && _mediaLibrary is not null)
        {
            await _mediaLibrary.RecomputeOwnerAsync(ownerUserId, cancellationToken);
        }
        return restored;
    }

    public async Task<bool> PermanentDeleteAsync(
        Guid ownerUserId,
        Guid folderId,
        CancellationToken cancellationToken = default)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);
            await TreeMutationLock.AcquireAsync(_db, ownerUserId, cancellationToken);

            var current = await _db.Folders
                .AsNoTracking()
                .Where(f => f.Id == folderId && f.OwnerUserId == ownerUserId)
                .Select(f => new { f.Id, f.DeletedAt })
                .FirstOrDefaultAsync(cancellationToken);
            if (current is null)
            {
                await tx.CommitAsync(cancellationToken);
                return false;
            }

            if (current.DeletedAt is null)
            {
                throw new ResourceNotInTrashException(folderId);
            }

            var hasChildFolders = await _db.Folders
                .AsNoTracking()
                .AnyAsync(f => f.ParentFolderId == folderId, cancellationToken);
            var hasChildFiles = await _db.FileItems
                .AsNoTracking()
                .AnyAsync(f => f.ParentFolderId == folderId, cancellationToken);
            if (hasChildFolders || hasChildFiles)
            {
                throw new FolderNotEmptyException(folderId);
            }

            // Slice 94: media-library rules reference folders with FK Restrict —
            // remove the folder's rule (if any) in the same transaction. The
            // denormalized flags die with the folder row; no recompute needed
            // (children were required to be gone above).
            await _db.MediaLibraryRules
                .Where(r => r.OwnerUserId == ownerUserId && r.FolderId == folderId)
                .ExecuteDeleteAsync(cancellationToken);

            var rowsDeleted = await _db.Folders
                .Where(f => f.Id == folderId
                    && f.OwnerUserId == ownerUserId
                    && f.DeletedAt != null)
                .ExecuteDeleteAsync(cancellationToken);

            await tx.CommitAsync(cancellationToken);
            return rowsDeleted > 0;
        });
    }

    // Returns true if walking the parent chain from `start` ever reaches `target`
    // (or start == target). Used to refuse "move folder X into itself or one of
    // its descendants". Self-loop guard via HashSet defends against stale cycles
    // in the existing data — should never happen, but safer than spinning.
    private async Task<bool> IsInAncestorChainAsync(
        Guid target,
        Guid start,
        Guid ownerUserId,
        CancellationToken cancellationToken)
    {
        Guid? current = start;
        var seen = new HashSet<Guid>();
        while (current is Guid id)
        {
            if (id == target)
            {
                return true;
            }
            if (!seen.Add(id))
            {
                return false;
            }
            current = await _db.Folders
                .AsNoTracking()
                .Where(f => f.Id == id && f.OwnerUserId == ownerUserId)
                .Select(f => (Guid?)f.ParentFolderId)
                .FirstOrDefaultAsync(cancellationToken);
        }
        return false;
    }

    private static string ValidateAndTrimName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var trimmed = name.Trim();

        if (trimmed.Length > MaxNameLength)
        {
            throw new ArgumentException(
                $"Folder name must be {MaxNameLength} characters or fewer.",
                nameof(name));
        }

        if (trimmed.Contains('/'))
        {
            throw new ArgumentException("Folder name must not contain '/'.", nameof(name));
        }

        if (trimmed.Contains('\\'))
        {
            throw new ArgumentException("Folder name must not contain '\\'.", nameof(name));
        }

        if (trimmed is "." or "..")
        {
            throw new ArgumentException("Folder name must not be '.' or '..'.", nameof(name));
        }

        return trimmed;
    }

    private static bool IsSiblingNameUniqueViolation(DbUpdateException ex)
    {
        return ex.InnerException is PostgresException pg
            && pg.SqlState == PostgresErrorCodes.UniqueViolation
            && pg.ConstraintName == SiblingNameUniqueIndex;
    }
}
