using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NubArca.Api.Data;
using NubArca.Api.Domain.Print;
using NubArca.Api.Party;
using NubArca.Api.Print;

namespace NubArca.Api.Albums.Sharing;

/// <summary>
/// PRINTING FROM A SHARE, through the owner's own print and nothing else.
///
/// <para>There is no second pipeline. A verified visitor's print is the
/// owner's album print (<see cref="IOwnerPhotoPrintService"/>), asked on the
/// owner's behalf: <c>share → album → owner → printer</c>. Everything that is
/// authority comes from the resolved grant — the album, its owner — and the
/// printer is the owner's, chosen here. The browser names photographs of the
/// album, the format and the framing, exactly as the owner's studio does, and
/// never an owner, a station or a printer: a request that carries them is
/// read without them.</para>
///
/// <para>Every method takes the grant and refuses unless
/// <see cref="AlbumShareAccess.CanPrint"/>, so no caller can forget the rule.</para>
/// </summary>
public interface IAlbumSharePrintService
{
    /// <summary>What the visitor's studio needs to offer the owner's printer — and nothing that identifies it.</summary>
    Task<AlbumSharePrintDto?> GetAsync(AlbumShareAccess access, CancellationToken cancellationToken);

    /// <summary>The date a print of this album photograph would carry, or null.</summary>
    Task<OwnerPhotoPrintDate?> ResolveDateAsync(
        AlbumShareAccess access, Guid fileItemId, TimeZoneInfo zone, CancellationToken cancellationToken);

    Task<OwnerPhotoPrintResult> SubmitAsync(
        AlbumShareAccess access, AlbumSharePrintSubmitRequest request, string? idempotencyKey,
        CancellationToken cancellationToken);
}

/// <summary>
/// What a visitor asks to print: the owner's print request without any of its
/// authority. No owner, no station, no printer — those are the server's.
/// </summary>
public sealed record AlbumSharePrintSubmitRequest(
    string? ExpectedPaperSize,
    string? Orientation,
    string? Layout = null,
    string? Style = null,
    IReadOnlyList<OwnerPrintPhotoRequest>? Photos = null,
    string? Caption = null,
    bool Brand = false,
    bool IncludeDate = false,
    string? DateLocale = null,
    string? TimeZone = null);

/// <summary>
/// The owner's printer as a visitor's studio sees it: whether it can print now,
/// the paper that is in, and whether it cuts the twin strip — and which cut
/// sheets it makes. No name, no id.
/// </summary>
public sealed record AlbumSharePrintDto(bool Available, string Paper, bool CutsStrips,
    IReadOnlyList<string>? CutFormats = null);

/// <summary>An accepted print, as a visitor sees it: its short code and its place in the queue.</summary>
public sealed record AlbumSharePrintAccepted(string ShortCode, string State, int QueueAhead);

public static class AlbumSharePrintErrors
{
    /// <summary>The grant does not allow printing (AlbumShareAccess.CanPrint).</summary>
    public const string NotAllowed = "print_not_allowed";
}

/// <summary>The owner's printer a share prints on, as resolved now.</summary>
public sealed record AlbumSharePrinter(Guid StationId, Guid DeviceId, string Paper, bool CutsStrips, bool Online,
    IReadOnlyList<string> CutFormats);

public sealed class AlbumSharePrintService : IAlbumSharePrintService
{
    private readonly AppDbContext _db;
    private readonly IPartyMediaService _media;
    private readonly IOwnerPhotoPrintService _prints;
    private readonly TimeProvider _clock;
    private readonly PrintOptions _options;

    public AlbumSharePrintService(
        AppDbContext db, IPartyMediaService media, IOwnerPhotoPrintService prints,
        TimeProvider clock, IOptions<PrintOptions> options)
    {
        _db = db;
        _media = media;
        _prints = prints;
        _clock = clock;
        _options = options.Value;
    }

    public async Task<AlbumSharePrintDto?> GetAsync(AlbumShareAccess access, CancellationToken cancellationToken)
    {
        if (!access.CanPrint) return null;
        var printer = await ResolvePrinterAsync(access.OwnerUserId, cancellationToken);
        return printer is null
            ? new AlbumSharePrintDto(false, PrintPapers.Photo10x15, false)
            : new AlbumSharePrintDto(printer.Online, printer.Paper, printer.CutsStrips, printer.CutFormats);
    }

    public async Task<OwnerPhotoPrintDate?> ResolveDateAsync(
        AlbumShareAccess access, Guid fileItemId, TimeZoneInfo zone, CancellationToken cancellationToken)
    {
        if (!access.CanPrint) return null;
        if (await _media.GetVisibleMediaKindAsync(access.OwnerUserId, access.AlbumId, fileItemId, cancellationToken)
            is not PartyMediaKind.Image)
        {
            return null;
        }
        return await _prints.ResolveDateAsync(access.OwnerUserId, fileItemId, zone, cancellationToken);
    }

