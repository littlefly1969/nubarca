using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Domain;

namespace NubArca.Api.Party;

public sealed class PartyParticipantService : IPartyParticipantService
{
    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;
    private readonly IPartyGuestIdentity _identity;

    public PartyParticipantService(
        AppDbContext db, TimeProvider clock, IPartyGuestIdentity identity)
    {
        _db = db;
        _clock = clock;
        _identity = identity;
    }

    public async Task<PartyParticipantResolution> ResolveOrCreateAsync(
        Guid partyAlbumLinkId, string browserToken, string? legacyParticipantToken = null,
        CancellationToken cancellationToken = default)
    {
        var hash = _identity.LinkIdentityHash(browserToken, partyAlbumLinkId);
        var canonical = await FindAsync(partyAlbumLinkId, hash, cancellationToken)
            ?? await CreateAsync(partyAlbumLinkId, hash, cancellationToken);

        // --- Legacy migration ------------------------------------------------
        //
        // Before this, the participant cookie was scoped to the path of the
        // capability token that minted it, so one browser at one party could
        // hold a separate guest — and a separate allowance — per capability. A
        // party that is running right now must not notice the change: the guest
        // keeps their counters, and nothing is duplicated or zeroed.
        //
        // Every old row is FOLDED into the canonical one, which is always the
        // row keyed by the derivation. Adopting — rewriting an old row's key —
        // was the obvious alternative and is not safe: two capabilities of one
        // browser arriving together would both rewrite their own row to the same
        // key, and the unique index would surface that ordinary race to a guest
        // as a 500. Folding has no such contention, because the canonical row is
        // reached by find-or-create and the counters move by atomic increment.
        //
        // Each capability contributes its old row EXACTLY ONCE, on its first
        // request after the upgrade. Because a capability only ever incremented
        // its own counters, the non-zero counters of two old rows are disjoint
        // and summing them is exact rather than generous.
        var legacy = await FindLegacyAsync(partyAlbumLinkId, legacyParticipantToken, cancellationToken);
        var folded = legacy is not null && legacy.Id != canonical.Id
            && await FoldAsync(legacy, canonical.Id, cancellationToken);

        await TouchAsync(canonical.Id, cancellationToken);
        return new PartyParticipantResolution(canonical.Id, folded);
    }

    /// <summary>
    /// Moves one pre-migration row's counters onto the canonical guest, exactly
    /// once, and retires it.
    ///
    /// <para>THE RETIREMENT IS THE CLAIM. A conditional update sets
    /// <c>RetiredAt</c> only while it is still null, so of any number of
    /// requests looking at the same old row exactly one is told it affected a
    /// row — and only that one adds the counters. Both statements are in one
    /// transaction, so a fold cannot retire a row and then lose what it was
    /// carrying.</para>
    ///
    /// <para>The transaction opens with a write, before any read, so it never
    /// upgrades a shared lock to an exclusive one — the shape SQLite refuses to
    /// wait on.</para>
    /// </summary>
    private async Task<bool> FoldAsync(
        PartyParticipant legacy, Guid canonicalId, CancellationToken ct)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        var claimed = await _db.Database.ExecuteSqlRawAsync(
            "UPDATE party_participants SET \"RetiredAt\" = {1} "
            + "WHERE \"Id\" = {0} AND \"RetiredAt\" IS NULL",
            [legacy.Id, now], ct);
        if (claimed != 1)
        {
            // Somebody else already folded this row. Not an error, and not a
            // reason to add its counters a second time.
            await tx.RollbackAsync(ct);
            return false;
        }

        // Increments rather than assignments: two capabilities folding two
        // different old rows onto the same canonical guest must both land, and
        // a read-modify-write would let the later one erase the earlier.
        await _db.Database.ExecuteSqlRawAsync(
            "UPDATE party_participants SET "
            + "\"AcceptedPhotoCount\" = \"AcceptedPhotoCount\" + {1}, "
            + "\"AcceptedVideoCount\" = \"AcceptedVideoCount\" + {2}, "
            + "\"ChallengeVoteCount\" = \"ChallengeVoteCount\" + {3}, "
            + "\"AcceptedPhotoPrintCount\" = \"AcceptedPhotoPrintCount\" + {4}, "
            + "\"AcceptedStripPrintCount\" = \"AcceptedStripPrintCount\" + {5}, "
            + "\"SubmittedMessageCount\" = \"SubmittedMessageCount\" + {6} "
            + "WHERE \"Id\" = {0}",
            [
                canonicalId,
                legacy.AcceptedPhotoCount, legacy.AcceptedVideoCount, legacy.ChallengeVoteCount,
                legacy.AcceptedPhotoPrintCount, legacy.AcceptedStripPrintCount,
                legacy.SubmittedMessageCount,
            ], ct);

