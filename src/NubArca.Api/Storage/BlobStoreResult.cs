using NubArca.Api.Domain;

namespace NubArca.Api.Storage;

// Slice 82: per-phase timings for a single blob ingest, in milliseconds.
// Read/Hash/Write come from the streaming write loop; BlobDb is the
// dedup/insert transaction. Used only by the admin import diagnostics; the
// normal upload path never reads them.
public sealed record BlobIngestTimings(
    long ReadMillis,
    long HashMillis,
    long WriteMillis,
    long BlobDbMillis);

public sealed record BlobStoreResult(BlobObject Blob, BlobIngestTimings Timings);
