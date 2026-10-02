using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Print;
using NubArca.Api.Storage;

namespace NubArca.Api.Print;

public interface IOwnerPhotoPrintService
{
    Task<OwnerPhotoPrintResult> SubmitAsync(
        Guid ownerUserId, OwnerPhotoPrintSubmitRequest request, string? idempotencyKey,
        CancellationToken cancellationToken);

    /// <summary>The date a print of this photograph would carry, or null for a photograph that is not the caller's.</summary>
    Task<OwnerPhotoPrintDate?> ResolveDateAsync(
        Guid ownerUserId, Guid fileItemId, TimeZoneInfo zone, CancellationToken cancellationToken);
}

/// <summary>
/// An owner prints one of their own photographs — from their library or one
/// of their albums — directly, on their printer or one lent to them.
///
/// Not a party: no party is involved, invented or charged. What it shares with
/// the party print is everything that keeps printing honest: who may use the
/// printer is <see cref="IPrinterAccess"/>'s answer, a loan's ceiling is taken
/// atomically and given back on any failure, the original is read inside the
/// server and never sent to a browser, the printer's calibration is applied,
/// and a key makes a retried or double-tapped request one sheet, not two.
///
/// The order is the design, as in the party print: validate, answer a repeat
/// from its record, resolve the photograph and the printer, take the loan's
/// sheet, read, render, publish and record the job — and from the sheet on,
/// any failure gives the sheet back unless a job exists.
/// </summary>
public sealed class OwnerPhotoPrintService : IOwnerPhotoPrintService
{
    private readonly AppDbContext _db;
    private readonly IPrinterAccess _printers;
    private readonly IPrintPhotoSourceReader _sources;
    private readonly PrintArtifactRenderer _renderer;
    private readonly IDerivedBlobStorage _artifacts;
    private readonly TimeProvider _clock;
    private readonly PrintOptions _options;
    private readonly ILogger<OwnerPhotoPrintService> _logger;

