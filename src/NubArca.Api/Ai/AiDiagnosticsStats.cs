namespace NubArca.Api.Ai;

// Aggregate-only AI diagnostics snapshot. Grouped counts + latest timestamps;
// the profile is referenced by its stable KEY (never a GUID). Contains NO file
// names, text snippets, vectors, blob SHA, storage keys, physical paths, raw
// payloads, stack traces, or secrets.
public sealed record AiDiagnosticsStats(
    int Total,
    DateTime? LastOccurredAt,
    IReadOnlyList<AiDiagnosticGroup> Groups);

public sealed record AiDiagnosticGroup(
    string Capability,
    string TargetKind,
    string? ProfileKey,
    string ErrorCode,
    bool IsPermanent,
    int Count,
    DateTime LatestOccurredAt);
