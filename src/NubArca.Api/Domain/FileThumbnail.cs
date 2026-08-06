namespace NubArca.Api.Domain;

public class FileThumbnail
{
    public Guid Id { get; set; }
    public Guid FileItemId { get; set; }
    public Guid BlobObjectId { get; set; }
    public string Size { get; set; } = string.Empty;
    public int Width { get; set; }
    public int Height { get; set; }
    public DateTime CreatedAt { get; set; }

    // Slice 95: provenance for POSTER rows only (null for image thumbnails
    // and for posters created before provenance was recorded — treated as
    // "unknown"). Lets the UI mark synthetic placeholders and lets the
    // operator regenerate only synthetic posters once a real provider
    // (e.g. FFmpeg) is enabled. See VideoPosterSources.
    public string? PosterSource { get; set; }
}
