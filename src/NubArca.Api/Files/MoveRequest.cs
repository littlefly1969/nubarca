namespace NubArca.Api.Files;

// Body for PATCH .../move. A null ParentFolderId moves the resource to the root.
public sealed record MoveRequest(Guid? ParentFolderId);
