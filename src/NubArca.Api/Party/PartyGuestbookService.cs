using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Metadata;
using NubArca.Api.Storage;

namespace NubArca.Api.Party;

public sealed class PartyGuestbookService : IPartyGuestbookService
{
    /// <summary>
    /// How many memories one public read hands over.
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
    private readonly IBlobService _blobs;
    private readonly PartyGuestbookPhotoCache _photos;

    public PartyGuestbookService(
        AppDbContext db,
        TimeProvider clock,
        IPartyMessageAccessResolver access,
        IPartyParticipantService participants,
        IBlobService blobs,
        PartyGuestbookPhotoCache photos)
    {
        _db = db;
        _clock = clock;
        _access = access;
        _participants = participants;
        _blobs = blobs;
        _photos = photos;
    }

    // ── Where the pictures are ──────────────────────────────────────────────
    //
    // Built here, from the capability that asked, every time. Never stored: a
    // URL is a statement about who may fetch something NOW, and a memory
    // outlives every token it was read through.

    /// <summary>A memory's picture, on the guest's own token.</summary>
    public static string PublicPhotoUrl(string token, Guid entryId) =>
        $"/api/party/{Uri.EscapeDataString(token)}/guestbook/{entryId}/photo";

    /// <summary>A photograph the guest may choose, on the guest's own token.</summary>
    public static string CandidateUrl(string token, Guid fileItemId, string variant) =>
        $"/api/party/{Uri.EscapeDataString(token)}/guestbook/photos/{fileItemId}/{variant}";

    /// <summary>A memory's picture, on the host's own route.</summary>
    public static string ManagerPhotoUrl(Guid partyId, Guid entryId) =>
        $"/api/parties/{partyId}/guestbook/{entryId}/photo";

    // ── The guest's book ────────────────────────────────────────────────────

    public async Task<PartyGuestbookPageDto?> GetPublicPageAsync(
        PartyAccess access,
        string token,
        Guid? participantId = null,
        CancellationToken cancellationToken = default)
    {
        if (!Readable(access))
        {
            return null;
        }

        // ONLY the public ones, decided by the same predicate the greetings use.
        // A pending memory is not "shown greyed out" and a rejected one is not
        // shown at all: the guest surface receives what is in the book, and
        // what is in the book is what a manager let in.
        var rows = await _db.PartyGuestbookEntries
            .AsNoTracking()
            .Where(e => e.PartyId == access.PartyId && e.Status == PartyMessageStatuses.Visible)
            .OrderByDescending(e => e.CreatedAt)
            .ThenByDescending(e => e.Id)
            .Take(PublicPageSize)
            .ToListAsync(cancellationToken);

        return new PartyGuestbookPageDto(
            rows.Select(e => ToPublicDto(e, PublicPhotoUrl(token, e.Id))).ToList(),
            CanWrite: Writable(access),
            Remaining: await RemainingAsync(access, participantId, cancellationToken));
    }

    public async Task<PartyGuestbookPhotosDto?> ListPhotosAsync(
        PartyAccess access, string token, CancellationToken cancellationToken = default)
    {
        // The chooser exists only where a memory may be written: there is
        // nothing to choose a photograph FOR in a closed book.
        if (!Writable(access))
        {
            return null;
        }

        var rows = await Candidates(access)
            // The album's own order, with the same stable tie-break every party
            // surface uses, so the grid does not reshuffle between visits.
            .OrderBy(x => x.AddedAt)
            .ThenBy(x => x.Id)
            .Select(x => new { x.Id, x.FileWidth, x.FileHeight, x.BlobWidth, x.BlobHeight, x.Orientation })
            .ToListAsync(cancellationToken);

        var photos = new List<PartyGuestbookPhotoDto>(rows.Count);
        foreach (var row in rows)
        {
            // A photograph whose shape is not known yet cannot be framed, so it
            // is not offered — the same answer the publish gives it.
            if (DisplaySize(row.FileWidth, row.FileHeight, row.BlobWidth, row.BlobHeight, row.Orientation)
                is not { } size)
            {
                continue;
            }

            var (width, height) = size;

            photos.Add(new PartyGuestbookPhotoDto(
                row.Id,
                CandidateUrl(token, row.Id, "thumbnail"),
                CandidateUrl(token, row.Id, "preview"),
                width,
                height,
                PartyGuestbookPhotoShape.Orientation(width, height)));
        }

        return new PartyGuestbookPhotosDto(photos);
    }

