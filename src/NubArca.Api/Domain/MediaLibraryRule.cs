namespace NubArca.Api.Domain;

// Slice 94: a user-configured media-library rule on a folder. Rules decide
// MEDIA LIBRARY MEMBERSHIP only (gallery / map / batch media jobs / future
// organizer) — never file-browser visibility, downloads, sharing, quota, or
// cleanup. The default with no rules is: every supported photo/video is in
// the media library (rules are opt-out).
//
// One rule per (owner, folder): RuleType applies to the kinds flagged by
// AppliesToPhotos/AppliesToVideos; an unflagged kind keeps inheriting from
// the nearest ancestor rule (or the include-by-default). AppliesToChildren
// controls whether the rule propagates to the folder's subtree or only to
// files directly inside the folder. The nearest applicable rule wins, so a
// child folder can be re-included under an excluded parent.
public class MediaLibraryRule
{
    public Guid Id { get; set; }

    public Guid OwnerUserId { get; set; }

    public Guid FolderId { get; set; }

    // "include" | "exclude" (see MediaLibraryRuleTypes).
    public string RuleType { get; set; } = MediaLibraryRuleTypes.Exclude;

    public bool AppliesToPhotos { get; set; } = true;
    public bool AppliesToVideos { get; set; } = true;

    // true: the rule covers the folder's whole subtree (until a more specific
    // rule overrides it); false: only files directly inside the folder.
    public bool AppliesToChildren { get; set; } = true;

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public static class MediaLibraryRuleTypes
{
    public const string Include = "include";
    public const string Exclude = "exclude";

    public static bool IsKnown(string? type) => type is Include or Exclude;
}

// Slice 94 (map preparation): an owner/file-scoped projection of a media
// file's GPS position. GPS originates from blob-level metadata, but map
// visibility depends on FILE ownership + media-library eligibility, so the
// projection hangs off the FileItem. Maintained by the metadata pipeline
// (extraction / create / blob repoint) and purged with the FileItem.
// Owner-private: rows are only ever queried owner-scoped, and coordinates
// never leave owner-scoped views (no share/public/aggregate exposure).
public class FileItemLocation
{
    // One row per FileItem → the file id IS the key.
    public Guid FileItemId { get; set; }

    public Guid OwnerUserId { get; set; }

    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public double? Altitude { get; set; }

    // The file's effective capture instant at projection time (for map
    // time filtering); refreshed together with the coordinates.
    public DateTime? TakenAt { get; set; }

    // The BlobMetadata row the coordinates came from (internal bookkeeping;
    // never serialized).
    public Guid SourceBlobMetadataId { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
