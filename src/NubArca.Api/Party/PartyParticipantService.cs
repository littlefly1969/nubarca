using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Domain;

namespace NubArca.Api.Party;

public sealed class PartyParticipantService : IPartyParticipantService
{
    // 32 bytes of CSPRNG output, base64url — the same order of entropy as the
    // party tokens themselves. Generated server-side so the value is never
    // something a client chose for itself.
    private const int TokenBytes = 32;

    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;

    public PartyParticipantService(AppDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<PartyParticipantResolution> ResolveOrCreateAsync(
        Guid partyAlbumLinkId, string? rawToken, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;

        if (!string.IsNullOrWhiteSpace(rawToken))
        {
            var hash = PartyLinkService.HashToken(rawToken);
            // Scoped to the LINK: a token minted at another party hashes fine but
            // matches no row here, so it silently yields a fresh session rather
            // than leaking one party's allowance into another.
            var existing = await _db.PartyParticipants
                .FirstOrDefaultAsync(
                    p => p.PartyAlbumLinkId == partyAlbumLinkId && p.TokenHash == hash,
                    cancellationToken);
            if (existing is not null)
            {
                existing.LastSeenAt = now;
                await _db.SaveChangesAsync(cancellationToken);
                return new PartyParticipantResolution(existing.Id, null);
            }
        }

        var newToken = GenerateToken();
        var participant = new PartyParticipant
        {
            Id = Guid.NewGuid(),
            PartyAlbumLinkId = partyAlbumLinkId,
            TokenHash = PartyLinkService.HashToken(newToken),
            AcceptedPhotoCount = 0,
            AcceptedVideoCount = 0,
            ChallengeVoteCount = 0,
            CreatedAt = now,
            LastSeenAt = now,
        };
        _db.PartyParticipants.Add(participant);
        await _db.SaveChangesAsync(cancellationToken);
        return new PartyParticipantResolution(participant.Id, newToken);
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

    private static string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(TokenBytes);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
