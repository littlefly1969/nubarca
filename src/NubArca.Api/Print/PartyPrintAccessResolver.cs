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
            .Select(l => new { l.Id, l.AlbumId, l.OwnerUserId })
            .FirstOrDefaultAsync(cancellationToken);
        if (link is null) return null;

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

        // The strip is a COMPOSITION on the same paper, so both products need
        // exactly one hardware capability, checked once.
        if (!PrintCapabilityMatcher.SupportsFormat(device.CapabilitiesJson, PrintFormats.Photo10x15))
        {
            return null;
        }

        var photo = new PartyPrintProductState(
            profile.PhotoEnabled,
            Math.Max(0, profile.PhotoMaxPrints - profile.PhotoAcceptedCount),
            profile.PhotoPrintsPerGuest);
        var strip = new PartyPrintProductState(
            profile.StripEnabled,
            Math.Max(0, profile.StripMaxPrints - profile.StripAcceptedCount),
            profile.StripPrintsPerGuest);

        // Nothing left to offer is the same as printing being closed: the guest
        // hub must not show a card that leads to two exhausted products.
        if (!photo.Available && !strip.Available) return null;

        var partyName = await _db.Albums.AsNoTracking()
            .Where(a => a.Id == link.AlbumId)
            .Select(a => a.Name)
            .FirstOrDefaultAsync(cancellationToken) ?? string.Empty;

        return new PartyPrintAccess(
            link.Id, link.AlbumId, link.OwnerUserId, station.Id, device.Id,
            partyName, profile.FooterText, photo, strip);
    }
}
