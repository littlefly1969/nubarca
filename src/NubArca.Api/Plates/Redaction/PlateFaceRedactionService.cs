using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NubArca.Api.Data;
using NubArca.Api.Domain;

namespace NubArca.Api.Plates.Redaction;

public sealed class PlateFaceRedactionService : IPlateFaceRedactionService
{
    private readonly AppDbContext _db;
    private readonly IPlateFaceRedactionDetector _detector;
    private readonly TimeProvider _clock;
    private readonly PlatesFaceRedactionOptions _options;
    private readonly ILogger<PlateFaceRedactionService> _logger;

    public PlateFaceRedactionService(
        AppDbContext db,
        IPlateFaceRedactionDetector detector,
        TimeProvider clock,
        ILogger<PlateFaceRedactionService> logger,
        IOptions<PlatesFaceRedactionOptions>? options = null)
    {
        _db = db;
        _detector = detector;
        _clock = clock;
        _logger = logger;
        _options = options?.Value ?? new PlatesFaceRedactionOptions();
    }

    public bool IsAvailable => _options.Enabled && _detector.IsAvailable;

    public string ProfileKey => _options.ProfileKey;

    public async Task<PlateRedactionInfo> GetInfoAsync(
        Guid ownerUserId, Guid plateImageId, CancellationToken cancellationToken = default)
    {
        var count = await _db.PlateFaceRedactionBoxes.AsNoTracking()
            .CountAsync(
                b => b.OwnerUserId == ownerUserId
                    && b.PlateImageId == plateImageId
                    && b.ModelProfileKey == _options.ProfileKey,
                cancellationToken);
        return new PlateRedactionInfo(IsAvailable, count, _options.ProfileKey);
    }

    public async Task<PlateFaceRedactionEnsureResult> EnsureBoxesAsync(
        Guid ownerUserId,
        Guid plateImageId,
        Func<CancellationToken, Task<PlateRedactionImageInput?>> sourceFactory,
        CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            throw new PlateFaceRedactionUnavailableException();
        }

        // Reuse boxes already detected under the CURRENT profile — the common
        // path after the first redacted render (no detector, no source read).
        var existing = await _db.PlateFaceRedactionBoxes.AsNoTracking()
            .Where(b => b.OwnerUserId == ownerUserId
                && b.PlateImageId == plateImageId
                && b.ModelProfileKey == _options.ProfileKey)
            .ToListAsync(cancellationToken);
        if (existing.Count > 0)
        {
            return new PlateFaceRedactionEnsureResult(existing, Regenerated: false);
        }

        // Missing for the current profile — (re)detect. Load the source lazily
        // only now (detection runs once per image/profile).
        var source = await sourceFactory(cancellationToken);
        if (source is null)
        {
            // Source unrenderable/missing — no boxes. Not a failure; the caller
            // renders an unchanged image (nothing to redact).
            return new PlateFaceRedactionEnsureResult(Array.Empty<PlateFaceRedactionBox>(), Regenerated: false);
        }

        var candidates = await _detector.DetectAsync(source, cancellationToken);
        var now = _clock.GetUtcNow().UtcDateTime;

        var accepted = new List<PlateFaceRedactionBox>();
        foreach (var c in candidates)
        {
            if (c.Confidence < _options.MinConfidence)
            {
                continue;
            }
            if (c.Width <= 0 || c.Height <= 0)
            {
                continue;
            }
            accepted.Add(new PlateFaceRedactionBox
            {
                Id = Guid.NewGuid(),
                OwnerUserId = ownerUserId,
                PlateImageId = plateImageId,
                Confidence = c.Confidence,
                BoundingBoxX = c.X,
                BoundingBoxY = c.Y,
                BoundingBoxWidth = c.Width,
                BoundingBoxHeight = c.Height,
                ModelProfileKey = _options.ProfileKey,
                CreatedAt = now,
                UpdatedAt = now,
            });
            if (accepted.Count >= _options.MaxFacesPerImage)
            {
                break;
            }
        }

        // Replace ALL boxes for this image (any stale prior-profile rows) with
        // the freshly detected set for the current profile.
        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);
            await _db.PlateFaceRedactionBoxes
                .Where(b => b.PlateImageId == plateImageId && b.OwnerUserId == ownerUserId)
                .ExecuteDeleteAsync(cancellationToken);
            if (accepted.Count > 0)
            {
                _db.PlateFaceRedactionBoxes.AddRange(accepted);
                await _db.SaveChangesAsync(cancellationToken);
            }
            await tx.CommitAsync(cancellationToken);
        });

        _logger.LogInformation(
            "Persisted {Count} privacy redaction box(es) for a plate image under profile {Profile}.",
            accepted.Count, _options.ProfileKey);

        return new PlateFaceRedactionEnsureResult(accepted, Regenerated: true);
    }
}
