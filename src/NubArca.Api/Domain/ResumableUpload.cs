namespace NubArca.Api.Domain;

// One owner's file arriving in chunks, so a large upload survives a dropped
// connection, a sleeping phone or a reloaded tab: the server remembers which
// chunks it already holds and the client sends only the rest.
//
// The bytes live in a single pre-sized temporary file next to the blob store
// (Storage:RootPath/resumable/{id:N}.part) until completion hands them to the
// ordinary owner ingestion, which content-addresses them like any other upload.
// Nothing here is user-visible content: no FileItem exists until completion,
// and the row is temporary bookkeeping the sweeper reclaims after ExpiresAt.
//
// Deliberately NOT the cloud staging session: staging is an optional,
// operator-enabled bulk import into a background job; this is the interactive
// path every owner upload of a large file takes, completed synchronously into
// the folder the person is looking at.
public class ResumableUpload
{
    public Guid Id { get; set; }

    // The authenticated uploader. Every lookup is scoped by it.
    public Guid OwnerUserId { get; set; }

    // The client's file name, kept only to name the file at completion.
    public string FileName { get; set; } = string.Empty;

    // The browser's declared type, handed to the ingest exactly as a multipart
    // part's type would be. A hint only: the ingest detects the bytes.
    public string? ContentType { get; set; }

    public long SizeBytes { get; set; }
    public int ChunkSizeBytes { get; set; }
    public int ExpectedChunkCount { get; set; }
    public int ReceivedChunkCount { get; set; }

    // See ResumableUploadStatuses.
    public string Status { get; set; } = ResumableUploadStatuses.Uploading;

    // The file the completion produced. Lets a retried completion answer with
    // the same file instead of ingesting twice.
    public Guid? FileItemId { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // Sliding: each received chunk pushes it forward, so only an abandoned
    // upload expires. A completed row keeps a short replay window.
    public DateTime ExpiresAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

public class ResumableUploadChunk
{
    public Guid UploadId { get; set; }
    public int ChunkIndex { get; set; }
    public int SizeBytes { get; set; }
    public DateTime ReceivedAt { get; set; }
}

public static class ResumableUploadStatuses
{
    public const string Uploading = "uploading";
    public const string Completed = "completed";
}
