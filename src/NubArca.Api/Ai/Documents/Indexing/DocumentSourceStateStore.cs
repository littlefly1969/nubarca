using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Domain.Ai;

namespace NubArca.Api.Ai.Documents.Indexing;

/// Reads and writes `document_source_states`, one row per (source, phase).
///
/// The rules live here, once:
///  - a write for a DIFFERENT revision than the row holds starts the row over
///    (attempts reset), because a content change re-opens every question;
///  - `failed` counts an attempt; `completed` clears the count; `pending`,
///    `skipped` and `running` leave it alone — an environment state and a
///    cancellation are never charged to the source;
///  - a write that changes nothing is not written, so a pass over an unchanged
///    library costs reads only.
public sealed class DocumentSourceStateStore
{
    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;

    public DocumentSourceStateStore(AppDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<IReadOnlyDictionary<string, DocumentSourceState>> GetAsync(
        string kind, Guid sourceId, CancellationToken cancellationToken)
    {
        var rows = await _db.DocumentSourceStates.AsNoTracking()
            .Where(s => s.SourceKind == kind && s.SourceId == sourceId)
            .ToListAsync(cancellationToken);
        return rows.ToDictionary(r => r.Phase, StringComparer.Ordinal);
    }

    public async Task RecordAsync(
        string kind, Guid sourceId, Guid ownerUserId, string phase, string revision,
        string status, string? reason = null, Guid? profileId = null, int? formatVersion = null,
        CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var row = await _db.DocumentSourceStates
            .FirstOrDefaultAsync(
                s => s.SourceKind == kind && s.SourceId == sourceId && s.Phase == phase,
                cancellationToken);

        if (row is null)
        {
            row = new DocumentSourceState
            {
                Id = Guid.NewGuid(),
                SourceKind = kind,
                SourceId = sourceId,
                Phase = phase,
                CreatedAt = now,
            };
            _db.DocumentSourceStates.Add(row);
        }
        else if (row.OwnerUserId == ownerUserId
                 && row.SourceRevision == revision
                 && row.Status == status
                 && row.ReasonCode == reason
                 && row.ProfileId == profileId
                 && row.FormatVersion == formatVersion
                 && status != DocumentSourceStatuses.Failed)
        {
            return;
        }

        if (row.SourceRevision != revision)
        {
            row.Attempts = 0;
            row.StartedAt = null;
            row.CompletedAt = null;
        }

        row.OwnerUserId = ownerUserId;
        row.SourceRevision = revision;
        row.Status = status;
        row.ReasonCode = reason;
        row.ProfileId = profileId;
        row.FormatVersion = formatVersion;
        row.UpdatedAt = now;

        switch (status)
        {
            case DocumentSourceStatuses.Running:
                row.StartedAt = now;
                break;
            case DocumentSourceStatuses.Completed:
                row.Attempts = 0;
                row.CompletedAt = now;
                break;
            case DocumentSourceStatuses.Skipped:
                row.CompletedAt = now;
                break;
            case DocumentSourceStatuses.Failed:
                row.Attempts++;
                break;
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    /// A phase left `running` by an interrupted attempt goes back to pending.
    ///
    /// Written with a set-based update rather than through the change tracker,
    /// and with no cancellation token, because it runs exactly when the job was
    /// cancelled — and must neither flush whatever half-built entities the
    /// interrupted phase left tracked nor be skipped because the token fired.
    public Task ResetRunningAsync(string kind, Guid sourceId, string? reason)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        return _db.DocumentSourceStates
            .Where(s => s.SourceKind == kind && s.SourceId == sourceId
                        && s.Status == DocumentSourceStatuses.Running)
            .ExecuteUpdateAsync(
                u => u
                    .SetProperty(s => s.Status, DocumentSourceStatuses.Pending)
                    .SetProperty(s => s.ReasonCode, reason)
                    .SetProperty(s => s.UpdatedAt, now),
                CancellationToken.None);
    }
}
