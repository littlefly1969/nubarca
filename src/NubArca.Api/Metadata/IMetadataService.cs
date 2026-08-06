namespace NubArca.Api.Metadata;

public interface IMetadataService
{
    // Effective, owner-scoped display metadata for one file: shared blob-derived
    // facts + this FileItem's private user metadata. Returns null for missing /
    // foreign / soft-deleted files (caller maps to 404). Works for files that
    // predate the metadata model — missing blob/user metadata falls back to
    // safe defaults derived from the FileItem itself.
    Task<FileMetadataResponse?> GetFileMetadataAsync(
        Guid ownerUserId,
        Guid fileItemId,
        CancellationToken cancellationToken = default);

    // Creates or replaces the owner's user-metadata for one file, then returns
    // the recomputed effective metadata. Only the user-metadata is touched —
    // the blob and its blob-derived metadata are never modified. Null for
    // missing / foreign / soft-deleted files. Throws ArgumentException on
    // invalid input (caller maps to 400).
    Task<FileMetadataResponse?> UpdateUserMetadataAsync(
        Guid ownerUserId,
        Guid fileItemId,
        UpdateFileMetadataRequest request,
        CancellationToken cancellationToken = default);

    // Narrow owner-level favorite mutation: touches ONLY IsFavorite on the
    // file's user-metadata row (creating the row when absent), leaving every
    // other user field and the blob untouched. Idempotent. Returns the new
    // favorite state, or null for missing / foreign / soft-deleted files.
    // Used by surfaces (e.g. the TV personal gallery) that must not replace
    // the full user-metadata document just to toggle one flag.
    Task<bool?> SetFavoriteAsync(
        Guid ownerUserId,
        Guid fileItemId,
        bool favorite,
        CancellationToken cancellationToken = default);
}
