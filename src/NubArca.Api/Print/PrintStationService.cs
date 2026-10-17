using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NubArca.Api.Audit;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Print;
using NubArca.Api.Storage;

namespace NubArca.Api.Print;

public sealed class PrintStationService
{
    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;
    private readonly PrintOptions _options;
    private readonly IDerivedBlobStorage _artifacts;
    private readonly PrintArtifactRenderer _renderer;

    private readonly IPrinterAccess _printers;

    private readonly ILogger<PrintStationService> _logger;
    private readonly PrintAgentBundleStore? _bundles;
    private readonly IAuditLogger? _audit;

    public PrintStationService(AppDbContext db, TimeProvider clock, IOptions<PrintOptions> options,
        IDerivedBlobStorage artifacts, PrintArtifactRenderer renderer, IPrinterAccess printers,
        ILogger<PrintStationService>? logger = null, PrintAgentBundleStore? bundles = null,
        IAuditLogger? audit = null)
    {
        _bundles = bundles;
        _audit = audit;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<PrintStationService>.Instance;
        _db = db;
        _clock = clock;
        _options = options.Value;
        _artifacts = artifacts;
        _renderer = renderer;
        _printers = printers;
    }

    public async Task<CreatePrintStationResponse> CreateAsync(
        Guid ownerId, string name, CancellationToken cancellationToken)
    {
        name = name.Trim();
        if (name.Length is < 1 or > 120) throw new ArgumentException("invalid_name");
        var now = Now;
        var station = new PrintStation
        {
            Id = Guid.NewGuid(), OwnerUserId = ownerId, Name = name,
            Enabled = true, DesiredState = PrintDesiredStates.Running, CreatedAt = now,
        };
        var (enrollment, raw) = NewEnrollment(station.Id, now);
        _db.AddRange(station, enrollment);
        await _db.SaveChangesAsync(cancellationToken);
        return new(station.Id, station.Name, raw, enrollment.ExpiresAt);
    }

    public async Task<CreatePrintStationResponse?> RenewEnrollmentAsync(
        Guid ownerId, Guid stationId, CancellationToken cancellationToken)
    {
        var station = await _db.PrintStations.SingleOrDefaultAsync(
            x => x.Id == stationId && x.OwnerUserId == ownerId && x.RevokedAt == null,
            cancellationToken);
        if (station is null) return null;
        var now = Now;
        var active = await _db.PrintStationEnrollments
            .Where(x => x.PrintStationId == stationId && x.ConsumedAt == null)
            .ToListAsync(cancellationToken);
        foreach (var old in active) old.ConsumedAt = now;
        var (enrollment, raw) = NewEnrollment(station.Id, now);
        _db.Add(enrollment);
        await _db.SaveChangesAsync(cancellationToken);
        return new(station.Id, station.Name, raw, enrollment.ExpiresAt);
    }

