using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using NubArca.Api.Audit;
using NubArca.Api.Data;
using NubArca.Api.Domain.Print;

namespace NubArca.Api.Print;

/// <summary>
/// Print Box self-update, asked for by a person and never forced. The server
/// is updated first and keeps serving every compatible older agent; a box
/// then updates only when its owner or an administrator asks, only to the
/// bundle this server publishes, and only while it is idle. The box's own
/// root update service fetches the bundle, checks it against the hash the
/// server names, swaps it in and rolls back if the new agent does not come
/// back (src/NubArca.PrintAgent/linux/update-print-box.sh).
/// </summary>
public static partial class PrintAgentUpdates
{
    public const string Ok = "ok";
    public const string Failed = "failed";
    public const string Superseded = "superseded";

    [GeneratedRegex(@"^(ok|failed) (\d{1,4}\.\d{1,4}\.\d{1,4}) (\d{1,20})$", RegexOptions.CultureInvariant)]
    private static partial Regex ResultPattern();

    /// <summary>
    /// The request pins one exact bundle — version, size and SHA-256 — when
    /// someone asks. The box is offered that bundle only while it is the one
    /// published; anything else (a newer bundle published meanwhile, the
    /// bundle withdrawn) is not what was asked for, and is never offered.
    /// </summary>
    public static bool Matches(PrintStation station, PrintAgentBundle? bundle) =>
        bundle is not null && station.AgentUpdateTarget == bundle.Version
        && station.AgentUpdateSha256 == bundle.Sha256 && station.AgentUpdateSizeBytes == bundle.SizeBytes;

    /// <summary>The pinned bundle this box is asked to install now, or null.</summary>
    public static PrintAgentUpdateOffer? Offer(PrintStation station, PrintAgentBundle? bundle)
    {
        if (station.AgentUpdateTarget is null || !station.AgentSelfUpdate || !Matches(station, bundle)) return null;
        return new(bundle!.Version, bundle.FileName, bundle.Sha256, bundle.SizeBytes, RequestId(station));
    }

    /// <summary>
    /// A request whose pinned bundle is no longer the published one ends as
    /// superseded: the box keeps its agent and someone asks again for the new one.
    /// </summary>
    public static bool EndIfSuperseded(PrintStation station, PrintAgentBundle? bundle, DateTime now)
    {
        if (station.AgentUpdateTarget is not { } target || Matches(station, bundle)) return false;
        Finish(station, Superseded, target, now);
        return true;
    }

    /// <summary>
    /// Names one request, so that the box's report of how an EARLIER attempt
    /// ended is never read as the outcome of this one.
    /// </summary>
    public static string RequestId(PrintStation station) =>
        station.AgentUpdateRequestedAt is { } at
            ? new DateTimeOffset(DateTime.SpecifyKind(at, DateTimeKind.Utc)).ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "0";

    /// <summary>The bundle is newer than what the agent reports; an unreadable version is never "newer".</summary>
    public static bool IsNewer(string bundleVersion, string? agentVersion)
    {
        var latest = PrintAgentCompatibility.Parse(bundleVersion);
        var current = PrintAgentCompatibility.Parse(agentVersion);
        return latest is not null && current is not null && latest > current;
    }

    /// <summary>
    /// What one heartbeat says about self-update: the capability, and whether the
    /// pending request is now done (the agent reports exactly the pinned version)
    /// or failed (its update service said so for THIS request). A failed update is
    /// not retried on its own: someone asks again.
    /// </summary>
    public static void ApplyHeartbeat(PrintStation station, PrintHeartbeatRequest request, DateTime now)
    {
        station.AgentSelfUpdate = request.SelfUpdate == true;
        if (station.AgentUpdateTarget is not { } target) return;
        var reported = PrintAgentCompatibility.Parse(station.AgentVersion);
        var wanted = PrintAgentCompatibility.Parse(target);
        if (reported is not null && wanted is not null && reported == wanted)
        {
            Finish(station, Ok, target, now);
            return;
        }
        // Updated some other way, past what was asked: this request has nothing left to do.
        if (reported is not null && wanted is not null && reported > wanted)
        {
            Finish(station, Superseded, target, now);
            return;
        }
        var match = request.UpdateResult is null ? null : ResultPattern().Match(request.UpdateResult.Trim());
        if (match is { Success: true } && match.Groups[1].Value == Failed
            && match.Groups[3].Value == RequestId(station) && match.Groups[2].Value == target)
        {
            Finish(station, Failed, target, now);
        }
    }

