namespace NubArca.Api.Files;

// Slice 82: a mutable, accumulating per-phase timing sink for FileItemService
// .CreateAsync. The admin import passes one instance and reuses it across all
// files in a run, then persists the totals on the run row. When CreateAsync is
// called without it (every normal upload), no timing work is recorded.
public sealed class FileCreateTimings
{
    public long ReadMillis;
    public long HashMillis;
    public long WriteMillis;
    // Blob row lookup + insert/refcount (the dedup decision).
    public long BlobDbMillis;
    // Slice 95: minimal media detection (ImageSharp header identify + video
    // signature sniff) — split from MetadataMillis so the latter measures
    // FULL embedded extraction only (0 by construction on the deferred path).
    public long DetectMillis;
    public long MetadataMillis;
    // FileItem validation + insert (and, slice 95, the new BlobMetadata row
    // committed in the same transaction).
    public long FileItemMillis;
    public long ThumbnailMillis;
}
