namespace NubArca.Api.Domain;

public class AuditLog
{
    public Guid Id { get; set; }
    public Guid? UserId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public Guid? EntityId { get; set; }
    public string? IpAddress { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? MetadataJson { get; set; }
}