    public async Task<bool> IsChoosableAsync(
        PartyAccess access, Guid fileItemId, CancellationToken cancellationToken = default) =>
        Writable(access)
        && await Candidates(access).AnyAsync(x => x.Id == fileItemId, cancellationToken);

    public async Task<PartyGuestbookSubmissionResult> SubmitAsync(
        PartyAccess access,
        string token,
        PartyGuestbookSubmission submission,
        Guid? participantId,
        CancellationToken cancellationToken = default)
    {
        // THE PARTY HAS TO KEEP A BOOK, and that is asked before anything sent
        // is looked at — so a hand-built request to a party with the book
        // switched off stores nothing and is told nothing about what it sent.
        if (!Writable(access))
        {
            return PartyGuestbookSubmissionResult.Fail(PartyGuestbookSubmissionError.Disabled);
        }

        // Normalise BEFORE measuring, so the limit applies to what will be
        // stored rather than to whitespace and format characters the guest
        // cannot see.
        if (!PartyGuestbookText.TryNormalizeAuthor(submission.AuthorDisplayName, out var author))
        {
            return PartyGuestbookSubmissionResult.Fail(
                PartyGuestbookSubmissionError.InvalidAuthorDisplayName);
        }

        if (!PartyGuestbookText.TryNormalizeBody(submission.Body, out var text))
        {
            return PartyGuestbookSubmissionResult.Fail(PartyGuestbookSubmissionError.InvalidBody);
        }

        // The client names the design; the SERVER decides its version.
        if (!PartyGuestbookTemplates.TryCurrentVersion(submission.TemplateKey, out var templateVersion))
        {
            return PartyGuestbookSubmissionResult.Fail(PartyGuestbookSubmissionError.InvalidTemplate);
        }

        // The crop editor's own limits, refused rather than clamped: a value
        // outside them is not something the composer produces, so it is not a
        // framing anybody chose.
        if (submission.CropZoom is not double zoom
            || submission.CropCenterX is not double centerX
            || submission.CropCenterY is not double centerY
            || !PartyGuestContentMediaOrientations.IsValidCrop(zoom, centerX, centerY))
        {
            return PartyGuestbookSubmissionResult.Fail(PartyGuestbookSubmissionError.InvalidCrop);
        }

        if (submission.SourceMediaItemId is not Guid sourceId)
        {
            return PartyGuestbookSubmissionResult.Fail(PartyGuestbookSubmissionError.PhotoRequired);
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
            TemplateKey = submission.TemplateKey!,
            TemplateVersion = templateVersion,
            CropCenterX = centerX,
            CropCenterY = centerY,
            CropZoom = zoom,
            Status = access.RequireGuestbookApproval
                ? PartyMessageStatuses.Pending
                : PartyMessageStatuses.Visible,
            CreatedAt = now,
            UpdatedAt = now,
        };

        // ONE UNIT: the photograph is re-checked, the guest's slot is claimed,
        // the memory takes its own reference to the photograph's blob, and the
        // row is written — all inside one transaction, so any failure leaves no
        // memory, no reference and no spent slot. Every write here goes through
        // this context's connection, the quota claim and the reference
        // increment included, so the rollback undoes them with the row.
        await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            // RE-CHECKED NOW, not trusted from the chooser. The host may have
            // removed this photograph since the guest saw it — that is an
            // ordinary race, and its answer is "choose another", with nothing
            // spent.
            var photo = await ResolvePhotoAsync(access, sourceId, cancellationToken);
            if (photo.Error is PartyGuestbookSubmissionError refused)
            {
                await tx.RollbackAsync(cancellationToken);
                return PartyGuestbookSubmissionResult.Fail(refused);
            }

            if (participantId is Guid writer
                && !await _participants.TryClaimGuestbookAsync(
                    writer, access.MaxGuestbookEntriesPerParticipant, cancellationToken))
            {
                await tx.RollbackAsync(cancellationToken);
                return PartyGuestbookSubmissionResult.Fail(PartyGuestbookSubmissionError.LimitReached);
            }

            // The memory's OWN reference to the very same bytes: no copy, no
            // second blob. From here the album file and the memory each hold
            // one, and either may go without the other noticing.
            await _blobs.AcquireExistingAsync(photo.BlobObjectId, cancellationToken);
            entry.BlobObjectId = photo.BlobObjectId;
            entry.PhotoWidth = photo.Width;
            entry.PhotoHeight = photo.Height;

            _db.PartyGuestbookEntries.Add(entry);
            await _db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
        }
        catch
        {
            // Disposing the uncommitted transaction rolls it back — including
            // after a failed COMMIT, where an explicit rollback could throw and
            // hide the error that matters. That rollback undoes the reference
            // and the claim with the row; a manual release here would be a
            // second decrement of a count the rollback already restored.
            _db.ChangeTracker.Clear();
            throw;
        }

