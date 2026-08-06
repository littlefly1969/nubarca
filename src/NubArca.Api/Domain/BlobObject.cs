namespace NubArca.Api.Domain;

public class BlobObject
{
    public Guid Id { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string StorageKey { get; set; } = string.Empty;
    public long ReferenceCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? PurgeEligibleAt { get; set; }
}
