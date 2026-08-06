namespace NubArca.Api.Audit;

public interface IAuditLogger
{
    // Best-effort: a failure here is logged and swallowed, because for most
    // actions the user-facing operation has ALREADY succeeded and failing it
    // afterwards would be worse than a gap in the trail.
    Task LogAsync(
        Guid? userId,
        string action,
        string entityType,
        Guid? entityId,
        string? ipAddress,
        object? metadata,
        CancellationToken cancellationToken = default);

    // SHARE-ALBUM-03: the TRANSACTIONAL variant. Adds the entry and saves
    // WITHOUT swallowing failures, so a caller that has an open transaction can
    // make the audit record atomic with the mutation it describes — either both
    // land or neither does.
    //
    // Used by the collaborative editing surface, where a curation change that
    // committed with no audit entry would be exactly the gap the audit exists
    // to close. Everything else should keep using LogAsync.
    Task WriteAsync(
        Guid? userId,
        string action,
        string entityType,
        Guid? entityId,
        string? ipAddress,
        object? metadata,
        CancellationToken cancellationToken = default);
}
