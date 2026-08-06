using NubArca.Api.Domain;

namespace NubArca.Api.Folders;

public interface IFolderService
{
    Task<Folder> CreateAsync(
        Guid ownerUserId,
        Guid? parentFolderId,
        string name,
        CancellationToken cancellationToken = default);

    // Slice 76: idempotent find-or-create of a folder chain under
    // `rootParentId` (null = the owner's root). Walks `segments` in order,
    // reusing an existing active folder when present and creating it otherwise.
    // Owner-scoped throughout. Returns the leaf folder's id (or `rootParentId`
    // when `segments` is empty). Throws FolderNotFoundException when
    // `rootParentId` is not a valid active owned folder. Used by folder upload
    // to materialise the relative directory structure as logical folders.
    // Slice 77: safe counts for a confirmation UI. Returns the number of
    // active files and active (non-deleted) descendant folders under the given
    // folder (recursive). Returns null for missing/foreign/soft-deleted folders
    // (same no-leak as other methods). Never exposes SHA, BlobObjectId,
    // StorageKey, or internal storage details.
    Task<FolderDeletePreview?> GetDeletePreviewAsync(
        Guid ownerUserId,
        Guid folderId,
        CancellationToken cancellationToken = default);

    // Slice 77: recursively soft-deletes all descendant FileItems (reusing
    // IFileItemService.SoftDeleteAsync to preserve blob refcounts, album
    // memberships, share-link, metadata, and audit semantics) then stamps
    // DeletedAt on all descendant folders and the root folder itself.
    // Owner-scoped throughout. Returns null for missing/foreign/soft-deleted.
    // All folder soft-deletes are batched in one ExecuteUpdateAsync; file
    // deletes happen individually (to preserve per-file blob/audit semantics).
    Task<RecursiveDeleteResult?> SoftDeleteRecursiveAsync(
        Guid ownerUserId,
        Guid folderId,
        CancellationToken cancellationToken = default);

    Task<Guid?> EnsureFolderPathAsync(
        Guid ownerUserId,
        Guid? rootParentId,
        IReadOnlyList<string> segments,
        CancellationToken cancellationToken = default);

    // As EnsureFolderPathAsync, but also reports how many folders were newly
    // created (the rest were reused). Used by the photo organizer to count
    // folders created for its run summary.
    Task<(Guid? LeafFolderId, int FoldersCreated)> EnsureFolderPathWithCountAsync(
        Guid ownerUserId,
        Guid? rootParentId,
        IReadOnlyList<string> segments,
        CancellationToken cancellationToken = default);

    Task<Folder?> GetByIdAsync(
        Guid folderId,
        Guid ownerUserId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FolderSummary>> ListChildrenAsync(
        Guid ownerUserId,
        Guid? parentFolderId,
        CancellationToken cancellationToken = default);

    // Files UI v2: child folders ordered by the requested directory sort.
    // Folders have no size/type, so those sorts fall back to name ordering
    // (still honouring direction). The full set is returned — child-folder
    // counts are bounded in practice, only files are seek-paginated.
    Task<IReadOnlyList<FolderSummary>> ListChildFoldersAsync(
        Guid ownerUserId,
        Guid? parentFolderId,
        DirectorySortField sort,
        DirectorySortDirection direction,
        CancellationToken cancellationToken = default);

    // Returns soft-deleted Folder rows for this owner, optionally filtered to
    // a specific parent. Ordered by DeletedAt desc, then Name asc.
    Task<IReadOnlyList<FolderTrashSummary>> ListTrashAsync(
        Guid ownerUserId,
        Guid? parentFolderId = null,
        CancellationToken cancellationToken = default);

    Task<Folder?> RenameAsync(
        Guid ownerUserId,
        Guid folderId,
        string newName,
        CancellationToken cancellationToken = default);

    Task<Folder?> MoveAsync(
        Guid ownerUserId,
        Guid folderId,
        Guid? newParentFolderId,
        CancellationToken cancellationToken = default);

    // Returns true if the folder was soft-deleted, false if it does not exist
    // (or is owned by another user / already deleted). Throws
    // FolderNotEmptyException when the folder still has live children.
    Task<bool> SoftDeleteAsync(
        Guid ownerUserId,
        Guid folderId,
        CancellationToken cancellationToken = default);

    // Returns the restored Folder, or null if no such folder exists for this
    // owner (treat as 404). Restoring an already-active folder is idempotent
    // and returns the folder unchanged. Throws RestoreParentDeletedException
    // when the parent folder is itself soft-deleted, and
    // DuplicateFolderNameException when an active sibling already occupies
    // the folder's slot.
    Task<Folder?> RestoreAsync(
        Guid ownerUserId,
        Guid folderId,
        CancellationToken cancellationToken = default);

    // Permanently deletes a soft-deleted, empty Folder. Returns true on
    // success, false when the folder is missing or foreign (404). Throws
    // ResourceNotInTrashException when the folder is owned but currently
    // active (409); throws FolderNotEmptyException when any child file or
    // folder still exists, active or soft-deleted (409).
    Task<bool> PermanentDeleteAsync(
        Guid ownerUserId,
        Guid folderId,
        CancellationToken cancellationToken = default);
}
