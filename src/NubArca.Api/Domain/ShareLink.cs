namespace NubArca.Api.Domain;

public class ShareLink
{
    public Guid Id { get; set; }
    public Guid OwnerUserId { get; set; }
    public Guid FileItemId { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public int DownloadCount { get; set; }
    public int? MaxDownloads { get; set; }
    public DateTime? LastAccessedAt { get; set; }
}
