namespace NubArca.Api.Folders;

// HTTP/listing projection of a Folder. Deliberately omits OwnerUserId,
// ParentFolderId, UpdatedAt, DeletedAt — those are storage-internal concerns
// that must never reach a response.
public sealed record FolderSummary(Guid Id, string Name, DateTime CreatedAt);