        return PartyGuestbookSubmissionResult.Ok(new PartyGuestbookSubmissionDto(
            entry.Id,
            entry.Status,
            entry.CreatedAt,
            await RemainingAsync(access, participantId, cancellationToken),
            ToPublicDto(entry, PublicPhotoUrl(token, entry.Id))));
    }

    public async Task<Stream?> OpenPublicPhotoAsync(
        PartyAccess access, Guid entryId, CancellationToken cancellationToken = default)
    {
        if (!Readable(access))
        {
            return null;
        }

        // The same predicate the book's page uses, in the query that finds the
        // memory: an entry of another party, or one not in the book, is the
        // same nothing as one that never existed.
        var visible = await _db.PartyGuestbookEntries
            .AsNoTracking()
            .AnyAsync(e => e.Id == entryId
                && e.PartyId == access.PartyId
                && e.Status == PartyMessageStatuses.Visible,
                cancellationToken);
        return visible ? await _photos.OpenAsync(entryId, cancellationToken) : null;
    }

    // ── The manager's book ──────────────────────────────────────────────────

    public async Task<PartyGuestbookManagerListDto?> ListForManagerAsync(
        Guid partyId,
        Guid actorUserId,
        Func<Guid, string> photoUrl,
        CancellationToken cancellationToken = default)
    {
        var grant = await ResolveManagerAsync(partyId, actorUserId, cancellationToken);
        if (grant is null)
        {
            return null;
        }

        var rows = await _db.PartyGuestbookEntries
            .AsNoTracking()
            .Where(e => e.PartyId == partyId)
            .OrderByDescending(e => e.CreatedAt)
            .ThenByDescending(e => e.Id)
            .ToListAsync(cancellationToken);

        return new PartyGuestbookManagerListDto(
            partyId, grant.GuestbookEnabled, grant.RequireGuestbookApproval, grant.IsOwner,
            rows.Select(e => new PartyGuestbookManagedEntryDto(
                e.Id, e.AuthorDisplayName, e.Body, e.Status, e.CreatedAt, e.ModeratedAt,
                Template(e), Media(e, photoUrl(e.Id)))).ToList());
    }

    public async Task<Stream?> OpenManagedPhotoAsync(
        Guid partyId, Guid actorUserId, Guid entryId, CancellationToken cancellationToken = default)
    {
        if (await ResolveManagerAsync(partyId, actorUserId, cancellationToken) is null)
        {
            return null;
        }

        // Every state: a manager deciding whether to let a memory in has to see
        // what it shows. Scoped to the party in the same query.
        var exists = await _db.PartyGuestbookEntries
            .AsNoTracking()
            .AnyAsync(e => e.Id == entryId && e.PartyId == partyId, cancellationToken);
        return exists ? await _photos.OpenAsync(entryId, cancellationToken) : null;
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

    // ── The photograph a guest may choose ───────────────────────────────────

    /// <summary>
    /// THE ONE RULE for which photographs a memory may be made from: what any
    /// party surface may show of the party's MAIN album
    /// (<see cref="PartyMediaService.DisplayableMembers"/> — owner's, active, in
    /// the library, not in the Vault, not an unapproved guest upload), narrowed
    /// to images the SERVER recognised. The chooser lists it, the chooser's
    /// thumbnails are served through it, and the publish re-asks it.
    /// </summary>
    private IQueryable<PartyMediaService.MemberRow> Candidates(PartyAccess access) =>
        PartyMediaService.DisplayableMembers(_db, access.OwnerUserId, access.MainAlbumId)
            .Where(x => x.MediaCategory == MediaCategories.Image && x.DetectedContentType != null);

    private async Task<ResolvedPhoto> ResolvePhotoAsync(
        PartyAccess access, Guid fileItemId, CancellationToken cancellationToken)
    {
        var member = await PartyMediaService.DisplayableMembers(_db, access.OwnerUserId, access.MainAlbumId)
            .Where(x => x.Id == fileItemId)
            .Select(x => new
            {
                x.BlobObjectId, x.MediaCategory, x.DetectedContentType,
                x.FileWidth, x.FileHeight, x.BlobWidth, x.BlobHeight, x.Orientation,
            })
            .FirstOrDefaultAsync(cancellationToken);

        // Missing, foreign, another party's, removed, trashed, vaulted,
        // excluded, unapproved: one answer, because to a guest they mean one
        // thing and because anything finer would be an oracle for file ids.
        if (member is null)
        {
            return ResolvedPhoto.Refused(PartyGuestbookSubmissionError.PhotoUnavailable);
        }

        if (member.MediaCategory != MediaCategories.Image || member.DetectedContentType is null)
        {
            return ResolvedPhoto.Refused(PartyGuestbookSubmissionError.PhotoNotImage);
        }

        // Metadata not read yet: the photograph cannot be framed, so it is not
        // usable now — the chooser does not offer it either.
        if (DisplaySize(member.FileWidth, member.FileHeight, member.BlobWidth, member.BlobHeight, member.Orientation)
            is not { } size)
        {
            return ResolvedPhoto.Refused(PartyGuestbookSubmissionError.PhotoUnavailable);
        }

        return new ResolvedPhoto(member.BlobObjectId, size.Width, size.Height, null);
    }

    private sealed record ResolvedPhoto(
        Guid BlobObjectId, int Width, int Height, PartyGuestbookSubmissionError? Error)
    {
        public static ResolvedPhoto Refused(PartyGuestbookSubmissionError error) => new(Guid.Empty, 0, 0, error);
    }

    /// <summary>
    /// DISPLAY dimensions — FileItem first, the blob's as fallback, swapped for
    /// a quarter-turn EXIF orientation — or null when either is unknown.
    /// </summary>
    private static (int Width, int Height)? DisplaySize(
        int? fileWidth, int? fileHeight, int? blobWidth, int? blobHeight, int? orientation)
    {
        var (width, height) = ImageDisplayDimensions.Resolve(
            fileWidth ?? blobWidth, fileHeight ?? blobHeight, orientation);
        return width is > 0 && height is > 0 ? (width.Value, height.Value) : null;
    }

    // ── Projections ─────────────────────────────────────────────────────────

    private static PartyGuestbookEntryDto ToPublicDto(PartyGuestbookEntry e, string photoUrl) =>
        new(e.Id, e.AuthorDisplayName, e.Body, e.CreatedAt, Template(e), Media(e, photoUrl));

    private static PartyGuestbookTemplateDto Template(PartyGuestbookEntry e) =>
        new(e.TemplateKey, e.TemplateVersion);

    private static PartyGuestbookMediaDto Media(PartyGuestbookEntry e, string url) =>
        new(
            url,
            e.PhotoWidth,
            e.PhotoHeight,
            PartyGuestbookPhotoShape.Orientation(e.PhotoWidth, e.PhotoHeight),
            new PartyGuestbookCropDto(e.CropCenterX, e.CropCenterY, e.CropZoom));

    private async Task<int?> RemainingAsync(
        PartyAccess access, Guid? participantId, CancellationToken cancellationToken) =>
        participantId is Guid guest && access.MaxGuestbookEntriesPerParticipant > 0
            ? Math.Max(
                0,
                access.MaxGuestbookEntriesPerParticipant
                    - await _participants.GuestbookCountAsync(guest, cancellationToken))
            : null;

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
    /// Whether a memory may be ADDED right now.
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
    /// party where somebody may take a greeting down but not a memory would be
    /// a distinction nobody asked for.</para>
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
