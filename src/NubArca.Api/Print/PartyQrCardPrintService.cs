using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NubArca.Api.Auth.Recovery;
using NubArca.Api.Data;
using NubArca.Api.Domain.Print;
using NubArca.Api.MediaLibrary;
using NubArca.Api.Party;
using NubArca.Api.Storage;

namespace NubArca.Api.Print;

public interface IPartyQrCardPrintService
{
    /// <summary>One sheet — two cards — of the party's QR, on the party's printer.</summary>
    Task<OwnerPhotoPrintResult> SubmitAsync(
        Guid ownerUserId, Guid albumId, PartyQrCardPrintRequest request, string? idempotencyKey,
        CancellationToken cancellationToken);
}

/// <summary>
/// The host prints the party's QR for its tables: one of their photographs over
/// the code, twice on a 10x15 that the party's printer cuts into two cards.
///
/// The host's own print, not a guest's: no budget, no number, no print token.
/// What it shares with every print is what keeps printing honest — who may use
/// the printer is <see cref="IPrinterAccess"/>'s answer, a loan's sheet is taken
/// atomically and given back on any failure, the photograph is read inside the
/// server, the printer's calibration is applied, and a key makes a retried
/// request one sheet, not two. Several sheets are several requests, each with
/// its own key.
///
/// The code is the party's public address — the one on its share card — built
/// on the installation's configured public origin, never on whatever address the
/// host happens to be using: a sheet printed from the LAN must still open on a
/// guest's phone. It is derived here, drawn on the sheet, and kept nowhere else:
/// not in the job's specification, not in a log, not in a response.
/// </summary>
public sealed class PartyQrCardPrintService : IPartyQrCardPrintService
{
    private readonly AppDbContext _db;
    private readonly IPartyLinkService _links;
    private readonly IPrinterAccess _printers;
    private readonly IPrintPhotoSourceReader _sources;
    private readonly PartyPrintComposer _composer;
    private readonly IDerivedBlobStorage _artifacts;
    private readonly IOptionsMonitor<MailOptions> _mail;
    private readonly TimeProvider _clock;
    private readonly PrintOptions _options;
    private readonly ILogger<PartyQrCardPrintService> _logger;

