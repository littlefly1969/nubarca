namespace NubArca.Api.Admin;

// Slice 81: admin-only server-side directory import. OFF by default. When
// enabled, imports are restricted to the explicitly whitelisted Roots — there
// is no fallback to unrestricted filesystem access.
//
// Wired from configuration (double-underscore env keys):
//   AdminImport__Enabled=true
//   AdminImport__Roots__0=/mnt/raid1/nubarca-import
//   AdminImport__Roots__1=/some/other/import-root
public sealed class AdminImportOptions
{
    public const string SectionName = "AdminImport";

    // Master switch. When false (default) every import endpoint reports the
    // feature as unavailable.
    public bool Enabled { get; set; } = false;

    // Whitelisted server-side import roots. An import may only read files that
    // canonicalize to a path *inside* one of these roots. Empty while enabled
    // is a configuration error (endpoints reject with a clear message), never
    // a silent fallback to the whole filesystem.
    public List<string> Roots { get; set; } = new();

    // ── Throttling (applies ONLY to admin server-side import jobs, never to
    // normal user uploads). Defaults are documented in .env.example. A manual
    // run-once that saturated the server is the reason these exist; pair them
    // with `nice -n 15 ionice -c3` for OS-level deprioritisation.

    // Sleep this long after each file so CPU/I-O are not monopolised. 0 = none.
    public int DelayBetweenFilesMs { get; set; } = 0;

    // Cap the source read/import byte rate (bytes/sec) while ingesting. The cap
    // is applied per file via a throttled read stream. 0 = unlimited.
    public long MaxBytesPerSecond { get; set; } = 0;

    // Wall-clock budget per job slice. When exceeded the run is persisted and
    // re-queued (resumes safely — already-imported files are skipped as
    // conflicts), instead of running for hours. 0 = unlimited.
    public int MaxRunMinutes { get; set; } = 0;

    // Yield to the scheduler (and flush progress) every N files so the API/web
    // stays responsive. Must be >= 1. Default 64 — negligible throughput cost.
    public int YieldEveryFiles { get; set; } = 64;

    // ── Slice 92: massive-import optimisation knobs.

    // When false (the default, optimised for massive import) the import does
    // NOT generate image thumbnails inline per file; a media.derivatives
    // backfill job is enqueued at the end of the run instead, and gallery
    // endpoints keep lazy-generating on first request. true restores the old
    // ingest-time small-thumbnail behaviour.
    public bool GenerateDerivativesInline { get; set; } = false;

    // Slice 94 (metadata pipeline V2): when false (the default) the import
    // writes only the cheap detection facts inline (content type, dimensions,
    // media category — what gallery safety needs) and defers the full
    // embedded EXIF/IPTC/XMP/GPS extraction to the asynchronous
    // metadata.embedded.backfill job enqueued at the end of the run. true
    // restores ingest-time full extraction.
    public bool ExtractMetadataInline { get; set; } = false;

    // Scan phase: persist discovered manifest items in batches of this size.
    public int ScanBatchSize { get; set; } = 500;

    // Import phase: page pending items from the manifest in batches of this
    // size (bounds memory; a 1M-file run never holds more than this in RAM).
    public int ItemBatchSize { get; set; } = 200;

    // Slice 98: DB batch pipeline — staged files are persisted in sub-batches
    // of this size, each committing blobs + metadata + FileItems + item
    // statuses in ONE transaction (one fsync) instead of 3-4 commits per file.
    // Conservative default; 1 disables batching (per-file path). A failed
    // batch falls back to the per-file path for exactly that batch, so unique
    // constraints remain the final authority either way.
    public int DbBatchSize { get; set; } = 100;
}
