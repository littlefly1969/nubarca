using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using Microsoft.EntityFrameworkCore;

namespace NubArca.Api.Uploads;

public interface IUploadIdempotencyService
{
    // Returns the logical result of a COMPLETED operation with this key, or
    // null when no replayable result exists. Only ever answers within the
    // caller's own owner scope. A completed row whose file has since been
    // soft-deleted reconstructs nothing: the row is dropped so an explicit
    // retry may ingest fresh rather than resurrect deleted content.
    Task<FileSummary?> FindCompletedResultAsync(
        Guid ownerUserId, string operationKey, CancellationToken cancellationToken = default);

    // Atomically claims the (owner, key) operation slot. Exactly one concurrent
    // caller gets Claimed; the others learn the slot is InFlight or already
    // completed. An expired PENDING claim (crashed uploader past its lease) may
    // be taken over; a COMPLETED one never can be.
    Task<UploadClaim> TryClaimAsync(
        Guid ownerUserId,
        string operationKey,
        TimeSpan lease,
        CancellationToken cancellationToken = default);

    // NOTE: there is deliberately NO standalone Complete method. Completion is
    // performed INSIDE the authoritative FileItem transaction (see
    // FileItemService.CreateAsync's optional claim-token parameter), making
    // "FileItem durable" and "operation Completed(FileItemId)" one atomic fact.
    // Any post-commit error (lost response included) is absorbed by replay.

    // Failure path before ingestion produced anything: removes a still-pending
    // claim so the next attempt starts clean. Never touches a completed row and
    // never another caller's token.
    Task ReleaseAsync(Guid claimToken, CancellationToken cancellationToken = default);
}

// Backed by the same database as everything else so replay state survives
// restarts and is visible to every instance. All statements are conditional on
// the claim token / owner scope; nothing here trusts client identity.
public sealed class UploadIdempotencyService : IUploadIdempotencyService
{
    // Longer than any legitimate single upload of the 10 GiB ceiling on a slow
    // link, short enough that a hard-crashed claim recovers within a day.
    public static readonly TimeSpan DefaultLease = TimeSpan.FromHours(24);

    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;

    public UploadIdempotencyService(AppDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<FileSummary?> FindCompletedResultAsync(
        Guid ownerUserId, string operationKey, CancellationToken cancellationToken = default)
    {
        var op = await _db.UploadOperations
            .AsNoTracking()
            .SingleOrDefaultAsync(
                o => o.OwnerUserId == ownerUserId && o.OperationKey == operationKey,
                cancellationToken);

        if (op is null || op.Status != UploadOperationStatus.Completed || op.FileItemId is null)
        {
            return null;
        }

        var file = await _db.FileItems
            .AsNoTracking()
            .Where(f => f.Id == op.FileItemId
                && f.OwnerUserId == ownerUserId
                && f.DeletedAt == null)
            .Select(f => new FileSummary(
                f.Id, f.Name, f.MimeType, f.SizeBytes, f.CreatedAt, f.Width, f.Height))
            .FirstOrDefaultAsync(cancellationToken);

        if (file is not null)
        {
            return file;
        }

        // Soft-deleted or gone: keep no memory that would block or mislead a
        // deliberate retry. (A hard purge already cascaded the row away.)
        await _db.UploadOperations
            .Where(o => o.Id == op.Id)
            .ExecuteDeleteAsync(cancellationToken);
        return null;
    }

    public async Task<UploadClaim> TryClaimAsync(
        Guid ownerUserId,
        string operationKey,
        TimeSpan lease,
        CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;

        var inserted = await InsertFreshClaimAsync(ownerUserId, operationKey, now, lease, cancellationToken);
        if (inserted is not null)
        {
            return UploadClaim.Claimed(inserted.Value);
        }

        // Lost the unique-index race: someone else holds the slot. Decide from
        // THEIR current state — never from our assumptions. NoTracking matters:
        // a scoped context that already touched this row earlier in the same
        // request must not answer from its stale tracked copy for a decision
        // that arbitrates who owns the operation NOW.
        var existing = await _db.UploadOperations
            .AsNoTracking()
            .SingleOrDefaultAsync(
                o => o.OwnerUserId == ownerUserId && o.OperationKey == operationKey,
                cancellationToken);

        if (existing is null)
        {
            // The winner released between our failed insert and this read.
            // Conservative, retryable answer; a real retry will claim cleanly.
            return UploadClaim.InFlight();
        }

        if (existing.Status == UploadOperationStatus.Completed)
        {
            return UploadClaim.AlreadyCompleted();
        }

        if (existing.LeaseExpiresAt > now)
        {
            return UploadClaim.InFlight();
        }

        // Expired pending claim: crash recovery. Takeover must be optimistic —
        // delete exactly the stale row we observed, so a claim being renewed or
        // completed concurrently cannot be stolen underneath us.
        var tookOver = await _db.UploadOperations
            .Where(o => o.Id == existing.Id
                && o.Status == UploadOperationStatus.Pending
                && o.LeaseExpiresAt == existing.LeaseExpiresAt)
            .ExecuteDeleteAsync(cancellationToken);
        if (tookOver == 0)
        {
            return UploadClaim.InFlight();
        }

        var retry = await InsertFreshClaimAsync(ownerUserId, operationKey, now, lease, cancellationToken);
        return retry is not null ? UploadClaim.Claimed(retry.Value) : UploadClaim.InFlight();
    }

    private async Task<Guid?> InsertFreshClaimAsync(
        Guid ownerUserId,
        string operationKey,
        DateTime now,
        TimeSpan lease,
        CancellationToken cancellationToken)
    {
        var row = new UploadOperation
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerUserId,
            OperationKey = operationKey,
            Status = UploadOperationStatus.Pending,
            CreatedAt = now,
            LeaseExpiresAt = now + lease,
        };
        _db.UploadOperations.Add(row);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return row.Id;
        }
        catch (DbUpdateException)
        {
            // Unique index says the slot is taken (or the store hiccupped —
            // both resolve to "not ours"). Detach so the scoped context cannot
            // re-attempt OUR insert during a later Save.
            _db.Entry(row).State = EntityState.Detached;
            return null;
        }
    }

    public async Task ReleaseAsync(Guid claimToken, CancellationToken cancellationToken = default)
    {
        await _db.UploadOperations
            .Where(o => o.Id == claimToken && o.Status == UploadOperationStatus.Pending)
            .ExecuteDeleteAsync(cancellationToken);
    }
}