namespace NubArca.Api.Domain.Print;

public sealed class PrinterDevice
{
    public Guid Id { get; set; }
    public Guid PrintStationId { get; set; }
    public string DeviceKey { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Manufacturer { get; set; }
    public string? Model { get; set; }
    public string AdapterKind { get; set; } = string.Empty;
    public string CapabilitiesJson { get; set; } = "{}";
    public string LastObservedState { get; set; } = PrintDeviceStates.Unknown;
    public DateTime LastSeenAt { get; set; }
}

public static class PrintDeviceStates
{
    public const string Ready = "ready";
    public const string Busy = "busy";
    public const string Offline = "offline";
    public const string Error = "error";
    public const string Unknown = "unknown";
}
