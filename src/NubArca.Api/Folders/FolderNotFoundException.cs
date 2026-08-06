namespace NubArca.Api.Folders;

public sealed class FolderNotFoundException : Exception
{
    public Guid FolderId { get; }

    public FolderNotFoundException(Guid folderId)
        : base($"Folder '{folderId}' was not found.")
    {
        FolderId = folderId;
    }
}
