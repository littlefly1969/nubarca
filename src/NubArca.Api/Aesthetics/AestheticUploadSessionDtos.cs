namespace NubArca.Api.Aesthetics;

// SAFE DTOs for the TV "Beauty Lab" QR upload-session flow. NONE ever carry the
// owner id, the raw token (except the one-time create response, which embeds it
// in the QR URL for that one render), a filename, a blob id, or a storage path.

// Returned ONCE when the TV creates a session. `UploadUrl` embeds the raw
// capability token in a PATH segment (never a query string) so it can be turned
// into a QR code; the token is never persisted in plaintext and never returned
// again. Everything else is safe to poll for.
public sealed record AestheticUploadSessionCreatedDto(
    Guid Id,
    string UploadUrl,
    DateTime ExpiresAt,
    int MaxFiles,
    long MaxTotalBytes,
    int Accepted,
    int Rejected,
    string Status);

// Grant-gated status the TV polls while the QR screen is open. No token.
public sealed record AestheticUploadSessionStatusDto(
    Guid Id,
    DateTime ExpiresAt,
    int MaxFiles,
    long MaxTotalBytes,
    int Accepted,
    int Rejected,
    string Status);

// PUBLIC state the mobile page reads by token. Deliberately carries NO owner
// info and NO lab contents — only the session's own lifecycle + progress.
public sealed record AestheticUploadPublicStateDto(
    string Status,
    DateTime ExpiresAt,
    int MaxFiles,
    long MaxTotalBytes,
    int Accepted,
    int Rejected);

// Per-file result echoed back to the SAME uploader (owner of the bytes). The
// display name is echoed for the mobile UI only; it is never persisted or logged.
public sealed record AestheticUploadFileResultDto(string Name, bool Ok, string? Reason);

public sealed record AestheticUploadResultDto(
    int Accepted,
    int Rejected,
    string Status,
    IReadOnlyList<AestheticUploadFileResultDto> Files);