    public PartyQrCardPrintService(
        AppDbContext db, IPartyLinkService links, IPrinterAccess printers, IPrintPhotoSourceReader sources,
        PartyPrintComposer composer, IDerivedBlobStorage artifacts, IOptionsMonitor<MailOptions> mail,
        TimeProvider clock, IOptions<PrintOptions> options, ILogger<PartyQrCardPrintService> logger)
    {
        _db = db;
        _links = links;
        _printers = printers;
        _sources = sources;
        _composer = composer;
        _artifacts = artifacts;
        _mail = mail;
        _clock = clock;
        _options = options.Value;
        _logger = logger;
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    public async Task<OwnerPhotoPrintResult> SubmitAsync(
        Guid ownerUserId, Guid albumId, PartyQrCardPrintRequest request, string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        // 1. The request's shape, before anything is read.
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 200
            || request.FileItemId is not Guid fileItemId
            || !PartyQrCardText.IsKnownLocale(request.Locale))
        {
            return Refuse(OwnerPhotoPrintErrors.InvalidRequest);
        }
        if (request.Placement is not { } requestedPlacement
            || !PhotoPlacementGeometry.IsStructurallyValid(requestedPlacement.ToPlacement()))
        {
            return Refuse(OwnerPhotoPrintErrors.InvalidPlacement);
        }
        var placement = requestedPlacement.ToPlacement();
        var locale = request.Locale!;

        // 2. The party: the caller's own, and open to guests — a closed party
        // has no address to print. A foreign or missing album is one answer.
        var status = await _links.GetOwnerStatusAsync(ownerUserId, albumId, cancellationToken);
        if (status is null) return Refuse(OwnerPhotoPrintErrors.NotFound);
        if (status.PartyUrl is not { } partyPath) return Refuse(PartyQrCardErrors.PartyClosed);
        if (PartyLinkPreview.Origin(_mail.CurrentValue.PublicOrigin) is not { } origin)
            return Refuse(PartyQrCardErrors.OriginUnavailable);

        // 3. Its printer, as the host chose it in the party's print settings.
        var profile = await _db.PartyPrintProfiles.AsNoTracking()
            .Where(p => p.PartyAlbumId == albumId && p.OwnerUserId == ownerUserId)
            .Select(p => new { p.PrintStationId, p.PrinterDeviceId })
            .FirstOrDefaultAsync(cancellationToken);
        if (profile?.PrintStationId is not Guid stationId || profile.PrinterDeviceId is not Guid deviceId)
            return Refuse(PartyQrCardErrors.NoPrinter);

        // 4. A repeat is answered from its record — the same card with the same
        // job, a different one under the same key refused. The address is not
        // part of it: it is the party's, whatever the request says.
        var keyHash = OwnerPrintRecords.Hash(idempotencyKey);
        var fingerprint = Fingerprint(albumId, fileItemId, stationId, deviceId, placement, locale);
        if (await OwnerPrintRecords.RepeatAsync(_db, ownerUserId, keyHash, fingerprint, cancellationToken) is { } repeat)
            return repeat;

        // 5. The photograph: the host's own and a photograph by the Library's
        // rule. Not necessarily in the library view — a party's photographs
        // include ones the host keeps out of it — but never one in the trash.
        if (!await _db.FileItems.AsNoTracking()
                .Where(f => f.Id == fileItemId && f.OwnerUserId == ownerUserId && f.DeletedAt == null)
                .AnyAsync(cancellationToken))
        {
            return Refuse(OwnerPhotoPrintErrors.NotFound);
        }
        if (!await _db.FileItems.AsNoTracking().Where(f => f.Id == fileItemId)
                .Where(LibraryPhotoRule.IsPhoto(_db)).AnyAsync(cancellationToken))
        {
            return Refuse(OwnerPhotoPrintErrors.NotImage);
        }

        // 6. The printer, by the one rule every print path asks: still the
        // host's or still lent to them, online, with a 10x15 in and its cut.
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
        var online = PrintStationStatus.Calculate(station.LastSeenAt, Now, station.RevokedAt is not null,
            station.Enabled, [device.LastObservedState], _options.HeartbeatOnlineSeconds, _options.HeartbeatOfflineSeconds);
        if (online == PrintStationStatus.Offline || device.LastObservedState == PrintDeviceStates.Offline)
            return Refuse(OwnerPhotoPrintErrors.PrinterOffline);
        var loaded = PrintPapers.IsKnown(device.LoadedPaperSize) ? device.LoadedPaperSize : PrintPapers.Photo10x15;
        if (loaded != PrintPapers.Photo10x15
            || !PrintCapabilityMatcher.SupportsFormat(device.CapabilitiesJson, PrintFormats.Strip2x6Pair))
        {
            return Refuse(OwnerPhotoPrintErrors.FormatUnsupported);
        }

        // 7. The framing: no further out than the whole photograph in its cell.
        var cellAspect = PartyPrintGeometry.QrCardPhotoAspect();
        var shapes = await PrintPhotoShapes.DisplayAspectsAsync(_db, ownerUserId, [fileItemId], cancellationToken);
        var shapeKnown = shapes.TryGetValue(fileItemId, out var photoAspect);
        if (shapeKnown && !PhotoPlacementGeometry.IsValid(photoAspect, cellAspect, placement))
            return Refuse(OwnerPhotoPrintErrors.InvalidPlacement);

        var partyName = await _db.Albums.AsNoTracking()
            .Where(a => a.Id == albumId).Select(a => a.Name).FirstOrDefaultAsync(cancellationToken) ?? string.Empty;

        // 8. On a lent printer, one sheet of the loan, taken with the proof it is live.
        var sheet = await _printers.TryTakeSheetAsync(use.ShareId, cancellationToken);
        if (sheet != PrinterSheetResult.Taken)
        {
            // The loan's last sheet may have gone to this very request's twin.
            if (await OwnerPrintRecords.RepeatAsync(_db, ownerUserId, keyHash, fingerprint, cancellationToken) is { } twin)
                return twin;
            return Refuse(sheet == PrinterSheetResult.Revoked
                ? OwnerPhotoPrintErrors.ShareRevoked
                : OwnerPhotoPrintErrors.ShareExhausted);
        }

        var jobId = Guid.NewGuid();
        // A sheet that never became a job goes back — and only then.
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
            if (!shapeKnown && placement.Zoom < 1)
            {
                var decoded = OwnerPhotoPrintService.DisplayAspectOf(bytes);
                if (decoded is null || !PhotoPlacementGeometry.IsValid(decoded.Value, cellAspect, placement))
                {
                    await ReturnUnlessAcceptedAsync();
                    return Refuse(decoded is null ? OwnerPhotoPrintErrors.InvalidSource : OwnerPhotoPrintErrors.InvalidPlacement);
                }
            }

            // 10. Rendered at 300dpi, the code carrying the party's address.
            byte[] artifact;
            try
            {
                artifact = await _composer.RenderQrCardAsync(new PartyQrCardComposition(
                    bytes, placement, origin + partyPath, partyName, PartyQrCardText.Line(locale),
                    PrintCalibration.Of(device)), cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await ReturnUnlessAcceptedAsync();
                _logger.LogWarning("print.party_qr.render_failed station={StationId} device={DeviceId} ({ExceptionType})",
                    stationId, deviceId, ex.GetType().Name);
                return Refuse(OwnerPhotoPrintErrors.RenderFailed);
            }

            // 11. Published and recorded together.
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
                    Kind = PrintJobKinds.PartyQrCard,
                    Format = PrintFormats.Strip2x6Pair,
                    State = PrintJobStates.Ready,
                    // What this sheet IS, without the artifact — and without the
                    // address its code carries.
                    RenderSpecificationJson = JsonSerializer.Serialize(new
                    {
                        type = PrintJobKinds.PartyQrCard,
                        version = 1,
                        albumId,
                        placement = new { centerX = placement.CenterX, centerY = placement.CenterY, zoom = placement.Zoom },
                        locale,
                    }),
                    ArtifactStorageKey = stored.StorageKey,
                    ArtifactContentType = "image/jpeg",
                    ArtifactByteLength = stored.SizeBytes,
                    CreatedAt = now,
                    RenderedAt = now,
                });
                var (cx, cy, cw, ch) = shapeKnown
                    ? PhotoPlacementGeometry.LegacyCrop(PhotoPlacementGeometry.Place(photoAspect, cellAspect, placement))
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

