using System.Text.Json.Serialization;

namespace NubArca.Api.Print;

public sealed record CreatePrintStationRequest(string Name);
public sealed record SetPrintStationStateRequest(string DesiredState);
public sealed record CreatePrintStationResponse(
    Guid Id, string Name, string EnrollmentToken, DateTime EnrollmentExpiresAt);
/// <summary><c>AgentBuild</c>: the source commit the agent was built from; absent from an agent that predates it.</summary>
public sealed record PrintEnrollmentRequest(Guid StationId, string EnrollmentToken, string AgentVersion,
    string? AgentBuild = null);
public sealed record PrintEnrollmentResponse(Guid StationId, string StationCredential, string DesiredState);
public sealed record PrinterDeviceReport(
    string DeviceKey, string DisplayName, string? Manufacturer, string? Model,
    string AdapterKind, object Capabilities, string ObservedState,
    /// <summary>
    /// Absent from an agent that predates it — the stored count is then left
    /// exactly as it is. A current agent always sends it, available or not.
    /// </summary>
    PrinterMediaRemainingReport? MediaRemaining = null,
    /// <summary>The simulated twin the agent offers; absent from an agent that has none.</summary>
    PrinterSimulationReport? Simulation = null);

/// <summary>
/// <c>available</c>: the agent has a simulated twin of this printer (same queues
/// and driver, a backend that writes a file); <c>only</c>: it has nothing else.
/// </summary>
public sealed record PrinterSimulationReport(bool Available, bool Only);

/// <summary>
/// The physical media count as the agent read it: <c>available</c> false when it
/// asked and the printer gave no number; <c>ageSeconds</c> how old the reading
/// is, never a timestamp of the box's own.
/// </summary>
public sealed record PrinterMediaRemainingReport(bool Available, int? RemainingPrints, int? AgeSeconds);
public sealed record PrintHeartbeatRequest(string AgentVersion, IReadOnlyList<PrinterDeviceReport> Devices,
    string? AgentBuild = null,
    /// <summary>The agent can update itself: a Print Box whose root update service is installed.</summary>
    bool? SelfUpdate = null,
    /// <summary>How its last self-update ended, as the box's update service wrote it: <c>failed 0.9.0 &lt;request id&gt;</c>.</summary>
    string? UpdateResult = null);
public sealed record PrintHeartbeatResponse(string DesiredState, DateTime ServerTime,
    /// <summary>The printers to show as their simulated twin: status and prints left come from it.</summary>
    IReadOnlyList<string>? SimulatedDeviceKeys = null,
    /// <summary>
    /// Per printer, the simulated output the owner cleared: the box deletes the
    /// files of exactly these jobs from that printer's twin queues, once per
    /// sequence, and touches nothing else.
    /// </summary>
    IReadOnlyList<SimulatedOutputPurge>? SimulatedOutputPurges = null,
    /// <summary>
    /// Update to this bundle, when it is idle: only while an update was asked
    /// for and the agent can update itself. The box's root update service then
    /// fetches it through <c>/api/print-agent/update</c> and checks this hash.
    /// </summary>
    PrintAgentUpdateOffer? AgentUpdate = null);

/// <summary>The bundle a box is asked to install, as the server published it.</summary>
public sealed record PrintAgentUpdateOffer(string Version, string FileName, string Sha256, long SizeBytes,
    /// <summary>Echoed back by the box with the outcome, so an earlier attempt's outcome is never this one's.</summary>
    string RequestId);

/// <summary>One printer's latest clear: its sequence, and the jobs (N-format ids) whose files go.</summary>
public sealed record SimulatedOutputPurge(string DeviceKey, long Sequence, IReadOnlyList<string> JobIds);
public sealed record PrintDeviceDto(
    Guid Id, string DisplayName, string? Manufacturer, string? Model,
    string AdapterKind, string ObservedState, DateTime LastSeenAt, bool SupportsPhoto10x15,
    /// <summary>The printer cuts a strip sheet into two 2x6 strips itself (reports 2x6x2).</summary>
    bool CutsStrips = false,
    /// <summary>The owner's tone compensation for this printer; all 1 when neutral.</summary>
    PrintCalibrationDto? Calibration = null,
    /// <summary>The paper the operator says is loaded (10x15, 13x18 or 20x15).</summary>
    string LoadedPaperSize = "10x15",
    /// <summary>The papers the Print Agent reports this printer can print, in PrintPapers order.</summary>
    IReadOnlyList<string>? Papers = null,
    /// <summary>Who last set the loaded paper — the owner or the person it is lent to — and when.</summary>
    string? LoadedPaperChangedBy = null,
    DateTime? LoadedPaperChangedAt = null,
    /// <summary>The owner's view only: whom this printer is lent to now.</summary>
    IReadOnlyList<PrinterShareDto>? Shares = null,
    /// <summary>The owner's view only: sheets per person, across the whole history.</summary>
    IReadOnlyList<PrinterUsageDto>? Usage = null,
    /// <summary>
    /// Prints physically left on the loaded media, as the printer last reported
    /// them, and when — null when it reports none. Shown as current only while
    /// the printer is online; otherwise as a last reading.
    /// </summary>
    int? MediaRemainingPrints = null,
    DateTime? MediaRemainingObservedAt = null,
    /// <summary>Its jobs are simulated (the owner's intent, or a simulation-only box) — whether or not the twin is there now.</summary>
    bool Simulated = false,
    /// <summary>The agent can run a simulated job right now: the owner may switch on, and simulated jobs can print.</summary>
    bool SimulationAvailable = false,
    /// <summary>Nothing but the twin: always simulated.</summary>
    bool SimulationOnly = false,
    /// <summary>The cut sheets the printer makes itself (2x6x2, 4x6x2, 2x6x4), as the Print Agent reports them.</summary>
    IReadOnlyList<string>? CutFormats = null);
