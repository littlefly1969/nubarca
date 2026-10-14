using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Domain.Print;

namespace NubArca.Api.Print;

/// <summary>
/// Whether a printer's jobs are to be SIMULATED: the owner's intent, or a box
/// that has nothing but a twin. Never whether the twin is there right now —
/// availability decides whether a simulated job can run, not whether a job is
/// simulated. A twin that disappears makes simulated jobs wait or fail
/// <c>simulation_unavailable</c>; it never turns one into a print on paper.
/// Fixed on each job when it is created.
/// </summary>
public static class PrintSimulation
{
    public static bool IsRequested(PrinterDevice device) => device.SimulationOnly || device.Simulated;

    /// <summary>The same rule, read for one printer by id when a job is about to be created.</summary>
    public static async Task<bool> IsRequestedAsync(AppDbContext db, Guid printerDeviceId, CancellationToken cancellationToken) =>
        await db.PrinterDevices.AsNoTracking()
            .Where(x => x.Id == printerDeviceId)
            .Select(x => x.SimulationOnly || x.Simulated)
            .SingleOrDefaultAsync(cancellationToken);

    /// <summary>The most recent cleared jobs a purge names: more than the twin's backend ever keeps.</summary>
    public const int PurgeJobLimit = 100;
}
