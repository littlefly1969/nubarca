namespace NubArca.Api.Domain;

public sealed class SubjectCutoutQueueItem
{
    public Guid OwnerUserId { get; set; }
    public Guid FileItemId { get; set; }
    public DateTime AddedAt { get; set; }
}
