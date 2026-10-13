namespace NubArca.PrintAgent;

/// <summary>
/// A CUPS printer's simulated twin: for each queue, a second one made by the
/// installer with the SAME Gutenprint driver and page sizes, whose device is
/// the <c>nubarca-sim</c> backend — it receives exactly what the driver would
/// send the DNP, writes it to a file and reports the media as a DNP does. The
/// owner decides, per printer, whether its jobs go there instead of to paper.
/// </summary>
public sealed class PrintSimulationOptions
{
    /// <summary>Appended to a queue's name for its twin: NubArca-RX1HS → NubArca-RX1HS-SIM. Empty: no twin.</summary>
    public string? QueueSuffix { get; set; } = "-SIM";

    /// <summary>No real printer: the configured queues ARE the twins, and every job is simulated.</summary>
    public bool Only { get; set; }

    /// <summary>Where the backend writes, one directory per twin queue — cleared on the owner's order.</summary>
    public string OutputPath { get; set; } = "/var/spool/nubarca-sim";

    /// <summary>The prints the simulated media always reports, as a DNP's driver would.</summary>
    public const int RemainingPrints = 100;
}
