using System.Text.Json;
using Microsoft.Extensions.Logging;
using NubArca.Api.Data;
using NubArca.Api.Domain;

namespace NubArca.Api.Audit;

public sealed class AuditLogger : IAuditLogger
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;
    private readonly ILogger<AuditLogger> _logger;

    public AuditLogger(AppDbContext db, TimeProvider clock, ILogger<AuditLogger> logger)
    {
        _db = db;
        _clock = clock;
        _logger = logger;
    }

    public async Task WriteAsync(
        Guid? userId,
        string action,
        string entityType,
        Guid? entityId,
        string? ipAddress,
        object? metadata,
        CancellationToken cancellationToken = default)
    {
        var entry = new AuditLog
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            IpAddress = ipAddress,
            CreatedAt = _clock.GetUtcNow().UtcDateTime,
            MetadataJson = metadata is null ? null : JsonSerializer.Serialize(metadata, JsonOptions),
        };

        // Same DbContext as the caller, so when they hold an open transaction
        // this enlists in it and commits atomically with their mutation.
        // Failures propagate — that is the whole point of this overload.
        _db.AuditLogs.Add(entry);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task LogAsync(
        Guid? userId,
        string action,
        string entityType,
        Guid? entityId,
        string? ipAddress,
        object? metadata,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await WriteAsync(
                userId, action, entityType, entityId, ipAddress, metadata, cancellationToken);
        }
        catch (Exception ex)
        {
            // Best-effort audit: a DB hiccup on the audit write must not fail
            // the user-facing operation that just succeeded. The warning lets
            // operators spot gaps via the application log.
            _logger.LogWarning(
                ex,
                "Failed to write audit log entry for {Action} ({EntityType}/{EntityId})",
                action,
                entityType,
                entityId);
        }
    }
}
