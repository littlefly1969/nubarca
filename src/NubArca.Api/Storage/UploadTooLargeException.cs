namespace NubArca.Api.Storage;

// Slice 65: thrown by the blob-storage layer when an upload stream exceeds
// the configured Storage:MaxUploadBytes ceiling. The endpoint maps this to
// HTTP 413 (Payload Too Large). Carries only the configured limit — never a
// path or storage key.
public sealed class UploadTooLargeException : Exception
{
    public long LimitBytes { get; }

    public UploadTooLargeException(long limitBytes)
        : base($"Upload exceeds the maximum allowed size of {limitBytes} bytes.")
    {
        LimitBytes = limitBytes;
    }
}
