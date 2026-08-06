namespace NubArca.Api.Files;

public sealed class DuplicateFileNameException : Exception
{
    public Guid OwnerUserId { get; }
    public Guid? ParentFolderId { get; }
    public string Name { get; }

    public DuplicateFileNameException(Guid ownerUserId, Guid? parentFolderId, string name)
        : base(FormatMessage(ownerUserId, parentFolderId, name))
    {
        OwnerUserId = ownerUserId;
        ParentFolderId = parentFolderId;
        Name = name;
    }

    private static string FormatMessage(Guid ownerUserId, Guid? parentFolderId, string name)
    {
        var parent = parentFolderId?.ToString() ?? "<root>";
        return $"A file named '{name}' already exists under parent '{parent}' for owner '{ownerUserId}'.";
    }
}
