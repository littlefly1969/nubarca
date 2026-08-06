namespace NubArca.Api.Folders;

// Response body for DELETE /api/trash. Aggregate counts plus a per-item list
// of failures so a client can show "couldn't delete N folders because they
// still have active children" without leaking storage internals.
public sealed record EmptyTrashResult(
    int DeletedFiles,
    int DeletedFolders,
    int Conflicts,
    int Errors,
    IReadOnlyList<EmptyTrashFailure> Failures);

// Lightweight, no-leak failure descriptor. `Type` is `"file"` or `"folder"`.
// `Reason` is a stable token (`"not_empty"`, `"unexpected_error"`) — never an
// exception message or stack trace.
public sealed record EmptyTrashFailure(
    Guid Id,
    string Type,
    string Reason);
