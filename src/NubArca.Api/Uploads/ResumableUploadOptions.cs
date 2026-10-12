namespace NubArca.Api.Uploads;

// Resumable owner uploads (chunked, interactive). ON by default: unlike cloud
// staging it needs no extra mount, because its temporary files live on the
// storage volume the API already writes (Storage:RootPath/resumable).
//
//   ResumableUploads__Enabled=false          turn the path off (clients fall back
//                                            to one multipart request per file)
//   ResumableUploads__ChunkSizeBytes=8388608 chunk size handed to clients
public sealed class ResumableUploadOptions
{
    public const string SectionName = "ResumableUploads";

    public bool Enabled { get; set; } = true;

    // Optional override of the temporary directory. Empty = a "resumable"
    // directory under Storage:RootPath, on the same filesystem as the blobs.
    public string RootPath { get; set; } = string.Empty;

    // 8 MiB keeps each request far below proxy/Kestrel body limits while not
    // flooding the API with requests. Clamped to [1 MiB, 64 MiB].
    public int ChunkSizeBytes { get; set; } = 8 * 1024 * 1024;

    // How long an untouched upload is kept. Each chunk received renews it.
    public int TtlHours { get; set; } = 24;

    // Bounds the disk one account can hold in unfinished uploads.
    public int MaxActiveUploadsPerUser { get; set; } = 32;

    // The sweeper reclaims expired uploads (rows and temporary files). On by
    // default: these are abandoned partial transfers, never user content.
    public bool CleanupEnabled { get; set; } = true;
    public int CleanupIntervalMinutes { get; set; } = 30;

    public int EffectiveChunkSizeBytes => Math.Clamp(ChunkSizeBytes, 1024 * 1024, 64 * 1024 * 1024);
}
