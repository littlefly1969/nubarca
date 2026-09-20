using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Domain;

namespace NubArca.Api.Party;

public sealed class PartyGuestbookService : IPartyGuestbookService
{
    /// <summary>
    /// How many dedications one public read hands over.
    ///
    /// <para>A book, not a feed: there is no cursor, no "load more" and no page
    /// token, because a guest book is read by scrolling the last hundred things
    /// people wrote and a party that produced more than this has a host who
    /// wants the whole thing exported, not paginated on a phone. The cap is
    /// here so a long evening cannot turn one anonymous request into an
    /// unbounded response.</para>
    /// </summary>
    internal const int PublicPageSize = 200;

    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;
    private readonly IPartyMessageAccessResolver _access;
    private readonly IPartyParticipantService _participants;

    public PartyGuestbookService(
        AppDbContext db,
        TimeProvider clock,
        IPartyMessageAccessResolver access,
        IPartyParticipantService participants)
    {
        _db = db;
        _clock = clock;
        _access = access;
        _participants = participants;
    }

    public async Task<PartyGuestbookPageDto?> GetPublicPageAsync(
        PartyAccess access, Guid? participantId = null, CancellationToken cancellationToken = default)
    {
        if (!Readable(access))
        {
            return null;
        }

        // ONLY the public ones, decided by the same predicate the greetings use.
        // A pending dedication is not "shown greyed out" and a rejected one is
        // not shown at all: the guest surface receives what is in the book, and
        // what is in the book is what a manager let in.
        var entries = await _db.PartyGuestbookEntries
            .AsNoTracking()
            .Where(e => e.PartyId == access.PartyId && e.Status == PartyMessageStatuses.Visible)
            .OrderByDescending(e => e.CreatedAt)
            .ThenByDescending(e => e.Id)
            .Take(PublicPageSize)
            .Select(e => new PartyGuestbookEntryDto(
                e.Id, e.AuthorDisplayName, e.Body, e.CreatedAt))
            .ToListAsync(cancellationToken);

        return new PartyGuestbookPageDto(
            entries,
            CanWrite: Writable(access),
            Remaining: participantId is Guid guest && access.MaxGuestbookEntriesPerParticipant > 0
                ? Math.Max(
                    0,
                    access.MaxGuestbookEntriesPerParticipant
                        - await _participants.GuestbookCountAsync(guest, cancellationToken))
                : null);
    }

    public async Task<PartyGuestbookSubmissionResult> SubmitAsync(
        PartyAccess access,
        string? authorDisplayName,
        string? body,
        Guid? participantId,
        CancellationToken cancellationToken = default)
    {
        // THE PARTY HAS TO KEEP A BOOK, and that is asked before the text is
        // looked at — so a hand-built request to a party with the book switched
        // off stores nothing and is told nothing about what it sent. The guest
        // surface does not draw the form; this is what makes that a rule.
        if (!Writable(access))
        {
            return PartyGuestbookSubmissionResult.Fail(PartyGuestbookSubmissionError.Disabled);
        }

        // Normalise BEFORE measuring, so the limit applies to what will be
        // stored rather than to whitespace and format characters the guest
        // cannot see.
        if (!PartyGuestbookText.TryNormalizeAuthor(authorDisplayName, out var author))
        {
            return PartyGuestbookSubmissionResult.Fail(
                PartyGuestbookSubmissionError.InvalidAuthorDisplayName);
        }

        if (!PartyGuestbookText.TryNormalizeBody(body, out var text))
        {
            return PartyGuestbookSubmissionResult.Fail(PartyGuestbookSubmissionError.InvalidBody);
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        var entry = new PartyGuestbookEntry
        {
            Id = Guid.NewGuid(),
            // THE PARTY, from the resolved grant. Never from the request: a
            // client naming its own party id would be naming its own authority.
            PartyId = access.PartyId,
            OwnerUserId = access.OwnerUserId,
            // Provenance. Which QR it came in on is worth knowing during an
            // incident and is authoritative for nothing.
            PartyAlbumLinkId = access.PartyAlbumLinkId,
            PartyParticipantId = participantId,
            AuthorDisplayName = author,
            Body = text,
            Status = access.RequireGuestbookApproval
                ? PartyMessageStatuses.Pending
                : PartyMessageStatuses.Visible,
            CreatedAt = now,
            UpdatedAt = now,
        };

        if (participantId is not Guid writer)
        {
            // No guest identity, no budget to spend. Only reachable through a
            // hand-built access; the endpoint always resolves one first.
            _db.PartyGuestbookEntries.Add(entry);
            await _db.SaveChangesAsync(cancellationToken);
            return PartyGuestbookSubmissionResult.Ok(
                new PartyGuestbookSubmissionDto(entry.Id, entry.Status, entry.CreatedAt));
        }

        // CLAIM FIRST, INSIDE THE TRANSACTION. A slot taken by a dedication
        // that then fails to insert has to roll back with it, or a guest loses
        // a go to something nobody will ever read.
        await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);
        if (!await _participants.TryClaimGuestbookAsync(
                writer, access.MaxGuestbookEntriesPerParticipant, cancellationToken))
        {
            await tx.RollbackAsync(cancellationToken);
            return PartyGuestbookSubmissionResult.Fail(PartyGuestbookSubmissionError.LimitReached);
        }

        _db.PartyGuestbookEntries.Add(entry);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            _db.ChangeTracker.Clear();
            throw;
        }

