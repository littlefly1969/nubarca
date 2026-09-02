using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NubArca.Api.Data;
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

    public PrintStationService(AppDbContext db, TimeProvider clock, IOptions<PrintOptions> options,
        IDerivedBlobStorage artifacts, PrintArtifactRenderer renderer)
    {
        _db = db;
        _clock = clock;
        _options = options.Value;
        _artifacts = artifacts;
        _renderer = renderer;
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
        var stationIds = stations.Select(x => x.Id).ToArray();
        var devices = await _db.PrinterDevices.AsNoTracking()
            .Where(x => stationIds.Contains(x.PrintStationId))
            .OrderBy(x => x.DisplayName).ToListAsync(cancellationToken);
        var jobs = await _db.PrintJobs.AsNoTracking()
            .Where(x => stationIds.Contains(x.PrintStationId))
            .OrderByDescending(x => x.CreatedAt).ToListAsync(cancellationToken);
        var now = Now;

        return stations.Select(station =>
        {
            var stationDevices = devices.Where(x => x.PrintStationId == station.Id).ToArray();
            var stationJobs = jobs.Where(x => x.PrintStationId == station.Id).ToArray();
            var current = stationJobs.FirstOrDefault(x => !PrintJobStates.IsTerminal(x.State));
            var lastError = stationJobs.FirstOrDefault(x => x.FailureCode != null)?.FailureCode;
            return new PrintStationDto(
                station.Id, station.Name, station.Enabled, station.DesiredState,
                PrintStationStatus.Calculate(station.LastSeenAt, now, station.RevokedAt != null,
                    station.Enabled, stationDevices.Select(x => x.LastObservedState),
                    _options.HeartbeatOnlineSeconds, _options.HeartbeatOfflineSeconds),
                station.LastSeenAt, station.AgentVersion, station.CreatedAt, station.RevokedAt,
                stationDevices.Select(ToDeviceDto).ToArray(),
                stationJobs.Count(x => !PrintJobStates.IsTerminal(x.State)),
                current is null ? null : ToJobDto(current), lastError);
        }).ToArray();
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

    public async Task<bool> RevokeAsync(Guid ownerId, Guid stationId, CancellationToken cancellationToken)
    {
        var now = Now;
        var rows = await _db.PrintStations
            .Where(x => x.Id == stationId && x.OwnerUserId == ownerId && x.RevokedAt == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.RevokedAt, now)
                .SetProperty(x => x.Enabled, false)
                .SetProperty(x => x.DesiredState, PrintDesiredStates.Disabled)
                .SetProperty(x => x.CredentialHash, (string?)null), cancellationToken);
        return rows == 1;
    }

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
        }
        foreach (var missing in knownDevices.Values.Where(x => !reportedKeys.Contains(x.DeviceKey)))
            missing.LastObservedState = PrintDeviceStates.Offline;
        await _db.SaveChangesAsync(cancellationToken);
        return new(station.DesiredState, now);
    }

    public async Task<PrintJobSummaryDto?> CreateTestPrintAsync(Guid ownerId, Guid stationId,
        Guid printerId, CancellationToken cancellationToken)
    {
        var station = await _db.PrintStations.SingleOrDefaultAsync(
            x => x.Id == stationId && x.OwnerUserId == ownerId && x.Enabled && x.RevokedAt == null,
            cancellationToken);
        var printer = await _db.PrinterDevices.SingleOrDefaultAsync(
            x => x.Id == printerId && x.PrintStationId == stationId, cancellationToken);
        if (station is null || printer is null
            || !PrintCapabilityMatcher.SupportsFormat(printer.CapabilitiesJson, PrintFormats.Photo10x15))
            return null;
        var now = Now;
        var job = new PrintJob
        {
            Id = Guid.NewGuid(), OwnerUserId = ownerId, PrintStationId = stationId,
            PrinterDeviceId = printerId, Kind = PrintJobKinds.Diagnostic,
            Format = PrintFormats.Photo10x15, State = PrintJobStates.Requested,
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
                cancellationToken: cancellationToken);
            await using var source = new MemoryStream(bytes, writable: false);
            var stored = await _artifacts.WriteAsync(source, cancellationToken);
            job.ArtifactStorageKey = stored.StorageKey;
            job.ArtifactContentType = "image/png";
            job.ArtifactByteLength = stored.SizeBytes;
            job.RenderedAt = Now;
            PrintJobStateMachine.EnsureTransition(job.State, PrintJobStates.Ready);
            job.State = PrintJobStates.Ready;
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch when (!cancellationToken.IsCancellationRequested)
        {
            job.State = PrintJobStates.Failed;
            job.FailureCode = "render_failed";
            job.CompletedAt = Now;
            await _db.SaveChangesAsync(CancellationToken.None);
        }
        return ToJobDto(job);
    }

    public async Task<PrintClaimResponse?> ClaimAsync(Guid stationId, string? adapterKind,
        CancellationToken cancellationToken)
    {
        var now = Now;
        var station = await _db.PrintStations.AsNoTracking().SingleOrDefaultAsync(
            x => x.Id == stationId && x.Enabled && x.RevokedAt == null
                && x.DesiredState == PrintDesiredStates.Running, cancellationToken);
        if (station is null) return null;

        for (var attempt = 0; attempt < 4; attempt++)
        {
            var query = _db.PrintJobs.AsNoTracking()
                .Where(x => x.PrintStationId == stationId && x.ArtifactStorageKey != null
                    && (x.State == PrintJobStates.Ready
                        || (x.State == PrintJobStates.Claimed && x.LeaseUntil < now)))
                .Where(x => _db.PrinterDevices.Any(d => d.Id == x.PrinterDeviceId
                    && d.PrintStationId == stationId
                    && (d.LastObservedState == PrintDeviceStates.Ready
                        || d.LastObservedState == PrintDeviceStates.Busy)));
            if (!string.IsNullOrWhiteSpace(adapterKind))
                query = query.Where(x => _db.PrinterDevices.Any(d => d.Id == x.PrinterDeviceId
                    && d.AdapterKind == adapterKind));
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
            return new(job.Id, rawClaim, job.Kind, job.Format,
                $"/api/print-agent/jobs/{job.Id:D}/artifact", job.ArtifactByteLength!.Value,
                job.ArtifactContentType!, deviceKey);
        }
        return null;
    }

    public async Task<PrintArtifact?> OpenArtifactAsync(Guid stationId, Guid jobId, string claimToken,
        CancellationToken cancellationToken)
    {
        var job = await FindClaimedAsync(stationId, jobId, claimToken, cancellationToken);
        if (job?.ArtifactStorageKey is null || job.ArtifactContentType is null) return null;
        return new(await _artifacts.OpenReadAsync(job.ArtifactStorageKey, cancellationToken),
            job.ArtifactContentType);
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
        var job = await _db.PrintJobs.SingleOrDefaultAsync(x => x.Id == jobId && x.OwnerUserId == ownerId,
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
        var job = await _db.PrintJobs.SingleOrDefaultAsync(x => x.Id == jobId && x.OwnerUserId == ownerId,
            cancellationToken);
        if (job is null || job.State != PrintJobStates.Failed || job.ArtifactStorageKey is null) return false;
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

    private static void ValidateDevice(PrinterDeviceReport report)
    {
        if (string.IsNullOrWhiteSpace(report.DeviceKey) || report.DeviceKey.Length > 256
            || string.IsNullOrWhiteSpace(report.DisplayName) || report.DisplayName.Length > 160
            || string.IsNullOrWhiteSpace(report.AdapterKind) || report.AdapterKind.Length > 40
            || report.ObservedState is not (PrintDeviceStates.Ready or PrintDeviceStates.Busy
                or PrintDeviceStates.Offline or PrintDeviceStates.Error or PrintDeviceStates.Unknown))
            throw new ArgumentException("invalid_device_report");
    }

    private static string NormalizeVersion(string value) =>
        string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim()[..Math.Min(64, value.Trim().Length)];
    private static string? TrimOrNull(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(max, value.Trim().Length)];
    private static string NormalizeFailure(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim()[..Math.Min(64, value.Trim().Length)];
    private static PrintDeviceDto ToDeviceDto(PrinterDevice x) =>
        new(x.Id, x.DisplayName, x.Manufacturer, x.Model, x.AdapterKind, x.LastObservedState, x.LastSeenAt,
            PrintCapabilityMatcher.SupportsFormat(x.CapabilitiesJson, PrintFormats.Photo10x15));
    private static PrintJobSummaryDto ToJobDto(PrintJob x) =>
        new(x.Id, x.Id.ToString("N")[..8], x.Kind, x.Format, x.State, x.CreatedAt, x.FailureCode);
    private DateTime Now => _clock.GetUtcNow().UtcDateTime;
}
