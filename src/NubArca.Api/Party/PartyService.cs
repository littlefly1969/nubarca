using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Domain;

namespace NubArca.Api.Party;

public sealed class PartyService : IPartyService
{
    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;

    public PartyService(AppDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<Domain.Party?> EnsureForAlbumAsync(
        Guid ownerUserId, Guid albumId, CancellationToken cancellationToken = default)
    {
        var album = await _db.Albums
            .AsNoTracking()
            .Where(a => a.Id == albumId && a.OwnerUserId == ownerUserId)
            .Select(a => new { a.Name })
            .FirstOrDefaultAsync(cancellationToken);
        if (album is null)
        {
            return null;
        }

        // ONE party per historical party album, which is also the rule the
        // migration follows: the lookup is by media source, so however many
        // links this album has been through, they all belong to the same event.
        // The owner filter is on the PARTY rather than only on the album,
        // because the party is the root and its ownership is the authority.
        var existingId = await _db.PartyMediaSources
            .AsNoTracking()
            .Where(s => s.AlbumId == albumId && s.Role == PartyMediaSourceRoles.Main)
            .Join(_db.Parties.AsNoTracking().Where(p => p.OwnerUserId == ownerUserId),
                s => s.PartyId, p => p.Id, (s, p) => new { p.Id, p.CreatedAt })
            .OrderBy(p => p.CreatedAt)
            .Select(p => (Guid?)p.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (existingId is Guid found)
        {
            // Re-read TRACKED, deliberately: the caller may publish a party the
            // migration left in Draft, and that write has to land in the same
            // unit of work as the capability that occasioned it.
            return await _db.Parties.FirstAsync(p => p.Id == found, cancellationToken);
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        var party = new Domain.Party
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerUserId,
            // The album's name is the only title the compatibility entry point
            // can honestly produce; there is no Party UI yet to ask for one.
            Title = album.Name,
            Status = PartyStatuses.Draft,
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.Parties.Add(party);
        _db.PartyMediaSources.Add(new PartyMediaSource
        {
            PartyId = party.Id,
            AlbumId = albumId,
            Role = PartyMediaSourceRoles.Main,
            SortOrder = 0,
            CreatedAt = now,
        });

        // Deliberately unsaved: see IPartyService.EnsureForAlbumAsync.
        return party;
    }

    public async Task<PartyDto?> GetAsync(
        Guid ownerUserId, Guid partyId, CancellationToken cancellationToken = default)
    {
        var party = await _db.Parties
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == partyId && p.OwnerUserId == ownerUserId, cancellationToken);
        return party is null ? null : await ProjectAsync(party, cancellationToken);
    }

    public async Task<PartyTransitionResult> TransitionAsync(
        Guid ownerUserId,
        Guid partyId,
        PartyLifecycleAction action,
        int expectedVersion,
        CancellationToken cancellationToken = default)
    {
        var party = await _db.Parties
            .FirstOrDefaultAsync(p => p.Id == partyId && p.OwnerUserId == ownerUserId, cancellationToken);
        if (party is null)
        {
            return new PartyTransitionResult(PartyTransitionOutcome.NotFound);
        }

        // Concurrency BEFORE the transition: a caller working from a stale read
        // must be told so, even when the move they asked for happens to be legal
        // from the state they never saw.
        if (party.Version != expectedVersion)
        {
            return new PartyTransitionResult(
                PartyTransitionOutcome.VersionConflict, await ProjectAsync(party, cancellationToken));
        }

        var target = PartyLifecycle.Target(party.Status, action);
        if (target is null)
        {
            return new PartyTransitionResult(
                PartyTransitionOutcome.InvalidTransition, await ProjectAsync(party, cancellationToken));
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        party.Status = target;
        // The timestamps record what HAPPENED, so they are written by the
        // transition that happened and never inferred from a status being read.
        if (action == PartyLifecycleAction.StartLive) party.LiveStartedAt = now;
        if (action == PartyLifecycleAction.EndLive) party.LiveEndedAt = now;
        party.Version++;
        party.UpdatedAt = now;
        await _db.SaveChangesAsync(cancellationToken);

        return new PartyTransitionResult(
            PartyTransitionOutcome.Ok, await ProjectAsync(party, cancellationToken));
    }

    private async Task<PartyDto> ProjectAsync(Domain.Party party, CancellationToken cancellationToken)
    {
        var sources = await _db.PartyMediaSources
            .AsNoTracking()
            .Where(s => s.PartyId == party.Id)
            .Join(_db.Albums.AsNoTracking(), s => s.AlbumId, a => a.Id,
                (s, a) => new { s.AlbumId, a.Name, s.Role, s.SortOrder })
            .OrderBy(s => s.Role)
            .ThenBy(s => s.SortOrder)
            .ThenBy(s => s.AlbumId)
            .ToListAsync(cancellationToken);

        return new PartyDto(
            party.Id, party.Title, party.Description, party.Status,
            party.EventStartsAt, party.LiveStartedAt, party.LiveEndedAt,
            party.GuestAccessExpiresAt, party.LibraryAccessExpiresAt,
            party.Version, party.CreatedAt, party.UpdatedAt,
            sources
                .Select(s => new PartyMediaSourceDto(s.AlbumId, s.Name, s.Role, s.SortOrder))
                .ToList());
    }
}