    private static void Finish(PrintStation station, string result, string version, DateTime now)
    {
        station.AgentUpdateTarget = null;
        station.AgentUpdateSha256 = null;
        station.AgentUpdateSizeBytes = null;
        station.AgentUpdateResult = result;
        station.AgentUpdateResultVersion = version;
        station.AgentUpdateResultAt = now;
    }

    /// <summary>The status line the printers and updates pages show, with the self-update state.</summary>
    public static PrintAgentStatusDto Describe(PrintStation station, PrintAgentBundle? bundle) =>
        PrintAgentCompatibility.Describe(station.AgentVersion, station.AgentBuild, bundle) with
        {
            SelfUpdate = station.AgentSelfUpdate,
            UpdateTarget = station.AgentUpdateTarget,
            UpdateRequestedAt = station.AgentUpdateRequestedAt,
            UpdateResult = station.AgentUpdateResult,
            UpdateResultVersion = station.AgentUpdateResultVersion,
            UpdateResultAt = station.AgentUpdateResultAt,
        };
}

public enum PrintAgentUpdateRequestResult
{
    Requested,
    NotFound,
    NoBundle,
    UpToDate,
    NotSelfUpdating,
}

/// <summary>One box as the Updates page lists it.</summary>
public sealed record PrintBoxUpdateDto(Guid StationId, string Name, string OwnerName, string Status,
    PrintAgentStatusDto Agent, bool CanUpdate);

public sealed class PrintAgentUpdateService
{
    private readonly AppDbContext _db;
    private readonly PrintAgentBundleStore _bundles;
    private readonly TimeProvider _time;
    private readonly IAuditLogger _audit;

    public PrintAgentUpdateService(AppDbContext db, PrintAgentBundleStore bundles, TimeProvider time, IAuditLogger audit)
    {
        _db = db;
        _bundles = bundles;
        _time = time;
        _audit = audit;
    }

    /// <summary>
    /// Asks one station's box to update to the published bundle: its owner, or
    /// an administrator (<paramref name="ownerId"/> null) for any station. The
    /// request and its audit record are saved together, or neither is.
    /// </summary>
    public async Task<(PrintAgentUpdateRequestResult Result, string? Version)> RequestAsync(
        Guid? ownerId, Guid stationId, AuditActor actor, string? ipAddress, CancellationToken cancellationToken)
    {
        var station = await _db.PrintStations.SingleOrDefaultAsync(x => x.Id == stationId
            && x.RevokedAt == null && (ownerId == null || x.OwnerUserId == ownerId), cancellationToken);
        if (station is null) return (PrintAgentUpdateRequestResult.NotFound, null);
        var bundle = await _bundles.CurrentAsync(cancellationToken);
        if (bundle is null) return (PrintAgentUpdateRequestResult.NoBundle, null);
        var result = Request(station, bundle);
        if (result == PrintAgentUpdateRequestResult.Requested)
        {
            // WriteAsync saves the station's change in the same SaveChanges as the entry.
            await _audit.WriteAsync(actor, AuditActions.PrintAgentUpdateRequest, AuditEntityTypes.PrintStation,
                station.Id, ipAddress, new { version = bundle.Version }, cancellationToken);
        }
        return (result, bundle.Version);
    }

    /// <summary>Asks every box that can update itself and is behind the published bundle, with one audit record.</summary>
    public async Task<(int Requested, string? Version)> RequestAllAsync(AuditActor actor, string? ipAddress,
        CancellationToken cancellationToken)
    {
        var bundle = await _bundles.CurrentAsync(cancellationToken);
        if (bundle is null) return (0, null);
        var stations = await _db.PrintStations
            .Where(x => x.RevokedAt == null && x.AgentSelfUpdate && x.AgentUpdateTarget == null)
            .ToListAsync(cancellationToken);
        var requested = stations.Where(station => Request(station, bundle) == PrintAgentUpdateRequestResult.Requested)
            .Select(station => station.Id).ToArray();
        if (requested.Length > 0)
        {
            await _audit.WriteAsync(actor, AuditActions.PrintAgentUpdateRequest, AuditEntityTypes.PrintStation, null,
                ipAddress, new { stations = requested, version = bundle.Version },
                cancellationToken);
        }
        return (requested.Length, bundle.Version);
    }

