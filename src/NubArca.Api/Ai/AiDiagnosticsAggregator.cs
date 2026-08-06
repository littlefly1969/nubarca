using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;

namespace NubArca.Api.Ai;

// Aggregates AiIndexDiagnostic rows into safe, aggregate-only counts for the
// `ai diagnostics` CLI and the admin status API. Mirrors the
// DerivativeDiagnosticsService aggregation pattern (GroupBy → counts + Max
// timestamp). Profile GUIDs are mapped to stable keys; raw rows never leave.
public sealed class AiDiagnosticsAggregator
{
    private readonly AppDbContext _db;

    public AiDiagnosticsAggregator(AppDbContext db)
    {
        _db = db;
    }

    public async Task<AiDiagnosticsStats> AggregateAsync(CancellationToken cancellationToken = default)
    {
        var grouped = await _db.AiIndexDiagnostics
            .AsNoTracking()
            .GroupBy(d => new { d.Capability, d.TargetKind, d.ProfileId, d.ErrorCode, d.IsPermanent })
            .Select(g => new
            {
                g.Key.Capability,
                g.Key.TargetKind,
                g.Key.ProfileId,
                g.Key.ErrorCode,
                g.Key.IsPermanent,
                Count = g.Count(),
                Latest = g.Max(d => d.OccurredAt),
            })
            .ToListAsync(cancellationToken);

        var total = grouped.Sum(g => g.Count);
        var lastOccurredAt = grouped.Count == 0 ? (DateTime?)null : grouped.Max(g => g.Latest);

        // Map profile ids → stable keys; never surface the GUIDs.
        var profileIds = grouped
            .Where(g => g.ProfileId.HasValue)
            .Select(g => g.ProfileId!.Value)
            .Distinct()
            .ToList();

        var keyByProfileId = profileIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await _db.AiProfiles.AsNoTracking()
                .Where(p => profileIds.Contains(p.Id))
                .ToDictionaryAsync(p => p.Id, p => p.Key, cancellationToken);

        var groups = grouped
            .OrderByDescending(g => g.Count)
            .ThenBy(g => g.Capability, StringComparer.Ordinal)
            .ThenBy(g => g.ErrorCode, StringComparer.Ordinal)
            .Select(g => new AiDiagnosticGroup(
                g.Capability,
                g.TargetKind,
                g.ProfileId.HasValue && keyByProfileId.TryGetValue(g.ProfileId.Value, out var key) ? key : null,
                g.ErrorCode,
                g.IsPermanent,
                g.Count,
                g.Latest))
            .ToList();

        return new AiDiagnosticsStats(total, lastOccurredAt, groups);
    }
}