            _logger.LogInformation("print.party_qr.accepted station={StationId} device={DeviceId} job={JobId}",
                stationId, deviceId, jobId);
            return OwnerPhotoPrintResult.Accept(await OwnerPrintRecords.AcceptedAsync(
                _db, jobId, stationId, device.MediaRemainingPrints, CancellationToken.None));
        }
        catch (DbUpdateException)
        {
            // Two requests with one key raced: the index kept one.
            await ReturnUnlessAcceptedAsync();
            return await OwnerPrintRecords.RepeatAsync(_db, ownerUserId, keyHash, fingerprint, CancellationToken.None)
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
            _logger.LogInformation("print.party_qr.refused code={Code}", error);
            return OwnerPhotoPrintResult.Refuse(error);
        }
    }

    /// <summary>
    /// The card a key was used for, as a digest. Its kind leads, so a key once
    /// spent on a direct print can never be answered as a card, or the reverse.
    /// </summary>
    internal static string Fingerprint(
        Guid albumId, Guid fileItemId, Guid stationId, Guid deviceId, PhotoPlacement placement, string locale)
    {
        static string N(double v) => Math.Round(v, 6).ToString("R", CultureInfo.InvariantCulture);
        return OwnerPrintRecords.Hash(string.Join('|',
            PrintJobKinds.PartyQrCard, albumId.ToString("N"), fileItemId.ToString("N"),
            stationId.ToString("N"), deviceId.ToString("N"),
            N(placement.CenterX), N(placement.CenterY), N(placement.Zoom), locale));
    }
}
