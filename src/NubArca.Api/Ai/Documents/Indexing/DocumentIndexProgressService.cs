using Microsoft.EntityFrameworkCore;
using NubArca.Api.Ai.Documents.Sources;
using NubArca.Api.Data;
using NubArca.Api.Domain.Ai;

namespace NubArca.Api.Ai.Documents.Indexing;

/// Counts per phase and status. Nothing else — no names, no ids, no reasons
/// attached to a particular source.
public sealed record DocumentIndexProgress(
    int EligibleSources,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> Phases,
    IReadOnlyDictionary<string, int> ReasonCounts);

/// The progress view behind `documents progress` and the owner's indexing
/// status endpoint.
///
/// OWNER-SCOPED COUNTS are joined to live eligibility per registered kind, so a
/// source deleted or vaulted since it was indexed stops counting the moment it
/// is. A missing row is implicit pending, so `pending` per phase is computed as
/// eligible sources minus every other state, never read from materialized rows.
///
/// A state row describes the last revision the job saw. A content change is
/// signalled and re-indexed; between the change and the job, the row still
/// shows the previous revision's state.
public sealed class DocumentIndexProgressService
{
    private readonly AppDbContext _db;
    private readonly OwnerDocumentSources _sources;

    public DocumentIndexProgressService(AppDbContext db, OwnerDocumentSources sources)
    {
        _db = db;
        _sources = sources;
    }

    /// One owner's progress, or the installation's aggregate when `owner` is
    /// null (operator CLI only).
    public async Task<DocumentIndexProgress> GetAsync(Guid? ownerUserId, CancellationToken cancellationToken = default)
    {
        var eligible = 0;
        var rows = new List<(string Phase, string Status, string? Reason, int Count)>();

        foreach (var source in _sources.All)
        {
            if (ownerUserId is { } owner)
            {
                var ids = source.EligibleSourceIds(_db, owner);
                eligible += await CountCandidatesAsync(source, owner, cancellationToken);

                var grouped = await _db.DocumentSourceStates.AsNoTracking()
                    .Where(s => s.OwnerUserId == owner && s.SourceKind == source.Kind && ids.Contains(s.SourceId))
                    .GroupBy(s => new { s.Phase, s.Status, s.ReasonCode })
                    .Select(g => new { g.Key.Phase, g.Key.Status, g.Key.ReasonCode, Count = g.Count() })
                    .ToListAsync(cancellationToken);
                rows.AddRange(grouped.Select(g => (g.Phase, g.Status, g.ReasonCode, g.Count)));
            }
            else
            {
                var grouped = await _db.DocumentSourceStates.AsNoTracking()
                    .Where(s => s.SourceKind == source.Kind)
                    .GroupBy(s => new { s.Phase, s.Status, s.ReasonCode })
                    .Select(g => new { g.Key.Phase, g.Key.Status, g.Key.ReasonCode, Count = g.Count() })
                    .ToListAsync(cancellationToken);
                rows.AddRange(grouped.Select(g => (g.Phase, g.Status, g.ReasonCode, g.Count)));
            }
        }

        var phases = new Dictionary<string, IReadOnlyDictionary<string, int>>(StringComparer.Ordinal);
        foreach (var phase in DocumentSourcePhases.All)
        {
            var counts = DocumentSourceStatuses.All.ToDictionary(
                status => status,
                status => rows.Where(r => r.Phase == phase && r.Status == status).Sum(r => r.Count),
                StringComparer.Ordinal);

            if (ownerUserId is not null)
            {
                // Implicit pending: eligible sources with no row for this phase.
                var tracked = counts.Values.Sum();
                counts[DocumentSourceStatuses.Pending] += Math.Max(0, eligible - tracked);
            }

            phases[phase] = counts;
        }

        var reasons = rows
            .Where(r => r.Reason is not null)
            .GroupBy(r => $"{r.Phase}.{r.Status}.{r.Reason}", StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Sum(r => r.Count), StringComparer.Ordinal);

        return new DocumentIndexProgress(eligible, phases, reasons);
    }

    /// Candidates, counted page by page through the kind's own keyset — the
    /// kind decides what a candidate is, and nothing is held in memory.
    private static async Task<int> CountCandidatesAsync(
        IOwnerDocumentSource source, Guid owner, CancellationToken cancellationToken)
    {
        var count = 0;
        Guid? cursor = null;
        while (true)
        {
            var page = await source.ListCandidatesAsync(owner, cursor, 500, cancellationToken);
            if (page.Count == 0) return count;
            count += page.Count;
            cursor = page[^1].SourceId;
        }
    }
}
