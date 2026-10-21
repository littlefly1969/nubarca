using System.Text.Json;
using System.Text.Json.Serialization;

namespace NubArca.Api.MailArchive;

// The .NET side of protocol nubarca-pst/1 (docs/mailarchive/contract.md §2).
// Records mirror the engine's JSON exactly; unknown fields are ignored so a
// newer engine can add information without breaking the backend.
public static class PstProtocol
{
    public const string Version = "nubarca-pst/1";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

public sealed record PstProbe(
    string Type,
    string? Protocol,
    string? Format,
    string? Encryption,
    string? StoreDisplayName,
    long? FileSize);

// One NDJSON line of `list`. `Type` is folder | message | error | end | fatal.
public sealed record PstListEvent(
    string Type,
    string? FolderId,
    string? ParentFolderId,
    string? Name,
    IReadOnlyList<string>? Path,
    int? Depth,
    string? ContainerClass,
    int? ContentCount,
    string? MessageId,
    string? Scope,
    string? Id,
    string? Code,
    string? Message,
    int? Folders,
    int? Messages,
    int? Errors);

public sealed record PstExtractedAttachmentFile(string Key, string File, long SizeBytes, string? Sha256);

public sealed record PstWarning(string Code, string? Message);

// One response line of `serve`: type extracted | error.
public sealed record PstExtractResponse(
    string Type,
    string? RequestId,
    string? MessageId,
    string? Document,
    long? DocumentBytes,
    IReadOnlyList<PstExtractedAttachmentFile>? Attachments,
    IReadOnlyList<PstWarning>? Warnings,
    string? Code,
    string? Message);

public sealed record PstMailAddress(string? Name, string? Address, string? AddressType);

public sealed record PstBodyTruncation(bool Text, bool Html, bool Rtf);

public sealed record PstBody(string? Text, string? Html, string? Rtf, PstBodyTruncation? Truncated);

public sealed record PstAttachmentDescriptor(
    string Key,
    string? Method,
    string? FileName,
    string? DisplayName,
    string? MimeType,
    string? ContentId,
    bool Inline,
    bool Hidden,
    long? SizeBytes,
    bool Extracted,
    string? SkipReason,
    PstMessageDocument? EmbeddedMessage);

public sealed record PstMessageSource(string? MessageId, string? FolderId);

// The normalized message document nubarca-mail-message v1. An embedded
// message reuses this record without Format/Version/Source.
public sealed record PstMessageDocument(
    string? Format,
    int? Version,
    PstMessageSource? Source,
    string? MessageClass,
    string? Subject,
    string? ConversationTopic,
    PstMailAddress? From,
    PstMailAddress? Sender,
    IReadOnlyList<PstMailAddress>? To,
    IReadOnlyList<PstMailAddress>? Cc,
    IReadOnlyList<PstMailAddress>? Bcc,
    DateTime? SentAt,
    DateTime? ReceivedAt,
    DateTime? CreatedAt,
    DateTime? ModifiedAt,
    string? InternetMessageId,
    string? InReplyTo,
    string? References,
    int? Importance,
    int? Sensitivity,
    int? Flags,
    bool? HasAttachments,
    string? TransportHeaders,
    PstBody? Body,
    IReadOnlyList<PstAttachmentDescriptor>? Attachments,
    IReadOnlyList<PstWarning>? Warnings);

/// <summary>A protocol violation or an engine failure that is not per-item.</summary>
public sealed class PstEngineException : Exception
{
    public string Code { get; }

    public PstEngineException(string code, string message) : base(message)
    {
        Code = code;
    }
}
