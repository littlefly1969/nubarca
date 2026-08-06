using NubArca.Api.Data;
using NubArca.Api.Domain.Ai;

namespace NubArca.Api.Ai.Diagnostics;

public sealed class AiDiagnosticsWriter : IAiDiagnosticsWriter
{
    private const int MaxErrorCodeLength = 100;

    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;

    public AiDiagnosticsWriter(AppDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task RecordProviderUnavailableAsync(
        string capability,
        Guid? profileId,
        string reasonCode,
        CancellationToken cancellationToken = default)
    {
        var code = AiDiagnosticSanitizer.Sanitize(reasonCode) ?? "unknown";
        if (code.Length > MaxErrorCodeLength)
        {
            code = code[..MaxErrorCodeLength];
        }

        var diagnostic = new AiIndexDiagnostic
        {
            Id = Guid.NewGuid(),
            Capability = capability,
            ProfileId = profileId,
            TargetKind = AiDiagnosticTargetKinds.Provider,
            // No blob/chunk/face/owner target for a provider-availability event.
            ErrorCode = code,
            IsPermanent = false,
            AttemptCount = 0,
            // Provider-availability diagnostics never carry a free-text message.
            SanitizedMessage = null,
            OccurredAt = _clock.GetUtcNow().UtcDateTime,
        };

        _db.AiIndexDiagnostics.Add(diagnostic);
        await _db.SaveChangesAsync(cancellationToken);
    }
}
