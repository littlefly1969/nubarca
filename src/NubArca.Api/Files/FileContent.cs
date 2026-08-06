namespace NubArca.Api.Files;

// Read-side projection of a FileItem returned from IFileItemService.OpenContentAsync.
// Deliberately does NOT expose StorageKey — physical storage paths are an internal
// concern of the storage layer and must never reach an HTTP response.
public sealed class FileContent : IAsyncDisposable
{
    public Stream Content { get; }

    // Client-supplied MIME stored on the FileItem. UNTRUSTED — callers must not
    // serve this as an authoritative Content-Type (slice 54.2).
    public string MimeType { get; }

    public long SizeBytes { get; }
    public string FileName { get; }

    // Server-detected content type from BlobMetadata (e.g. "image/jpeg"), or
    // null when detection failed / the blob is not a recognized image. This is
    // the only MIME the serving layer may trust.
    public string? DetectedContentType { get; }

    public FileContent(
        Stream content,
        string mimeType,
        long sizeBytes,
        string fileName,
        string? detectedContentType = null)
    {
        Content = content;
        MimeType = mimeType;
        SizeBytes = sizeBytes;
        FileName = fileName;
        DetectedContentType = detectedContentType;
    }

    public ValueTask DisposeAsync() => Content.DisposeAsync();
}
