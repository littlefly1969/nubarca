namespace NubArca.Api.Folders;

// Thrown by IFileItemService.PermanentDeleteAsync and
// IFolderService.PermanentDeleteAsync when the target is owned by the caller
// but is currently active (DeletedAt IS NULL). Permanent delete is only valid
// against rows already in the user's trash. Mapped to 409 at the HTTP layer.
public sealed class ResourceNotInTrashException : Exception
{
    public Guid ResourceId { get; }

    public ResourceNotInTrashException(Guid resourceId)
        : base($"Resource '{resourceId}' is not in the trash; soft-delete it first.")
    {
        ResourceId = resourceId;
    }
}
