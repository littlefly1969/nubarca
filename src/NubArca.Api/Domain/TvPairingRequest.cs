namespace NubArca.Api.Domain;

public class TvPairingRequest
{
    public Guid Id { get; set; }
    public string PublicCode { get; set; } = string.Empty;
    public string SecretHash { get; set; } = string.Empty;
    public string Status { get; set; } = TvPairingStatuses.Pending;
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public Guid? ApprovedByUserId { get; set; }
    public Guid? TvSessionId { get; set; }
}

public static class TvPairingStatuses
{
    public const string Pending = "pending";
    public const string Approved = "approved";
    // Transaction-internal claim state; never committed as a client-visible state.
    public const string Claiming = "claiming";
    public const string Paired = "paired";
    public const string Expired = "expired";
}
