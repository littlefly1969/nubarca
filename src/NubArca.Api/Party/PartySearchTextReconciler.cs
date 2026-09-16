using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Domain;

namespace NubArca.Api.Party;

/// <summary>
/// Re-derives every guest-list row's <c>SearchText</c> from the fields it
/// folds, and writes only the rows where the two disagree.
///
/// <para>The folded text is a cache (<see cref="PartySearchText"/>): every
/// write that changes a name, a label, an address or a phone writes it too. This
/// is what makes that true for rows those writes did not produce — the rows that
/// existed before the column did, and rows written by an application that did
/// not know it (the previous release, while rolled back). It runs once when the
/// API starts, in keyset batches, and costs one read of three narrow tables when
/// nothing is stale.</para>
///
/// <para>Each repair is conditional on the row still holding the values it was
/// folded from, so a rename that commits meanwhile is never overwritten with the
/// fold of the old name.</para>
/// </summary>
public static class PartySearchTextReconciler
{
    private const int BatchSize = 500;

    public static async Task<int> RunAsync(AppDbContext db, CancellationToken cancellationToken = default)
    {
        var repaired = 0;

        Guid? after = null;
        while (true)
        {
            var batch = await db.PartyInvitationGroups.AsNoTracking()
                .Where(g => after == null || g.Id.CompareTo(after.Value) > 0)
                .OrderBy(g => g.Id)
                .Take(BatchSize)
                .Select(g => new { g.Id, g.Label, g.RecipientEmail, g.Phone, g.SearchText })
                .ToListAsync(cancellationToken);
            if (batch.Count == 0) break;
            foreach (var row in batch)
            {
                var folded = PartySearchText.ForGroup(row.Label, row.RecipientEmail, row.Phone);
                if (folded == row.SearchText) continue;
                repaired += await db.PartyInvitationGroups
                    .Where(g => g.Id == row.Id && g.Label == row.Label
                        && g.RecipientEmail == row.RecipientEmail && g.Phone == row.Phone)
                    .ExecuteUpdateAsync(s => s.SetProperty(g => g.SearchText, folded), cancellationToken);
            }
            after = batch[^1].Id;
        }

        after = null;
        while (true)
        {
            var batch = await db.PartyGuests.AsNoTracking()
                .Where(g => after == null || g.Id.CompareTo(after.Value) > 0)
                .OrderBy(g => g.Id)
                .Take(BatchSize)
                .Select(g => new { g.Id, g.Name, g.Email, g.Phone, g.SearchText })
                .ToListAsync(cancellationToken);
            if (batch.Count == 0) break;
            foreach (var row in batch)
            {
                var folded = PartySearchText.ForGuest(row.Name, row.Email, row.Phone);
                if (folded == row.SearchText) continue;
                repaired += await db.PartyGuests
                    .Where(g => g.Id == row.Id && g.Name == row.Name && g.Email == row.Email && g.Phone == row.Phone)
                    .ExecuteUpdateAsync(s => s.SetProperty(g => g.SearchText, folded), cancellationToken);
            }
            after = batch[^1].Id;
        }

        after = null;
        while (true)
        {
            var batch = await db.PartyAttendanceGuests.AsNoTracking()
                .Where(g => after == null || g.Id.CompareTo(after.Value) > 0)
                .OrderBy(g => g.Id)
                .Take(BatchSize)
                .Select(g => new { g.Id, g.Name, g.SearchText })
                .ToListAsync(cancellationToken);
            if (batch.Count == 0) break;
            foreach (var row in batch)
            {
                var folded = PartySearchText.ForName(row.Name);
                if (folded == row.SearchText) continue;
                repaired += await db.PartyAttendanceGuests
                    .Where(g => g.Id == row.Id && g.Name == row.Name)
                    .ExecuteUpdateAsync(s => s.SetProperty(g => g.SearchText, folded), cancellationToken);
            }
            after = batch[^1].Id;
        }

        return repaired;
    }
}