    public async Task<OwnerPhotoPrintResult> SubmitAsync(
        AlbumShareAccess access, AlbumSharePrintSubmitRequest request, string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (!access.CanPrint) return OwnerPhotoPrintResult.Refuse(AlbumSharePrintErrors.NotAllowed);
        if (request.Photos is not { Count: > 0 } photos || photos.Count > PrintLayouts.MostPhotos || photos.Any(p => p.FileItemId is null))
            return OwnerPhotoPrintResult.Refuse(OwnerPhotoPrintErrors.InvalidRequest);

        // The photographs must be THIS album's, visible to its share — the
        // owner's print asks only that they are the owner's, and a share is
        // narrower than the owner's library. Missing, foreign and not in the
        // album are one answer.
        foreach (var photo in photos)
        {
            var kind = await _media.GetVisibleMediaKindAsync(
                access.OwnerUserId, access.AlbumId, photo.FileItemId!.Value, cancellationToken);
            if (kind is null) return OwnerPhotoPrintResult.Refuse(OwnerPhotoPrintErrors.NotFound);
            if (kind != PartyMediaKind.Image) return OwnerPhotoPrintResult.Refuse(OwnerPhotoPrintErrors.NotImage);
        }

        // share → album → owner → printer. A RETRY keeps its first printer: the
        // choice below can change between two requests (one goes offline,
        // another comes online), and a key whose job already exists must be
        // answered with that job — re-choosing would make the owner print's
        // fingerprint differ and turn a lost response into idempotency_conflict.
        var printer = await PrinterOfKeyAsync(access.OwnerUserId, idempotencyKey, cancellationToken)
            ?? await ResolvePrinterAsync(access.OwnerUserId, cancellationToken);
        if (printer is null) return OwnerPhotoPrintResult.Refuse(OwnerPhotoPrintErrors.PrinterUnavailable);

        return await _prints.SubmitAsync(
            access.OwnerUserId,
            new OwnerPhotoPrintSubmitRequest(
                FileItemId: null,
                PrintStationId: printer.StationId,
                PrinterDeviceId: printer.DeviceId,
                ExpectedPaperSize: request.ExpectedPaperSize,
                Orientation: request.Orientation,
                Placement: null,
                IncludeDate: request.IncludeDate,
                DateLocale: request.DateLocale,
                TimeZone: request.TimeZone,
                Layout: request.Layout,
                Style: request.Style,
                Photos: photos,
                Caption: request.Caption,
                Brand: request.Brand),
            idempotencyKey, cancellationToken, new OwnerPrintOrigin(access.AlbumId));
    }

    /// <summary>
    /// The printer the job already made under this key went to, or null when
    /// the key has made none. Read from the owner print's own idempotency
    /// record, so the destination is kept exactly as long as the key is.
    /// </summary>
    private async Task<AlbumSharePrinter?> PrinterOfKeyAsync(
        Guid ownerUserId, string? idempotencyKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 200) return null;
        var keyHash = OwnerPrintRecords.Hash(idempotencyKey);
        var job = await _db.OwnerPhotoPrintRequests.AsNoTracking()
            .Where(r => r.OwnerUserId == ownerUserId && r.IdempotencyKeyHash == keyHash)
            .Join(_db.PrintJobs, r => r.PrintJobId, j => j.Id, (_, j) => new { j.PrintStationId, j.PrinterDeviceId })
            .FirstOrDefaultAsync(cancellationToken);
        // Only the destination matters here: the owner print answers the repeat
        // from its record (or refuses a different composition under the key).
        return job is null ? null : new AlbumSharePrinter(job.PrintStationId, job.PrinterDeviceId, string.Empty, false, true, []);
    }

    /// <summary>
    /// The owner's printer for a share: one of the owner's OWN printers — never
    /// one lent to them, which is somebody else's paper — on a live, enabled
    /// station, with paper in that it prints. An online one first; among equals
    /// the oldest station, so the answer is the same from one request to the next.
    /// </summary>
    internal async Task<AlbumSharePrinter?> ResolvePrinterAsync(Guid ownerUserId, CancellationToken cancellationToken)
    {
        var stations = await _db.PrintStations.AsNoTracking()
            .Where(s => s.OwnerUserId == ownerUserId && s.RevokedAt == null && s.Enabled)
            .Select(s => new { s.Id, s.LastSeenAt, s.CreatedAt })
            .ToListAsync(cancellationToken);
        if (stations.Count == 0) return null;
        var stationIds = stations.Select(s => s.Id).ToArray();
        var devices = await _db.PrinterDevices.AsNoTracking()
            .Where(d => stationIds.Contains(d.PrintStationId))
            .ToListAsync(cancellationToken);
        var now = _clock.GetUtcNow().UtcDateTime;

        return devices
            .Select(device =>
            {
                var station = stations.First(s => s.Id == device.PrintStationId);
                var paper = PrintPapers.IsKnown(device.LoadedPaperSize) ? device.LoadedPaperSize : PrintPapers.Photo10x15;
                var status = PrintStationStatus.Calculate(station.LastSeenAt, now, false, true,
                    [device.LastObservedState], _options.HeartbeatOnlineSeconds, _options.HeartbeatOfflineSeconds);
                var online = status != PrintStationStatus.Offline && device.LastObservedState != PrintDeviceStates.Offline;
                return new
                {
                    Printer = new AlbumSharePrinter(station.Id, device.Id, paper,
                        PrintCapabilityMatcher.SupportsFormat(device.CapabilitiesJson, PrintFormats.Strip2x6Pair),
                        online, PrintCapabilityMatcher.CutFormats(device.CapabilitiesJson)),
                    Prints = PrintCapabilityMatcher.SupportsFormat(device.CapabilitiesJson, paper),
                    station.CreatedAt,
                };
            })
            .Where(x => x.Prints)
            .OrderByDescending(x => x.Printer.Online)
            .ThenBy(x => x.CreatedAt)
            .ThenBy(x => x.Printer.DeviceId)
            .Select(x => x.Printer)
            .FirstOrDefault();
    }
}
