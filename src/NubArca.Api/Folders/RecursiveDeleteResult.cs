namespace NubArca.Api.Folders;

// Slice 77: safe summary returned after a recursive folder soft-delete.
// Counts only — never SHA, BlobObjectId, StorageKey, or internal paths.
public sealed record RecursiveDeleteResult(int DeletedFileCount, int DeletedFolderCount);

// Slice 77: safe pre-delete counts for the confirmation UI.
// Returned by GET /api/folders/{id}/delete-preview.
public sealed record FolderDeletePreview(int FileCount, int FolderCount);
