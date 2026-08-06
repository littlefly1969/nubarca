namespace NubArca.Api.Domain;

// Per-FileItem, owner-scoped user annotations and overrides (title, tags,
// rating, favorite, manual date/location overrides). These hang off the
// FileItem — which belongs to exactly one user — so they NEVER propagate to
// another user's FileItem, even when the underlying blob is deduplicated and
// shared. At most one row per FileItem (unique FK); absence means "all
// defaults", so files created before this model existed need no backfill.
public class FileItemUserMetadata
{
    public Guid Id { get; set; }

    public Guid FileItemId { get; set; }

    public string? Title { get; set; }

    public string? Description { get; set; }

    // Curated list of normalized tag strings, stored as a JSON array. Null /
    // empty means no tags. This is a curated field, not a raw metadata bag.
    public string? TagsJson { get; set; }

    // Optional 0..5 star rating (CHECK-constrained). Null = unrated.
    public int? Rating { get; set; }

    public bool IsFavorite { get; set; }

    // Placeholder for a manual "date taken" override that wins over any
    // extracted EXIF date once Slice 54 lands.
    public DateTime? DateTakenOverride { get; set; }

    // Placeholder for a manual location override (free text for now; a richer
    // GPS schema can replace this without touching the ownership model).
    public string? LocationOverride { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }
}
