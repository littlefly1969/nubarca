namespace NubArca.Api.Folders;

public sealed class FolderNotEmptyException : Exception
{
    public Guid FolderId { get; }

    public FolderNotEmptyException(Guid folderId)
        : base($"Folder '{folderId}' is not empty and cannot be deleted.")
    {
        FolderId = folderId;
    }
}
