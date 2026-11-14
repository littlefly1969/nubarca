namespace NubArca.Api.Domain;

public sealed class LithophaneProject
{
    public Guid Id { get; set; }
    public Guid OwnerUserId { get; set; }
    public Guid RequestId { get; set; }
    public Guid? FileItemId { get; set; }
    // The photograph the request named. Unlike FileItemId it survives the
    // source's purge, so a retried request stays idempotent afterwards.
    public Guid SourceFileItemId { get; set; }
    public Guid JobId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string SettingsJson { get; set; } = "{}";
    public int EngineVersion { get; set; } = 1;
    public string? PixelMapStorageKey { get; set; }
    public string? ArtifactStorageKey { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class LithophaneStrip
{
    public Guid ProjectId { get; set; }
    public int FirstRow { get; set; }
    public int EndRow { get; set; }
    public string StorageKey { get; set; } = string.Empty;
}
