using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Domain;

namespace NubArca.Api.Party;

public sealed class PartyChallengeService : IPartyChallengeService
{
    private readonly AppDbContext _db;
    private readonly IPartyParticipantService _participants;
    private readonly TimeProvider _clock;
    private readonly ILogger<PartyChallengeService> _logger;

    public PartyChallengeService(
        AppDbContext db,
        IPartyParticipantService participants,
        TimeProvider clock,
        ILogger<PartyChallengeService> logger)
    {
        _db = db;
        _participants = participants;
        _clock = clock;
        _logger = logger;
    }

    public async Task<PartyChallengeListDto?> ListOwnerAsync(Guid ownerId, Guid albumId, CancellationToken ct = default)
    {
        if (!await OwnsAsync(ownerId, albumId, ct)) return null;
        var linkId = await ActiveLinkIdAsync(ownerId, albumId, ct);
        var rows = await _db.PartyChallenges.AsNoTracking()
            .Where(x => x.AlbumId == albumId)
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
            .ToListAsync(ct);
        var counts = linkId is Guid lid
            ? await _db.PartyChallengeVotes.AsNoTracking().Where(x => x.PartyAlbumLinkId == lid)
                .GroupBy(x => x.PartyChallengeId)
                .Select(g => new { Id = g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Id, x => x.Count, ct)
            : [];
        return new PartyChallengeListDto(albumId, rows.Select(x => OwnerDto(x, counts.GetValueOrDefault(x.Id))).ToList());
    }

    public async Task<PartyChallengeDto?> CreateAsync(Guid ownerId, Guid albumId, PartyChallengeWriteRequest request, CancellationToken ct = default)
    {
        if (!await OwnsAsync(ownerId, albumId, ct) || !Valid(request)
            || !await MediaAllowedAsync(ownerId, albumId, request.MediaFileItemId, ct)) return null;
        var now = Now;
        var order = (await _db.PartyChallenges.Where(x => x.AlbumId == albumId)
            .Select(x => (int?)x.SortOrder).MaxAsync(ct) ?? -1) + 1;
        var row = new PartyChallenge
        {
            Id = Guid.NewGuid(), AlbumId = albumId, Title = request.Title!.Trim(),
            Body = request.Body!.Trim(), Kind = request.Kind!, MediaFileItemId = request.MediaFileItemId,
            IsEnabled = request.IsEnabled, SortOrder = order, CreatedAt = now, UpdatedAt = now,
        };
        _db.PartyChallenges.Add(row);
        await _db.SaveChangesAsync(ct);
        return OwnerDto(row, 0);
    }

    public async Task<PartyChallengeDto?> UpdateAsync(Guid ownerId, Guid albumId, Guid challengeId, PartyChallengeWriteRequest request, CancellationToken ct = default)
    {
        if (!Valid(request) || !await MediaAllowedAsync(ownerId, albumId, request.MediaFileItemId, ct)) return null;
        var row = await _db.PartyChallenges
            .Where(x => x.Id == challengeId && x.AlbumId == albumId
                && _db.Albums.Any(a => a.Id == albumId && a.OwnerUserId == ownerId))
            .FirstOrDefaultAsync(ct);
        if (row is null) return null;
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        row.Title = request.Title!.Trim();
        row.Body = request.Body!.Trim();
        row.Kind = request.Kind!;
        row.MediaFileItemId = request.MediaFileItemId;
        row.IsEnabled = request.IsEnabled;
        row.UpdatedAt = Now;
        if (!row.IsEnabled) await ReleaseVotesForChallengeAsync(row.Id, ct);
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        var linkId = await ActiveLinkIdAsync(ownerId, albumId, ct);
        var count = linkId is Guid lid
            ? await _db.PartyChallengeVotes.CountAsync(v => v.PartyAlbumLinkId == lid && v.PartyChallengeId == row.Id, ct) : 0;
        return OwnerDto(row, count);
    }

    public async Task<bool> DeleteAsync(Guid ownerId, Guid albumId, Guid challengeId, CancellationToken ct = default)
    {
        if (!await OwnsAsync(ownerId, albumId, ct)) return false;
        var exists = await _db.PartyChallenges.AnyAsync(x => x.Id == challengeId && x.AlbumId == albumId, ct);
        if (!exists) return false;
        // A challenge currently held is immutable until NEXT; deleting it
        // underneath a TV would violate reconnect-safe presentation.
        if (await _db.PartyChallengeSessions.AnyAsync(x => x.ActiveChallengeId == challengeId, ct)) return false;
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        await ReleaseVotesForChallengeAsync(challengeId, ct);
        await _db.PartyChallengeCompletions.Where(x => x.PartyChallengeId == challengeId).ExecuteDeleteAsync(ct);
        await _db.PartyChallenges.Where(x => x.Id == challengeId && x.AlbumId == albumId).ExecuteDeleteAsync(ct);
        await tx.CommitAsync(ct);
        return true;
    }

    public async Task<bool> ReorderAsync(Guid ownerId, Guid albumId, IReadOnlyList<Guid> ids, CancellationToken ct = default)
    {
        if (!await OwnsAsync(ownerId, albumId, ct)) return false;
        var rows = await _db.PartyChallenges.Where(x => x.AlbumId == albumId).ToListAsync(ct);
        if (ids.Count != rows.Count || ids.Distinct().Count() != ids.Count
            || rows.Any(x => !ids.Contains(x.Id))) return false;
        var byId = rows.ToDictionary(x => x.Id);
        for (var i = 0; i < ids.Count; i++) { byId[ids[i]].SortOrder = i; byId[ids[i]].UpdatedAt = Now; }
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<PartyGuestChallengesDto?> ListGuestAsync(PartyAccess access, Guid participantId, CancellationToken ct = default)
    {
        if (access.PartyAlbumLinkId is not Guid linkId) return null;
        var state = await GuestStateAsync(access, participantId, linkId, ct);
        if (state is null) return null;
        var voted = await _db.PartyChallengeVotes.AsNoTracking()
            .Where(x => x.PartyAlbumLinkId == linkId && x.PartyParticipantId == participantId)
            .Select(x => x.PartyChallengeId).ToListAsync(ct);
        var items = await _db.PartyChallenges.AsNoTracking()
            .Where(x => x.AlbumId == access.AlbumId && x.IsEnabled)
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
            .Select(x => new PartyGuestChallengeDto(x.Id, x.Title, x.Body, x.Kind,
                x.MediaFileItemId == null ? null : $"/api/party/challenge-media/{x.Id}", voted.Contains(x.Id)))
            .ToListAsync(ct);
        return new PartyGuestChallengesDto(state.Value.AlbumName, state.Value.Max,
            state.Value.Used, Math.Max(0, state.Value.Max - state.Value.Used), items);
    }

    public async Task<PartyVoteResultDto?> VoteAsync(
        PartyAccess access, Guid participantId, Guid challengeId, bool voted, CancellationToken ct = default)
    {
        if (access.PartyAlbumLinkId is not Guid linkId) return null;
        var state = await GuestStateAsync(access, participantId, linkId, ct);
        if (state is null) return null;
        var eligible = await _db.PartyChallenges.AsNoTracking()
            .AnyAsync(x => x.Id == challengeId && x.AlbumId == access.AlbumId && x.IsEnabled, ct);
        if (!eligible) return null;

        // The conditional participant UPDATE and the unique vote index are the
        // concurrency authorities. Read-committed avoids turning a harmless
        // loser into a PostgreSQL serialization error under simultaneous taps.
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        var existing = await _db.PartyChallengeVotes
            .FirstOrDefaultAsync(x => x.PartyAlbumLinkId == linkId
                && x.PartyParticipantId == participantId && x.PartyChallengeId == challengeId, ct);
        if (voted && existing is null)
        {
            if (!await _participants.TryClaimChallengeVoteAsync(participantId, state.Value.Max, ct))
            {
                await tx.RollbackAsync(ct);
                return new PartyVoteResultDto(false, state.Value.Used, 0);
            }
            _db.PartyChallengeVotes.Add(new PartyChallengeVote
            {
                Id = Guid.NewGuid(), PartyAlbumLinkId = linkId,
                PartyParticipantId = participantId, PartyChallengeId = challengeId, CreatedAt = Now,
            });
            try
            {
                await _db.SaveChangesAsync(ct);
                _logger.LogInformation(
                    "party.challenge.voted AlbumId={AlbumId} PartyAlbumLinkId={PartyAlbumLinkId} ChallengeId={ChallengeId}",
                    access.AlbumId, linkId, challengeId);
            }
            catch (DbUpdateException)
            {
                // A concurrent identical PUT won the unique vote insert. The
                // conditional counter claim is in this transaction, so rollback
                // restores it and the request returns the now-authoritative
                // idempotent state rather than a 500.
                await tx.RollbackAsync(ct);
                _db.ChangeTracker.Clear();
                var concurrentUsed = await _db.PartyChallengeVotes.AsNoTracking()
                    .CountAsync(x => x.PartyAlbumLinkId == linkId
                        && x.PartyParticipantId == participantId, ct);
                var existsNow = await _db.PartyChallengeVotes.AsNoTracking()
                    .AnyAsync(x => x.PartyAlbumLinkId == linkId
                        && x.PartyParticipantId == participantId
                        && x.PartyChallengeId == challengeId, ct);
                return new PartyVoteResultDto(existsNow, concurrentUsed,
                    Math.Max(0, state.Value.Max - concurrentUsed));
            }
        }
        else if (!voted && existing is not null)
        {
            _db.PartyChallengeVotes.Remove(existing);
            await _db.SaveChangesAsync(ct);
            await _participants.ReleaseChallengeVoteAsync(participantId, ct);
            _logger.LogInformation(
                "party.challenge.unvoted AlbumId={AlbumId} PartyAlbumLinkId={PartyAlbumLinkId} ChallengeId={ChallengeId}",
                access.AlbumId, linkId, challengeId);
        }
        await tx.CommitAsync(ct);
        var used = await _db.PartyChallengeVotes.AsNoTracking()
            .CountAsync(x => x.PartyAlbumLinkId == linkId && x.PartyParticipantId == participantId, ct);
        return new PartyVoteResultDto(voted, used, Math.Max(0, state.Value.Max - used));
    }

    public async Task<PartyPlaybackSnapshotDto?> GetSnapshotAsync(Guid ownerId, Guid albumId, CancellationToken ct = default)
    {
        var link = await ActiveGameLinkAsync(ownerId, albumId, ct);
        if (link is null) return await OwnsAsync(ownerId, albumId, ct)
            ? new PartyPlaybackSnapshotDto(PartyPlaybackModes.Media, null, null, 0) : null;
        var session = await EnsureSessionAsync(link, ct);
        return await SnapshotAsync(session, ct);
    }

    public async Task<PartyPlaybackSnapshotDto?> OnMediaBoundaryAsync(Guid ownerId, Guid albumId, CancellationToken ct = default)
    {
        var link = await ActiveGameLinkAsync(ownerId, albumId, ct);
        if (link is null) return await GetSnapshotAsync(ownerId, albumId, ct);
        var session = await EnsureSessionAsync(link, ct);
        if (session.Mode == PartyPlaybackModes.ChallengeHold || session.NextChallengeAt > Now)
            return await SnapshotAsync(session, ct);
        if (link.MaxChallengesPerSession is int max && session.CompletedCount >= max)
            return await SnapshotAsync(session, ct);

        var completed = _db.PartyChallengeCompletions.Where(x => x.PartyAlbumLinkId == link.Id)
            .Select(x => x.PartyChallengeId);
        var candidates = await _db.PartyChallenges.AsNoTracking()
            .Where(x => x.AlbumId == albumId && x.IsEnabled && !completed.Contains(x.Id))
            .Select(x => new
            {
                Row = x,
                Votes = _db.PartyChallengeVotes.Count(v => v.PartyAlbumLinkId == link.Id && v.PartyChallengeId == x.Id),
            }).ToListAsync(ct);
        if (candidates.Count == 0)
        {
            await _db.PartyChallengeSessions
                .Where(x => x.Id == session.Id && x.Version == session.Version
                    && x.Mode == PartyPlaybackModes.Media)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.NextChallengeAt, Deadline(link))
                    .SetProperty(x => x.Version, x => x.Version + 1)
                    .SetProperty(x => x.UpdatedAt, Now), ct);
            return await SnapshotAsync(await ReloadSessionAsync(link.Id, ct), ct);
        }
        var pickedId = PartyChallengePolicy.Select(
            candidates.Select(x => new PartyChallengeCandidate(x.Row.Id, x.Votes)).ToList(),
            Random.Shared.Next());
        var picked = candidates.Single(x => x.Row.Id == pickedId).Row;
        var affected = await _db.PartyChallengeSessions
            .Where(x => x.Id == session.Id && x.Version == session.Version
                && x.Mode == PartyPlaybackModes.Media)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Mode, PartyPlaybackModes.ChallengeHold)
                .SetProperty(x => x.ActiveChallengeId, picked.Id)
                .SetProperty(x => x.NextChallengeAt, (DateTime?)null)
                .SetProperty(x => x.Version, x => x.Version + 1)
                .SetProperty(x => x.UpdatedAt, Now), ct);
        if (affected == 1)
        {
            _logger.LogInformation(
                "party.challenge.revealed AlbumId={AlbumId} PartyAlbumLinkId={PartyAlbumLinkId} PlaybackSessionId={PlaybackSessionId} ChallengeId={ChallengeId}",
                albumId, link.Id, session.Id, picked.Id);
        }
        return await SnapshotAsync(await ReloadSessionAsync(link.Id, ct), ct);
    }

    public async Task<PartyPlaybackSnapshotDto?> CompleteActiveAsync(Guid ownerId, Guid albumId, CancellationToken ct = default)
    {
        var link = await ActiveGameLinkAsync(ownerId, albumId, ct);
        if (link is null) return await GetSnapshotAsync(ownerId, albumId, ct);
        // Versioned conditional UPDATE provides compare-and-swap semantics;
        // read-committed lets a duplicate NEXT observe 0 affected rows rather
        // than surfacing a serializable-transaction retry to the TV.
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        var session = await _db.PartyChallengeSessions.AsNoTracking()
            .FirstAsync(x => x.PartyAlbumLinkId == link.Id, ct);
        if (session.Mode == PartyPlaybackModes.ChallengeHold && session.ActiveChallengeId is Guid active)
        {
            var now = Now;
            var deadline = Deadline(link);
            var affected = await _db.PartyChallengeSessions
                .Where(x => x.Id == session.Id && x.Version == session.Version
                    && x.Mode == PartyPlaybackModes.ChallengeHold && x.ActiveChallengeId == active)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.Mode, PartyPlaybackModes.Media)
                    .SetProperty(x => x.ActiveChallengeId, (Guid?)null)
                    .SetProperty(x => x.CompletedCount, x => x.CompletedCount + 1)
                    .SetProperty(x => x.NextChallengeAt, deadline)
                    .SetProperty(x => x.Version, x => x.Version + 1)
                    .SetProperty(x => x.UpdatedAt, now), ct);
            if (affected == 1)
            {
                _db.PartyChallengeCompletions.Add(new PartyChallengeCompletion
                    { Id = Guid.NewGuid(), PartyAlbumLinkId = link.Id, PartyChallengeId = active, CompletedAt = now });
                await _db.SaveChangesAsync(ct);
                _logger.LogInformation(
                    "party.challenge.completed AlbumId={AlbumId} PartyAlbumLinkId={PartyAlbumLinkId} PlaybackSessionId={PlaybackSessionId} ChallengeId={ChallengeId}",
                    albumId, link.Id, session.Id, active);
            }
        }
        await tx.CommitAsync(ct);
        var latest = await _db.PartyChallengeSessions.AsNoTracking()
            .SingleAsync(x => x.PartyAlbumLinkId == link.Id, ct);
        return await SnapshotAsync(latest, ct);
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;
    private DateTime Deadline(PartyAlbumLink link) =>
        PartyChallengePolicy.NextDeadline(Now, link.MinChallengeIntervalSeconds, link.MaxChallengeIntervalSeconds,
            Random.Shared.Next(link.MaxChallengeIntervalSeconds - link.MinChallengeIntervalSeconds + 1));

    private async Task<PartyChallengeSession> EnsureSessionAsync(PartyAlbumLink link, CancellationToken ct)
    {
        var row = await _db.PartyChallengeSessions.FirstOrDefaultAsync(x => x.PartyAlbumLinkId == link.Id, ct);
        if (row is not null)
        {
            if (row.Mode == PartyPlaybackModes.Media && row.NextChallengeAt is null)
            {
                row.NextChallengeAt = Deadline(link);
                row.Version++;
                row.UpdatedAt = Now;
                try
                {
                    await _db.SaveChangesAsync(ct);
                }
                catch (DbUpdateConcurrencyException)
                {
                    _db.ChangeTracker.Clear();
                    return await ReloadSessionAsync(link.Id, ct);
                }
            }
            return row;
        }
        row = new PartyChallengeSession
        {
            Id = Guid.NewGuid(), PartyAlbumLinkId = link.Id, Mode = PartyPlaybackModes.Media,
            NextChallengeAt = Deadline(link), CreatedAt = Now, UpdatedAt = Now,
        };
        _db.PartyChallengeSessions.Add(row);
        try
        {
            await _db.SaveChangesAsync(ct);
            return row;
        }
        catch (DbUpdateException)
        {
            // Snapshot polling and the first media boundary may arrive together.
            // The unique link index elects one session; the loser adopts it.
            _db.ChangeTracker.Clear();
            return await ReloadSessionAsync(link.Id, ct);
        }
    }

    private Task<PartyChallengeSession> ReloadSessionAsync(Guid linkId, CancellationToken ct) =>
        _db.PartyChallengeSessions.AsNoTracking()
            .SingleAsync(x => x.PartyAlbumLinkId == linkId, ct);

    private async Task<PartyPlaybackSnapshotDto> SnapshotAsync(PartyChallengeSession session, CancellationToken ct)
    {
        PartyChallengePresentationDto? active = null;
        if (session.ActiveChallengeId is Guid id)
        {
            var row = await _db.PartyChallenges.AsNoTracking().Where(x => x.Id == id)
                .Select(x => new { x.Id, x.Title, x.Body, x.Kind, x.MediaFileItemId, x.AlbumId })
                .FirstOrDefaultAsync(ct);
            if (row is not null)
            {
                var mediaOk = row.MediaFileItemId is Guid mediaId
                    && await _db.AlbumItems.AsNoTracking()
                        .AnyAsync(x => x.AlbumId == row.AlbumId && x.FileItemId == mediaId, ct);
                active = new PartyChallengePresentationDto(
                    row.Id, row.Title, row.Body, row.Kind,
                    mediaOk ? $"/api/tv/media/{row.MediaFileItemId}/preview" : null);
            }
        }
        return new PartyPlaybackSnapshotDto(session.Mode, active, session.NextChallengeAt, session.CompletedCount);
    }

    private Task<bool> OwnsAsync(Guid ownerId, Guid albumId, CancellationToken ct) =>
        _db.Albums.AsNoTracking().AnyAsync(x => x.Id == albumId && x.OwnerUserId == ownerId, ct);

    private async Task<Guid?> ActiveLinkIdAsync(Guid ownerId, Guid albumId, CancellationToken ct) =>
        await _db.PartyAlbumLinks.AsNoTracking()
            .Where(x => x.OwnerUserId == ownerId && x.AlbumId == albumId && x.Enabled && x.RevokedAt == null)
            .OrderByDescending(x => x.CreatedAt).Select(x => (Guid?)x.Id).FirstOrDefaultAsync(ct);

    private Task<PartyAlbumLink?> ActiveGameLinkAsync(Guid ownerId, Guid albumId, CancellationToken ct) =>
        _db.PartyAlbumLinks.Where(x => x.OwnerUserId == ownerId && x.AlbumId == albumId
            && x.Enabled && x.RevokedAt == null && x.GameEnabled)
            .OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(ct);

    private async Task<(string AlbumName, int Max, int Used)?> GuestStateAsync(
        PartyAccess access, Guid participantId, Guid linkId, CancellationToken ct)
    {
        var row = await _db.PartyAlbumLinks.AsNoTracking()
            .Where(x => x.Id == linkId && x.AlbumId == access.AlbumId && x.Enabled && x.GameEnabled)
            .Join(_db.Albums.AsNoTracking(), x => x.AlbumId, a => a.Id,
                (x, a) => new { a.Name, x.VotesPerGuest }).FirstOrDefaultAsync(ct);
        if (row is null) return null;
        var used = await _db.PartyChallengeVotes.AsNoTracking()
            .CountAsync(x => x.PartyAlbumLinkId == linkId && x.PartyParticipantId == participantId, ct);
        return (row.Name, row.VotesPerGuest, used);
    }

    private Task<bool> MediaAllowedAsync(Guid ownerId, Guid albumId, Guid? mediaId, CancellationToken ct) =>
        mediaId is null ? Task.FromResult(true) :
        _db.AlbumItems.AsNoTracking().AnyAsync(ai => ai.AlbumId == albumId && ai.FileItemId == mediaId
            && _db.Albums.Any(a => a.Id == albumId && a.OwnerUserId == ownerId)
            && _db.FileItems.Any(f => f.Id == mediaId && f.OwnerUserId == ownerId && f.DeletedAt == null), ct);

    private static bool Valid(PartyChallengeWriteRequest r) =>
        !string.IsNullOrWhiteSpace(r.Title) && r.Title.Trim().Length <= PartyChallengeLimits.MaxTitleLength
        && !string.IsNullOrWhiteSpace(r.Body) && r.Body.Trim().Length <= PartyChallengeLimits.MaxBodyLength
        && PartyChallengeKinds.IsKnown(r.Kind);

    private static PartyChallengeDto OwnerDto(PartyChallenge x, int votes) =>
        new(x.Id, x.Title, x.Body, x.Kind, x.MediaFileItemId,
            x.MediaFileItemId is Guid id ? $"/api/files/{id}/thumbnail?size=medium" : null,
            x.IsEnabled, x.SortOrder, votes, x.CreatedAt, x.UpdatedAt);

    private async Task ReleaseVotesForChallengeAsync(Guid challengeId, CancellationToken ct)
    {
        var participants = await _db.PartyChallengeVotes.AsNoTracking()
            .Where(x => x.PartyChallengeId == challengeId)
            .GroupBy(x => x.PartyParticipantId)
            .Select(g => new { ParticipantId = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        foreach (var guest in participants)
        {
            var count = guest.Count;
            await _db.PartyParticipants.Where(x => x.Id == guest.ParticipantId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(
                    x => x.ChallengeVoteCount,
                    x => x.ChallengeVoteCount > count ? x.ChallengeVoteCount - count : 0), ct);
        }
        await _db.PartyChallengeVotes.Where(x => x.PartyChallengeId == challengeId)
            .ExecuteDeleteAsync(ct);
    }
}
