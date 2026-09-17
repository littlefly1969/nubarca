namespace NubArca.Api.Domain;

public class AuditLog
{
    public Guid Id { get; set; }
    public Guid? UserId { get; set; }

    /// <summary>
    /// The Party Crew collaborator who acted, when the actor was not a user.
    /// Mutually exclusive with <see cref="UserId"/>; see <c>AuditActor</c>.
    /// </summary>
    public Guid? PartyCollaboratorId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public Guid? EntityId { get; set; }
    public string? IpAddress { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? MetadataJson { get; set; }
}
