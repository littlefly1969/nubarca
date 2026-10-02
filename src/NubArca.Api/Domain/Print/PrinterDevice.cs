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

    // The owner's tone compensation for this printer (see PrintCalibration).
    // 1 is neutral; the agent never writes these, so a heartbeat keeps them.
    public double CalibrationBrightness { get; set; } = 1;
    public double CalibrationContrast { get; set; } = 1;
    public double CalibrationGamma { get; set; } = 1;
    public double CalibrationSaturation { get; set; } = 1;

    /// <summary>
    /// The paper the OPERATOR says is loaded (a <see cref="PrintPapers"/> id).
    /// One roll at a time; the agent never writes it, so a heartbeat keeps it.
    /// Guests are offered only what this paper can make.
    /// </summary>
    public string LoadedPaperSize { get; set; } = PrintPapers.Photo10x15;

    /// <summary>
    /// When the loaded paper was last set, and by whom — the owner, or the
    /// person the printer is lent to, who is the one changing rolls. Shown
    /// beside the paper so nobody has to guess who changed it.
    /// </summary>
    public DateTime? LoadedPaperChangedAt { get; set; }
    public Guid? LoadedPaperChangedByUserId { get; set; }

    /// <summary>
    /// How many prints the media physically loaded in the printer still holds,
    /// as the PRINTER reports it (a DNP through Gutenprint and CUPS, read by the
    /// Print Agent). Null when the printer reports no reliable count, or an
    /// agent has said it has none. Telemetry only: never a party's budget,
    /// never a loan's ceiling, never a lock on a job.
    /// </summary>
    public int? MediaRemainingPrints { get; set; }

    /// <summary>
    /// When the printer last reported that count, on the SERVER's clock (the
    /// agent sends only how old its reading is). An agent that predates the
    /// count never touches either column.
    /// </summary>
    public DateTime? MediaRemainingObservedAt { get; set; }
}

public static class PrintDeviceStates
{
    public const string Ready = "ready";
    public const string Busy = "busy";
    public const string Offline = "offline";
    public const string Error = "error";
    public const string Unknown = "unknown";
}