        var used = await _participants.GuestbookCountAsync(writer, cancellationToken);
        return PartyGuestbookSubmissionResult.Ok(new PartyGuestbookSubmissionDto(
            entry.Id, entry.Status, entry.CreatedAt,
            access.MaxGuestbookEntriesPerParticipant > 0
                ? Math.Max(0, access.MaxGuestbookEntriesPerParticipant - used)
                : null));
    }

    public async Task<PartyGuestbookManagerListDto?> ListForManagerAsync(
        Guid partyId, Guid actorUserId, CancellationToken cancellationToken = default)
    {
        var grant = await ResolveManagerAsync(partyId, actorUserId, cancellationToken);
        if (grant is null)
        {
            return null;
        }

        var entries = await _db.PartyGuestbookEntries
            .AsNoTracking()
            .Where(e => e.PartyId == partyId)
            .OrderByDescending(e => e.CreatedAt)
            .ThenByDescending(e => e.Id)
            .Select(e => new PartyGuestbookManagedEntryDto(
                e.Id, e.AuthorDisplayName, e.Body, e.Status, e.CreatedAt, e.ModeratedAt))
            .ToListAsync(cancellationToken);

        return new PartyGuestbookManagerListDto(
            partyId, grant.GuestbookEnabled, grant.RequireGuestbookApproval, grant.IsOwner, entries);
    }

    public async Task<PartyMessageMutation> ModerateAsync(
        Guid partyId,
        Guid actorUserId,
        Guid entryId,
        PartyMessageModeration action,
        CancellationToken cancellationToken = default)
    {
        var grant = await ResolveManagerAsync(partyId, actorUserId, cancellationToken);
        if (grant is null)
        {
            return PartyMessageMutation.NotFound;
        }

        // SCOPED TO THE PARTY the caller proved authority over, in the same
        // query that finds the row: an entry id belonging to somebody else's
        // book is the same nothing as one that never existed, so a route the
        // caller does legitimately hold cannot become a probe.
        var entry = await _db.PartyGuestbookEntries
            .FirstOrDefaultAsync(e => e.Id == entryId && e.PartyId == partyId, cancellationToken);
        if (entry is null)
        {
            return PartyMessageMutation.NotFound;
        }

        // The shared state machine decides, from the CURRENT state and the
        // ACTION — never from a target status, which cannot tell approving
        // something nobody has read apart from restoring something that was
        // taken down.
        var target = PartyMessageTransitions.Target(entry.Status, action);
        if (target is null)
        {
            return PartyMessageMutation.InvalidTransition;
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        entry.Status = target;
        entry.ModeratedAt = now;
        entry.ModeratedByUserId = actorUserId;
        entry.UpdatedAt = now;
        await _db.SaveChangesAsync(cancellationToken);
        return PartyMessageMutation.Ok;
    }

    /// <summary>
    /// Whether the book's PAGES are reachable at all right now.
    ///
    /// <para>The same window the album's photographs use
    /// (<see cref="PartyGuestExperience.AllowsAlbumMedia"/>): during the party,
    /// and afterwards for as long as the memories last. A book is a keepsake,
    /// so it keeps exactly the hours the memories do rather than acquiring a
    /// third window nobody configured.</para>
    /// </summary>
    private static bool Readable(PartyAccess access) =>
        access.GuestbookEnabled && access.Experience.AllowsAlbumMedia;

    /// <summary>
    /// Whether a dedication may be ADDED right now.
    ///
    /// <para>Reading and writing are different questions and get different
    /// answers: the book stays open long after the party, and nothing new goes
    /// into it once the party is over. <c>Capabilities.Contributions</c> already
    /// carries the host's permission with the phase folded in, so this adds no
    /// lifecycle test of its own.</para>
    /// </summary>
    private static bool Writable(PartyAccess access) =>
        Readable(access) && access.Capabilities.Contributions;

    /// <summary>
    /// Who may moderate this party's book.
    ///
    /// <para>The SAME authority that moderates its greetings — owner, or a
    /// delegate holding <c>CanManagePartyMessages</c> on the party's main album
    /// — resolved through the existing <see cref="IPartyMessageAccessResolver"/>
    /// rather than through a second predicate. Contributions are one job; a
    /// party where somebody may take a greeting down but not a dedication would
    /// be a distinction nobody asked for.</para>
    ///
    /// <para>The owner's authority comes from the PARTY row and needs no album
    /// at all, which is what lets a host read their book before the party has
    /// an album and after they have unlinked it.</para>
    /// </summary>
    private async Task<GuestbookManagerGrant?> ResolveManagerAsync(
        Guid partyId, Guid actorUserId, CancellationToken cancellationToken)
    {
        var party = await _db.Parties
            .AsNoTracking()
            .Where(p => p.Id == partyId)
            .Select(p => new { p.Id, p.OwnerUserId })
            .FirstOrDefaultAsync(cancellationToken);
        if (party is null)
        {
            return null;
        }

        var isOwner = party.OwnerUserId == actorUserId;
        var mainAlbumId = await MainAlbumIdAsync(partyId, cancellationToken);

        if (!isOwner)
        {
            if (mainAlbumId is not Guid albumId)
            {
                // A delegate's authority is a membership of an ALBUM. With no
                // album there is nothing for one to be a membership of, and the
                // owner is the only person left — which is the honest answer
                // rather than a widened one.
                return null;
            }

            var delegated = await _access.ResolveAsync(albumId, actorUserId, cancellationToken);
            if (delegated is null || delegated.OwnerUserId != party.OwnerUserId)
            {
                return null;
            }
        }

        var (enabled, requireApproval) = await ActiveLinkSettingsAsync(
            party.OwnerUserId, mainAlbumId, cancellationToken);
        return new GuestbookManagerGrant(party.OwnerUserId, isOwner, enabled, requireApproval);
    }

    private async Task<Guid?> MainAlbumIdAsync(Guid partyId, CancellationToken cancellationToken) =>
        await _db.PartyMediaSources
            .AsNoTracking()
            .Where(s => s.PartyId == partyId && s.Role == PartyMediaSourceRoles.Main)
            .OrderBy(s => s.SortOrder)
            .ThenBy(s => s.AlbumId)
            .Select(s => (Guid?)s.AlbumId)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>
    /// The book's switches, read from the party's ACTIVE capability — the same
    /// predicate the rest of Party uses, evaluated now rather than remembered.
    /// No active link means no book is open, which is also what a guest sees.
    /// </summary>
    private async Task<(bool Enabled, bool RequireApproval)> ActiveLinkSettingsAsync(
        Guid ownerUserId, Guid? albumId, CancellationToken cancellationToken)
    {
        if (albumId is not Guid album)
        {
            return (false, false);
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        var link = await _db.PartyAlbumLinks
            .AsNoTracking()
            .Where(p => p.AlbumId == album && p.OwnerUserId == ownerUserId
                && p.Enabled && p.RevokedAt == null
                && (p.ExpiresAt == null || p.ExpiresAt > now))
            .OrderByDescending(p => p.CreatedAt)
            .Select(p => new { p.GuestbookEnabled, p.RequireGuestbookApproval })
            .FirstOrDefaultAsync(cancellationToken);

        return link is null ? (false, false) : (link.GuestbookEnabled, link.RequireGuestbookApproval);
    }

    private sealed record GuestbookManagerGrant(
        Guid OwnerUserId, bool IsOwner, bool GuestbookEnabled, bool RequireGuestbookApproval);
}
