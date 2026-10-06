using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Print;
using NubArca.Api.MediaLibrary;
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
    private readonly PartyPrintComposer _composer;
    private readonly IDerivedBlobStorage _artifacts;
    private readonly TimeProvider _clock;
    private readonly PrintOptions _options;
    private readonly ILogger<OwnerPhotoPrintService> _logger;

    public OwnerPhotoPrintService(
        AppDbContext db, IPrinterAccess printers, IPrintPhotoSourceReader sources,
        PrintArtifactRenderer renderer, PartyPrintComposer composer, IDerivedBlobStorage artifacts, TimeProvider clock,
        IOptions<PrintOptions> options, ILogger<OwnerPhotoPrintService> logger)
    {
        _db = db;
        _printers = printers;
        _sources = sources;
        _renderer = renderer;
        _composer = composer;
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
            || request.PrintStationId is not Guid stationId
            || request.PrinterDeviceId is not Guid deviceId
            || !PrintPapers.IsKnown(request.ExpectedPaperSize))
        {
            return Refuse(OwnerPhotoPrintErrors.InvalidRequest);
        }
        var paper = request.ExpectedPaperSize!;

        // The format and its style, from the catalogue every print shares. One
        // photograph is to the edges unless framed; four and the strips are framed.
        var layout = request.Layout ?? PrintLayouts.Photo;
        if (!PrintLayouts.IsKnown(layout)) return Refuse(OwnerPhotoPrintErrors.InvalidLayout);
        var style = request.Style ?? (layout == PrintLayouts.Photo ? PrintLayouts.FullBleed : PrintLayouts.Framed);
        if (!PrintLayouts.SupportsStyle(layout, style)) return Refuse(OwnerPhotoPrintErrors.InvalidLayout);

        // The photographs, in the order they were arranged: as many as the format
        // takes — a twin strip four (the same strip twice) or eight — and each a
        // different one.
        var requested = request.Photos
            ?? (request.FileItemId is Guid single ? [new OwnerPrintPhotoRequest(single, request.Placement)] : null);
        if (requested is null) return Refuse(OwnerPhotoPrintErrors.InvalidRequest);
        if (!PrintLayouts.PhotoCounts(layout).Contains(requested.Count)) return Refuse(OwnerPhotoPrintErrors.InvalidLayout);
        if (requested.Any(p => p.FileItemId is null)) return Refuse(OwnerPhotoPrintErrors.InvalidRequest);
        var fileIds = requested.Select(p => p.FileItemId!.Value).ToList();
        if (fileIds.Distinct().Count() != fileIds.Count) return Refuse(OwnerPhotoPrintErrors.InvalidLayout);
        var placements = new List<PhotoPlacement>(requested.Count);
        foreach (var photo in requested)
        {
            if (photo.Placement is not { } framing || !PhotoPlacementGeometry.IsStructurallyValid(framing.ToPlacement()))
                return Refuse(OwnerPhotoPrintErrors.InvalidPlacement);
            placements.Add(framing.ToPlacement());
        }

        // Which way a single photograph's sheet stands is the person's choice;
        // four and the strips stand as the catalogue sets them.
        if (layout == PrintLayouts.Photo
            ? request.Orientation is not ("portrait" or "landscape")
            : request.Orientation is not (null or "portrait" or "landscape"))
        {
            return Refuse(OwnerPhotoPrintErrors.InvalidOrientation);
        }
        var portrait = PrintLayouts.SheetPortrait(layout, paper, request.Orientation != "landscape");

        // Words only where there is a band for them.
        if (!OwnerPrintText.TryNormaliseCaption(request.Caption, out var caption))
            return Refuse(OwnerPhotoPrintErrors.InvalidCaption);
        if (style == PrintLayouts.FullBleed && (caption is not null || request.Brand))
            return Refuse(OwnerPhotoPrintErrors.InvalidRequest);

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
        var fingerprint = layout == PrintLayouts.Photo && style == PrintLayouts.FullBleed
            // Today's one-photograph print keeps the very digest it always had.
            ? Fingerprint(fileIds[0], stationId, deviceId, paper, request.Orientation!, placements[0],
                request.IncludeDate, request.IncludeDate ? request.DateLocale : null,
                request.IncludeDate ? zone!.Id : null)
            : SheetFingerprint(layout, style, fileIds, placements, stationId, deviceId, paper, portrait,
                caption, request.Brand, request.IncludeDate, request.IncludeDate ? request.DateLocale : null,
                request.IncludeDate ? zone!.Id : null);
        if (await RepeatAsync(ownerUserId, keyHash, fingerprint, cancellationToken) is { } repeat) return repeat;

        // 3. The photographs: the caller's own, in their library, and photographs
        // by the Library's own rule — a legacy file with no metadata row
        // included. A file that is missing, someone else's, trashed or excluded
        // is one answer, so this is no oracle for file ids.
        var files = await _db.FileItems.AsNoTracking()
            .Where(f => fileIds.Contains(f.Id) && f.OwnerUserId == ownerUserId && f.DeletedAt == null
                && f.MediaLibraryState == MediaLibraryState.Active)
            .Select(f => new
            {
                f.Id,
                Embedded = _db.BlobMetadata.Where(m => m.BlobObjectId == f.BlobObjectId)
                    .Select(m => m.DateTaken).FirstOrDefault(),
                Override = _db.FileItemUserMetadata.Where(u => u.FileItemId == f.Id)
                    .Select(u => u.DateTakenOverride).FirstOrDefault(),
            })
            .ToDictionaryAsync(f => f.Id, cancellationToken);
        if (files.Count != fileIds.Count) return Refuse(OwnerPhotoPrintErrors.NotFound);
        var photographs = await _db.FileItems.AsNoTracking().Where(f => fileIds.Contains(f.Id))
            .Where(LibraryPhotoRule.IsPhoto(_db)).CountAsync(cancellationToken);
        if (photographs != fileIds.Count) return Refuse(OwnerPhotoPrintErrors.NotImage);

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

        // 5. The paper it was composed for, still in, one this printer prints —
        // and for the twin strip, a printer that cuts it in two.
        var loaded = PrintPapers.IsKnown(device.LoadedPaperSize) ? device.LoadedPaperSize : PrintPapers.Photo10x15;
        if (loaded != paper) return Refuse(OwnerPhotoPrintErrors.PaperChanged);
        var printFormat = layout == PrintLayouts.TwinStrip4 ? PrintFormats.Strip2x6Pair : paper;
        if (!PrintLayouts.Allowed(paper, layout)
            || !PrintCapabilityMatcher.SupportsFormat(device.CapabilitiesJson, paper)
            || !PrintCapabilityMatcher.SupportsFormat(device.CapabilitiesJson, printFormat))
        {
            return Refuse(OwnerPhotoPrintErrors.FormatUnsupported);
        }

        // 6. The framing: no further out than the whole photograph in its slot.
        // The shape comes from the stored dimensions; a legacy file that has
        // none is checked against its own decoded pixels once they are read.
        var (sheetW, sheetH) = PrintLayouts.Sheet(paper, portrait);
        var frameAspect = PrintLayouts.SlotAspect(layout, style, paper, portrait);
        var shapes = await PrintPhotoShapes.DisplayAspectsAsync(_db, ownerUserId, fileIds, cancellationToken);
        for (var i = 0; i < fileIds.Count; i++)
        {
            if (shapes.TryGetValue(fileIds[i], out var aspect)
                && !PhotoPlacementGeometry.IsValid(aspect, frameAspect, placements[i]))
            {
                return Refuse(OwnerPhotoPrintErrors.InvalidPlacement);
            }
        }

        // 7. The date, only when asked for: the first photograph's — the owner's
        // own, the camera's, else today.
        var first = files[fileIds[0]];
        var (date, dateSource) = request.IncludeDate
            ? OwnerPhotoPrintDates.Resolve(first.Override, first.Embedded, Now, zone!)
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
            // 9. The originals, read inside the server.
            // Held until the sheet is drawn: a HEIC frame keeps its decode slot.
            await using var sources = await _sources.OpenAsync(ownerUserId, fileIds, cancellationToken);
            if (sources is null || sources.Photos.Any(p => p is null))
            {
                await ReturnUnlessAcceptedAsync();
                return Refuse(OwnerPhotoPrintErrors.InvalidSource);
            }
            for (var i = 0; i < fileIds.Count; i++)
            {
                if (shapes.ContainsKey(fileIds[i]) || placements[i].Zoom >= 1) continue;
                // No stored shape: the framing is held to the photograph as it
                // decodes, turned by its EXIF orientation like the print is.
                var decoded = DisplayAspectOf(sources.Photos[i]!);
                if (decoded is null)
                {
                    await ReturnUnlessAcceptedAsync();
                    return Refuse(OwnerPhotoPrintErrors.InvalidSource);
                }
                if (!PhotoPlacementGeometry.IsValid(decoded.Value, frameAspect, placements[i]))
                {
                    await ReturnUnlessAcceptedAsync();
                    return Refuse(OwnerPhotoPrintErrors.InvalidPlacement);
                }
            }

            // 10. Rendered at 300dpi from them: Piena by the owner's own renderer,
            // a framed sheet by the composer every framed print is drawn with,
            // in white, with the owner's words in its band.
            byte[] artifact;
            try
            {
                artifact = style == PrintLayouts.FullBleed
                    ? await _renderer.RenderOwnerPhotoAsync(new OwnerPhotoComposition(
                        sources.Photos[0]!, paper, portrait, placements[0], dateText, PrintCalibration.Of(device)),
                        cancellationToken)
                    : await _composer.RenderAsync(new PartyPrintComposition(
                        layout,
                        PartyPrintTheme.Pure,
                        [.. sources.Photos.Select((bytes, i) => PartyPrintPhoto.Placed(bytes!, placements[i]))],
                        PartyName: string.Empty,
                        FooterText: null,
                        Orientation: portrait ? PartyPrintOrientation.Portrait : PartyPrintOrientation.Landscape,
                        Calibration: PrintCalibration.Of(device),
                        Paper: paper,
                        Words: new PrintSheetWords(caption, null, dateText, request.Brand)),
                        cancellationToken);
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
            var kind = layout switch
            {
                PrintLayouts.Grid4 => PrintJobKinds.OwnerGrid4,
                PrintLayouts.TwinStrip4 => PrintJobKinds.OwnerStrip4,
                _ => PrintJobKinds.OwnerPhoto,
            };
            await StoragePublish.PublishOwnedAsync(_db, _artifacts, staged, async (stored, ct) =>
            {
                var now = Now;
                _db.PrintJobs.Add(new PrintJob
                {
                    Id = jobId,
                    OwnerUserId = ownerUserId,
                    PrintStationId = stationId,
                    PrinterDeviceId = deviceId,
                    FileItemId = fileIds[0],
                    Kind = kind,
                    Format = printFormat,
                    State = PrintJobStates.Ready,
                    // What this sheet IS, without the artifact — and nothing of the
                    // photographs' private metadata beyond the date it printed.
                    RenderSpecificationJson = JsonSerializer.Serialize(new
                    {
                        type = kind,
                        version = 2,
                        paperSize = paper,
                        layout,
                        style,
                        orientation = portrait ? "portrait" : "landscape",
                        placements = placements.Select(p => new { centerX = p.CenterX, centerY = p.CenterY, zoom = p.Zoom }),
                        caption,
                        brand = request.Brand,
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
                for (var i = 0; i < fileIds.Count; i++)
                {
                    var (cx, cy, cw, ch) = shapes.TryGetValue(fileIds[i], out var aspect)
                        ? PhotoPlacementGeometry.LegacyCrop(PhotoPlacementGeometry.Place(aspect, frameAspect, placements[i]))
                        : (0, 0, 1, 1);
                    _db.PrintJobSources.Add(new PrintJobSource
                    {
                        Id = Guid.NewGuid(), PrintJobId = jobId, SlotIndex = i, FileItemId = fileIds[i],
                        CropX = cx, CropY = cy, CropWidth = cw, CropHeight = ch,
                        PlacementCenterX = placements[i].CenterX, PlacementCenterY = placements[i].CenterY,
                        PlacementZoom = placements[i].Zoom,
                    });
                }
                _db.OwnerPhotoPrintRequests.Add(new OwnerPhotoPrintRequest
                {
                    Id = Guid.NewGuid(), OwnerUserId = ownerUserId, IdempotencyKeyHash = keyHash,
                    RequestFingerprint = fingerprint, PrintJobId = jobId, CreatedAt = now,
                });
                await _db.SaveChangesAsync(ct);
            }, cancellationToken);

            _logger.LogInformation("print.owner_photo.accepted station={StationId} device={DeviceId} job={JobId} layout={Layout}",
                stationId, deviceId, jobId, layout);
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

    private Task<OwnerPhotoPrintResult?> RepeatAsync(
        Guid ownerUserId, string keyHash, string fingerprint, CancellationToken cancellationToken) =>
        OwnerPrintRecords.RepeatAsync(_db, ownerUserId, keyHash, fingerprint, cancellationToken);

    private Task<OwnerPhotoPrintAccepted> AcceptedAsync(
        Guid jobId, Guid stationId, int? mediaRemaining, CancellationToken cancellationToken) =>
        OwnerPrintRecords.AcceptedAsync(_db, jobId, stationId, mediaRemaining, cancellationToken);

    /// <summary>The display shape of encoded image bytes, from their header and EXIF orientation; null when they are not an image.</summary>
    internal static double? DisplayAspectOf(byte[] bytes)
    {
        try
        {
            var info = SixLabors.ImageSharp.Image.Identify(bytes);
            int? orientation = info.Metadata.ExifProfile is { } exif
                && exif.TryGetValue(SixLabors.ImageSharp.Metadata.Profiles.Exif.ExifTag.Orientation, out var value)
                    ? value.Value
                    : null;
            var (width, height) = Metadata.ImageDisplayDimensions.Resolve(info.Width, info.Height, orientation);
            return width is > 0 && height is > 0 ? (double)width.Value / height.Value : null;
        }
        catch (Exception ex) when (ex is SixLabors.ImageSharp.UnknownImageFormatException
            or SixLabors.ImageSharp.InvalidImageContentException)
        {
            return null;
        }
    }

    private static string Hash(string value) => OwnerPrintRecords.Hash(value);

    /// <summary>
    /// The digest of any other composition — a format, a style, each
    /// photograph with its framing in its place, the words: every choice that
    /// changes the sheet, so a key reused for another one is refused.
    /// </summary>
    internal static string SheetFingerprint(
        string layout, string style, IReadOnlyList<Guid> fileIds, IReadOnlyList<PhotoPlacement> placements,
        Guid stationId, Guid deviceId, string paper, bool portrait, string? caption, bool brand,
        bool includeDate, string? dateLocale, string? zone)
    {
        static string N(double v) => Math.Round(v, 6).ToString("R", CultureInfo.InvariantCulture);
        var photos = fileIds.Select((id, i) =>
            $"{id:N}:{N(placements[i].CenterX)}:{N(placements[i].CenterY)}:{N(placements[i].Zoom)}");
        var canonical = string.Join('|',
            "sheet", layout, style, string.Join(',', photos), stationId.ToString("N"), deviceId.ToString("N"),
            paper, portrait ? "portrait" : "landscape", caption ?? "", brand ? "brand" : "nobrand",
            includeDate ? "date" : "nodate", dateLocale ?? "", zone ?? "");
        return Hash(canonical);
    }

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
