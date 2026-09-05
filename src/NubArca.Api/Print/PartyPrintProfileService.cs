using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Domain.Print;

namespace NubArca.Api.Print;

/// <summary>
/// The HOST's side of party printing: which station and printer a party prints
/// on, which products are open, and how much paper each may spend.
///
/// This is the only place those settings are written. Two rules shape it.
///
/// The budgets are INDEPENDENT and the counters are HISTORY. Photo and strip
/// cost different things and were set separately, so they are never summed and
/// never reset — a host who turns a product off and on again resumes from the
/// same spent count, because the paper already used did not come back. That is
/// also why a budget can never be lowered below what has already been printed:
/// the alternative is a negative remainder, which no honest number can express.
///
/// And enabling printing is a PROMISE to guests. A party that offers a print
/// card must be able to print, so the station and printer are validated here —
/// owned by this host, not revoked, and actually capable of 10x15 — rather than
/// discovered to be wrong by the first guest who tries.
/// </summary>
public interface IPartyPrintProfileService
{
    Task<PartyPrintProfileDto?> GetAsync(
        Guid ownerUserId, Guid albumId, CancellationToken cancellationToken);

    Task<PartyPrintProfileResult> SaveAsync(
        Guid ownerUserId, Guid albumId, PartyPrintProfileRequest request,
        CancellationToken cancellationToken);
}

/// <summary>What the host sees: the settings, plus what has actually been spent.</summary>
public sealed record PartyPrintProfileDto(
    bool Enabled,
    Guid? PrintStationId,
    Guid? PrinterDeviceId,
    PartyPrintProductSettingsDto Photo,
    PartyPrintProductSettingsDto Strip,
    string? FooterText,
    int FooterMaxLength,
    int MinBudget,
    int MaxBudget);

/// <summary>One product's own switch, its own budget, and its own usage.</summary>
public sealed record PartyPrintProductSettingsDto(
    bool Enabled, int MaxPrints, int Used, int Remaining,
    /// <summary>0 means the host set no per-guest limit.</summary>
    int PerGuest);

public sealed record PartyPrintProfileRequest(
    bool? Enabled,
    Guid? PrintStationId,
    Guid? PrinterDeviceId,
    bool? PhotoEnabled,
    int? PhotoMaxPrints,
    int? PhotoPrintsPerGuest,
    bool? StripEnabled,
    int? StripMaxPrints,
    int? StripPrintsPerGuest,
    string? FooterText);

/// <summary>A saved profile, or the one reason it was refused.</summary>
public sealed record PartyPrintProfileResult(PartyPrintProfileDto? Profile, string? Error)
{
    public static PartyPrintProfileResult Ok(PartyPrintProfileDto profile) => new(profile, null);
    public static PartyPrintProfileResult Refused(string error) => new(null, error);
}

public sealed class PartyPrintProfileService : IPartyPrintProfileService
{
    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;

