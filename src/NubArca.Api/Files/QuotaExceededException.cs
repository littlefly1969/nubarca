namespace NubArca.Api.Files;

// Slice 65: thrown by FileItemService.CreateAsync when accepting an upload
// would push the owner's logical bytes past their configured quota. The
// endpoint maps this to HTTP 413. Carries only the owner's own aggregate
// figures (their used/quota/attempted bytes) — never another user's data,
// no paths, no storage keys.
public sealed class QuotaExceededException : Exception
{
    public long QuotaBytes { get; }
    public long UsedBytes { get; }
    public long AttemptedBytes { get; }

    public QuotaExceededException(long quotaBytes, long usedBytes, long attemptedBytes)
        : base("Upload would exceed your storage quota.")
    {
        QuotaBytes = quotaBytes;
        UsedBytes = usedBytes;
        AttemptedBytes = attemptedBytes;
    }
}