    public OwnerPhotoPrintService(
        AppDbContext db, IPrinterAccess printers, IPrintPhotoSourceReader sources,
        PrintArtifactRenderer renderer, IDerivedBlobStorage artifacts, TimeProvider clock,
        IOptions<PrintOptions> options, ILogger<OwnerPhotoPrintService> logger)
    {
        _db = db;
        _printers = printers;
        _sources = sources;
        _renderer = renderer;
        _artifacts = artifacts;
        _clock = clock;
        _options = options.Value;
        _logger = logger;
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    public async Task<OwnerPhotoPrintResult> SubmitAsync(
        Guid ownerUserId, OwnerPhotoPrintSubmitRequest request, string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        // 1. The request's shape, before anything is read.
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 200
            || request.FileItemId is not Guid fileItemId
            || request.PrintStationId is not Guid stationId
            || request.PrinterDeviceId is not Guid deviceId
            || !PrintPapers.IsKnown(request.ExpectedPaperSize))
        {
            return Refuse(OwnerPhotoPrintErrors.InvalidRequest);
        }
        var paper = request.ExpectedPaperSize!;
        if (request.Orientation is not ("portrait" or "landscape"))
            return Refuse(OwnerPhotoPrintErrors.InvalidOrientation);
        var portrait = request.Orientation == "portrait";
        if (request.Placement is not { } requestedPlacement
            || !PhotoPlacementGeometry.IsStructurallyValid(requestedPlacement.ToPlacement()))
        {
            return Refuse(OwnerPhotoPrintErrors.InvalidPlacement);
        }
        var placement = requestedPlacement.ToPlacement();
        TimeZoneInfo? zone = null;
        if (request.IncludeDate)
        {
            if (!OwnerPhotoPrintDates.IsKnownLocale(request.DateLocale)) return Refuse(OwnerPhotoPrintErrors.InvalidRequest);
            // Refused, never defaulted: a date from another zone could be a day
            // the preview did not show.
            zone = OwnerPhotoPrintDates.Zone(request.TimeZone);
            if (zone is null) return Refuse(OwnerPhotoPrintErrors.InvalidTimezone);
        }

        // 2. A repeat is answered from its record — the same composition with
        // the same job, a different one under the same key refused.
        var keyHash = Hash(idempotencyKey);
        var fingerprint = Fingerprint(fileItemId, stationId, deviceId, paper, request.Orientation!, placement,
            request.IncludeDate, request.IncludeDate ? request.DateLocale : null,
            request.IncludeDate ? zone!.Id : null);
        if (await RepeatAsync(ownerUserId, keyHash, fingerprint, cancellationToken) is { } repeat) return repeat;

        // 3. The photograph: the caller's own, in their library, an image. A
        // file that is missing, someone else's, trashed or excluded is one
        // answer, so this is no oracle for file ids.
        var file = await _db.FileItems.AsNoTracking()
            .Where(f => f.Id == fileItemId && f.OwnerUserId == ownerUserId && f.DeletedAt == null
                && f.MediaLibraryState == MediaLibraryState.Active)
            .Select(f => new
            {
                f.Id,
                Meta = _db.BlobMetadata.Where(m => m.BlobObjectId == f.BlobObjectId)
                    .Select(m => new { m.MediaCategory, m.DetectedContentType, m.DateTaken }).FirstOrDefault(),
                Override = _db.FileItemUserMetadata.Where(u => u.FileItemId == f.Id)
                    .Select(u => u.DateTakenOverride).FirstOrDefault(),
            })
            .FirstOrDefaultAsync(cancellationToken);
        if (file is null) return Refuse(OwnerPhotoPrintErrors.NotFound);
        if (file.Meta is not { MediaCategory: MediaCategories.Image, DetectedContentType: not null })
            return Refuse(OwnerPhotoPrintErrors.NotImage);

        // 4. The printer, by the one rule every print path asks.
        var use = await _printers.ForUserAsync(ownerUserId, stationId, deviceId, cancellationToken);
        if (use is null) return Refuse(OwnerPhotoPrintErrors.PrinterNotFound);
        if (!use.StationEnabled) return Refuse(OwnerPhotoPrintErrors.PrinterUnavailable);
        var station = await _db.PrintStations.AsNoTracking()
            .Where(s => s.Id == stationId)
            .Select(s => new { s.LastSeenAt, s.Enabled, s.RevokedAt })
            .FirstAsync(cancellationToken);
        var device = await _db.PrinterDevices.AsNoTracking()
            .Where(d => d.Id == deviceId && d.PrintStationId == stationId)
            .FirstAsync(cancellationToken);
        var status = PrintStationStatus.Calculate(station.LastSeenAt, Now, station.RevokedAt is not null,
            station.Enabled, [device.LastObservedState], _options.HeartbeatOnlineSeconds, _options.HeartbeatOfflineSeconds);
        if (status == PrintStationStatus.Offline || device.LastObservedState == PrintDeviceStates.Offline)
            return Refuse(OwnerPhotoPrintErrors.PrinterOffline);

        // 5. The paper it was composed for, still in, and one this printer prints.
        var loaded = PrintPapers.IsKnown(device.LoadedPaperSize) ? device.LoadedPaperSize : PrintPapers.Photo10x15;
        if (loaded != paper) return Refuse(OwnerPhotoPrintErrors.PaperChanged);
        if (!PrintCapabilityMatcher.SupportsFormat(device.CapabilitiesJson, paper))
            return Refuse(OwnerPhotoPrintErrors.FormatUnsupported);

        // 6. The framing: no further out than the whole photograph on the sheet.
        var (sheetW, sheetH) = PartyPrintGeometry.Sheet(paper, portrait);
        var shapes = await PrintPhotoShapes.DisplayAspectsAsync(_db, ownerUserId, [fileItemId], cancellationToken);
        if (shapes.TryGetValue(fileItemId, out var photoAspect)
            ? !PhotoPlacementGeometry.IsValid(photoAspect, (double)sheetW / sheetH, placement)
            : placement.Zoom < 1)
        {
            return Refuse(OwnerPhotoPrintErrors.InvalidPlacement);
        }

        // 7. The date, only when asked for: the owner's, the camera's, else today.
        var (date, dateSource) = request.IncludeDate
            ? OwnerPhotoPrintDates.Resolve(file.Override, file.Meta.DateTaken, Now, zone!)
            : (default(DateOnly?), OwnerPhotoPrintDates.SourceNone);
        var dateText = date is DateOnly d ? OwnerPhotoPrintDates.Format(d, request.DateLocale!) : null;

        // 8. On a lent printer, one sheet of the loan, taken with the proof it is live.
        var sheet = await _printers.TryTakeSheetAsync(use.ShareId, cancellationToken);
        if (sheet != PrinterSheetResult.Taken)
        {
            // The loan's last sheet may have gone to this very request's twin —
            // the same key, a moment earlier. Then the answer is its job.
            if (await RepeatAsync(ownerUserId, keyHash, fingerprint, cancellationToken) is { } twin) return twin;
            return Refuse(sheet == PrinterSheetResult.Revoked
                ? OwnerPhotoPrintErrors.ShareRevoked
                : OwnerPhotoPrintErrors.ShareExhausted);
        }

        var jobId = Guid.NewGuid();
        // A sheet that never became a job goes back — and only then: accepted
        // is the job's row existing, read from the database.
        async Task<bool> ReturnUnlessAcceptedAsync()
        {
            _db.ChangeTracker.Clear();
            if (await _db.PrintJobs.AsNoTracking().AnyAsync(j => j.Id == jobId, CancellationToken.None)) return false;
            await _printers.ReturnSheetAsync(use.ShareId, CancellationToken.None);
            return true;
        }

        try
        {
            // 9. The original, read inside the server.
            var bytes = await _sources.ReadAsync(ownerUserId, fileItemId, cancellationToken);
            if (bytes is null)
            {
                await ReturnUnlessAcceptedAsync();
                return Refuse(OwnerPhotoPrintErrors.InvalidSource);
            }

            // 10. Rendered at 300dpi from it.
            byte[] artifact;
            try
            {
                artifact = await _renderer.RenderOwnerPhotoAsync(new OwnerPhotoComposition(
                    bytes, paper, portrait, placement, dateText, PrintCalibration.Of(device)), cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await ReturnUnlessAcceptedAsync();
                _logger.LogWarning("print.owner_photo.render_failed station={StationId} device={DeviceId} ({ExceptionType})",
                    stationId, deviceId, ex.GetType().Name);
                return Refuse(OwnerPhotoPrintErrors.RenderFailed);
            }

            // 11. Published and recorded together: the artifact is owned only by
            // the job's storage key, so the bytes and the row become durable as one.
            await using var stream = new MemoryStream(artifact, writable: false);
            var staged = await _artifacts.StageAsync(stream, cancellationToken);
            await using var stagedScope = staged.ConfigureAwait(false);
            await StoragePublish.PublishOwnedAsync(_db, _artifacts, staged, async (stored, ct) =>
            {
                var now = Now;
                _db.PrintJobs.Add(new PrintJob
                {
                    Id = jobId,
                    OwnerUserId = ownerUserId,
                    PrintStationId = stationId,
                    PrinterDeviceId = deviceId,
                    FileItemId = fileItemId,
                    Kind = PrintJobKinds.OwnerPhoto,
                    Format = paper,
                    State = PrintJobStates.Ready,
                    // What this sheet IS, without the artifact — and nothing of the
                    // photograph's private metadata beyond the date it printed.
                    RenderSpecificationJson = JsonSerializer.Serialize(new
                    {
                        type = PrintJobKinds.OwnerPhoto,
                        version = 1,
                        paperSize = paper,
                        orientation = request.Orientation,
                        placement = new { centerX = placement.CenterX, centerY = placement.CenterY, zoom = placement.Zoom },
                        includeDate = request.IncludeDate,
                        resolvedDate = date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        dateSource,
                        dateLocale = request.IncludeDate ? request.DateLocale : null,
                        background = "white",
                    }),
                    ArtifactStorageKey = stored.StorageKey,
                    ArtifactContentType = "image/jpeg",
                    ArtifactByteLength = stored.SizeBytes,
                    CreatedAt = now,
                    RenderedAt = now,
                });
                var (cx, cy, cw, ch) = shapes.TryGetValue(fileItemId, out var aspect)
                    ? PhotoPlacementGeometry.LegacyCrop(PhotoPlacementGeometry.Place(aspect, (double)sheetW / sheetH, placement))
                    : (0, 0, 1, 1);
                _db.PrintJobSources.Add(new PrintJobSource
                {
                    Id = Guid.NewGuid(), PrintJobId = jobId, SlotIndex = 0, FileItemId = fileItemId,
                    CropX = cx, CropY = cy, CropWidth = cw, CropHeight = ch,
                    PlacementCenterX = placement.CenterX, PlacementCenterY = placement.CenterY,
                    PlacementZoom = placement.Zoom,
                });
                _db.OwnerPhotoPrintRequests.Add(new OwnerPhotoPrintRequest
                {
                    Id = Guid.NewGuid(), OwnerUserId = ownerUserId, IdempotencyKeyHash = keyHash,
                    RequestFingerprint = fingerprint, PrintJobId = jobId, CreatedAt = now,
                });
                await _db.SaveChangesAsync(ct);
            }, cancellationToken);

            _logger.LogInformation("print.owner_photo.accepted station={StationId} device={DeviceId} job={JobId}",
                stationId, deviceId, jobId);
            return OwnerPhotoPrintResult.Accept(await AcceptedAsync(jobId, stationId, device.MediaRemainingPrints,
                CancellationToken.None));
        }
        catch (DbUpdateException)
        {
            // Two requests with one key raced: the index kept one. The loser's
            // sheet goes back, and it is answered with the winner's job — or
            // refused, if the winner was a different composition.
            await ReturnUnlessAcceptedAsync();
            return await RepeatAsync(ownerUserId, keyHash, fingerprint, CancellationToken.None)
                ?? Refuse(OwnerPhotoPrintErrors.RenderFailed);
        }
        catch when (cancellationToken.IsCancellationRequested)
        {
            await ReturnUnlessAcceptedAsync();
            throw;
        }
        catch
        {
            if (!await ReturnUnlessAcceptedAsync()) throw;
            return Refuse(OwnerPhotoPrintErrors.RenderFailed);
        }

        OwnerPhotoPrintResult Refuse(string error)
        {
            _logger.LogInformation("print.owner_photo.refused code={Code}", error);
            return OwnerPhotoPrintResult.Refuse(error);
        }
    }

    public async Task<OwnerPhotoPrintDate?> ResolveDateAsync(
        Guid ownerUserId, Guid fileItemId, TimeZoneInfo zone, CancellationToken cancellationToken)
    {
        var file = await _db.FileItems.AsNoTracking()
            .Where(f => f.Id == fileItemId && f.OwnerUserId == ownerUserId && f.DeletedAt == null)
            .Select(f => new
            {
                Embedded = _db.BlobMetadata.Where(m => m.BlobObjectId == f.BlobObjectId)
                    .Select(m => m.DateTaken).FirstOrDefault(),
                Override = _db.FileItemUserMetadata.Where(u => u.FileItemId == f.Id)
                    .Select(u => u.DateTakenOverride).FirstOrDefault(),
            })
            .FirstOrDefaultAsync(cancellationToken);
        if (file is null) return null;
        var (date, source) = OwnerPhotoPrintDates.Resolve(file.Override, file.Embedded, Now, zone);
        return new OwnerPhotoPrintDate(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), source);
    }

