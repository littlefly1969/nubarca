namespace NubArca.Api.Domain.Print;

public sealed class PrintStationEnrollment
{
    public Guid Id { get; set; }
    public Guid PrintStationId { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? ConsumedAt { get; set; }
}
