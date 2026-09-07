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
        // request after the upgrade, and contributes ALL of it: the guest's
        // votes, greetings and photographs are re-parented onto the canonical
        // identity alongside the counters, because a counter on one row and the
        // history on another is still two guests as far as every feature that
        // asks "has this guest already voted" is concerned.
        var legacy = await FindLegacyAsync(partyAlbumLinkId, legacyParticipantToken, cancellationToken);
        var folded = legacy is not null && legacy.Id != canonical.Id
            && await FoldAsync(legacy, canonical.Id, cancellationToken);

        await TouchAsync(canonical.Id, cancellationToken);
        return new PartyParticipantResolution(canonical.Id, folded);
    }

    /// <summary>
    /// Moves one pre-migration row ONTO the canonical guest — its activity as
    /// well as its counters — exactly once, and retires it.
    ///
    /// <para>The contract is that <c>PartyParticipantId</c> means "the guest who
    /// did this", so after a fold no feature may still observe the alias as a
    /// second guest. Moving only the counters would leave the history pointing
    /// at an identity nothing can reach: a challenge already voted would look
    /// unvoted to the canonical guest, who could then vote it AGAIN — one
    /// browser, two votes, which is precisely what an anonymous identity exists
    /// to prevent.</para>
    ///
    /// <para>Four foreign keys reference a participant, and they need two
    /// different treatments. <see cref="PartyChallengeVote"/> and
    /// <see cref="PartyGameVote"/> are CURRENT STATE with a uniqueness rule —
    /// one vote per guest per challenge, one per guest per round — so a
    /// collision is possible and must be reconciled before the re-parent, not
    /// left to the index. <see cref="PartyMessage"/> and
    /// <see cref="PartyUploadItem"/> are history with no participant
    /// uniqueness, so they simply move. Print has no participant row of its own;
    /// it reads the counters, which is why they must land here correctly.</para>
    ///
    /// <para>WHERE THEY COLLIDE, THE CANONICAL VOTE WINS. It was cast after the
    /// new session was established, so it is the guest's more recent answer, and
    /// keeping it means the fold never overwrites something the guest did with
    /// something they did earlier. The alias's copy is deleted.</para>
    ///
    /// <para>THE RETIREMENT IS THE CLAIM. A conditional update sets
    /// <c>RetiredAt</c> only while it is still null, so of any number of
    /// requests looking at the same old row exactly one is told it affected a
    /// row — and only that one moves anything. It also LOCKS that row for the
    /// rest of the transaction, which is what lets the counters be read from the
    /// row itself rather than from an entity loaded before the fold began.</para>
    ///
    /// <para>All of it is one transaction, so a failure anywhere leaves the alias
    /// live and unfolded rather than half-moved: retired with its votes still on
    /// it, or re-parented with its counters lost.</para>
    ///
    /// <para>The transaction opens with a write, before any read, so it never
    /// upgrades a shared lock to an exclusive one — the shape SQLite refuses to
    /// wait on.</para>
    /// </summary>
    private async Task<bool> FoldAsync(
        PartyParticipant legacy, Guid canonicalId, CancellationToken ct)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var linkId = legacy.PartyAlbumLinkId;
        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        var claimed = await _db.Database.ExecuteSqlRawAsync(
            "UPDATE party_participants SET \"RetiredAt\" = {1} "
            + "WHERE \"Id\" = {0} AND \"RetiredAt\" IS NULL",
            [legacy.Id, now], ct);
        if (claimed != 1)
        {
            // Somebody else already folded this row. Not an error, and not a
            // reason to move its activity or its counters a second time.
            await tx.RollbackAsync(ct);
            return false;
        }

        // --- Challenge votes: reconcile, then re-parent ----------------------
        //
        // Deleting the alias's colliding copy FIRST is what keeps the unique
        // index a safety net rather than a mechanism: the update that follows
        // cannot violate it, because every row that would have collided is gone.
        await _db.Database.ExecuteSqlRawAsync(
            "DELETE FROM party_challenge_votes WHERE \"PartyParticipantId\" = {0} AND EXISTS ("
            + "SELECT 1 FROM party_challenge_votes mine "
            + "WHERE mine.\"PartyParticipantId\" = {1} "
            + "AND mine.\"PartyAlbumLinkId\" = party_challenge_votes.\"PartyAlbumLinkId\" "
            + "AND mine.\"PartyChallengeId\" = party_challenge_votes.\"PartyChallengeId\")",
            [legacy.Id, canonicalId], ct);
        await _db.Database.ExecuteSqlRawAsync(
            "UPDATE party_challenge_votes SET \"PartyParticipantId\" = {1} "
            + "WHERE \"PartyParticipantId\" = {0}",
            [legacy.Id, canonicalId], ct);

        // --- Game votes: the same, per round ---------------------------------
        await _db.Database.ExecuteSqlRawAsync(
            "DELETE FROM party_game_votes WHERE \"PartyParticipantId\" = {0} AND EXISTS ("
            + "SELECT 1 FROM party_game_votes mine "
            + "WHERE mine.\"PartyParticipantId\" = {1} "
            + "AND mine.\"PartyGameRoundId\" = party_game_votes.\"PartyGameRoundId\")",
            [legacy.Id, canonicalId], ct);
        await _db.Database.ExecuteSqlRawAsync(
            "UPDATE party_game_votes SET \"PartyParticipantId\" = {1} "
            + "WHERE \"PartyParticipantId\" = {0}",
            [legacy.Id, canonicalId], ct);

        // --- History: nothing to reconcile, so it just moves -----------------
        //
        // Neither table constrains the participant, so no collision exists. They
        // move anyway, because "who sent this greeting" and "who took this
        // photograph" must name a guest the party can still reach.
        await _db.Database.ExecuteSqlRawAsync(
            "UPDATE party_messages SET \"PartyParticipantId\" = {1} "
            + "WHERE \"PartyParticipantId\" = {0}",
            [legacy.Id, canonicalId], ct);
        await _db.Database.ExecuteSqlRawAsync(
            "UPDATE party_upload_items SET \"PartyParticipantId\" = {1} "
            + "WHERE \"PartyParticipantId\" = {0}",
            [legacy.Id, canonicalId], ct);

        // --- Counters --------------------------------------------------------
        //
        // Increments rather than assignments: two capabilities folding two
        // different old rows onto the same canonical guest must both land, and a
        // read-modify-write would let the later one erase the earlier.
        //
        // The amounts are read from the alias ROW, inside the transaction that
        // locked it, rather than from the entity loaded before the fold started
        // — so a request still holding the old identity cannot slip an increment
        // in between the read and the move.
        //
        // These five are exact sums because each capability only ever
        // incremented its own row, so their non-zero counters are disjoint, and
        // because none of them was reduced by the reconciliation above.
        await _db.Database.ExecuteSqlRawAsync(
            "UPDATE party_participants SET "
            + "\"AcceptedPhotoCount\" = \"AcceptedPhotoCount\" + "
            + "(SELECT old.\"AcceptedPhotoCount\" FROM party_participants old WHERE old.\"Id\" = {1}), "
            + "\"AcceptedVideoCount\" = \"AcceptedVideoCount\" + "
            + "(SELECT old.\"AcceptedVideoCount\" FROM party_participants old WHERE old.\"Id\" = {1}), "
            + "\"AcceptedPhotoPrintCount\" = \"AcceptedPhotoPrintCount\" + "
            + "(SELECT old.\"AcceptedPhotoPrintCount\" FROM party_participants old WHERE old.\"Id\" = {1}), "
            + "\"AcceptedStripPrintCount\" = \"AcceptedStripPrintCount\" + "
            + "(SELECT old.\"AcceptedStripPrintCount\" FROM party_participants old WHERE old.\"Id\" = {1}), "
            + "\"SubmittedMessageCount\" = \"SubmittedMessageCount\" + "
            + "(SELECT old.\"SubmittedMessageCount\" FROM party_participants old WHERE old.\"Id\" = {1}) "
            + "WHERE \"Id\" = {0}",
            [canonicalId, legacy.Id], ct);

        // The vote budget is the ONE counter that cannot be summed. Reconciling
        // a collision removed a vote, so the sum would leave the guest paying
        // for a vote they no longer hold — and the counter is a budget, so an
        // inflated one silently costs them a vote later in the evening. Counted
        // from the rows that now exist, it is exact by construction, and it stays
        // exact whichever order two folds arrive in.
        await _db.Database.ExecuteSqlRawAsync(
            "UPDATE party_participants SET \"ChallengeVoteCount\" = "
            + "(SELECT COUNT(*) FROM party_challenge_votes v "
            + "WHERE v.\"PartyParticipantId\" = {0} AND v.\"PartyAlbumLinkId\" = {1}) "
            + "WHERE \"Id\" = {0}",
            [canonicalId, linkId], ct);

        // The alias keeps nothing. Its counters have moved, so leaving copies
        // behind would make every aggregate over the table double-count, and
        // would leave "no second allowance" resting on a WHERE clause instead of
        // on the data.
        await _db.Database.ExecuteSqlRawAsync(
            "UPDATE party_participants SET "
            + "\"AcceptedPhotoCount\" = 0, \"AcceptedVideoCount\" = 0, "
            + "\"ChallengeVoteCount\" = 0, \"AcceptedPhotoPrintCount\" = 0, "
            + "\"AcceptedStripPrintCount\" = 0, \"SubmittedMessageCount\" = 0 "
            + "WHERE \"Id\" = {0}",
            [legacy.Id], ct);

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

    public async Task<PartyPrintQuotaSnapshot> GetPrintQuotaAsync(
        Guid participantId, CancellationToken cancellationToken = default)
    {
        var row = await _db.PartyParticipants.AsNoTracking()
            .Where(p => p.Id == participantId)
            .Select(p => new { p.AcceptedPhotoPrintCount, p.AcceptedStripPrintCount })
            .FirstOrDefaultAsync(cancellationToken);
        return row is null
            ? new PartyPrintQuotaSnapshot(0, 0)
            : new PartyPrintQuotaSnapshot(row.AcceptedPhotoPrintCount, row.AcceptedStripPrintCount);
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