    private async Task<OwnerPhotoPrintResult?> RepeatAsync(
        Guid ownerUserId, string keyHash, string fingerprint, CancellationToken cancellationToken)
    {
        var existing = await _db.OwnerPhotoPrintRequests.AsNoTracking()
            .Where(r => r.OwnerUserId == ownerUserId && r.IdempotencyKeyHash == keyHash)
            .Select(r => new { r.PrintJobId, r.RequestFingerprint })
            .FirstOrDefaultAsync(cancellationToken);
        if (existing is null) return null;
        if (existing.RequestFingerprint != fingerprint)
            return OwnerPhotoPrintResult.Refuse(OwnerPhotoPrintErrors.IdempotencyConflict);
        var job = await _db.PrintJobs.AsNoTracking()
            .Where(j => j.Id == existing.PrintJobId)
            .Select(j => new { j.PrintStationId, j.PrinterDeviceId })
            .FirstAsync(cancellationToken);
        var remaining = await _db.PrinterDevices.AsNoTracking()
            .Where(d => d.Id == job.PrinterDeviceId).Select(d => d.MediaRemainingPrints).FirstOrDefaultAsync(cancellationToken);
        return OwnerPhotoPrintResult.Accept(await AcceptedAsync(existing.PrintJobId, job.PrintStationId, remaining, cancellationToken));
    }

