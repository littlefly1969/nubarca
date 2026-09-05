using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Domain.Print;

namespace NubArca.Api.Print;

/// <summary>
/// The one place a party print budget is spent.
///
/// WHAT A UNIT MEANS. One accepted job is one unit of ONE product's budget. A
/// four-photo strip composes four photographs and costs one strip; a photo costs
/// one photo. The two budgets never meet: running out of strips leaves photos
/// untouched, and they are never added together into a single "prints left",
/// because the host set two numbers for two different things.
///
/// WHEN IT IS SPENT. At acceptance — the moment the request becomes a real job
/// in the queue. A render that fails BEFORE acceptance costs nothing; everything
/// after acceptance keeps the unit, including a job that later fails, is
/// cancelled, or ends delivery-unknown, because by then the paper may already
/// have moved. An owner retrying that same job does not spend a second unit: a
/// retry is the same print, and charging it twice would push hosts toward
/// letting a bad print stand.
///
/// HOW IT IS SPENT SAFELY. With one conditional UPDATE, not a read followed by a
/// write. Two guests tapping "print" on the last strip at the same moment both
/// reach the database; the statement below only matches a row whose counter is
/// still below the maximum, so exactly one of them increments it and the other
/// is told the budget is gone. The same statement takes the party's next public
/// number, so two guests can never be handed the same one either.
/// </summary>
public interface IPartyPrintBudget
{
    /// <summary>
    /// Take one unit of <paramref name="product"/> and the next public number,
    /// atomically. Null when the product is off or nothing is left.
    /// </summary>
    Task<PartyPrintReservation?> TryReserveAsync(
        Guid partyAlbumId, string product, CancellationToken cancellationToken);

    /// <summary>
    /// Give a unit back, for a request that never became a job. Used only on the
    /// path where acceptance itself fails after the counter moved — never to
    /// "refund" a print that reached the queue.
    /// </summary>
    Task ReleaseAsync(Guid partyAlbumId, string product, CancellationToken cancellationToken);
}

/// <summary>What a successful reservation hands back.</summary>
public sealed record PartyPrintReservation(long PublicSequence, int RemainingAfter);

public sealed class PartyPrintBudget : IPartyPrintBudget
{
    private readonly AppDbContext _db;

    public PartyPrintBudget(AppDbContext db) => _db = db;

    public async Task<PartyPrintReservation?> TryReserveAsync(
        Guid partyAlbumId, string product, CancellationToken cancellationToken)
    {
        if (!PartyPrintProducts.IsKnown(product)) return null;
        var photo = product == PartyPrintProducts.Photo;

        // One statement: the guard, the increment and the sequence draw. The
        // WHERE is the guard — a row that no longer qualifies simply does not
        // match, and RETURNING tells us what this caller got.
        //
        // Written per product rather than built from a string, so the column
        // names are compile-time text and nothing is interpolated into SQL. The
        // timestamp is a parameter rather than a database function, which keeps
        // the one statement identical on PostgreSQL and on the SQLite the tests
        // run against.
        var sql = photo
            ? """
              UPDATE party_print_profiles
                 SET "PhotoAcceptedCount" = "PhotoAcceptedCount" + 1,
                     "PublicSequenceNext" = "PublicSequenceNext" + 1,
                     "UpdatedAt" = {1}
               WHERE "PartyAlbumId" = {0}
                 AND "Enabled" = TRUE
                 AND "PhotoEnabled" = TRUE
                 AND "PhotoAcceptedCount" < "PhotoMaxPrints"
              RETURNING "PublicSequenceNext" - 1 AS "PublicSequence",
                        "PhotoMaxPrints" - "PhotoAcceptedCount" AS "RemainingAfter"
              """
            : """
              UPDATE party_print_profiles
                 SET "StripAcceptedCount" = "StripAcceptedCount" + 1,
                     "PublicSequenceNext" = "PublicSequenceNext" + 1,
                     "UpdatedAt" = {1}
               WHERE "PartyAlbumId" = {0}
                 AND "Enabled" = TRUE
                 AND "StripEnabled" = TRUE
                 AND "StripAcceptedCount" < "StripMaxPrints"
              RETURNING "PublicSequenceNext" - 1 AS "PublicSequence",
                        "StripMaxPrints" - "StripAcceptedCount" AS "RemainingAfter"
              """;

        var rows = await _db.Database
            .SqlQueryRaw<PartyPrintReservationRow>(sql, partyAlbumId, DateTime.UtcNow)
            .ToListAsync(cancellationToken);
        if (rows.Count == 0) return null;
        return new PartyPrintReservation(rows[0].PublicSequence, rows[0].RemainingAfter);
    }

    public async Task ReleaseAsync(
        Guid partyAlbumId, string product, CancellationToken cancellationToken)
    {
        if (!PartyPrintProducts.IsKnown(product)) return;
        var photo = product == PartyPrintProducts.Photo;

        // The counter only ever goes back down to where it was, never below
        // zero: a release that arrives twice cannot manufacture budget.
        var sql = photo
            ? """
              UPDATE party_print_profiles
                 SET "PhotoAcceptedCount" = "PhotoAcceptedCount" - 1,
                     "UpdatedAt" = {1}
               WHERE "PartyAlbumId" = {0} AND "PhotoAcceptedCount" > 0
              """
            : """
              UPDATE party_print_profiles
                 SET "StripAcceptedCount" = "StripAcceptedCount" - 1,
                     "UpdatedAt" = {1}
               WHERE "PartyAlbumId" = {0} AND "StripAcceptedCount" > 0
              """;
        await _db.Database.ExecuteSqlRawAsync(
            sql, [partyAlbumId, DateTime.UtcNow], cancellationToken);
    }

    /// <summary>Shape of the RETURNING clause above.</summary>
    private sealed class PartyPrintReservationRow
    {
        public long PublicSequence { get; set; }
        public int RemainingAfter { get; set; }
    }
}
