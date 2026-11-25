using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Lithophanes;
using NubArca.Api.PhotoExport;

namespace NubArca.Api.Ai.SubjectCutout;

// Waiting references only: the generated PNG is an ordinary owner file.
public sealed class SubjectCutoutQueue(AppDbContext db, TimeProvider clock)
{
    private const int MaxItems = 200;
    private IQueryable<FileItem> Sources(Guid owner) => PhotoExportEligibility.EligiblePhotos(db, owner);

    public async Task<IReadOnlyList<LithophaneQueueEntry>> ListAsync(Guid owner, CancellationToken ct)
    {
        var rows = await (from q in db.SubjectCutoutQueueItems.AsNoTracking()
                          join f in Sources(owner) on q.FileItemId equals f.Id
                          where q.OwnerUserId == owner
                          orderby q.AddedAt, q.FileItemId
                          select new { f.Id, f.Name, q.AddedAt }).Take(MaxItems).ToListAsync(ct);
        return rows.Select(x => new LithophaneQueueEntry(x.Id, x.Name, $"/api/files/{x.Id}/thumbnail?size=small", x.AddedAt)).ToList();
    }

    public async Task<LithophaneQueueResult> AddAsync(Guid owner, IReadOnlyList<Guid>? fileItemIds, CancellationToken ct)
    {
        var ids = (fileItemIds ?? []).Where(x => x != Guid.Empty).Distinct().ToList();
        if (ids.Count == 0 || ids.Count > MaxItems) throw new LithophaneException("invalid_request");
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await TreeMutationLock.AcquireAsync(db, owner, ct);
            var eligible = (await Sources(owner).Where(x => ids.Contains(x.Id)).Select(x => x.Id).ToListAsync(ct)).ToHashSet();
            var queued = (await db.SubjectCutoutQueueItems.AsNoTracking().Where(x => x.OwnerUserId == owner).Select(x => x.FileItemId).ToListAsync(ct)).ToHashSet();
            var added = new List<Guid>();
            var skipped = new List<LithophaneQueueSkip>();
            var now = clock.GetUtcNow().UtcDateTime;
            foreach (var id in ids)
            {
                if (!eligible.Contains(id)) { skipped.Add(new(id, "not_a_photo")); continue; }
                if (queued.Contains(id)) { added.Add(id); continue; }
                if (queued.Count >= MaxItems) { skipped.Add(new(id, "queue_full")); continue; }
                db.SubjectCutoutQueueItems.Add(new SubjectCutoutQueueItem { OwnerUserId = owner, FileItemId = id, AddedAt = now.AddTicks(added.Count) });
                queued.Add(id); added.Add(id);
            }
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return new LithophaneQueueResult(added, skipped);
        });
    }

    public async Task<bool> RemoveAsync(Guid owner, Guid fileId, CancellationToken ct) =>
        await db.SubjectCutoutQueueItems.Where(x => x.OwnerUserId == owner && x.FileItemId == fileId).ExecuteDeleteAsync(ct) > 0;
}
