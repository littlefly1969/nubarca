namespace NubArca.Api.Print;

public sealed record CreatePrintStationRequest(string Name);
public sealed record SetPrintStationStateRequest(string DesiredState);
public sealed record CreatePrintStationResponse(
    Guid Id, string Name, string EnrollmentToken, DateTime EnrollmentExpiresAt);
public sealed record PrintEnrollmentRequest(Guid StationId, string EnrollmentToken, string AgentVersion);
public sealed record PrintEnrollmentResponse(Guid StationId, string StationCredential, string DesiredState);
public sealed record PrinterDeviceReport(
    string DeviceKey, string DisplayName, string? Manufacturer, string? Model,
    string AdapterKind, object Capabilities, string ObservedState);
public sealed record PrintHeartbeatRequest(string AgentVersion, IReadOnlyList<PrinterDeviceReport> Devices);
public sealed record PrintHeartbeatResponse(string DesiredState, DateTime ServerTime);
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
    IReadOnlyList<PrinterUsageDto>? Usage = null);
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
    string? LoadedPaperChangedBy, DateTime? LoadedPaperChangedAt, int? MaxSheets, int UsedSheets);
public sealed record SharePrinterRequest(string? Email, int? MaxSheets);
public sealed record UpdatePrinterShareRequest(int? MaxSheets);
/// <summary>The paper now in the printer: one of 10x15, 13x18, 20x15.</summary>
public sealed record SetPrinterPaperRequest(string? PaperSize);
public sealed record PrintCalibrationDto(double Brightness, double Contrast, double Gamma, double Saturation);
public sealed record PrintJobSummaryDto(Guid Id, string ShortCode, string Kind, string Format,
    string State, DateTime CreatedAt, string? FailureCode,
    /// <summary>The paper a ready job waits for, when its printer has another in.</summary>
    string? WaitingForPaper = null,
    /// <summary>Who sent it, when that is not the printer's owner — a job from a loan.</summary>
    string? OwnerName = null);
public sealed record PrintStationDto(
    Guid Id, string Name, bool Enabled, string DesiredState, string Status,
    DateTime? LastSeenAt, string? AgentVersion, DateTime CreatedAt, DateTime? RevokedAt,
    IReadOnlyList<PrintDeviceDto> Devices, int QueueCount,
    PrintJobSummaryDto? CurrentJob, string? LastError,
    /// <summary>What is waiting, oldest first, whoever sent it.</summary>
    IReadOnlyList<PrintJobSummaryDto>? Queue = null);
public sealed record CreateTestPrintRequest(Guid PrinterDeviceId);
public sealed record PrintClaimRequest(string? AdapterKind = null);
public sealed record PrintClaimResponse(
    Guid JobId, string ClaimToken, string Kind, string Format,
    string ArtifactUrl, long ArtifactByteLength, string ContentType, string DeviceKey);
public sealed record PrintSubmittingRequest(string ClaimToken);
public sealed record PrintResultRequest(string ClaimToken, string Outcome, string? FailureCode, string? SpoolReference);
public sealed record PrintArtifact(Stream Content, string ContentType);