/// <summary>A live loan of a printer, as its owner sees it.</summary>
public sealed record PrinterShareDto(
    Guid Id, string GranteeName, string GranteeEmail, int? MaxSheets, int UsedSheets, DateTime CreatedAt);
/// <summary>
/// One person's sheets on one printer: accepted (what a ceiling counts),
/// printed, per paper, and from where. Never what was on them.
/// </summary>
public sealed record PrinterUsageDto(
    string? Name, bool IsYou, int Sheets, int Completed, IReadOnlyDictionary<string, int> ByPaper,
    int Parties, int Album, int Tests);
/// <summary>A printer lent TO the reader: what they may print on and set, nothing of the owner's other things.</summary>
public sealed record SharedPrinterDto(
    Guid ShareId, Guid StationId, string StationName, string StationStatus,
    Guid DeviceId, string DisplayName, string ObservedState, string OwnerName,
    string LoadedPaperSize, IReadOnlyList<string> Papers, bool SupportsPhoto10x15, bool CutsStrips,
    string? LoadedPaperChangedBy, DateTime? LoadedPaperChangedAt, int? MaxSheets, int UsedSheets,
    /// <summary>Prints physically left on the media (the printer's count, not the loan's).</summary>
    int? MediaRemainingPrints = null, DateTime? MediaRemainingObservedAt = null,
    /// <summary>Its prints go to a file, not to paper.</summary>
    bool Simulated = false,
    /// <summary>The cut sheets the printer makes itself (2x6x2, 4x6x2, 2x6x4).</summary>
    IReadOnlyList<string>? CutFormats = null);
public sealed record SharePrinterRequest(string? Email, int? MaxSheets);
/// <summary>
/// A loan's new ceiling. <c>maxSheets</c> must be present: null removes the
/// ceiling, so it is never inferred from a body that left it out.
/// </summary>
public sealed record UpdatePrinterShareRequest([property: JsonRequired] int? MaxSheets);
/// <summary>The paper now in the printer: one of 10x15, 13x18, 20x15.</summary>
public sealed record SetPrinterPaperRequest(string? PaperSize);
public sealed record PrintCalibrationDto(double Brightness, double Contrast, double Gamma, double Saturation);
public sealed record PrintJobSummaryDto(Guid Id, string ShortCode, string Kind, string Format,
    string State, DateTime CreatedAt, string? FailureCode,
    /// <summary>The paper a ready job waits for, when its printer has another in.</summary>
    string? WaitingForPaper = null,
    /// <summary>Who sent it, when that is not the printer's owner — a job from a loan.</summary>
    string? OwnerName = null,
    /// <summary>Goes to the simulated twin, not to paper.</summary>
    bool Simulated = false);
public sealed record PrintStationDto(
    Guid Id, string Name, bool Enabled, string DesiredState, string Status,
    DateTime? LastSeenAt, string? AgentVersion, DateTime CreatedAt, DateTime? RevokedAt,
    IReadOnlyList<PrintDeviceDto> Devices, int QueueCount,
    PrintJobSummaryDto? CurrentJob, string? LastError,
    /// <summary>What is waiting, oldest first, whoever sent it.</summary>
    IReadOnlyList<PrintJobSummaryDto>? Queue = null,
    /// <summary>The agent's version and build against this server and the bundle it hands out.</summary>
    PrintAgentStatusDto? Agent = null);
public sealed record CreateTestPrintRequest(Guid PrinterDeviceId);
/// <summary>What erasing a revoked station takes with it: counts, never what was printed or for whom.</summary>
public sealed record PrintStationErasePreview(int Jobs, int Parties);
/// <summary><c>SupportsSimulation</c>: the agent sends a simulated job to its twin; an agent that does not say so is never given one.</summary>
public sealed record PrintClaimRequest(string? AdapterKind = null, bool SupportsSimulation = false);
public sealed record PrintClaimResponse(
    Guid JobId, string ClaimToken, string Kind, string Format,
    string ArtifactUrl, long ArtifactByteLength, string ContentType, string DeviceKey,
    bool Simulated = false,
    /// <summary>A joined 20x15's two 10x15 files, in order, each downloaded on its own; null for a single file.</summary>
    IReadOnlyList<PrintArtifactPartDto>? ArtifactParts = null);
/// <summary>One file of a print artifact made of several.</summary>
public sealed record PrintArtifactPartDto(string Url, long ByteLength, string ContentType);
public sealed record SetPrinterSimulationRequest(bool Enabled);
/// <summary>A simulated print as its printer's owner sees it: what, when, whose — the sheet is downloaded apart.</summary>
public sealed record SimulatedPrintDto(Guid Id, string ShortCode, string Kind, string Format, string State,
    DateTime CreatedAt, string? OwnerName);
public sealed record PrintSubmittingRequest(string ClaimToken);
public sealed record PrintResultRequest(string ClaimToken, string Outcome, string? FailureCode, string? SpoolReference);
public sealed record PrintArtifact(Stream Content, string ContentType);