    public async Task<PrintEnrollmentResponse?> EnrollAsync(
        PrintEnrollmentRequest request, CancellationToken cancellationToken)
    {
        // Before the one-shot token is spent: an agent too old to be served is
        // told so, and the same token still enrolls the updated one.
        if (!PrintAgentCompatibility.IsCompatible(request.AgentVersion))
            throw new PrintAgentIncompatibleException();
        var now = Now;
        var digest = PrintSecurity.Digest(request.EnrollmentToken);
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        var station = await _db.PrintStations.SingleOrDefaultAsync(
            x => x.Id == request.StationId && x.Enabled && x.RevokedAt == null,
            cancellationToken);
        if (station is null) return null;

        // Consumption is conditional in the database, rather than a tracked
        // read followed by a write. Exactly one concurrent enrollment request
        // can turn the one-shot token from unused into consumed.
        var consumed = await _db.PrintStationEnrollments
            .Where(x => x.PrintStationId == request.StationId
                && x.TokenHash == digest && x.ConsumedAt == null && x.ExpiresAt > now)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.ConsumedAt, now),
                cancellationToken);
        if (consumed != 1) return null;

        var secret = PrintSecurity.NewToken();
        var credential = $"{station.Id:N}.{secret}";
        station.CredentialHash = PrintSecurity.Digest(credential);
        station.AgentVersion = NormalizeVersion(request.AgentVersion);
        station.AgentBuild = NormalizeBuild(request.AgentBuild);
        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(station.Id, credential, station.DesiredState);
    }

    public async Task<IReadOnlyList<PrintStationDto>> ListAsync(
        Guid ownerId, CancellationToken cancellationToken)
    {
        var stations = await _db.PrintStations.AsNoTracking()
            .Where(x => x.OwnerUserId == ownerId)
            .OrderBy(x => x.Name).ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);
        var bundle = _bundles is null || stations.Count == 0 ? null : await _bundles.CurrentAsync(cancellationToken);
        var stationIds = stations.Select(x => x.Id).ToArray();
        var devices = await _db.PrinterDevices.AsNoTracking()
            .Where(x => stationIds.Contains(x.PrintStationId))
            .OrderBy(x => x.DisplayName).ToListAsync(cancellationToken);
        // What is still to print, row by row; the history only as counts. A
        // printer that has served a year of parties lists as fast as a new one.
        var jobs = await _db.PrintJobs.AsNoTracking()
            .Where(x => stationIds.Contains(x.PrintStationId) && !PrintJobStates.Terminal.Contains(x.State))
            .OrderByDescending(x => x.CreatedAt).ToListAsync(cancellationToken);
        var lastErrors = new Dictionary<Guid, string?>();
        foreach (var stationId in stationIds)
        {
            lastErrors[stationId] = await _db.PrintJobs.AsNoTracking()
                .Where(x => x.PrintStationId == stationId && x.FailureCode != null)
                .OrderByDescending(x => x.CreatedAt).Select(x => x.FailureCode)
                .FirstOrDefaultAsync(cancellationToken);
        }
        var deviceIds = devices.Select(x => x.Id).ToArray();
        var usage = await _db.PrintJobs.AsNoTracking()
            .Where(x => stationIds.Contains(x.PrintStationId) && deviceIds.Contains(x.PrinterDeviceId)
                && x.RenderedAt != null && x.ArtifactStorageKey != null)
            .GroupBy(x => new { x.PrinterDeviceId, x.OwnerUserId, x.Kind, x.Format, x.State })
            .Select(g => new UsageRow(g.Key.PrinterDeviceId, g.Key.OwnerUserId,
                g.Key.Kind, g.Key.Format, g.Key.State, g.Count()))
            .ToListAsync(cancellationToken);
        var shares = await _db.PrinterShares.AsNoTracking()
            .Where(x => deviceIds.Contains(x.PrinterDeviceId) && x.RevokedAt == null)
            .OrderBy(x => x.CreatedAt).ToListAsync(cancellationToken);
        // Names for everyone who appears: whoever the printers are lent to, who
        // sent a job, and who last set a paper. Nothing else about them.
        var people = await NamesAsync(
            shares.Select(x => x.GranteeUserId)
                .Concat(jobs.Select(x => x.OwnerUserId))
                .Concat(usage.Select(x => x.OwnerUserId))
                .Concat(devices.Where(x => x.LoadedPaperChangedByUserId != null)
                    .Select(x => x.LoadedPaperChangedByUserId!.Value)),
            cancellationToken);
        var now = Now;

        return stations.Select(station =>
        {
            var stationDevices = devices.Where(x => x.PrintStationId == station.Id).ToArray();
            var stationJobs = jobs.Where(x => x.PrintStationId == station.Id).ToArray();
            var current = stationJobs.FirstOrDefault();
            var lastError = lastErrors.GetValueOrDefault(station.Id);
            PrintJobSummaryDto Job(PrintJob job) => ToJobDto(job,
                WaitingFor(job, stationDevices),
                job.OwnerUserId == station.OwnerUserId ? null : people.GetValueOrDefault(job.OwnerUserId)?.Name);
            return new PrintStationDto(
                station.Id, station.Name, station.Enabled, station.DesiredState,
                PrintStationStatus.Calculate(station.LastSeenAt, now, station.RevokedAt != null,
                    station.Enabled, stationDevices.Select(x => x.LastObservedState),
                    _options.HeartbeatOnlineSeconds, _options.HeartbeatOfflineSeconds),
                station.LastSeenAt, station.AgentVersion, station.CreatedAt, station.RevokedAt,
                stationDevices.Select(device => ToDeviceDto(device, people) with
                {
                    Shares = shares.Where(x => x.PrinterDeviceId == device.Id)
                        .Select(x => new PrinterShareDto(x.Id,
                            people.GetValueOrDefault(x.GranteeUserId)?.Name ?? string.Empty,
                            people.GetValueOrDefault(x.GranteeUserId)?.Email ?? string.Empty,
                            x.MaxSheets, x.UsedSheets, x.CreatedAt))
                        .ToArray(),
                    Usage = Usage(usage.Where(x => x.PrinterDeviceId == device.Id),
                        station.OwnerUserId, people),
                }).ToArray(),
                stationJobs.Length,
                current is null ? null : Job(current), lastError,
                // The queue, oldest first: what is waiting, for which paper, and
                // whose — so the owner can see and clear a lent printer's work.
                stationJobs.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).Take(25).Select(Job).ToArray(),
                // Never enrolled: no agent to describe yet.
                station.AgentVersion is null ? null
                    : PrintAgentUpdates.Describe(station, bundle));
        }).ToArray();
    }

    /// <summary>
    /// The printers other users have lent to <paramref name="userId"/>: what
    /// they may print on, and the two things they may set — nothing about the
    /// owner's other printers, stations or work.
    /// </summary>
    public async Task<IReadOnlyList<SharedPrinterDto>> ListSharedAsync(
        Guid userId, CancellationToken cancellationToken)
    {
        var shares = await _db.PrinterShares.AsNoTracking()
            .Where(s => s.GranteeUserId == userId && s.RevokedAt == null
                && _db.Users.Any(u => u.Id == s.OwnerUserId && u.DisabledAt == null))
            .ToListAsync(cancellationToken);
        var deviceIds = shares.Select(x => x.PrinterDeviceId).ToArray();
        var devices = await _db.PrinterDevices.AsNoTracking()
            .Where(d => deviceIds.Contains(d.Id)).ToListAsync(cancellationToken);
        var stationIds = devices.Select(d => d.PrintStationId).ToArray();
        var stations = await _db.PrintStations.AsNoTracking()
            .Where(s => stationIds.Contains(s.Id) && s.RevokedAt == null).ToListAsync(cancellationToken);
        var stationDevices = await _db.PrinterDevices.AsNoTracking()
            .Where(d => stationIds.Contains(d.PrintStationId))
            .Select(d => new { d.PrintStationId, d.LastObservedState }).ToListAsync(cancellationToken);
        var people = await NamesAsync(
            shares.Select(x => x.OwnerUserId).Concat(devices.Where(d => d.LoadedPaperChangedByUserId != null)
                .Select(d => d.LoadedPaperChangedByUserId!.Value)), cancellationToken);
        var now = Now;

        return shares.Select(share =>
        {
            var device = devices.FirstOrDefault(d => d.Id == share.PrinterDeviceId);
            var station = device is null ? null : stations.FirstOrDefault(s => s.Id == device.PrintStationId);
            // A station its owner revoked, or one that changed hands, lends nothing.
            if (device is null || station is null || station.OwnerUserId != share.OwnerUserId) return null;
            return new SharedPrinterDto(
                share.Id, station.Id, station.Name,
                PrintStationStatus.Calculate(station.LastSeenAt, now, false, station.Enabled,
                    stationDevices.Where(d => d.PrintStationId == station.Id).Select(d => d.LastObservedState),
                    _options.HeartbeatOnlineSeconds, _options.HeartbeatOfflineSeconds),
                device.Id, device.DisplayName, device.LastObservedState,
                people.GetValueOrDefault(share.OwnerUserId)?.Name ?? string.Empty,
                PaperOf(device), Papers(device),
                PrintCapabilityMatcher.SupportsFormat(device.CapabilitiesJson, PrintFormats.Photo10x15),
                PrintCapabilityMatcher.SupportsFormat(device.CapabilitiesJson, PrintFormats.Strip2x6Pair),
                device.LoadedPaperChangedByUserId is Guid by ? people.GetValueOrDefault(by)?.Name : null,
                device.LoadedPaperChangedAt,
                share.MaxSheets, share.UsedSheets,
                device.MediaRemainingPrints, device.MediaRemainingObservedAt,
                PrintSimulation.IsRequested(device),
                PrintCapabilityMatcher.CutFormats(device.CapabilitiesJson));
        }).Where(x => x is not null).Cast<SharedPrinterDto>()
            .OrderBy(x => x.OwnerName).ThenBy(x => x.DisplayName).ToArray();
    }

    // --- Lending a printer ----------------------------------------------------

    /// <summary>
    /// Lends one of the owner's printers to the account with <paramref name="email"/>.
    /// Refusals are codes the owner's page can speak: not_found, recipient_not_found,
    /// recipient_is_owner, already_shared, invalid_ceiling.
    /// </summary>
    public async Task<(PrinterShareDto? Share, string? Error)> ShareAsync(
        Guid ownerId, Guid stationId, Guid printerId, string? email, int? maxSheets,
        CancellationToken cancellationToken)
    {
        if (!PrinterShareLimits.IsValidCeiling(maxSheets)) return (null, "invalid_ceiling");
        var owned = await _db.PrintStations.AnyAsync(
            x => x.Id == stationId && x.OwnerUserId == ownerId && x.RevokedAt == null, cancellationToken);
        var onStation = owned && await _db.PrinterDevices.AnyAsync(
            x => x.Id == printerId && x.PrintStationId == stationId, cancellationToken);
        if (!onStation) return (null, "not_found");

        // The same lookup an album transfer makes: the exact address of an
        // active account, nothing that lists or suggests the others.
        var normalized = (email ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized.Length is 0 or > 320) return (null, "recipient_not_found");
        var grantee = await _db.Users.AsNoTracking()
            .Where(u => u.Email.ToLower() == normalized && u.DisabledAt == null)
            .Select(u => new { u.Id, u.DisplayName, u.Email })
            .FirstOrDefaultAsync(cancellationToken);
        if (grantee is null) return (null, "recipient_not_found");
        if (grantee.Id == ownerId) return (null, "recipient_is_owner");

        var share = new PrinterShare
        {
            Id = Guid.NewGuid(), PrinterDeviceId = printerId, OwnerUserId = ownerId,
            GranteeUserId = grantee.Id, MaxSheets = maxSheets, UsedSheets = 0, CreatedAt = Now,
        };
        _db.PrinterShares.Add(share);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // The index allows one live loan per printer and person.
            _db.ChangeTracker.Clear();
            return (null, "already_shared");
        }
        return (new PrinterShareDto(share.Id, Name(grantee.DisplayName, grantee.Email), grantee.Email,
            share.MaxSheets, 0, share.CreatedAt), null);
    }

    /// <summary>
    /// Changes a loan's ceiling. Refusals: not_found, invalid_ceiling, and
    /// ceiling_below_used — the sheets already taken are history.
    /// </summary>
    public async Task<(PrinterShareDto? Share, string? Error)> UpdateShareAsync(
        Guid ownerId, Guid shareId, int? maxSheets, CancellationToken cancellationToken)
    {
        if (!PrinterShareLimits.IsValidCeiling(maxSheets)) return (null, "invalid_ceiling");
        var share = await _db.PrinterShares.SingleOrDefaultAsync(
            x => x.Id == shareId && x.OwnerUserId == ownerId && x.RevokedAt == null, cancellationToken);
        if (share is null) return (null, "not_found");
        if (maxSheets is int max && max < share.UsedSheets) return (null, "ceiling_below_used");
        share.MaxSheets = maxSheets;
        await _db.SaveChangesAsync(cancellationToken);
        var grantee = (await NamesAsync([share.GranteeUserId], cancellationToken))
            .GetValueOrDefault(share.GranteeUserId);
        return (new PrinterShareDto(share.Id, grantee?.Name ?? string.Empty, grantee?.Email ?? string.Empty,
            share.MaxSheets, share.UsedSheets, share.CreatedAt), null);
    }

    /// <summary>
    /// Ends a loan. From the next request nothing new is accepted for its
    /// user on this printer; what is already in the queue still prints.
    /// </summary>
    public async Task<PrinterShare?> RevokeShareAsync(Guid ownerId, Guid shareId, CancellationToken cancellationToken)
    {
        var share = await _db.PrinterShares.SingleOrDefaultAsync(
            x => x.Id == shareId && x.OwnerUserId == ownerId && x.RevokedAt == null, cancellationToken);
        if (share is null) return null;
        share.RevokedAt = Now;
        share.RevokedByUserId = ownerId;
        await _db.SaveChangesAsync(cancellationToken);
        return share;
    }

    public async Task<bool> SetDesiredStateAsync(Guid ownerId, Guid stationId, string desiredState,
        CancellationToken cancellationToken)
    {
        if (!PrintDesiredStates.IsValid(desiredState)) throw new ArgumentException("invalid_state");
        var rows = await _db.PrintStations
            .Where(x => x.Id == stationId && x.OwnerUserId == ownerId && x.RevokedAt == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.DesiredState, desiredState)
                .SetProperty(x => x.Enabled, desiredState != PrintDesiredStates.Disabled), cancellationToken);
        return rows == 1;
    }

    /// <summary>
    /// Sets how this printer's sheets are compensated. Null when the printer is
    /// not on one of this owner's stations; throws when a value is out of range.
    /// </summary>
    public async Task<PrintDeviceDto?> SetCalibrationAsync(Guid ownerId, Guid stationId, Guid printerId,
        PrintCalibrationDto request, CancellationToken cancellationToken)
    {
        var calibration = new PrintCalibration(request.Brightness, request.Contrast, request.Gamma, request.Saturation);
        if (!calibration.IsValid) throw new ArgumentException("invalid_calibration");
        var owned = await _db.PrintStations.AnyAsync(
            x => x.Id == stationId && x.OwnerUserId == ownerId && x.RevokedAt == null, cancellationToken);
        var printer = owned
            ? await _db.PrinterDevices.SingleOrDefaultAsync(
                x => x.Id == printerId && x.PrintStationId == stationId, cancellationToken)
            : null;
        if (printer is null) return null;
        printer.CalibrationBrightness = calibration.Brightness;
        printer.CalibrationContrast = calibration.Contrast;
        printer.CalibrationGamma = calibration.Gamma;
        printer.CalibrationSaturation = calibration.Saturation;
        await _db.SaveChangesAsync(cancellationToken);
        return ToDeviceDto(printer);
    }

    /// <summary>
    /// Records which paper the operator loaded. Null when the printer is not on
    /// one of this owner's stations; throws for a paper NubArca does not know.
    /// A paper the agent does not report is still recorded — it is what is in
    /// the printer — and simply offers guests nothing until the agent can print it.
    /// </summary>
    public async Task<PrintDeviceDto?> SetLoadedPaperAsync(Guid userId, Guid stationId, Guid printerId,
        string? paperSize, CancellationToken cancellationToken)
    {
        if (!PrintPapers.IsKnown(paperSize)) throw new ArgumentException("invalid_paper");
        // The owner, or the person the printer is lent to: whoever changes the
        // roll is the one who knows what is in it.
        var use = await _printers.ForUserAsync(userId, stationId, printerId, cancellationToken);
        var printer = use is null
            ? null
            : await _db.PrinterDevices.SingleOrDefaultAsync(x => x.Id == printerId, cancellationToken);
        if (printer is null) return null;
        printer.LoadedPaperSize = paperSize!;
        printer.LoadedPaperChangedAt = Now;
        printer.LoadedPaperChangedByUserId = userId;
        await _db.SaveChangesAsync(cancellationToken);
        return ToDeviceDto(printer, await NamesAsync([userId], cancellationToken));
    }

    /// <summary>
    /// Revokes a station, and in the same transaction every loan of a printer
    /// on it. A lent sheet is taken on the share's row, so the loan ended here
    /// refuses the next sheet whichever of the two commits first — the station
    /// check in that statement is the second guard, not the only one. Null
    /// when the station is not the owner's or is already revoked; otherwise
    /// the loans it ended, for the audit.
    /// </summary>
    public async Task<IReadOnlyList<PrinterShare>?> RevokeAsync(
        Guid ownerId, Guid stationId, CancellationToken cancellationToken)
    {
        var now = Now;
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        var rows = await _db.PrintStations
            .Where(x => x.Id == stationId && x.OwnerUserId == ownerId && x.RevokedAt == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.RevokedAt, now)
                .SetProperty(x => x.Enabled, false)
                .SetProperty(x => x.DesiredState, PrintDesiredStates.Disabled)
                .SetProperty(x => x.CredentialHash, (string?)null), cancellationToken);
        if (rows != 1) return null;
        var ended = await _db.PrinterShares
            .Where(x => x.RevokedAt == null
                && _db.PrinterDevices.Any(d => d.Id == x.PrinterDeviceId && d.PrintStationId == stationId))
            .ToListAsync(cancellationToken);
        foreach (var share in ended)
        {
            share.RevokedAt = now;
            share.RevokedByUserId = ownerId;
        }
        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ended;
    }

    /// <summary>
    /// What <see cref="EraseAsync"/> would take: the station's jobs, and how many
    /// parties lose their print history with them. Null for no such station of
    /// this owner; refused for one not revoked.
    /// </summary>
    public async Task<PrintStationErasePreview?> PreviewEraseAsync(Guid ownerId, Guid stationId,
        CancellationToken cancellationToken)
    {
        var station = await _db.PrintStations.AsNoTracking().SingleOrDefaultAsync(
            x => x.Id == stationId && x.OwnerUserId == ownerId, cancellationToken);
        if (station is null) return null;
        if (station.RevokedAt is null) throw new InvalidOperationException("station_not_revoked");
        var jobIds = _db.PrintJobs.Where(x => x.PrintStationId == stationId).Select(x => x.Id);
        return new PrintStationErasePreview(
            await jobIds.CountAsync(cancellationToken),
            await _db.PartyPrintRequests.Where(x => jobIds.Contains(x.PrintJobId))
                .Select(x => x.PartyAlbumId).Distinct().CountAsync(cancellationToken));
    }

    /// <summary>
    /// Erases a REVOKED station and everything it left: its jobs and what hangs
    /// off them (party and owner requests, sources), its printers' loans, its
    /// printers, its enrollment codes and the station itself — then the
    /// rendered sheets nothing else owns. A party keeps its budgets and its
    /// numbering (they are counters on the party, not on the jobs) and only
    /// loses its printer choice and its history of prints on this station.
    /// Revocation already stopped every print here, so nothing live is cut.
    ///
    /// The erase and its security record are ONE transaction: the
    /// <c>print.station.erase</c> audit row is written through the same
    /// connection before the commit, and if it cannot be written nothing is
    /// erased. There is never an erased station without its erase record.
    /// Null: no such station of this owner; false: not revoked.
    /// </summary>
    public async Task<bool?> EraseAsync(Guid ownerId, Guid stationId, string? ipAddress,
        CancellationToken cancellationToken)
    {
        if (_audit is null) throw new InvalidOperationException("An erase is never made without its audit record.");
        List<string> artifactKeys = [];
        var strategy = _db.Database.CreateExecutionStrategy();
        var erased = await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
            var station = await _db.PrintStations.AsNoTracking().SingleOrDefaultAsync(
                x => x.Id == stationId && x.OwnerUserId == ownerId, cancellationToken);
            if (station is null) return (bool?)null;
            if (station.RevokedAt is null) return false;

            var deviceIds = await _db.PrinterDevices.Where(x => x.PrintStationId == stationId)
                .Select(x => x.Id).ToListAsync(cancellationToken);
            artifactKeys = await DeleteJobsAsync(
                _db.PrintJobs.Where(x => x.PrintStationId == stationId || deviceIds.Contains(x.PrinterDeviceId)),
                cancellationToken);
            await _db.PartyPrintProfiles
                .Where(x => x.PrintStationId == stationId
                    || (x.PrinterDeviceId != null && deviceIds.Contains(x.PrinterDeviceId.Value)))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.PrintStationId, (Guid?)null)
                    .SetProperty(x => x.PrinterDeviceId, (Guid?)null), cancellationToken);
            await _db.PrinterShares.Where(x => deviceIds.Contains(x.PrinterDeviceId)).ExecuteDeleteAsync(cancellationToken);
            await _db.PrinterDevices.Where(x => x.PrintStationId == stationId).ExecuteDeleteAsync(cancellationToken);
            await _db.PrintStationEnrollments.Where(x => x.PrintStationId == stationId).ExecuteDeleteAsync(cancellationToken);
            await _db.PrintStations.Where(x => x.Id == stationId).ExecuteDeleteAsync(cancellationToken);
            try
            {
                await _audit.WriteAsync(AuditActor.User(ownerId), AuditActions.PrintStationErase,
                    AuditEntityTypes.PrintStation, stationId, ipAddress, null, cancellationToken);
            }
            catch
            {
                // Rolled back with the transaction: an attempt that is retried
                // must not carry this one's unsaved record into the next.
                foreach (var entry in _db.ChangeTracker.Entries<AuditLog>()
                             .Where(x => x.State == EntityState.Added).ToList())
                    entry.State = EntityState.Detached;
                throw;
            }
            await transaction.CommitAsync(cancellationToken);
            return true;
        });
        if (erased == true) await DeleteUnownedArtifactsAsync(artifactKeys, cancellationToken);
        return erased;
    }

    /// <summary>
    /// Deletes <paramref name="jobs"/> with what hangs off them — party and owner
    /// requests, sources — inside the caller's transaction, and returns the keys
    /// of their rendered sheets for <see cref="DeleteUnownedArtifactsAsync"/>
    /// once that transaction has committed.
    /// </summary>
    private async Task<List<string>> DeleteJobsAsync(IQueryable<PrintJob> jobs, CancellationToken cancellationToken)
    {
        var keys = await jobs.Where(x => x.ArtifactStorageKey != null)
            .Select(x => x.ArtifactStorageKey!).Distinct().ToListAsync(cancellationToken);
        var jobIds = jobs.Select(x => x.Id);
        await _db.PartyPrintRequests.Where(x => jobIds.Contains(x.PrintJobId)).ExecuteDeleteAsync(cancellationToken);
        await _db.OwnerPhotoPrintRequests.Where(x => jobIds.Contains(x.PrintJobId)).ExecuteDeleteAsync(cancellationToken);
        await _db.PrintJobSources.Where(x => jobIds.Contains(x.PrintJobId)).ExecuteDeleteAsync(cancellationToken);
        await jobs.ExecuteDeleteAsync(cancellationToken);
        return keys;
    }

    /// <summary>
    /// Each rendered sheet of the erased jobs, unless something still owns the
    /// same bytes: another job, or a library blob of identical content. Decided
    /// under the exclusive storage lock, as the blob janitor decides a purge, so
    /// no writer can publish these bytes between the check and the unlink. A
    /// sheet that cannot be deleted is left for storage reconciliation to report.
    /// </summary>
    private async Task DeleteUnownedArtifactsAsync(IReadOnlyList<string> keys, CancellationToken cancellationToken)
    {
        foreach (var key in keys)
        {
            var sha = StorageMutationLock.ContentIdentityOf(key);
            if (sha is null) continue;
            try
            {
                var strategy = _db.Database.CreateExecutionStrategy();
                await strategy.ExecuteAsync(async () =>
                {
                    await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
                    await StorageMutationLock.AcquireExclusiveAsync(_db, sha, cancellationToken);
                    var owned = await _db.BlobObjects.AsNoTracking()
                            .AnyAsync(x => x.StorageKey == key || x.Sha256 == sha, cancellationToken)
                        || await _db.PrintJobs.AsNoTracking()
                            .AnyAsync(x => x.ArtifactStorageKey == key, cancellationToken);
                    if (!owned) await _artifacts.DeleteAsync(key, cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning("A rendered sheet of an erased print station could not be deleted ({ExceptionType}).",
                    ex.GetType().Name);
            }
        }
    }

    /// <summary>
    /// The owner switches a printer to its simulated twin, or back. Only the
    /// owner — a loan never decides where prints go — and only while the agent
    /// offers a twin; a simulation-only box cannot be switched to paper.
    /// </summary>
    public async Task<(PrintDeviceDto? Device, string? Error)> SetSimulationAsync(Guid ownerId, Guid stationId,
        Guid deviceId, bool enabled, CancellationToken cancellationToken)
    {
        var device = await _db.PrinterDevices.SingleOrDefaultAsync(
            x => x.Id == deviceId && x.PrintStationId == stationId
                && _db.PrintStations.Any(s => s.Id == stationId && s.OwnerUserId == ownerId && s.RevokedAt == null),
            cancellationToken);
        if (device is null) return (null, null);
        if (device.SimulationOnly && !enabled) return (null, "simulation_only");
        // Switching ON needs a twin to switch to; switching OFF is always the
        // owner's to do, twin or not.
        if (enabled && !device.Simulated && !device.SimulationAvailable && !device.SimulationOnly)
            return (null, "simulation_unavailable");
        device.Simulated = enabled;
        await _db.SaveChangesAsync(cancellationToken);
        return (ToDeviceDto(device), null);
    }

    /// <summary>The simulated prints of one of the owner's printers, newest first: the sheets themselves are downloaded apart.</summary>
    public async Task<IReadOnlyList<SimulatedPrintDto>?> ListSimulatedAsync(Guid ownerId, Guid stationId, Guid deviceId,
        CancellationToken cancellationToken)
    {
        if (!await OwnsDeviceAsync(ownerId, stationId, deviceId, cancellationToken)) return null;
        var jobs = await _db.PrintJobs.AsNoTracking()
            .Where(x => x.PrinterDeviceId == deviceId && x.Simulated)
            .OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id).Take(100)
            .ToListAsync(cancellationToken);
        var people = await NamesAsync(jobs.Where(x => x.OwnerUserId != ownerId).Select(x => x.OwnerUserId),
            cancellationToken);
        return jobs.Select(x => new SimulatedPrintDto(x.Id, x.Id.ToString("N")[..8], x.Kind, x.Format, x.State,
            x.CreatedAt, x.OwnerUserId == ownerId ? null : people.GetValueOrDefault(x.OwnerUserId)?.Name)).ToArray();
    }

    /// <summary>
    /// The sheet of a SIMULATED print, for its printer's owner only — the same
    /// sheet a real print would put on their paper. A real print's sheet is
    /// never served here.
    /// </summary>
    public async Task<PrintArtifact?> OpenSimulatedSheetAsync(Guid ownerId, Guid jobId, CancellationToken cancellationToken)
    {
        var job = await _db.PrintJobs.AsNoTracking().SingleOrDefaultAsync(
            x => x.Id == jobId && x.Simulated && x.ArtifactStorageKey != null
                && _db.PrintStations.Any(s => s.Id == x.PrintStationId && s.OwnerUserId == ownerId),
            cancellationToken);
        if (job is null) return null;
        var stored = await _artifacts.OpenReadAsync(job.ArtifactStorageKey!, cancellationToken);
        if (job.ArtifactContentType != PrintArtifactParts.ContentType)
            return new PrintArtifact(stored, job.ArtifactContentType ?? "image/png");
        // A joined 20x15 is two files on the printer: the owner sees them as
        // the sheet they come out of.
        await using (stored)
        {
            var parts = await PrintArtifactParts.ReadAllAsync(stored, cancellationToken);
            if (parts is null) return null;
            return new PrintArtifact(new MemoryStream(await PrintArtifactParts.JoinForViewAsync(parts, cancellationToken),
                writable: false), PrintArtifactParts.PartContentType);
        }
    }

    /// <summary>
    /// Clears a printer's finished simulated prints — rows, requests, sheets —
    /// and records, on THIS printer, which jobs were cleared: its box deletes
    /// those jobs' files from this printer's twin queues at the next heartbeat,
    /// and nothing of any other printer, nor anything printed after the clear.
    /// Prints still on their way are left to finish. Clearing nothing records
    /// nothing, so a repeated clear is a no-op.
    /// </summary>
    public async Task<int?> ClearSimulatedAsync(Guid ownerId, Guid stationId, Guid deviceId,
        CancellationToken cancellationToken)
    {
        if (!await OwnsDeviceAsync(ownerId, stationId, deviceId, cancellationToken)) return null;
        List<string> keys = [];
        var count = 0;
        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
            var finished = _db.PrintJobs.Where(x => x.PrinterDeviceId == deviceId && x.Simulated
                && PrintJobStates.Terminal.Contains(x.State));
            var cleared = await finished.OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id)
                .Select(x => x.Id).ToListAsync(cancellationToken);
            count = cleared.Count;
            if (count == 0)
            {
                await transaction.CommitAsync(cancellationToken);
                return;
            }
            keys = await DeleteJobsAsync(finished, cancellationToken);
            var named = JsonSerializer.Serialize(cleared.Take(PrintSimulation.PurgeJobLimit).Select(x => x.ToString("N")));
            await _db.PrinterDevices.Where(x => x.Id == deviceId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.SimulatedPurgeSequence, x => x.SimulatedPurgeSequence + 1)
                    .SetProperty(x => x.SimulatedPurgeJobIds, named), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        });
        await DeleteUnownedArtifactsAsync(keys, cancellationToken);
        return count;
    }

    private Task<bool> OwnsDeviceAsync(Guid ownerId, Guid stationId, Guid deviceId, CancellationToken cancellationToken) =>
        _db.PrinterDevices.AnyAsync(x => x.Id == deviceId && x.PrintStationId == stationId
            && _db.PrintStations.Any(s => s.Id == stationId && s.OwnerUserId == ownerId), cancellationToken);

    public async Task<PrintHeartbeatResponse?> HeartbeatAsync(Guid stationId,
        PrintHeartbeatRequest request, CancellationToken cancellationToken)
    {
        var station = await _db.PrintStations.SingleOrDefaultAsync(
            x => x.Id == stationId && x.Enabled && x.RevokedAt == null, cancellationToken);
        if (station is null) return null;
        if (request.Devices.Count > 32) throw new ArgumentException("too_many_devices");
        var now = Now;
        station.LastSeenAt = now;
        station.AgentVersion = NormalizeVersion(request.AgentVersion);
        station.AgentBuild = NormalizeBuild(request.AgentBuild);
        PrintAgentUpdates.ApplyHeartbeat(station, request, now);
        // The request pins one bundle; once another is published it is no longer offered.
        var bundle = station.AgentUpdateTarget is null || _bundles is null ? null
            : await _bundles.CurrentAsync(cancellationToken);
        if (_bundles is not null) PrintAgentUpdates.EndIfSuperseded(station, bundle, now);
        var knownDevices = await _db.PrinterDevices
            .Where(x => x.PrintStationId == stationId)
            .ToDictionaryAsync(x => x.DeviceKey, StringComparer.Ordinal, cancellationToken);
        var reportedKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var report in request.Devices)
        {
            ValidateDevice(report);
            reportedKeys.Add(report.DeviceKey);
            if (!knownDevices.TryGetValue(report.DeviceKey, out var device))
            {
                device = new PrinterDevice { Id = Guid.NewGuid(), PrintStationId = stationId, DeviceKey = report.DeviceKey };
                _db.PrinterDevices.Add(device);
            }
            device.DisplayName = report.DisplayName.Trim();
            device.Manufacturer = TrimOrNull(report.Manufacturer, 120);
            device.Model = TrimOrNull(report.Model, 120);
            device.AdapterKind = report.AdapterKind.Trim();
            device.CapabilitiesJson = JsonSerializer.Serialize(report.Capabilities);
            device.LastObservedState = report.ObservedState;
            device.LastSeenAt = now;
            ApplyMediaRemaining(stationId, device, report.MediaRemaining, now);
            // Whether a simulated job can run NOW; it never changes whether a
            // job is simulated. An agent without a twin, or one that predates
            // it, offers none: simulated jobs wait rather than reach paper.
            device.SimulationAvailable = report.Simulation?.Available == true;
            // A box with only a twin is simulated for good: a later report
            // never turns it into a printer on paper.
            if (report.Simulation?.Only == true)
            {
                device.SimulationOnly = true;
                device.Simulated = true;
            }
        }
        foreach (var missing in knownDevices.Values.Where(x => !reportedKeys.Contains(x.DeviceKey)))
            missing.LastObservedState = PrintDeviceStates.Offline;
        await _db.SaveChangesAsync(cancellationToken);
        var devices = knownDevices.Values.Concat(_db.PrinterDevices.Local.Where(x => x.PrintStationId == stationId))
            .DistinctBy(x => x.Id).ToArray();
        var simulated = devices.Where(PrintSimulation.IsRequested).Select(x => x.DeviceKey).Distinct().ToArray();
        var purges = devices.Where(x => x.SimulatedPurgeSequence > 0 && x.SimulatedPurgeJobIds is not null)
            .Select(x => new SimulatedOutputPurge(x.DeviceKey, x.SimulatedPurgeSequence,
                JsonSerializer.Deserialize<string[]>(x.SimulatedPurgeJobIds!) ?? []))
            .ToArray();
        return new(station.DesiredState, now, simulated, purges, PrintAgentUpdates.Offer(station, bundle));
    }

    public async Task<(PrintJobSummaryDto? Job, string? Error)> CreateTestPrintAsync(Guid ownerId, Guid stationId,
        Guid printerId, CancellationToken cancellationToken)
    {
        // The owner, or the person the printer is lent to — who wants to see
        // the paper they just loaded — and then a sheet of their loan.
        var use = await _printers.ForUserAsync(ownerId, stationId, printerId, cancellationToken);
        if (use is null || !use.StationEnabled) return (null, null);
        var station = await _db.PrintStations.SingleOrDefaultAsync(x => x.Id == stationId, cancellationToken);
        var printer = await _db.PrinterDevices.SingleOrDefaultAsync(
            x => x.Id == printerId && x.PrintStationId == stationId, cancellationToken);
        // The test page goes on the paper that is in the printer: sending a
        // 10x15 job to a printer loaded with 20x15 stops it with a media error.
        var paper = printer is not null && PrintPapers.IsKnown(printer.LoadedPaperSize)
            ? printer.LoadedPaperSize
            : PrintPapers.Photo10x15;
        if (station is null || printer is null
            || !PrintCapabilityMatcher.SupportsFormat(printer.CapabilitiesJson, paper))
            return (null, null);
        switch (await _printers.TryTakeSheetAsync(use.ShareId, cancellationToken))
        {
            case PrinterSheetResult.Revoked: return (null, null);
            case PrinterSheetResult.Exhausted: return (null, "share_exhausted");
        }
        var now = Now;
        var job = new PrintJob
        {
            Id = Guid.NewGuid(), OwnerUserId = ownerId, PrintStationId = stationId,
            PrinterDeviceId = printerId, Kind = PrintJobKinds.Diagnostic,
            Format = paper, State = PrintJobStates.Requested,
            Simulated = PrintSimulation.IsRequested(printer),
            RenderSpecificationJson = JsonSerializer.Serialize(new { type = "diagnostic", width = 1800, height = 1200 }),
            CreatedAt = now,
        };
        _db.PrintJobs.Add(job);
        await _db.SaveChangesAsync(cancellationToken);
        await TransitionAsync(job, PrintJobStates.Rendering, cancellationToken);
        try
        {
            var bytes = await _renderer.RenderDiagnosticAsync(
                stationName: station.Name,
                printerModel: printer.Model ?? printer.DisplayName,
                now: now,
                format: job.Format,
                shortCode: job.Id.ToString("N")[..8],
                calibration: PrintCalibration.Of(printer),
                cancellationToken: cancellationToken);
            await using var source = new MemoryStream(bytes, writable: false);
            // Stage outside the lock, then publish and claim the artifact in one
            // protected step: the job's ArtifactStorageKey is this object's ONLY
            // owner, so it must become durable before a purge of the same
            // content can look at it.
            var staged = await _artifacts.StageAsync(source, cancellationToken);
            await using var stagedScope = staged.ConfigureAwait(false);
            await StoragePublish.PublishOwnedAsync(
                _db, _artifacts, staged,
                async (stored, ct) =>
                {
                    job.ArtifactStorageKey = stored.StorageKey;
                    job.ArtifactContentType = "image/png";
                    job.ArtifactByteLength = stored.SizeBytes;
                    job.RenderedAt = Now;
                    PrintJobStateMachine.EnsureTransition(job.State, PrintJobStates.Ready);
                    job.State = PrintJobStates.Ready;
                    await _db.SaveChangesAsync(ct);
                },
                cancellationToken);
        }
        catch when (!cancellationToken.IsCancellationRequested)
        {
            job.State = PrintJobStates.Failed;
            job.FailureCode = "render_failed";
            job.CompletedAt = Now;
            await _db.SaveChangesAsync(CancellationToken.None);
        }
        return (ToJobDto(job), null);
    }

    /// <summary>Every job format that names a paper, and must wait for it.</summary>
    private static readonly string[] PaperFormats =
        [PrintFormats.Photo10x15, PrintFormats.Photo13x18, PrintFormats.Photo20x15, PrintFormats.Strip2x6Pair,
         PrintFormats.Photo4x6Pair, PrintFormats.Strip2x6Quad];

    public async Task<PrintClaimResponse?> ClaimAsync(Guid stationId, string? adapterKind,
        CancellationToken cancellationToken, bool supportsSimulation = false)
    {
        var now = Now;
        var station = await _db.PrintStations.AsNoTracking().SingleOrDefaultAsync(
            x => x.Id == stationId && x.Enabled && x.RevokedAt == null
                && x.DesiredState == PrintDesiredStates.Running, cancellationToken);
        if (station is null) return null;
        // An agent too old to be served gets no work: its station says why.
        if (!PrintAgentCompatibility.IsCompatible(station.AgentVersion)) return null;

        for (var attempt = 0; attempt < 4; attempt++)
        {
            var query = _db.PrintJobs.AsNoTracking()
                .Where(x => x.PrintStationId == stationId && x.ArtifactStorageKey != null
                    && (x.State == PrintJobStates.Ready
                        || (x.State == PrintJobStates.Claimed && x.LeaseUntil < now)))
                .Where(x => _db.PrinterDevices.Any(d => d.Id == x.PrinterDeviceId
                    && d.PrintStationId == stationId
                    && (d.LastObservedState == PrintDeviceStates.Ready
                        || d.LastObservedState == PrintDeviceStates.Busy)
                    // A sheet for another paper WAITS for that paper rather than
                    // reaching a printer that would waste it or stop on it. It
                    // prints the moment its paper is set again. A format that
                    // names no paper is left to the agent to refuse.
                    && (x.Format == d.LoadedPaperSize
                        || (x.Format == PrintFormats.Strip2x6Pair && d.LoadedPaperSize == PrintPapers.Photo10x15)
                        || ((x.Format == PrintFormats.Photo4x6Pair || x.Format == PrintFormats.Strip2x6Quad)
                            && d.LoadedPaperSize == PrintPapers.Photo20x15)
                        || !PaperFormats.Contains(x.Format))));
            if (!string.IsNullOrWhiteSpace(adapterKind))
                query = query.Where(x => _db.PrinterDevices.Any(d => d.Id == x.PrinterDeviceId
                    && d.AdapterKind == adapterKind));
            // A simulated job goes only to an agent that says it sends it to
            // the twin: an older one would put it on paper.
            if (!supportsSimulation) query = query.Where(x => !x.Simulated);
            // A joined 20x15 is two files: it waits for an agent that prints them.
            if (!PrintAgentCompatibility.PrintsJoinedParts(station.AgentVersion))
                query = query.Where(x => !PrintFormats.Joined.Contains(x.Format));
            var candidate = await query.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id)
                .Select(x => new { x.Id, x.State }).FirstOrDefaultAsync(cancellationToken);
            if (candidate is null) return null;
            var rawClaim = PrintSecurity.NewToken();
            var claimHash = PrintSecurity.Digest(rawClaim);
            var leaseUntil = now.AddSeconds(_options.ClaimLeaseSeconds);
            var rows = await _db.PrintJobs.Where(x => x.Id == candidate.Id && x.PrintStationId == stationId
                    && (x.State == PrintJobStates.Ready
                        || (x.State == PrintJobStates.Claimed && x.LeaseUntil < now)))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.State, PrintJobStates.Claimed)
                    .SetProperty(x => x.ClaimedAt, now)
                    .SetProperty(x => x.LeaseUntil, leaseUntil)
                    .SetProperty(x => x.ClaimTokenHash, claimHash), cancellationToken);
            if (rows != 1) continue;
            var job = await _db.PrintJobs.AsNoTracking().SingleAsync(x => x.Id == candidate.Id, cancellationToken);
            var deviceKey = await _db.PrinterDevices.AsNoTracking()
                .Where(x => x.Id == job.PrinterDeviceId && x.PrintStationId == stationId)
                .Select(x => x.DeviceKey).SingleAsync(cancellationToken);
            var url = $"/api/print-agent/jobs/{job.Id:D}/artifact";
            if (job.ArtifactContentType == PrintArtifactParts.ContentType)
            {
                // Each file on its own: its address, its size, a JPEG.
                await using var stored = await _artifacts.OpenReadAsync(job.ArtifactStorageKey!, cancellationToken);
                var lengths = await PrintArtifactParts.ReadLengthsAsync(stored, cancellationToken)
                    ?? throw new InvalidDataException("The job's parts artifact has no readable header.");
                var parts = lengths.Select((length, index) => new PrintArtifactPartDto(
                    $"{url}/{index}", length, PrintArtifactParts.PartContentType)).ToArray();
                return new(job.Id, rawClaim, job.Kind, job.Format, parts[0].Url, parts[0].ByteLength,
                    parts[0].ContentType, deviceKey, job.Simulated, parts);
            }
            return new(job.Id, rawClaim, job.Kind, job.Format,
                url, job.ArtifactByteLength!.Value,
                job.ArtifactContentType!, deviceKey, job.Simulated);
        }
        return null;
    }

    /// <summary>
    /// The claimed job's artifact, or with <paramref name="part"/> one file of
    /// an artifact made of several. A single file is its part 0; a parts
    /// artifact is never served whole.
    /// </summary>
    public async Task<PrintArtifact?> OpenArtifactAsync(Guid stationId, Guid jobId, string claimToken,
        CancellationToken cancellationToken, int? part = null)
    {
        var job = await FindClaimedAsync(stationId, jobId, claimToken, cancellationToken);
        if (job?.ArtifactStorageKey is null || job.ArtifactContentType is null) return null;
        if (job.ArtifactContentType != PrintArtifactParts.ContentType)
        {
            return part is null or 0
                ? new(await _artifacts.OpenReadAsync(job.ArtifactStorageKey, cancellationToken), job.ArtifactContentType)
                : null;
        }
        if (part is not int index) return null;
        await using var stored = await _artifacts.OpenReadAsync(job.ArtifactStorageKey, cancellationToken);
        var lengths = await PrintArtifactParts.ReadLengthsAsync(stored, cancellationToken);
        if (lengths is null || index < 0 || index >= lengths.Count) return null;
        // The stream stands after the header: skip the parts before this one.
        for (var i = 0; i < index; i++) await Skip(stored, lengths[i], cancellationToken);
        var bytes = new byte[lengths[index]];
        await stored.ReadExactlyAsync(bytes, cancellationToken);
        return new(new MemoryStream(bytes, writable: false), PrintArtifactParts.PartContentType);
    }

    private static async Task Skip(Stream stream, int count, CancellationToken cancellationToken)
    {
        var buffer = new byte[Math.Min(count, 81920)];
        while (count > 0)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(count, buffer.Length)), cancellationToken);
            if (read == 0) throw new EndOfStreamException();
            count -= read;
        }
    }

    public async Task<bool> MarkSubmittingAsync(Guid stationId, Guid jobId, string claimToken,
        CancellationToken cancellationToken)
    {
        var job = await FindClaimedAsync(stationId, jobId, claimToken, cancellationToken);
        if (job is null) return false;
        if (job.State == PrintJobStates.Submitting) return true;
        PrintJobStateMachine.EnsureTransition(job.State, PrintJobStates.Submitting);
        job.State = PrintJobStates.Submitting;
        job.LeaseUntil = Now.AddSeconds(_options.ClaimLeaseSeconds);
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> ReportResultAsync(Guid stationId, Guid jobId, PrintResultRequest request,
        CancellationToken cancellationToken)
    {
        var job = await _db.PrintJobs.SingleOrDefaultAsync(
            x => x.Id == jobId && x.PrintStationId == stationId, cancellationToken);
        if (job is null || job.ClaimTokenHash is null
            || !PrintSecurity.FixedTimeEquals(job.ClaimTokenHash, request.ClaimToken)) return false;
        var target = request.Outcome switch
        {
            "completed" => PrintJobStates.Completed,
            "failed" => PrintJobStates.Failed,
            "delivery-unknown" => PrintJobStates.DeliveryUnknown,
            _ => throw new ArgumentException("invalid_outcome"),
        };
        if (job.State == target) return true;
        if (PrintJobStates.IsTerminal(job.State)) return false;
        if (target == PrintJobStates.Completed)
        {
            if (job.State == PrintJobStates.Submitting)
            {
                job.State = PrintJobStates.Submitted;
                job.SubmittedAt = Now;
            }
            PrintJobStateMachine.EnsureTransition(job.State, PrintJobStates.Completed);
        }
        else PrintJobStateMachine.EnsureTransition(job.State, target);
        job.State = target;
        job.FailureCode = target == PrintJobStates.Completed ? null : NormalizeFailure(request.FailureCode, target);
        job.CompletedAt = Now;
        job.LeaseUntil = null;
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> CancelAsync(Guid ownerId, Guid jobId, CancellationToken cancellationToken)
    {
        // Whoever sent it, or the owner of the printer it waits on: a lent
        // printer's queue is still the owner's to clear.
        var job = await _db.PrintJobs.SingleOrDefaultAsync(x => x.Id == jobId
            && (x.OwnerUserId == ownerId
                || _db.PrintStations.Any(s => s.Id == x.PrintStationId && s.OwnerUserId == ownerId)),
            cancellationToken);
        if (job is null || job.State is not (PrintJobStates.Requested or PrintJobStates.Rendering or PrintJobStates.Ready))
            return false;
        PrintJobStateMachine.EnsureTransition(job.State, PrintJobStates.Cancelled);
        job.State = PrintJobStates.Cancelled;
        job.CompletedAt = Now;
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> RetryAsync(Guid ownerId, Guid jobId, CancellationToken cancellationToken)
    {
        var job = await _db.PrintJobs.SingleOrDefaultAsync(x => x.Id == jobId
            && (x.OwnerUserId == ownerId
                || _db.PrintStations.Any(s => s.Id == x.PrintStationId && s.OwnerUserId == ownerId)),
            cancellationToken);
        if (job is null || job.State != PrintJobStates.Failed || job.ArtifactStorageKey is null) return false;
        // Sending it again puts it back on the printer: the printer's owner
        // always may, its sender only while they may still print on it. A
        // retry is the same sheet, so it takes nothing of a loan's ceiling.
        if (await _printers.ForUserAsync(ownerId, job.PrintStationId, job.PrinterDeviceId, cancellationToken) is null)
            return false;
        PrintJobStateMachine.EnsureTransition(job.State, PrintJobStates.Ready);
        job.State = PrintJobStates.Ready;
        job.FailureCode = null;
        job.CompletedAt = null;
        job.ClaimTokenHash = null;
        job.LeaseUntil = null;
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task<PrintJob?> FindClaimedAsync(Guid stationId, Guid jobId, string claimToken,
        CancellationToken cancellationToken)
    {
        var job = await _db.PrintJobs.SingleOrDefaultAsync(
            x => x.Id == jobId && x.PrintStationId == stationId
                && (x.State == PrintJobStates.Claimed || x.State == PrintJobStates.Submitting),
            cancellationToken);
        return job?.ClaimTokenHash is not null
            && PrintSecurity.FixedTimeEquals(job.ClaimTokenHash, claimToken) ? job : null;
    }

    private async Task TransitionAsync(PrintJob job, string target, CancellationToken cancellationToken)
    {
        PrintJobStateMachine.EnsureTransition(job.State, target);
        job.State = target;
        await _db.SaveChangesAsync(cancellationToken);
    }

    private (PrintStationEnrollment Enrollment, string Raw) NewEnrollment(Guid stationId, DateTime now)
    {
        var raw = PrintSecurity.NewToken();
        return (new PrintStationEnrollment
        {
            Id = Guid.NewGuid(), PrintStationId = stationId, TokenHash = PrintSecurity.Digest(raw),
            CreatedAt = now, ExpiresAt = now.AddMinutes(Math.Clamp(_options.EnrollmentMinutes, 1, 60)),
        }, raw);
    }

    /// <summary>No photo printer's media holds anywhere near this many prints.</summary>
    internal const int MaxMediaRemainingPrints = 100_000;

    /// <summary>A reading older than this is still the last one, dated no earlier.</summary>
    internal static readonly TimeSpan MaxMediaReadingAge = TimeSpan.FromDays(30);

    /// <summary>
    /// The physical media count. An agent that does not send it is an older one,
    /// and the stored reading is left exactly as it was — never made to look
    /// fresh. "Not available" clears it. A count outside any plausible media is
    /// treated as no count rather than refusing the heartbeat: the printer's
    /// state matters more than a number it could not report sensibly.
    /// </summary>
    private void ApplyMediaRemaining(Guid stationId, PrinterDevice device,
        PrinterMediaRemainingReport? report, DateTime now)
    {
        if (report is null) return;
        if (!report.Available || report.RemainingPrints is not int remaining
            || remaining < 0 || remaining > MaxMediaRemainingPrints)
        {
            if (device.MediaRemainingPrints is not null)
                _logger.LogInformation("print.media.remaining.unavailable station={StationId} device={DeviceId}",
                    stationId, device.Id);
            device.MediaRemainingPrints = null;
            device.MediaRemainingObservedAt = null;
            return;
        }
        var age = TimeSpan.FromSeconds(Math.Clamp(report.AgeSeconds ?? 0, 0, (int)MaxMediaReadingAge.TotalSeconds));
        if (device.MediaRemainingPrints != remaining)
            _logger.LogInformation(
                "print.media.remaining.observed station={StationId} device={DeviceId} remainingPrints={RemainingPrints}",
                stationId, device.Id, remaining);
        device.MediaRemainingPrints = remaining;
        device.MediaRemainingObservedAt = now - age;
    }

    private static void ValidateDevice(PrinterDeviceReport report)
    {
        if (string.IsNullOrWhiteSpace(report.DeviceKey) || report.DeviceKey.Length > 256
            || string.IsNullOrWhiteSpace(report.DisplayName) || report.DisplayName.Length > 160
            || string.IsNullOrWhiteSpace(report.AdapterKind) || report.AdapterKind.Length > 40
            || report.ObservedState is not (PrintDeviceStates.Ready or PrintDeviceStates.Busy
                or PrintDeviceStates.Offline or PrintDeviceStates.Error or PrintDeviceStates.Unknown))
            throw new ArgumentException("invalid_device_report");
    }

    /// <summary>A 40-hex source commit, lower case; anything else is no build.</summary>
    private static string? NormalizeBuild(string? value) =>
        value is { Length: 40 } && value.All(Uri.IsHexDigit) ? value.ToLowerInvariant() : null;
    private static string NormalizeVersion(string value) =>
        string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim()[..Math.Min(64, value.Trim().Length)];
    private static string? TrimOrNull(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(max, value.Trim().Length)];
    private static string NormalizeFailure(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim()[..Math.Min(64, value.Trim().Length)];
    private static PrintDeviceDto ToDeviceDto(PrinterDevice x, IReadOnlyDictionary<Guid, Person>? people = null) =>
        new(x.Id, x.DisplayName, x.Manufacturer, x.Model, x.AdapterKind, x.LastObservedState, x.LastSeenAt,
            PrintCapabilityMatcher.SupportsFormat(x.CapabilitiesJson, PrintFormats.Photo10x15),
            PrintCapabilityMatcher.SupportsFormat(x.CapabilitiesJson, PrintFormats.Strip2x6Pair),
            new PrintCalibrationDto(x.CalibrationBrightness, x.CalibrationContrast,
                x.CalibrationGamma, x.CalibrationSaturation),
            PaperOf(x), Papers(x),
            x.LoadedPaperChangedByUserId is Guid by ? people?.GetValueOrDefault(by)?.Name : null,
            x.LoadedPaperChangedAt,
            MediaRemainingPrints: x.MediaRemainingPrints,
            MediaRemainingObservedAt: x.MediaRemainingObservedAt,
            Simulated: PrintSimulation.IsRequested(x),
            SimulationAvailable: x.SimulationAvailable,
            SimulationOnly: x.SimulationOnly,
            CutFormats: PrintCapabilityMatcher.CutFormats(x.CapabilitiesJson));

    private static string PaperOf(PrinterDevice x) =>
        PrintPapers.IsKnown(x.LoadedPaperSize) ? x.LoadedPaperSize : PrintPapers.Photo10x15;

    private static IReadOnlyList<string> Papers(PrinterDevice x) =>
        PrintPapers.All.Where(p => PrintCapabilityMatcher.SupportsFormat(x.CapabilitiesJson, p)).ToList();

    private static PrintJobSummaryDto ToJobDto(PrintJob x, string? waitingForPaper = null, string? ownerName = null) =>
        new(x.Id, x.Id.ToString("N")[..8], x.Kind, x.Format, x.State, x.CreatedAt, x.FailureCode,
            waitingForPaper, ownerName, x.Simulated);

    /// <summary>
    /// The paper a READY job is held for, when its printer has another in — or
    /// null when it is not waiting for paper.
    /// </summary>
    private static string? WaitingFor(PrintJob job, IEnumerable<PrinterDevice> devices)
    {
        if (job.State != PrintJobStates.Ready) return null;
        var needed = PrintPapers.RequiredFor(job.Format);
        var device = devices.FirstOrDefault(d => d.Id == job.PrinterDeviceId);
        return needed is not null && device is not null && PaperOf(device) != needed ? needed : null;
    }

    /// <summary>
    /// Per person, what one printer has been given to print: every sheet it
    /// accepted — which is what a loan's ceiling counts too — how many came
    /// out, on which paper, and from where. The whole history, across loans.
    /// </summary>
    private static IReadOnlyList<PrinterUsageDto> Usage(
        IEnumerable<UsageRow> rows, Guid ownerId, IReadOnlyDictionary<Guid, Person> people) =>
        rows.GroupBy(r => r.OwnerUserId)
            .Select(g => new PrinterUsageDto(
                g.Key == ownerId ? null : people.GetValueOrDefault(g.Key)?.Name ?? string.Empty,
                g.Key == ownerId,
                g.Sum(r => r.Count),
                g.Where(r => r.State == PrintJobStates.Completed).Sum(r => r.Count),
                g.GroupBy(r => PrintPapers.RequiredFor(r.Format) ?? r.Format)
                    .OrderBy(p => p.Key, StringComparer.Ordinal)
                    .ToDictionary(p => p.Key, p => p.Sum(r => r.Count)),
                // A party's sheets: the guests' and the host's QR cards for its tables.
                g.Where(r => PrintJobKinds.IsParty(r.Kind) || r.Kind == PrintJobKinds.PartyQrCard).Sum(r => r.Count),
                g.Where(r => PrintJobKinds.IsOwner(r.Kind)).Sum(r => r.Count),
                g.Where(r => r.Kind == PrintJobKinds.Diagnostic).Sum(r => r.Count)))
            .OrderByDescending(u => u.IsYou).ThenByDescending(u => u.Sheets)
            .ToArray();

    /// <summary>How many rendered sheets share one printer, sender, kind, format and state.</summary>
    private sealed record UsageRow(
        Guid PrinterDeviceId, Guid OwnerUserId, string Kind, string Format, string State, int Count);

    private sealed record Person(string Name, string Email);

    private static string Name(string displayName, string email) =>
        string.IsNullOrWhiteSpace(displayName) ? email : displayName;

    private async Task<IReadOnlyDictionary<Guid, Person>> NamesAsync(
        IEnumerable<Guid> ids, CancellationToken cancellationToken)
    {
        var wanted = ids.Distinct().ToArray();
        if (wanted.Length == 0) return new Dictionary<Guid, Person>();
        var rows = await _db.Users.AsNoTracking().Where(u => wanted.Contains(u.Id))
            .Select(u => new { u.Id, u.DisplayName, u.Email }).ToListAsync(cancellationToken);
        return rows.ToDictionary(u => u.Id, u => new Person(Name(u.DisplayName, u.Email), u.Email));
    }
    private DateTime Now => _clock.GetUtcNow().UtcDateTime;
}
