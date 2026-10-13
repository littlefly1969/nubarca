using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Domain.Print;

namespace NubArca.Api.Print;

/// <summary>
/// Whether a printer's jobs go to its simulated twin. One rule, used when a job
/// is created — and fixed on the job then — and wherever a printer is shown.
/// </summary>
public static class PrintSimulation
{
    public static bool IsActive(PrinterDevice device) =>
        device.SimulationOnly || (device.Simulated && device.SimulationAvailable);

    /// <summary>The same rule, read for one printer by id when a job is about to be created.</summary>
    public static async Task<bool> IsActiveAsync(AppDbContext db, Guid printerDeviceId, CancellationToken cancellationToken) =>
        await db.PrinterDevices.AsNoTracking()
            .Where(x => x.Id == printerDeviceId)
            .Select(x => x.SimulationOnly || (x.Simulated && x.SimulationAvailable))
            .SingleOrDefaultAsync(cancellationToken);
}