    public PartyPrintProfileService(AppDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<PartyPrintProfileDto?> GetAsync(
        Guid ownerUserId, Guid albumId, CancellationToken cancellationToken)
    {
        var owns = await _db.Albums.AsNoTracking()
            .AnyAsync(a => a.Id == albumId && a.OwnerUserId == ownerUserId, cancellationToken);
        // A foreign or missing album is indistinguishable, on purpose.
        if (!owns) return null;

        var profile = await _db.PartyPrintProfiles.AsNoTracking()
            .FirstOrDefaultAsync(p => p.PartyAlbumId == albumId, cancellationToken);

        // No row yet is not an error: it is a party that has never been
        // configured for printing, which is exactly the default this returns.
        return Describe(profile);
    }

    public async Task<PartyPrintProfileResult> SaveAsync(
        Guid ownerUserId, Guid albumId, PartyPrintProfileRequest request,
        CancellationToken cancellationToken)
    {
        var owns = await _db.Albums.AsNoTracking()
            .AnyAsync(a => a.Id == albumId && a.OwnerUserId == ownerUserId, cancellationToken);
        if (!owns) return PartyPrintProfileResult.Refused("not_found");

        var now = _clock.GetUtcNow().UtcDateTime;
        var profile = await _db.PartyPrintProfiles
            .FirstOrDefaultAsync(p => p.PartyAlbumId == albumId, cancellationToken);
        if (profile is null)
        {
            profile = new PartyPrintProfile
            {
                Id = Guid.NewGuid(),
                PartyAlbumId = albumId,
                OwnerUserId = ownerUserId,
                PhotoMaxPrints = 0,
                StripMaxPrints = 0,
                CreatedAt = now,
                UpdatedAt = now,
            };
            _db.PartyPrintProfiles.Add(profile);
        }

        // Everything the host sent, applied to a copy of the current settings:
        // an omitted field keeps its value rather than being cleared, so a panel
        // that saves one switch cannot silently reset the rest.
        var enabled = request.Enabled ?? profile.Enabled;
        var stationId = request.PrintStationId ?? profile.PrintStationId;
        var deviceId = request.PrinterDeviceId ?? profile.PrinterDeviceId;
        var photoEnabled = request.PhotoEnabled ?? profile.PhotoEnabled;
        var photoMax = request.PhotoMaxPrints ?? profile.PhotoMaxPrints;
        var photoPerGuest = request.PhotoPrintsPerGuest ?? profile.PhotoPrintsPerGuest;
        var stripEnabled = request.StripEnabled ?? profile.StripEnabled;
        var stripMax = request.StripMaxPrints ?? profile.StripMaxPrints;
        var stripPerGuest = request.StripPrintsPerGuest ?? profile.StripPrintsPerGuest;

        if (!PartyPrintText.TryNormaliseFooter(
                request.FooterText, out var footer, out var footerError))
        {
            return PartyPrintProfileResult.Refused(footerError!);
        }
        // Only a field that was actually sent may clear the line.
        if (request.FooterText is null) footer = profile.FooterText;

        if (photoEnabled)
        {
            var error = ValidateBudget(photoMax, profile.PhotoAcceptedCount, "photo")
                ?? ValidatePerGuest(photoPerGuest, photoMax, "photo");
            if (error is not null) return PartyPrintProfileResult.Refused(error);
        }
        if (stripEnabled)
        {
            var error = ValidateBudget(stripMax, profile.StripAcceptedCount, "strip")
                ?? ValidatePerGuest(stripPerGuest, stripMax, "strip");
            if (error is not null) return PartyPrintProfileResult.Refused(error);
        }

        // Turning printing ON is a promise the guest hub will make on this
        // host's behalf, so what it promises is checked at the moment it is
        // made rather than by the first guest who tries.
        if (request.Enabled == true && !photoEnabled && !stripEnabled)
        {
            return PartyPrintProfileResult.Refused("product_required");
        }

        // The printer is validated when printing is switched on and whenever the
        // party is re-aimed at a different one — but NOT on an unrelated budget
        // edit. A station that went offline while the host was adjusting numbers
        // is already handled where it matters: the capability resolver stops
        // publishing a print URL, so guests see no card either way, and blocking
        // the edit would only strand the host.
        var choosingPrinter = request.Enabled == true
            || request.PrintStationId is not null
            || request.PrinterDeviceId is not null;
        if (enabled && choosingPrinter)
        {
            if (stationId is null || deviceId is null)
                return PartyPrintProfileResult.Refused("printer_required");

            var device = await _db.PrinterDevices.AsNoTracking()
                .Where(d => d.Id == deviceId.Value && d.PrintStationId == stationId.Value)
                .Select(d => new { d.CapabilitiesJson })
                .FirstOrDefaultAsync(cancellationToken);
            if (device is null) return PartyPrintProfileResult.Refused("printer_not_found");

            var stationOk = await _db.PrintStations.AsNoTracking()
                .AnyAsync(s => s.Id == stationId.Value
                    && s.OwnerUserId == ownerUserId
                    && s.Enabled
                    && s.RevokedAt == null, cancellationToken);
            // A station belonging to someone else is "not found" here too: a
            // host may not aim their party at another host's printer.
            if (!stationOk) return PartyPrintProfileResult.Refused("station_unavailable");

            if (!PrintCapabilityMatcher.SupportsFormat(
                    device.CapabilitiesJson, PrintFormats.Photo10x15))
            {
                // Both products compose a 10x15 sheet. A printer that cannot do
                // that size cannot print either of them.
                return PartyPrintProfileResult.Refused("format_unsupported");
            }
        }

        profile.Enabled = enabled;
        profile.PrintStationId = stationId;
        profile.PrinterDeviceId = deviceId;
        profile.PhotoEnabled = photoEnabled;
        profile.PhotoMaxPrints = photoMax;
        profile.PhotoPrintsPerGuest = photoPerGuest;
        profile.StripEnabled = stripEnabled;
        profile.StripMaxPrints = stripMax;
        profile.StripPrintsPerGuest = stripPerGuest;
        profile.FooterText = footer;
        profile.UpdatedAt = now;

        await _db.SaveChangesAsync(cancellationToken);
        return PartyPrintProfileResult.Ok(Describe(profile));
    }

    /// <summary>
    /// A per-guest ceiling above the party's own budget promises something the
    /// party cannot pay: it would read as "10 each" on a party with 6 sheets.
    /// Zero is not a small number here — it means no per-guest limit at all.
    /// </summary>
    private static string? ValidatePerGuest(int value, int partyMax, string product)
    {
        if (value < 0) return $"{product}_per_guest_range";
        return value > partyMax ? $"{product}_per_guest_above_budget" : null;
    }

    private static string? ValidateBudget(int value, int alreadyUsed, string product)
    {
        if (!PartyPrintLimits.IsValidBudget(value)) return $"{product}_budget_range";
        // The counter is history. A budget under it would mean a negative
        // remainder, which is not a number this product can show a guest.
        return value < alreadyUsed ? $"{product}_budget_below_used" : null;
    }

    /// <summary>
    /// The host's view of a profile — or of the absence of one, which is a
    /// party that has never been set up for printing rather than an error.
    /// </summary>
    private static PartyPrintProfileDto Describe(PartyPrintProfile? profile) =>
        new(
            profile?.Enabled ?? false,
            profile?.PrintStationId,
            profile?.PrinterDeviceId,
            new PartyPrintProductSettingsDto(
                profile?.PhotoEnabled ?? false,
                profile?.PhotoMaxPrints ?? 0,
                profile?.PhotoAcceptedCount ?? 0,
                Math.Max(0, (profile?.PhotoMaxPrints ?? 0) - (profile?.PhotoAcceptedCount ?? 0)),
                profile?.PhotoPrintsPerGuest ?? 0),
            new PartyPrintProductSettingsDto(
                profile?.StripEnabled ?? false,
                profile?.StripMaxPrints ?? 0,
                profile?.StripAcceptedCount ?? 0,
                Math.Max(0, (profile?.StripMaxPrints ?? 0) - (profile?.StripAcceptedCount ?? 0)),
                profile?.StripPrintsPerGuest ?? 0),
            profile?.FooterText,
            PartyPrintLimits.FooterMaxLength,
            PartyPrintLimits.MinBudget,
            PartyPrintLimits.MaxBudget);
}