    private async Task<OwnerPhotoPrintAccepted> AcceptedAsync(
        Guid jobId, Guid stationId, int? mediaRemaining, CancellationToken cancellationToken)
    {
        var state = await _db.PrintJobs.AsNoTracking().Where(j => j.Id == jobId)
            .Select(j => j.State).FirstAsync(cancellationToken);
        // The queue a sheet waits in is the machine's, whoever sent the others.
        var ahead = await _db.PrintJobs.AsNoTracking()
            .Where(j => j.PrintStationId == stationId && j.Id != jobId && !PrintJobStates.Terminal.Contains(j.State)
                && j.CreatedAt <= _db.PrintJobs.Where(x => x.Id == jobId).Select(x => x.CreatedAt).First())
            .CountAsync(cancellationToken);
        return new OwnerPhotoPrintAccepted(jobId, jobId.ToString("N")[..8], state, ahead, mediaRemaining);
    }

    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    /// <summary>
    /// The composition a key was used for, as a digest: every choice that
    /// changes the sheet. A key reused for another composition is refused.
    /// </summary>
    internal static string Fingerprint(
        Guid fileItemId, Guid stationId, Guid deviceId, string paper, string orientation,
        PhotoPlacement placement, bool includeDate, string? dateLocale, string? zone)
    {
        static string N(double v) => Math.Round(v, 6).ToString("R", CultureInfo.InvariantCulture);
        var canonical = string.Join('|',
            fileItemId.ToString("N"), stationId.ToString("N"), deviceId.ToString("N"), paper, orientation,
            N(placement.CenterX), N(placement.CenterY), N(placement.Zoom),
            includeDate ? "date" : "nodate", dateLocale ?? "", zone ?? "");
        return Hash(canonical);
    }
}
