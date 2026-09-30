using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;

namespace NubArca.Api.Print;

/// <summary>
/// What one user may do on one printer, as resolved right now.
///
/// <paramref name="ShareId"/> is null for the owner. A lent printer carries
/// the share it is lent under, and what is left of its ceiling (null: none).
/// </summary>
public sealed record PrinterUse(
    Guid StationId,
    Guid DeviceId,
    Guid StationOwnerUserId,
    bool StationEnabled,
    Guid? ShareId = null,
    int? SheetsLeft = null)
{
    public bool IsOwner => ShareId is null;

    /// <summary>Whether a sheet could be taken now: an owner always, a share while its ceiling holds.</summary>
    public bool HasSheets => SheetsLeft is null or > 0;
}

/// <summary>
/// THE question every print path asks: may this user print on this printer?
///
/// One answer for all of them — a party's studio, its host's settings, the
/// test page, the paper, and the album prints still to come — so that sharing
/// a printer is one rule rather than a check each feature remembers. It is
/// asked on every request, never trusted from earlier: a revoked share, a
/// revoked station or a disabled account stops printing on the next one.
/// </summary>
public interface IPrinterAccess
{
    /// <summary>Null when <paramref name="userId"/> may do nothing on that printer.</summary>
    Task<PrinterUse?> ForUserAsync(Guid userId, Guid stationId, Guid deviceId, CancellationToken cancellationToken);

    /// <summary>
    /// Takes one sheet of a share's ceiling, atomically with the check that the
    /// share is still live. The owner takes nothing and is always answered yes.
    /// </summary>
    Task<PrinterSheetResult> TryTakeSheetAsync(Guid? shareId, CancellationToken cancellationToken);

    /// <summary>Gives a taken sheet back, for a print that never became a job.</summary>
    Task ReturnSheetAsync(Guid? shareId, CancellationToken cancellationToken);
}

public enum PrinterSheetResult
{
    Taken,
    /// <summary>The share was revoked a moment ago: this print is not allowed any more.</summary>
    Revoked,
    /// <summary>The share's ceiling is spent.</summary>
    Exhausted,
}

public sealed class PrinterAccess : IPrinterAccess
{
    private readonly AppDbContext _db;

    public PrinterAccess(AppDbContext db) => _db = db;

    public async Task<PrinterUse?> ForUserAsync(
        Guid userId, Guid stationId, Guid deviceId, CancellationToken cancellationToken)
    {
        var station = await _db.PrintStations.AsNoTracking()
            .Where(s => s.Id == stationId && s.RevokedAt == null)
            .Select(s => new { s.OwnerUserId, s.Enabled })
            .FirstOrDefaultAsync(cancellationToken);
        if (station is null) return null;
        var onStation = await _db.PrinterDevices.AsNoTracking()
            .AnyAsync(d => d.Id == deviceId && d.PrintStationId == stationId, cancellationToken);
        if (!onStation) return null;

        if (station.OwnerUserId == userId)
            return new PrinterUse(stationId, deviceId, station.OwnerUserId, station.Enabled);

        // Lent: a live share from THIS owner (a station that changed hands does
        // not carry the previous owner's loans), to an account that is still
        // active, from an owner who still is.
        var share = await _db.PrinterShares.AsNoTracking()
            .Where(s => s.PrinterDeviceId == deviceId
                && s.GranteeUserId == userId
                && s.OwnerUserId == station.OwnerUserId
                && s.RevokedAt == null
                && _db.Users.Any(u => u.Id == s.GranteeUserId && u.DisabledAt == null)
                && _db.Users.Any(u => u.Id == s.OwnerUserId && u.DisabledAt == null))
            .Select(s => new { s.Id, s.MaxSheets, s.UsedSheets })
            .FirstOrDefaultAsync(cancellationToken);
        if (share is null) return null;
        return new PrinterUse(stationId, deviceId, station.OwnerUserId, station.Enabled, share.Id,
            share.MaxSheets is int max ? Math.Max(0, max - share.UsedSheets) : null);
    }

    public async Task<PrinterSheetResult> TryTakeSheetAsync(Guid? shareId, CancellationToken cancellationToken)
    {
        if (shareId is not Guid id) return PrinterSheetResult.Taken;
        // One statement decides and records: two guests on the last sheet of a
        // lent printer cannot both take it, and a share revoked between the
        // studio opening and this print is caught here, at acceptance.
        var taken = await _db.Database.ExecuteSqlRawAsync(
            """
            UPDATE printer_shares
               SET "UsedSheets" = "UsedSheets" + 1
             WHERE "Id" = {0}
               AND "RevokedAt" IS NULL
               AND ("MaxSheets" IS NULL OR "UsedSheets" < "MaxSheets")
            """,
            [id], cancellationToken);
        if (taken == 1) return PrinterSheetResult.Taken;
        var revoked = await _db.PrinterShares.AsNoTracking()
            .AnyAsync(s => s.Id == id && s.RevokedAt != null, cancellationToken);
        return revoked ? PrinterSheetResult.Revoked : PrinterSheetResult.Exhausted;
    }

    public async Task ReturnSheetAsync(Guid? shareId, CancellationToken cancellationToken)
    {
        if (shareId is not Guid id) return;
        // Only ever back to where it was, never below zero.
        await _db.Database.ExecuteSqlRawAsync(
            """
            UPDATE printer_shares
               SET "UsedSheets" = "UsedSheets" - 1
             WHERE "Id" = {0} AND "UsedSheets" > 0
            """,
            [id], cancellationToken);
    }
}
