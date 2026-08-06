namespace NubArca.Api.Storage;

public class BlobStorageOptions
{
    public const string SectionName = "Storage";

    public string RootPath { get; set; } = string.Empty;

    // Slice 72: optional separate physical root for DERIVED media artifacts
    // (image thumbnails, medium previews, video posters). Originals always use
    // RootPath. When this is unset/blank, derived artifacts use RootPath too —
    // preserving the pre-slice-72 single-root behaviour exactly. Setting it
    // lets operators keep large originals on a slow disk and the regenerable
    // derived cache on a faster disk. Derived artifacts are NOT source data:
    // if this root is wiped, endpoints / the media-derivatives backfill
    // regenerate them.
    public string? DerivedRootPath { get; set; }

    // The physical root derived artifacts actually use: DerivedRootPath when
    // configured, otherwise RootPath (single-root default).
    public string EffectiveDerivedRootPath =>
        string.IsNullOrWhiteSpace(DerivedRootPath) ? RootPath : DerivedRootPath;

    // Slice 65: app-level per-file upload ceiling, enforced while streaming the
    // blob to disk (never buffers the whole file in memory). 0 or negative
    // means "no app-level limit" — the reverse proxy / Kestrel body-size limit
    // then governs, which is a SEPARATE concern documented in the README.
    // Default 2 GiB: large enough for video clips, small enough to refuse a
    // runaway / accidental multi-gigabyte upload before it fills the disk.
    public long MaxUploadBytes { get; set; } = 2L * 1024 * 1024 * 1024;

    // Slice 65: default per-user logical-storage quota in bytes. Counts the
    // sum of the user's owned FileItem.SizeBytes (logical bytes, INCLUDING
    // trashed-but-not-purged files), NOT the deduplicated physical footprint —
    // a user who uploads a duplicate of someone else's file still owns a
    // logical file and pays for it. 0 or negative means "unlimited", which
    // preserves the pre-slice-65 behaviour exactly.
    public long DefaultUserQuotaBytes { get; set; }

    public bool HasUploadLimit => MaxUploadBytes > 0;

    public bool HasUserQuota => DefaultUserQuotaBytes > 0;
}
