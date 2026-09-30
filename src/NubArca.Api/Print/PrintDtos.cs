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
    IReadOnlyList<string>? Papers = null);
/// <summary>The paper now in the printer: one of 10x15, 13x18, 20x15.</summary>
public sealed record SetPrinterPaperRequest(string? PaperSize);
public sealed record PrintCalibrationDto(double Brightness, double Contrast, double Gamma, double Saturation);
public sealed record PrintJobSummaryDto(Guid Id, string ShortCode, string Kind, string Format,
    string State, DateTime CreatedAt, string? FailureCode);
public sealed record PrintStationDto(
    Guid Id, string Name, bool Enabled, string DesiredState, string Status,
    DateTime? LastSeenAt, string? AgentVersion, DateTime CreatedAt, DateTime? RevokedAt,
    IReadOnlyList<PrintDeviceDto> Devices, int QueueCount,
    PrintJobSummaryDto? CurrentJob, string? LastError);
public sealed record CreateTestPrintRequest(Guid PrinterDeviceId);
public sealed record PrintClaimRequest(string? AdapterKind = null);
public sealed record PrintClaimResponse(
    Guid JobId, string ClaimToken, string Kind, string Format,
    string ArtifactUrl, long ArtifactByteLength, string ContentType, string DeviceKey);
public sealed record PrintSubmittingRequest(string ClaimToken);
public sealed record PrintResultRequest(string ClaimToken, string Outcome, string? FailureCode, string? SpoolReference);
public sealed record PrintArtifact(Stream Content, string ContentType);
