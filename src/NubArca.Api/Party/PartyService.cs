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

    public async Task<IReadOnlyList<PartySummaryDto>> ListAsync(
        Guid ownerUserId, CancellationToken cancellationToken = default)
    {
        // The main source is LEFT-joined rather than required: a party with no
        // album yet is an ordinary, expected state — the event exists before the
        // photographs — and it must appear in the list like any other.
        var rows = await _db.Parties
            .AsNoTracking()
            .Where(p => p.OwnerUserId == ownerUserId)
            .GroupJoin(
                _db.PartyMediaSources.AsNoTracking()
                    .Where(s => s.Role == PartyMediaSourceRoles.Main)
                    .Join(_db.Albums.AsNoTracking(), s => s.AlbumId, a => a.Id,
                        (s, a) => new { s.PartyId, a.Id, a.Name }),
                p => p.Id, s => s.PartyId, (p, sources) => new { Party = p, Sources = sources })
            .SelectMany(x => x.Sources.DefaultIfEmpty(), (x, source) => new
            {
                x.Party.Id,
                x.Party.Title,
                x.Party.Status,
                x.Party.EventStartsAt,
                x.Party.LiveStartedAt,
                x.Party.LiveEndedAt,
                x.Party.UpdatedAt,
                x.Party.CreatedAt,
                AlbumId = (Guid?)source.Id,
                AlbumName = source.Name,
            })
            .ToListAsync(cancellationToken);

        // Ordered in MEMORY, and deliberately: "what is happening, then what is
        // coming, then what is over" is a product statement about status, not
        // something a database index expresses, and an owner's parties are a
        // handful of rows. Ordering it here rather than half here and half in
        // SQL is what keeps the list from reshuffling between two reads.
        return rows
            .OrderBy(r => StatusRank(r.Status))
            .ThenBy(r => r.EventStartsAt ?? r.CreatedAt)
            .ThenBy(r => r.Id)
            .Select(r => new PartySummaryDto(
                r.Id, r.Title, r.Status, r.EventStartsAt, r.LiveStartedAt, r.LiveEndedAt,
                r.UpdatedAt, r.AlbumId, r.AlbumName))
            .ToList();
    }

    // Live first — it is happening now — then what is being prepared or
    // announced, then what is over. Everything an owner might still act on
    // sorts above everything they cannot.
    private static int StatusRank(string status) => status switch
    {
        PartyStatuses.Live => 0,
        PartyStatuses.Published => 1,
        PartyStatuses.Draft => 2,
        _ => 3,
    };

    public async Task<PartyMutationResult> CreateAsync(
        Guid ownerUserId, PartyMetadataRequest request, CancellationToken cancellationToken = default)
    {
        var title = PartyTextLimits.Normalize(request.Title);
        var description = PartyTextLimits.Normalize(request.Description);
        if (!PartyTextLimits.IsValidTitle(title) || !PartyTextLimits.IsValidDescription(description))
        {
            return PartyMutationResult.Refused(PartyMutationOutcome.InvalidRequest);
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        var party = new Domain.Party
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerUserId,
            Title = title!,
            Description = description,
            EventStartsAt = request.EventStartsAt,
            // Guest access is not configured at creation: there is nothing to
            // give access TO yet, and a window on a party with no capability
            // would be a setting with no effect.
            Status = PartyStatuses.Draft,
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.Parties.Add(party);
        await _db.SaveChangesAsync(cancellationToken);

        // No album, no capability, no token, no television, no game and no print
        // configuration. Every one of those is a later decision by the host.
        return PartyMutationResult.Ok(await ProjectAsync(party, cancellationToken));
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

        // ONE party per party album — the rule the migration follows, and since
        // P2 a UNIQUE (AlbumId, Role) in the database rather than a winner this
        // method picks. There is therefore at most one row to find, and no
        // ordering that decides which of several it means.
        var existingId = await _db.PartyMediaSources
            .AsNoTracking()
            .Where(s => s.AlbumId == albumId && s.Role == PartyMediaSourceRoles.Main)
            .Select(s => (Guid?)s.PartyId)
            .FirstOrDefaultAsync(cancellationToken);
        if (existingId is Guid found)
        {
            // Re-read TRACKED, deliberately: the caller may publish a party the
            // migration left in Draft, and that write has to land in the same
            // unit of work as the capability that occasioned it.
            //
            // The owner filter is on the PARTY: the album is already known to be
            // this caller's, so a party of somebody else's holding it is a state
            // the application cannot produce — and answering null is the safe
            // reading of it rather than handing over another owner's event.
            return await _db.Parties
                .FirstOrDefaultAsync(p => p.Id == found && p.OwnerUserId == ownerUserId, cancellationToken);
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

    public async Task<PartyMutationResult> TransitionAsync(
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
            return PartyMutationResult.Refused(PartyMutationOutcome.NotFound);
        }

        // Concurrency BEFORE the transition: a caller working from a stale read
        // must be told so, even when the move they asked for happens to be legal
        // from the state they never saw.
        if (party.Version != expectedVersion)
        {
            return PartyMutationResult.Refused(
                PartyMutationOutcome.VersionConflict, await ProjectAsync(party, cancellationToken));
        }

        var target = PartyLifecycle.Target(party.Status, action);
        if (target is null)
        {
            return PartyMutationResult.Refused(
                PartyMutationOutcome.InvalidTransition, await ProjectAsync(party, cancellationToken));
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

        return PartyMutationResult.Ok(await ProjectAsync(party, cancellationToken));
    }

    public async Task<PartyMutationResult> UpdateMetadataAsync(
        Guid ownerUserId,
        Guid partyId,
        PartyMetadataRequest request,
        int expectedVersion,
        CancellationToken cancellationToken = default)
    {
        var title = PartyTextLimits.Normalize(request.Title);
        var description = PartyTextLimits.Normalize(request.Description);
        if (!PartyTextLimits.IsValidTitle(title) || !PartyTextLimits.IsValidDescription(description))
        {
            return PartyMutationResult.Refused(PartyMutationOutcome.InvalidRequest);
        }

        var party = await _db.Parties
            .FirstOrDefaultAsync(p => p.Id == partyId && p.OwnerUserId == ownerUserId, cancellationToken);
        if (party is null)
        {
            return PartyMutationResult.Refused(PartyMutationOutcome.NotFound);
        }
        if (party.Version != expectedVersion)
        {
            return PartyMutationResult.Refused(
                PartyMutationOutcome.VersionConflict, await ProjectAsync(party, cancellationToken));
        }

        // The party's DATA, and only its data. Status, LiveStartedAt and
        // LiveEndedAt are absent from this method by construction, not by a
        // filter somebody could relax: they belong to the transitions, and a
        // form able to write them could describe an evening that never happened.
        //
        // The title is the PARTY's, not the album's. Renaming one has never
        // renamed the other since P2, and there is deliberately no sync: the
        // event and the collection of photographs are different things that
        // happened to share a name when the party was made from an album.
        party.Title = title!;
        party.Description = description;
        party.EventStartsAt = request.EventStartsAt;
        party.GuestAccessExpiresAt = request.GuestAccessExpiresAt;
        party.Version++;
        party.UpdatedAt = _clock.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync(cancellationToken);

        return PartyMutationResult.Ok(await ProjectAsync(party, cancellationToken));
    }

    public async Task<PartyMutationResult> SetMainMediaSourceAsync(
        Guid ownerUserId,
        Guid partyId,
        Guid albumId,
        int expectedVersion,
        CancellationToken cancellationToken = default)
    {
        var party = await _db.Parties
            .FirstOrDefaultAsync(p => p.Id == partyId && p.OwnerUserId == ownerUserId, cancellationToken);
        if (party is null)
        {
            return PartyMutationResult.Refused(PartyMutationOutcome.NotFound);
        }
        if (party.Version != expectedVersion)
        {
            return PartyMutationResult.Refused(
                PartyMutationOutcome.VersionConflict, await ProjectAsync(party, cancellationToken));
        }

        // OWNERSHIP, not authority. A shared album's Editor may curate it and
        // still must never be able to make it a party's source: the owner id on
        // the album is the whole test, and a missing or foreign album is the
        // same generic not-found as a party that is not the caller's.
        var albumOk = await _db.Albums
            .AsNoTracking()
            .AnyAsync(a => a.Id == albumId && a.OwnerUserId == ownerUserId, cancellationToken);
        if (!albumOk)
        {
            return PartyMutationResult.Refused(PartyMutationOutcome.NotFound);
        }

        var current = await _db.PartyMediaSources
            .FirstOrDefaultAsync(
                s => s.PartyId == partyId && s.Role == PartyMediaSourceRoles.Main, cancellationToken);
        if (current is not null && current.AlbumId == albumId)
        {
            // Already true. Nothing to write, no version to spend, and no
            // pretence that a decision was taken.
            return PartyMutationResult.Ok(await ProjectAsync(party, cancellationToken));
        }

        // THE LOCK. Any capability that has ever existed for this party — active,
        // revoked or superseded — fixes the main album, because the guests,
        // greetings, uploads, prints, games and face searches that may already
        // exist are scoped to a link that names it. Moving the album afterwards
        // would silently turn a UI edit into a domain migration, so it is refused
        // out loud instead.
        var everHadCapability = await _db.PartyAlbumLinks
            .AsNoTracking()
            .AnyAsync(l => l.PartyId == partyId, cancellationToken);
        if (everHadCapability)
        {
            return PartyMutationResult.Refused(
                PartyMutationOutcome.MediaSourceLocked, await ProjectAsync(party, cancellationToken));
        }

        // One album is one party's main source. The database says so as well —
        // UNIQUE (AlbumId, Role) — so this check is the courteous answer and the
        // constraint is the true one; a concurrent second attempt loses there
        // rather than here.
        var taken = await _db.PartyMediaSources
            .AsNoTracking()
            .AnyAsync(s => s.AlbumId == albumId && s.Role == PartyMediaSourceRoles.Main, cancellationToken);
        if (taken)
        {
            return PartyMutationResult.Refused(
                PartyMutationOutcome.AlbumAlreadyInUse, await ProjectAsync(party, cancellationToken));
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        if (current is not null)
        {
            // Replaced, not edited: the key is (PartyId, AlbumId), so pointing
            // at another album is a different row.
            _db.PartyMediaSources.Remove(current);
        }
        _db.PartyMediaSources.Add(new PartyMediaSource
        {
            PartyId = partyId,
            AlbumId = albumId,
            Role = PartyMediaSourceRoles.Main,
            SortOrder = 0,
            CreatedAt = now,
        });
        party.Version++;
        party.UpdatedAt = now;

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // The unique index refused it: somebody else claimed this album for
            // their own party between the check above and this write. The
            // courteous answer and the enforced one agree.
            _db.ChangeTracker.Clear();
            var reread = await GetAsync(ownerUserId, partyId, cancellationToken);
            return PartyMutationResult.Refused(PartyMutationOutcome.AlbumAlreadyInUse, reread);
        }

        return PartyMutationResult.Ok(await ProjectAsync(party, cancellationToken));
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

        // Whether the main album is still the host's to choose. Answered here so
        // the owner surface can say so plainly rather than learning it from a
        // refusal after they have already picked something.
        var everHadCapability = await _db.PartyAlbumLinks
            .AsNoTracking()
            .AnyAsync(l => l.PartyId == party.Id, cancellationToken);

        return new PartyDto(
            party.Id, party.Title, party.Description, party.Status,
            party.EventStartsAt, party.LiveStartedAt, party.LiveEndedAt,
            party.GuestAccessExpiresAt, party.LibraryAccessExpiresAt,
            party.Version, party.CreatedAt, party.UpdatedAt,
            sources
                .Select(s => new PartyMediaSourceDto(s.AlbumId, s.Name, s.Role, s.SortOrder))
                .ToList(),
            CanChangeMainMediaSource: !everHadCapability);
    }
}
