namespace NubArca.Api.Folders;

public sealed class DuplicateFolderNameException : Exception
{
    public Guid OwnerUserId { get; }
    public Guid? ParentFolderId { get; }
    public string Name { get; }

    public DuplicateFolderNameException(Guid ownerUserId, Guid? parentFolderId, string name)
        : base(FormatMessage(ownerUserId, parentFolderId, name))
    {
        OwnerUserId = ownerUserId;
        ParentFolderId = parentFolderId;
        Name = name;
    }

    private static string FormatMessage(Guid ownerUserId, Guid? parentFolderId, string name)
    {
        var parent = parentFolderId?.ToString() ?? "<root>";
        return $"A folder named '{name}' already exists under parent '{parent}' for owner '{ownerUserId}'.";
    }
}
