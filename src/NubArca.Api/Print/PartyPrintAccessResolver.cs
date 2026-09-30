using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Domain.Print;
using NubArca.Api.Party;

namespace NubArca.Api.Print;

/// <summary>
/// Turns a print token into a capability, or into nothing.
///
/// Every condition is re-read here, on every request. A capability that was
/// handed out an hour ago means nothing on its own: the host may have turned
/// printing off, revoked the party, swapped the printer for one that cannot do
/// 10x15, or simply run out of paper budget. Checking once at issue time and
/// trusting the token afterwards is exactly how a disabled feature keeps
/// printing.
/// </summary>
public sealed class PartyPrintAccessResolver : IPartyPrintAccessResolver
{
    private readonly AppDbContext _db;
    private readonly IPartyCapabilityPolicy _capabilities;

    public PartyPrintAccessResolver(AppDbContext db, IPartyCapabilityPolicy capabilities)
    {
        _db = db;
        _capabilities = capabilities;
    }

    public async Task<PartyPrintAccess?> ResolveAsync(
        string printToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(printToken)) return null;
        var hash = PartyLinkService.HashToken(printToken);
        var now = DateTime.UtcNow;

        // The link must still be a live party: not revoked, not expired, and
        // with its master switch on. Printing rides on the party being open at
        // all — a closed party prints nothing.
        var link = await _db.PartyAlbumLinks.AsNoTracking()
            .Where(l => l.PrintTokenHash == hash
                && l.Enabled
                && l.RevokedAt == null
                && (l.ExpiresAt == null || l.ExpiresAt > now))
            .Select(l => new { l.Id, l.PartyId, l.AlbumId, l.OwnerUserId })
            .FirstOrDefaultAsync(cancellationToken);
        if (link is null) return null;

        // THE PHASE, from the same policy the view and upload seams use. A print
        // studio belongs to the party itself: there is nothing to print from an
        // invitation, and an evening that is over prints no more keepsakes. One
        // pure function, asked here rather than an `if (status …)` of its own.
        var party = await _db.Parties.AsNoTracking()
            .Where(p => p.Id == link.PartyId)
            .Select(p => new { p.Status, p.GuestAccessExpiresAt, p.LibraryAccessExpiresAt })
            .FirstOrDefaultAsync(cancellationToken);
        if (party is null) return null;
        var experience = NubArca.Api.Domain.PartyGuestExperience.Resolve(
            party.Status, party.GuestAccessExpiresAt, party.LibraryAccessExpiresAt, now);
        if (experience?.AllowsLiveCapabilities != true) return null;

        // The HOST's role, re-read like everything else here. A capability a
        // guest is holding cannot outrank a permission the owner no longer has,
        // and losing `party.print` must close the studio on the next request
        // rather than at the next party. Same rule, same shape, as the public
        // view/upload seam in PartyLinkService — asked once, at the entrance.
        var capabilities = await _capabilities.ForOwnerAsync(link.OwnerUserId, cancellationToken);
        if (!capabilities.Print) return null;

        var profile = await _db.PartyPrintProfiles.AsNoTracking()
            .FirstOrDefaultAsync(p => p.PartyAlbumId == link.AlbumId, cancellationToken);
        if (profile is null || !profile.Enabled) return null;
        if (profile.PrintStationId is null || profile.PrinterDeviceId is null) return null;

        // The station must still be the owner's and still be usable, and the
        // printer must still belong to that station: an operator who revoked a
        // station has revoked printing on it, whatever a guest is holding.
        var station = await _db.PrintStations.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == profile.PrintStationId
                && s.OwnerUserId == link.OwnerUserId
                && s.RevokedAt == null,
                cancellationToken);
        if (station is null) return null;

        var device = await _db.PrinterDevices.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == profile.PrinterDeviceId
                && d.PrintStationId == station.Id,
                cancellationToken);
        if (device is null) return null;

        // The paper the operator loaded, which the printer must be able to
        // print: every product is a sheet of it. A printer that does not report
        // that paper — an older Print Agent, a driver without the size — prints
        // nothing rather than something else.
        var paper = PrintPapers.IsKnown(device.LoadedPaperSize)
            ? device.LoadedPaperSize
            : PrintPapers.Photo10x15;
        if (!PrintCapabilityMatcher.SupportsFormat(device.CapabilitiesJson, paper))
        {
            return null;
        }

        // The twin strip is two strips the PRINTER cuts from a 10x15 sheet; a
        // printer that cannot cut it, or has other paper in, has no twin strip.
        var stripCutByPrinter = paper == PrintPapers.Photo10x15
            && PrintCapabilityMatcher.SupportsFormat(device.CapabilitiesJson, PrintFormats.Strip2x6Pair);

        var photo = new PartyPrintProductState(
            profile.PhotoEnabled,
            Math.Max(0, profile.PhotoMaxPrints - profile.PhotoAcceptedCount),
            profile.PhotoPrintsPerGuest);
        var strip = new PartyPrintProductState(
            profile.StripEnabled,
            Math.Max(0, profile.StripMaxPrints - profile.StripAcceptedCount),
            profile.StripPrintsPerGuest);
        var grid = new PartyPrintProductState(
            profile.GridEnabled,
            Math.Max(0, profile.GridMaxPrints - profile.GridAcceptedCount),
            profile.GridPrintsPerGuest);

        var partyName = await _db.Albums.AsNoTracking()
            .Where(a => a.Id == link.AlbumId)
            .Select(a => a.Name)
            .FirstOrDefaultAsync(cancellationToken) ?? string.Empty;

        var access = new PartyPrintAccess(
            link.Id, link.AlbumId, link.OwnerUserId, station.Id, device.Id,
            partyName, profile.FooterText, photo, strip, stripCutByPrinter,
            PrintCalibration.Of(device), grid, paper);

        // Nothing left to offer is the same as printing being closed: the guest
        // hub must not show a card that leads only to exhausted products, or to
        // products this paper cannot make.
        return PartyPrintProducts.All.Any(p => access.Product(p)?.Available == true) ? access : null;
    }
}
