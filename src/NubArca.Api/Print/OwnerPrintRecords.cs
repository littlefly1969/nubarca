using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Domain.Print;

namespace NubArca.Api.Print;

/// <summary>
/// The record every print an owner sends keeps, whatever the sheet: the
/// direct print of a photograph and the party's QR card. A key names one sheet
/// (<see cref="OwnerPhotoPrintRequest"/>): repeated, it is answered with the
/// job it made; reused for another composition, it is refused.
/// </summary>
internal static class OwnerPrintRecords
{
    public static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public static async Task<OwnerPhotoPrintResult?> RepeatAsync(
        AppDbContext db, Guid ownerUserId, string keyHash, string fingerprint, CancellationToken cancellationToken)
    {
        var existing = await db.OwnerPhotoPrintRequests.AsNoTracking()
            .Where(r => r.OwnerUserId == ownerUserId && r.IdempotencyKeyHash == keyHash)
            .Select(r => new { r.PrintJobId, r.RequestFingerprint })
            .FirstOrDefaultAsync(cancellationToken);
        if (existing is null) return null;
        if (existing.RequestFingerprint != fingerprint)
            return OwnerPhotoPrintResult.Refuse(OwnerPhotoPrintErrors.IdempotencyConflict);
        var job = await db.PrintJobs.AsNoTracking()
            .Where(j => j.Id == existing.PrintJobId)
            .Select(j => new { j.PrintStationId, j.PrinterDeviceId })
            .FirstAsync(cancellationToken);
        var remaining = await db.PrinterDevices.AsNoTracking()
            .Where(d => d.Id == job.PrinterDeviceId).Select(d => d.MediaRemainingPrints).FirstOrDefaultAsync(cancellationToken);
        return OwnerPhotoPrintResult.Accept(
            await AcceptedAsync(db, existing.PrintJobId, job.PrintStationId, remaining, cancellationToken));
    }

    public static async Task<OwnerPhotoPrintAccepted> AcceptedAsync(
        AppDbContext db, Guid jobId, Guid stationId, int? mediaRemaining, CancellationToken cancellationToken)
    {
        var state = await db.PrintJobs.AsNoTracking().Where(j => j.Id == jobId)
            .Select(j => j.State).FirstAsync(cancellationToken);
        // The queue a sheet waits in is the machine's, whoever sent the others.
        var ahead = await db.PrintJobs.AsNoTracking()
            .Where(j => j.PrintStationId == stationId && j.Id != jobId && !PrintJobStates.Terminal.Contains(j.State)
                && j.CreatedAt <= db.PrintJobs.Where(x => x.Id == jobId).Select(x => x.CreatedAt).First())
            .CountAsync(cancellationToken);
        return new OwnerPhotoPrintAccepted(jobId, jobId.ToString("N")[..8], state, ahead, mediaRemaining);
    }
}
