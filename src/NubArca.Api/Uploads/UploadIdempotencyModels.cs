namespace NubArca.Api.Uploads;

// Models for the optional upload idempotency contract (mobile-sync-v1).
//
// The key is an OPERATION identity, never a content hash: the server keeps
// deriving blob identity from bytes (SHA-256) and owner identity from the
// authenticated cookie. Validation is deliberately narrow — opaque, bounded,
// URL-safe characters — so the value can never smuggle header/DB structure and
// can never carry anything sensitive.

public static class UploadOperationKey
{
    public const int MinLength = 8;
    public const int MaxLength = 128;

    // Matches the mobile client's "sync-v1-{uuid}" shape without coupling to it.
    private static readonly System.Text.RegularExpressions.Regex Pattern = new(
        "^[A-Za-z0-9._:-]{8,128}$",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    public static bool IsValid(string? key) =>
        !string.IsNullOrWhiteSpace(key) && Pattern.IsMatch(key);
}

public enum UploadClaimOutcome
{
    // This request owns the operation now; proceed with ingestion.
    Claimed,

    // A completed result exists for this key: replay it instead of ingesting.
    AlreadyCompleted,

    // Another live claim owns the key right now. Safe, bounded answer: come
    // back later. Never treated as a content failure by clients.
    InFlight,
}

public readonly record struct UploadClaim(UploadClaimOutcome Outcome, Guid Token)
{
    public static UploadClaim Claimed(Guid token) => new(UploadClaimOutcome.Claimed, token);
    public static UploadClaim AlreadyCompleted() => new(UploadClaimOutcome.AlreadyCompleted, Guid.Empty);
    public static UploadClaim InFlight() => new(UploadClaimOutcome.InFlight, Guid.Empty);
}