    private PrintAgentUpdateRequestResult Request(PrintStation station, PrintAgentBundle bundle)
    {
        if (!station.AgentSelfUpdate) return PrintAgentUpdateRequestResult.NotSelfUpdating;
        if (!PrintAgentUpdates.IsNewer(bundle.Version, station.AgentVersion)) return PrintAgentUpdateRequestResult.UpToDate;
        station.AgentUpdateTarget = bundle.Version;
        station.AgentUpdateSha256 = bundle.Sha256;
        station.AgentUpdateSizeBytes = bundle.SizeBytes;
        station.AgentUpdateRequestedAt = _time.GetUtcNow().UtcDateTime;
        return PrintAgentUpdateRequestResult.Requested;
    }

    /// <summary>Withdraws a pending request; a box already installing finishes or rolls back on its own.</summary>
    public async Task<bool> CancelAsync(Guid? ownerId, Guid stationId, AuditActor actor, string? ipAddress,
        CancellationToken cancellationToken)
    {
        var station = await _db.PrintStations.SingleOrDefaultAsync(x => x.Id == stationId
            && (ownerId == null || x.OwnerUserId == ownerId), cancellationToken);
        if (station is null) return false;
        station.AgentUpdateTarget = null;
        station.AgentUpdateSha256 = null;
        station.AgentUpdateSizeBytes = null;
        station.AgentUpdateRequestedAt = null;
        await _audit.WriteAsync(actor, AuditActions.PrintAgentUpdateCancel, AuditEntityTypes.PrintStation,
            station.Id, ipAddress, null, cancellationToken);
        return true;
    }

    /// <summary>What the box's update service is to fetch now, asked with the station's own credential.</summary>
    public async Task<(PrintAgentUpdateOffer Offer, PrintAgentBundle Bundle)?> OfferAsync(
        Guid stationId, CancellationToken cancellationToken)
    {
        var station = await _db.PrintStations.AsNoTracking().SingleOrDefaultAsync(
            x => x.Id == stationId && x.Enabled && x.RevokedAt == null, cancellationToken);
        if (station is null) return null;
        var bundle = await _bundles.CurrentAsync(cancellationToken);
        return PrintAgentUpdates.Offer(station, bundle) is { } offer ? (offer, bundle!) : null;
    }

    /// <summary>Every enrolled, unrevoked box, for an administrator.</summary>
    public async Task<IReadOnlyList<PrintBoxUpdateDto>> FleetAsync(int onlineSeconds, int offlineSeconds,
        CancellationToken cancellationToken)
    {
        var bundle = await _bundles.CurrentAsync(cancellationToken);
        var stations = await _db.PrintStations.AsNoTracking()
            .Where(x => x.RevokedAt == null && x.AgentVersion != null)
            .OrderBy(x => x.Name).ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);
        var ownerIds = stations.Select(x => x.OwnerUserId).Distinct().ToArray();
        var owners = await _db.Users.AsNoTracking().Where(u => ownerIds.Contains(u.Id))
            .Select(u => new { u.Id, u.DisplayName, u.Email })
            .ToDictionaryAsync(u => u.Id, cancellationToken);
        var stationIds = stations.Select(x => x.Id).ToArray();
        var deviceStates = await _db.PrinterDevices.AsNoTracking()
            .Where(d => stationIds.Contains(d.PrintStationId))
            .Select(d => new { d.PrintStationId, d.LastObservedState })
            .ToListAsync(cancellationToken);
        var now = _time.GetUtcNow().UtcDateTime;
        return stations.Select(station =>
        {
            var owner = owners.GetValueOrDefault(station.OwnerUserId);
            var agent = PrintAgentUpdates.Describe(station, bundle);
            var canUpdate = station.AgentSelfUpdate && station.AgentUpdateTarget is null && bundle is not null
                && PrintAgentUpdates.IsNewer(bundle.Version, station.AgentVersion);
            return new PrintBoxUpdateDto(station.Id, station.Name,
                owner is null ? string.Empty : string.IsNullOrWhiteSpace(owner.DisplayName) ? owner.Email : owner.DisplayName,
                PrintStationStatus.Calculate(station.LastSeenAt, now, false, station.Enabled,
                    deviceStates.Where(d => d.PrintStationId == station.Id).Select(d => d.LastObservedState),
                    onlineSeconds, offlineSeconds),
                agent, canUpdate);
        }).ToArray();
    }
}