        await tx.CommitAsync(ct);
        _db.ChangeTracker.Clear();
        return true;
    }

    private async Task<PartyParticipant> CreateAsync(
        Guid partyAlbumLinkId, string hash, CancellationToken ct)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var participant = new PartyParticipant
        {
            Id = Guid.NewGuid(),
            PartyAlbumLinkId = partyAlbumLinkId,
            TokenHash = hash,
            CreatedAt = now,
            LastSeenAt = now,
        };
        _db.PartyParticipants.Add(participant);
        try
        {
            await _db.SaveChangesAsync(ct);
            return participant;
        }
        catch (DbUpdateException)
        {
            // Two capabilities of the same browser arrived together and the
            // unique (link, key) index elected one. Narrow on purpose: the ONLY
            // outcome accepted here is that the elected row is now findable. A
            // failure that is not this race re-throws unchanged, so a real
            // database problem never hides behind a retry.
            _db.ChangeTracker.Clear();
            var elected = await FindAsync(partyAlbumLinkId, hash, ct);
            if (elected is null) throw;
            return elected;
        }
    }

    private Task TouchAsync(Guid participantId, CancellationToken ct) =>
        _db.Database.ExecuteSqlRawAsync(
            "UPDATE party_participants SET \"LastSeenAt\" = {1} WHERE \"Id\" = {0}",
            [participantId, _clock.GetUtcNow().UtcDateTime], ct);

    public async Task<Guid?> ResolveAsync(
        Guid partyAlbumLinkId, string? browserToken, CancellationToken cancellationToken = default)
    {
        if (!_identity.LooksIssued(browserToken)) return null;
        var hash = _identity.LinkIdentityHash(browserToken!, partyAlbumLinkId);
        var existing = await FindAsync(partyAlbumLinkId, hash, cancellationToken);
        if (existing is null) return null;
        // A presence heartbeat, not game state: it is what keeps a guest holding
        // the voting screen open counted as being in the room.
        existing.LastSeenAt = _clock.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync(cancellationToken);
        return existing.Id;
    }

    // Retired rows are invisible to every lookup: their counters already live on
    // the surviving guest, so finding one would hand out a second allowance.
    private Task<PartyParticipant?> FindAsync(Guid linkId, string hash, CancellationToken ct) =>
        _db.PartyParticipants.FirstOrDefaultAsync(
            p => p.PartyAlbumLinkId == linkId && p.TokenHash == hash && p.RetiredAt == null, ct);

    private Task<PartyParticipant?> FindLegacyAsync(
        Guid linkId, string? legacyToken, CancellationToken ct)
    {
        if (!_identity.LooksIssued(legacyToken)) return Task.FromResult<PartyParticipant?>(null);
        return FindAsync(linkId, _identity.LegacyIdentityHash(legacyToken!), ct);
    }

    public async Task<bool> TryClaimChallengeVoteAsync(
        Guid participantId, int max, CancellationToken cancellationToken = default)
    {
        var affected = await _db.Database.ExecuteSqlRawAsync(
            "UPDATE party_participants "
            + "SET \"ChallengeVoteCount\" = \"ChallengeVoteCount\" + 1, \"LastSeenAt\" = {1} "
            + "WHERE \"Id\" = {0} AND \"ChallengeVoteCount\" < {2}",
            [participantId, _clock.GetUtcNow().UtcDateTime, max], cancellationToken);
        return affected == 1;
    }

    /// <summary>
    /// Claim one print slot for this guest, atomically.
    ///
    /// The same discipline as the upload quota: ONE statement decides and
    /// records, so two taps from the same phone cannot both observe the guest's
    /// last free slot. `max` of 0 means the host set no per-guest limit, and the
    /// party-wide budget is then the only ceiling.
    ///
    /// The column name comes from a bool, never from caller input, so the
    /// interpolation carries no injection surface.
    /// </summary>
    public async Task<bool> TryClaimPrintAsync(
        Guid participantId, bool isStrip, int max, CancellationToken cancellationToken = default)
    {
        if (max <= 0)
        {
            await TouchAsync(participantId, isStrip, cancellationToken);
            return true;
        }
        var column = isStrip ? "AcceptedStripPrintCount" : "AcceptedPhotoPrintCount";
        var affected = await _db.Database.ExecuteSqlRawAsync(
            $"UPDATE party_participants SET \"{column}\" = \"{column}\" + 1, "
            + "\"LastSeenAt\" = {1} "
            + $"WHERE \"Id\" = {{0}} AND \"{column}\" < {{2}}",
            [participantId, _clock.GetUtcNow().UtcDateTime, max], cancellationToken);
        return affected == 1;
    }

    /// <summary>Give a claimed print slot back when the sheet never happened.</summary>
    public Task ReleasePrintAsync(
        Guid participantId, bool isStrip, int max, CancellationToken cancellationToken = default)
    {
        if (max <= 0) return Task.CompletedTask;
        var column = isStrip ? "AcceptedStripPrintCount" : "AcceptedPhotoPrintCount";
        return _db.Database.ExecuteSqlRawAsync(
            $"UPDATE party_participants SET \"{column}\" = "
            + $"CASE WHEN \"{column}\" > 0 THEN \"{column}\" - 1 ELSE 0 END "
            + "WHERE \"Id\" = {0}",
            [participantId], cancellationToken);
    }

    /// <summary>Counting nothing still means the guest was here.</summary>
    private Task TouchAsync(
        Guid participantId, bool isStrip, CancellationToken cancellationToken)
    {
        var column = isStrip ? "AcceptedStripPrintCount" : "AcceptedPhotoPrintCount";
        return _db.Database.ExecuteSqlRawAsync(
            $"UPDATE party_participants SET \"{column}\" = \"{column}\" + 1, "
            + "\"LastSeenAt\" = {1} WHERE \"Id\" = {0}",
            [participantId, _clock.GetUtcNow().UtcDateTime], cancellationToken);
    }

    public Task ReleaseChallengeVoteAsync(
        Guid participantId, CancellationToken cancellationToken = default) =>
        _db.Database.ExecuteSqlRawAsync(
            "UPDATE party_participants SET \"ChallengeVoteCount\" = "
            + "CASE WHEN \"ChallengeVoteCount\" > 0 THEN \"ChallengeVoteCount\" - 1 ELSE 0 END, "
            + "\"LastSeenAt\" = {1} WHERE \"Id\" = {0}",
            [participantId, _clock.GetUtcNow().UtcDateTime], cancellationToken);

    public async Task<PartyQuotaSnapshot> GetQuotaAsync(
        Guid partyAlbumLinkId, Guid participantId, CancellationToken cancellationToken = default)
    {
        var link = await _db.PartyAlbumLinks
            .AsNoTracking()
            .Where(p => p.Id == partyAlbumLinkId)
            .Select(p => new { p.MaxPhotoUploadsPerParticipant, p.MaxVideoUploadsPerParticipant })
            .FirstOrDefaultAsync(cancellationToken);
        var used = await _db.PartyParticipants
            .AsNoTracking()
            .Where(p => p.Id == participantId && p.PartyAlbumLinkId == partyAlbumLinkId)
            .Select(p => new { p.AcceptedPhotoCount, p.AcceptedVideoCount })
            .FirstOrDefaultAsync(cancellationToken);

        return new PartyQuotaSnapshot(
            link?.MaxPhotoUploadsPerParticipant ?? 0,
            link?.MaxVideoUploadsPerParticipant ?? 0,
            used?.AcceptedPhotoCount ?? 0,
            used?.AcceptedVideoCount ?? 0);
    }

    public async Task<bool> TryClaimSlotAsync(
        Guid participantId, bool isVideo, int max, CancellationToken cancellationToken = default)
    {
        // ONE statement decides and records. A COUNT followed by an INSERT would
        // let two concurrent uploads both read "one slot left" and both take it;
        // here the database evaluates the predicate and applies the increment
        // under the same row lock, so the loser sees 0 rows affected.
        //
        // The column name comes from a bool, never from caller input, so the
        // interpolation below carries no injection surface. Quoted identifiers
        // and positional parameters keep the statement valid on both PostgreSQL
        // (production) and SQLite (the integration-test harness).
        var column = isVideo ? "AcceptedVideoCount" : "AcceptedPhotoCount";
        var sql =
            $"UPDATE party_participants "
            + $"SET \"{column}\" = \"{column}\" + 1, \"LastSeenAt\" = {{1}} "
            + $"WHERE \"Id\" = {{0}} AND ({{2}} = 0 OR \"{column}\" < {{2}})";

        var affected = await _db.Database.ExecuteSqlRawAsync(
            sql,
            [participantId, _clock.GetUtcNow().UtcDateTime, max],
            cancellationToken);
        return affected == 1;
    }

    public async Task<bool> TryClaimMessageAsync(
        Guid participantId, int max, CancellationToken cancellationToken = default)
    {
        // ONE statement decides and records, exactly like the upload slot: a
        // COUNT followed by an INSERT would let two simultaneous greetings both
        // read "one slot left" and both take it.
        var affected = await _db.Database.ExecuteSqlRawAsync(
            "UPDATE party_participants "
            + "SET \"SubmittedMessageCount\" = \"SubmittedMessageCount\" + 1, \"LastSeenAt\" = {1} "
            + "WHERE \"Id\" = {0} AND ({2} = 0 OR \"SubmittedMessageCount\" < {2})",
            [participantId, _clock.GetUtcNow().UtcDateTime, max],
            cancellationToken);
        return affected == 1;
    }

    public async Task<int> MessageCountAsync(
        Guid participantId, CancellationToken cancellationToken = default) =>
        await _db.PartyParticipants.AsNoTracking()
            .Where(p => p.Id == participantId)
            .Select(p => p.SubmittedMessageCount)
            .FirstOrDefaultAsync(cancellationToken);
}
