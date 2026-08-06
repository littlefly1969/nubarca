using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Metadata;
using NubArca.Api.Security;

namespace NubArca.Api.Albums.Sharing;

// SHARE-ALBUM-01 invitation lifecycle + recipient read model.
//
// PRIVATE VAULT — an audited inconsistency, and why it is already closed:
// `AddItemAsync` reads `_db.FileItems`, which carries the global
// `PrivateVaultId == null` query filter, so a vaulted file CANNOT be added to an
// album. A file already in an album that is later moved INTO the vault keeps its
// `album_items` row, but that row's file is invisible to every query in this
// file and in AlbumAccessResolver — including the owner's own album listing. So
// vaulted media is unreachable through a share without any predicate of our own,
// and this slice deliberately does not widen that. The stale `album_items` row
// is pre-existing behavior (visible in AlbumService.ListItemsAsync too) and is
// reported, not changed here.
public sealed class AlbumSharingService : IAlbumSharingService
{
    private readonly AppDbContext _db;
    private readonly TimeProvider _time;
    private readonly IAlbumAccessResolver _access;

    // The resolver is injected rather than re-implemented: "may this user act on
    // this album" must have exactly one answer, and the contribution path needs
    // the same one the media routes use. No cycle — the resolver depends only on
    // the DbContext.
    public AlbumSharingService(AppDbContext db, TimeProvider time, IAlbumAccessResolver access)
    {
        _db = db;
        _time = time;
        _access = access;
    }

    // ── Owner side ──────────────────────────────────────────────────────────

    public async Task<ResolveAlbumRecipientResponse?> ResolveRecipientAsync(
        Guid ownerUserId, Guid albumId, string? email,
        CancellationToken cancellationToken = default)
    {
        var ownsAlbum = await _db.Albums
            .AsNoTracking()
            .AnyAsync(a => a.Id == albumId && a.OwnerUserId == ownerUserId, cancellationToken);
        if (!ownsAlbum)
        {
            return null;
        }

        var recipient = await FindInvitableRecipientAsync(ownerUserId, email, cancellationToken);
        return recipient is null ? null : new ResolveAlbumRecipientResponse(recipient.DisplayName);
    }

    public async Task<IReadOnlyList<AlbumMemberDto>?> ListMembersAsync(
        Guid ownerUserId, Guid albumId,
        CancellationToken cancellationToken = default)
    {
        var ownsAlbum = await _db.Albums
            .AsNoTracking()
            .AnyAsync(a => a.Id == albumId && a.OwnerUserId == ownerUserId, cancellationToken);
        if (!ownsAlbum)
        {
            return null;
        }

        // Joins Users for DisplayName and for the address that becomes the
        // MASKED hint. The member's user id is never projected, and the raw
        // address never leaves this method — see the privacy note on
        // AlbumMemberDto and RecipientEmailMask.
        //
        // Projects to an ANONYMOUS type, not straight into the positional
        // record: EF cannot compose OrderBy over a record-typed join selector
        // and falls back to client evaluation, which it then refuses. The DTO is
        // built in memory from the ordered rows instead.
        var rows = await _db.AlbumMemberships
            .AsNoTracking()
            .Where(m => m.AlbumId == albumId)
            .Join(_db.Users.AsNoTracking(),
                m => m.MemberUserId,
                u => u.Id,
                (m, u) => new
                {
                    m.Id,
                    u.DisplayName,
                    // Masked in memory below, never in the projection: the raw
                    // address must not survive past this method.
                    u.Email,
                    m.Role,
                    m.State,
                    m.AllowOriginalDownload,
                    m.InvitedAt,
                    m.AcceptedAt,
                    m.DeclinedAt,
                    m.RevokedAt,
                })
            .OrderBy(x => x.InvitedAt)
            .ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);

        return rows.Select(x => new AlbumMemberDto(
            x.Id, x.DisplayName, RecipientEmailMask.Mask(x.Email),
            x.Role, x.State, x.AllowOriginalDownload,
            x.InvitedAt, x.AcceptedAt, x.DeclinedAt, x.RevokedAt)).ToList();
    }

    public async Task<(InviteAlbumMemberResult Result, AlbumMemberDto? Member)> InviteAsync(
        Guid ownerUserId, Guid albumId, InviteAlbumMemberRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var role = string.IsNullOrWhiteSpace(request.Role) ? AlbumRoles.Viewer : request.Role.Trim();
        if (!AlbumRoles.IsAssignable(role))
        {
            return (InviteAlbumMemberResult.RoleNotAssignable, null);
        }

        var normalized = NormalizeEmail(request.Email);
        if (normalized is null)
        {
            return (InviteAlbumMemberResult.InvalidEmail, null);
        }

        var ownsAlbum = await _db.Albums
            .AsNoTracking()
            .AnyAsync(a => a.Id == albumId && a.OwnerUserId == ownerUserId, cancellationToken);
        if (!ownsAlbum)
        {
            return (InviteAlbumMemberResult.AlbumNotFound, null);
        }

        // Self-invite is reported distinctly ONLY here: the owner obviously
        // already knows their own address exists, so there is nothing to leak,
        // and a generic "unavailable" would be a confusing error for a very
        // easy mistake.
        var self = await _db.Users
            .AsNoTracking()
            .AnyAsync(u => u.Id == ownerUserId && u.Email.ToLower() == normalized, cancellationToken);
        if (self)
        {
            return (InviteAlbumMemberResult.RecipientIsOwner, null);
        }

        var recipient = await FindInvitableRecipientAsync(ownerUserId, request.Email, cancellationToken);
        if (recipient is null)
        {
            return (InviteAlbumMemberResult.RecipientUnavailable, null);
        }

        var now = _time.GetUtcNow().UtcDateTime;

        // One row per (album, member). A declined or revoked row is REUSED so
        // the unique index stays a plain one and history lives in the audit log.
        var existing = await _db.AlbumMemberships
            .FirstOrDefaultAsync(
                m => m.AlbumId == albumId && m.MemberUserId == recipient.Id,
                cancellationToken);

        if (existing is not null)
        {
            var active = existing.RevokedAt == null
                && (existing.State == AlbumMembershipStates.Pending
                    || existing.State == AlbumMembershipStates.Accepted);
            if (active)
            {
                return (InviteAlbumMemberResult.AlreadyInvited, null);
            }

            existing.Role = role;
            existing.State = AlbumMembershipStates.Pending;
            existing.AllowOriginalDownload = request.AllowOriginalDownload;
            existing.InvitedByUserId = ownerUserId;
            existing.InvitedAt = now;
            existing.AcceptedAt = null;
            existing.DeclinedAt = null;
            existing.RevokedAt = null;
            existing.UpdatedAt = now;
            await _db.SaveChangesAsync(cancellationToken);
            return (InviteAlbumMemberResult.Ok, ToDto(existing, recipient.DisplayName, recipient.Email));
        }

        var membership = new AlbumMembership
        {
            Id = Guid.NewGuid(),
            AlbumId = albumId,
            MemberUserId = recipient.Id,
            Role = role,
            State = AlbumMembershipStates.Pending,
            AllowOriginalDownload = request.AllowOriginalDownload,
            InvitedByUserId = ownerUserId,
            InvitedAt = now,
            UpdatedAt = now,
        };
        _db.AlbumMemberships.Add(membership);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Two concurrent invites for the same recipient: the unique index
            // decided, and the loser reports the same duplicate outcome as the
            // sequential case rather than a 500.
            _db.Entry(membership).State = EntityState.Detached;
            return (InviteAlbumMemberResult.AlreadyInvited, null);
        }

        return (InviteAlbumMemberResult.Ok, ToDto(membership, recipient.DisplayName, recipient.Email));
    }

    public async Task<(AlbumMemberMutationResult Result, AlbumMemberDto? Member)> UpdateMemberAsync(
        Guid ownerUserId, Guid albumId, Guid membershipId, bool allowOriginalDownload,
        CancellationToken cancellationToken = default)
    {
        var membership = await LoadOwnedMembershipAsync(ownerUserId, albumId, membershipId, cancellationToken);
        if (membership is null)
        {
            return (AlbumMemberMutationResult.NotFound, null);
        }

        membership.AllowOriginalDownload = allowOriginalDownload;
        membership.UpdatedAt = _time.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync(cancellationToken);

        var member = await _db.Users
            .AsNoTracking()
            .Where(u => u.Id == membership.MemberUserId)
            .Select(u => new { u.DisplayName, u.Email })
            .FirstOrDefaultAsync(cancellationToken);

        return (AlbumMemberMutationResult.Ok,
            ToDto(membership, member?.DisplayName ?? string.Empty, member?.Email));
    }

    public async Task<(InviteAlbumMemberResult Result, AlbumMemberDto? Member)> ChangeMemberRoleAsync(
        Guid ownerUserId, Guid albumId, Guid membershipId, string? role,
        CancellationToken cancellationToken = default)
    {
        var requested = role?.Trim();
        if (!AlbumRoles.IsAssignable(requested))
        {
            // Includes `editor`: in the catalog and the check constraint for
            // SHARE-ALBUM-03, but refused here exactly as it is on invite.
            return (InviteAlbumMemberResult.RoleNotAssignable, null);
        }

        var membership = await LoadOwnedMembershipAsync(ownerUserId, albumId, membershipId, cancellationToken);
        if (membership is null)
        {
            return (InviteAlbumMemberResult.AlbumNotFound, null);
        }

        // A demotion deliberately leaves existing contributions in place. The
        // right to withdraw them survives it (see WithdrawContributionAsync), so
        // demoting somebody does not strand their media in the album.
        membership.Role = requested!;
        membership.UpdatedAt = _time.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync(cancellationToken);

        var member = await _db.Users
            .AsNoTracking()
            .Where(u => u.Id == membership.MemberUserId)
            .Select(u => new { u.DisplayName, u.Email })
            .FirstOrDefaultAsync(cancellationToken);

        return (InviteAlbumMemberResult.Ok,
            ToDto(membership, member?.DisplayName ?? string.Empty, member?.Email));
    }

    public async Task<(AlbumMemberMutationResult Result, RevokedMembership? Revoked)> RevokeMemberAsync(
        Guid ownerUserId, Guid albumId, Guid membershipId,
        CancellationToken cancellationToken = default)
    {
        var membership = await LoadOwnedMembershipAsync(ownerUserId, albumId, membershipId, cancellationToken);
        if (membership is null)
        {
            return (AlbumMemberMutationResult.NotFound, null);
        }

        // Idempotent: an already-revoked row keeps its original RevokedAt (that
        // is the timestamp the audit trail refers to). Its contributions are
        // still swept, so a partially-applied earlier revoke self-heals.
        var alreadyRevoked = membership.RevokedAt is not null;

        // ONE transaction: the membership ending and its contributions leaving
        // the album are a single fact. A crash between them would leave media in
        // an album whose contributor no longer has access — which the resolver
        // would refuse to serve, but which would still be visible to the owner
        // as a phantom item.
        await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);

        if (!alreadyRevoked)
        {
            var now = _time.GetUtcNow().UtcDateTime;
            membership.State = AlbumMembershipStates.Revoked;
            membership.RevokedAt = now;
            membership.UpdatedAt = now;
            await _db.SaveChangesAsync(cancellationToken);
        }

        var withdrawn = await WithdrawAllContributionsAsync(
            albumId, membership.MemberUserId, cancellationToken);

        await tx.CommitAsync(cancellationToken);

        return (AlbumMemberMutationResult.Ok,
            new RevokedMembership(membership.MemberUserId, withdrawn));
    }

    // ── Contribution ────────────────────────────────────────────────────────

    public async Task<AlbumContributionResult> ContributeAsync(
        Guid actorUserId, Guid albumId, Guid fileItemId,
        CancellationToken cancellationToken = default)
    {
        // (1) + (2) The actor's own live grant on this album, which also
        // establishes that the album exists and its owner is active. Reusing the
        // resolver keeps "may I act on this album" in one place.
        var grant = await _access.ResolveAsync(albumId, actorUserId, cancellationToken);
        if (grant is null)
        {
            return AlbumContributionResult.AlbumNotAccessible;
        }

        // The album OWNER does not contribute — they add through the ordinary
        // owner path, which is also the only path that may touch their own
        // media. Sending them here would create an owner-added row through a
        // collaborator route.
        if (grant.IsOwner)
        {
            return AlbumContributionResult.RoleNotPermitted;
        }

        if (!AlbumRoles.CanContribute(grant.Role))
        {
            return AlbumContributionResult.RoleNotPermitted;
        }

        // (3) + (4) + (5) The file must be the ACTOR's own, servable, and
        // displayable media. _db.FileItems carries the global Private-Vault
        // filter, so a vaulted file is invisible here and collapses into the
        // same single failure value as every other ineligibility.
        var eligible = await _db.FileItems
            .AsNoTracking()
            .Where(f => f.Id == fileItemId
                && f.OwnerUserId == actorUserId
                && f.DeletedAt == null
                && f.MediaLibraryState == MediaLibraryState.Active)
            .Join(_db.BlobMetadata.AsNoTracking(),
                f => f.BlobObjectId,
                m => m.BlobObjectId,
                (f, m) => m.MediaCategory)
            .FirstOrDefaultAsync(cancellationToken);
        if (eligible is not (MediaCategories.Image or MediaCategories.Video))
        {
            return AlbumContributionResult.FileNotContributable;
        }

        // (6) Not already in this album — by anyone.
        var already = await _db.AlbumItems
            .AnyAsync(ai => ai.AlbumId == albumId && ai.FileItemId == fileItemId, cancellationToken);
        if (already)
        {
            return AlbumContributionResult.AlreadyPresent;
        }

        var nextOrder = (await _db.AlbumItems
            .Where(ai => ai.AlbumId == albumId)
            .Select(ai => (int?)ai.SortOrder)
            .MaxAsync(cancellationToken) ?? 0) + 1;
        _db.AlbumItems.Add(new AlbumItem
        {
            Id = Guid.NewGuid(),
            AlbumId = albumId,
            FileItemId = fileItemId,
            AddedAt = _time.GetUtcNow().UtcDateTime,
            // The invariant the resolver verifies: whoever added it owns it.
            AddedByUserId = actorUserId,
            // A contribution appends to the album's curated order.
            SortOrder = nextOrder,
        });

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Two concurrent contributions of the same file: the primary key
            // decided, and the loser reports the same outcome as the sequential
            // case rather than a 500.
            return AlbumContributionResult.AlreadyPresent;
        }

        await BumpAlbumVersionAsync(albumId, cancellationToken);
        return AlbumContributionResult.Ok;
    }

    public async Task<AlbumItemRemovalResult> WithdrawContributionAsync(
        Guid actorUserId, Guid albumId, Guid fileItemId,
        CancellationToken cancellationToken = default)
    {
        // Withdrawal follows from OWNERSHIP + PROVENANCE, not from the role the
        // actor holds now: a contributor downgraded to Viewer, or one whose
        // membership was revoked, must still be able to take their media back.
        // There is deliberately no membership check here at all.
        var removed = await _db.AlbumItems
            .Where(ai => ai.AlbumId == albumId
                && ai.FileItemId == fileItemId
                && ai.AddedByUserId == actorUserId
                && _db.FileItems.IgnoreQueryFilters()
                    .Any(f => f.Id == ai.FileItemId && f.OwnerUserId == actorUserId))
            .ExecuteDeleteAsync(cancellationToken);

        // IgnoreQueryFilters above on purpose: a file the actor has since moved
        // into their Private Vault is unreachable through normal queries, and
        // refusing to let them withdraw it would strand the row in somebody
        // else's album permanently.
        if (removed == 0)
        {
            return AlbumItemRemovalResult.NotFound;
        }

        await ClearCoverIfPointingAtAsync(albumId, fileItemId, cancellationToken);
        await CompactSortOrderAsync(albumId, cancellationToken);
        await BumpAlbumVersionAsync(albumId, cancellationToken);
        return AlbumItemRemovalResult.Ok;
    }

    public async Task<(AlbumItemRemovalResult Result, RemovedAlbumItem? Removed)> RemoveItemAsOwnerAsync(
        Guid ownerUserId, Guid albumId, Guid fileItemId,
        CancellationToken cancellationToken = default)
    {
        var ownsAlbum = await _db.Albums
            .AsNoTracking()
            .AnyAsync(a => a.Id == albumId && a.OwnerUserId == ownerUserId, cancellationToken);
        if (!ownsAlbum)
        {
            return (AlbumItemRemovalResult.NotFound, null);
        }

        // Read provenance BEFORE deleting, so the audit can name the source
        // owner. IgnoreQueryFilters so a vaulted source still yields its
        // provenance — the owner must be able to clear such a row.
        var provenance = await _db.AlbumItems
            .AsNoTracking()
            .Where(ai => ai.AlbumId == albumId && ai.FileItemId == fileItemId)
            .Select(ai => new
            {
                ai.AddedByUserId,
                SourceOwnerUserId = _db.FileItems.IgnoreQueryFilters()
                    .Where(f => f.Id == ai.FileItemId)
                    .Select(f => (Guid?)f.OwnerUserId)
                    .FirstOrDefault(),
            })
            .FirstOrDefaultAsync(cancellationToken);
        if (provenance is null)
        {
            return (AlbumItemRemovalResult.NotFound, null);
        }

        // Album membership only. The source file is never handed to a deletion
        // service — an owner removing a collaborator's item must not be able to
        // destroy their media, and an owner removing their OWN item is a
        // curation action, not a delete.
        await _db.AlbumItems
            .Where(ai => ai.AlbumId == albumId && ai.FileItemId == fileItemId)
            .ExecuteDeleteAsync(cancellationToken);

        await ClearCoverIfPointingAtAsync(albumId, fileItemId, cancellationToken);
        await CompactSortOrderAsync(albumId, cancellationToken);
        await BumpAlbumVersionAsync(albumId, cancellationToken);

        return (AlbumItemRemovalResult.Ok, new RemovedAlbumItem(
            provenance.SourceOwnerUserId ?? provenance.AddedByUserId,
            provenance.AddedByUserId));
    }

    public async Task<AlbumContentResponse?> ListAlbumContentAsync(
        Guid actorUserId, Guid albumId,
        CancellationToken cancellationToken = default)
    {
        // SHARE-ALBUM-03: the moderation view is reachable by the OWNER and by
        // an EDITOR, through the same grant the mutations use — so what a
        // curator can see and what they can act on cannot drift apart.
        var grant = await _access.ResolveAsync(albumId, actorUserId, cancellationToken);
        if (grant is null || (!grant.IsOwner && !AlbumRoles.CanEdit(grant.Role)))
        {
            return null;
        }
        var album = await _db.Albums.AsNoTracking()
            .Where(a => a.Id == albumId)
            .Select(a => new { a.Version, a.CoverFileItemId, a.OwnerUserId })
            .FirstAsync(cancellationToken);
        var ownerUserId = album.OwnerUserId;

        // Every row of the album, including ones whose source is no longer
        // servable — this is the moderation view, so a row the owner needs to
        // clear must be visible rather than silently filtered out.
        // IgnoreQueryFilters reaches a source that has since been vaulted; it
        // reports it as unavailable and never yields a URL for it.
        var rows = await _db.AlbumItems
            .AsNoTracking()
            .Where(ai => ai.AlbumId == albumId)
            .Select(ai => new
            {
                AlbumItemId = ai.Id,
                ai.FileItemId,
                ai.AddedAt,
                ai.AddedByUserId,
                ai.SortOrder,
                Source = _db.FileItems.IgnoreQueryFilters()
                    .Where(f => f.Id == ai.FileItemId)
                    .Select(f => new
                    {
                        f.OwnerUserId,
                        f.DeletedAt,
                        f.MediaLibraryState,
                        f.PrivateVaultId,
                        MediaCategory = _db.BlobMetadata
                            .Where(m => m.BlobObjectId == f.BlobObjectId)
                            .Select(m => m.MediaCategory)
                            .FirstOrDefault(),
                    })
                    .FirstOrDefault(),
                ContributorDisplayName = _db.Users
                    .Where(u => u.Id == ai.AddedByUserId)
                    .Select(u => u.DisplayName)
                    .FirstOrDefault(),
                ContributorEmail = _db.Users
                    .Where(u => u.Id == ai.AddedByUserId)
                    .Select(u => u.Email)
                    .FirstOrDefault(),
                ContributorActive = _db.Users
                    .Any(u => u.Id == ai.AddedByUserId && u.DisabledAt == null),
                ContributorStillMember = _db.AlbumMemberships.Any(m =>
                    m.AlbumId == albumId
                    && m.MemberUserId == ai.AddedByUserId
                    && m.State == AlbumMembershipStates.Accepted
                    && m.RevokedAt == null),
            })
            // The album's CURATED order, not the order things happened to be
            // added in. FileItemId stays the final tie-break so the sequence is
            // stable even if two rows share a SortOrder.
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.FileItemId)
            .ToListAsync(cancellationToken);

        var items = rows.Select(x =>
        {
            var isOwnerItem = x.AddedByUserId == ownerUserId;
            var source = x.Source;
            var servable = source is not null
                && source.DeletedAt == null
                && source.MediaLibraryState == MediaLibraryState.Active
                && source.PrivateVaultId == null
                && source.MediaCategory is MediaCategories.Image or MediaCategories.Video
                // A contribution is only servable while its contributor is still
                // an active member — the same condition the resolver enforces.
                && (isOwnerItem || (x.ContributorActive && x.ContributorStillMember));
            var isVideo = source?.MediaCategory == MediaCategories.Video;

            return new AlbumContentItem(
                x.AlbumItemId,
                x.FileItemId,
                isVideo ? "video" : "image",
                // Owner-scoped URLs for the owner's own media; album-scoped for
                // a contribution, since the owner does not own those bytes and
                // /api/files/{id}/* would (correctly) refuse them.
                isOwnerItem
                    ? (isVideo
                        ? $"/api/files/{x.FileItemId}/poster"
                        : $"/api/files/{x.FileItemId}/thumbnail?size=small")
                    : (isVideo
                        ? SharedMediaUrls.Poster(albumId, x.FileItemId)
                        : SharedMediaUrls.Thumbnail(albumId, x.FileItemId)),
                isOwnerItem ? AlbumContentOrigins.Owner : AlbumContentOrigins.Contribution,
                isOwnerItem ? null : x.ContributorDisplayName,
                isOwnerItem ? null : RecipientEmailMask.Mask(x.ContributorEmail),
                servable ? AlbumContentSourceStates.Available : AlbumContentSourceStates.Unavailable,
                x.AddedAt,
                album.CoverFileItemId == x.FileItemId);
        }).ToList();

        return new AlbumContentResponse(
            album.Version,
            album.CoverFileItemId,
            grant.IsOwner || AlbumRoles.CanEdit(grant.Role),
            items);
    }

    // Removes every item a given user contributed to an album. Used by the
    // revocation path; the caller owns the surrounding transaction.
    private async Task<IReadOnlyList<Guid>> WithdrawAllContributionsAsync(
        Guid albumId, Guid memberUserId, CancellationToken cancellationToken)
    {
        var fileIds = await _db.AlbumItems
            .AsNoTracking()
            .Where(ai => ai.AlbumId == albumId && ai.AddedByUserId == memberUserId)
            .Select(ai => ai.FileItemId)
            .ToListAsync(cancellationToken);
        if (fileIds.Count == 0)
        {
            return Array.Empty<Guid>();
        }

        await _db.AlbumItems
            .Where(ai => ai.AlbumId == albumId && ai.AddedByUserId == memberUserId)
            .ExecuteDeleteAsync(cancellationToken);

        foreach (var fileId in fileIds)
        {
            await ClearCoverIfPointingAtAsync(albumId, fileId, cancellationToken);
        }
        await CompactSortOrderAsync(albumId, cancellationToken);
        await BumpAlbumVersionAsync(albumId, cancellationToken);

        return fileIds;
    }

    // ── Recipient side ──────────────────────────────────────────────────────

    public async Task<IReadOnlyList<SharedAlbumSummary>> ListSharedWithMeAsync(
        Guid actorUserId, CancellationToken cancellationToken = default)
    {
        var rows = await ActiveMembershipsOf(actorUserId)
            .Join(_db.Albums.AsNoTracking(),
                m => m.AlbumId,
                a => a.Id,
                (m, a) => new { m.Role, m.AllowOriginalDownload, m.AcceptedAt, Album = a })
            // A disabled owner's albums disappear from the list, matching
            // AlbumAccessResolver — the listing must not advertise something the
            // media routes would refuse.
            .Join(_db.Users.AsNoTracking().Where(u => u.DisabledAt == null),
                x => x.Album.OwnerUserId,
                u => u.Id,
                (x, u) => new
                {
                    x.Album.Id,
                    x.Album.Name,
                    x.Album.Description,
                    OwnerDisplayName = u.DisplayName,
                    x.Role,
                    x.AllowOriginalDownload,
                    x.AcceptedAt,
                })
            .OrderBy(x => x.Name)
            .ToListAsync(cancellationToken);

        if (rows.Count == 0)
        {
            return Array.Empty<SharedAlbumSummary>();
        }

        // ONE query for every listed album's displayable members (no N+1),
        // mirroring AlbumService.ListAsync. Item counts and cover tiles are
        // derived from the SAME visibility predicate the media routes enforce,
        // so a count can never promise an item the viewer cannot open.
        var albumIds = rows.Select(x => x.Id).ToList();
        var facts = await DisplayableMembers()
            .Where(x => albumIds.Contains(x.AlbumId))
            // The DERIVED cover follows the album's curated order, so choosing
            // an order also chooses what the card shows.
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.FileItemId)
            .Select(x => new { x.AlbumId, x.FileItemId, x.AlbumOwnerUserId, x.MediaCategory })
            .ToListAsync(cancellationToken);

        var byAlbum = facts.GroupBy(x => x.AlbumId).ToDictionary(g => g.Key, g => g.ToList());

        return rows.Select(x =>
        {
            var members = byAlbum.GetValueOrDefault(x.Id) ?? [];
            var cover = members
                .Take(4)
                .Select(m => new SharedAlbumCoverItem(
                    m.FileItemId,
                    m.MediaCategory == MediaCategories.Video ? "video" : "image",
                    m.MediaCategory == MediaCategories.Video
                        ? SharedMediaUrls.Poster(x.Id, m.FileItemId)
                        : SharedMediaUrls.Thumbnail(x.Id, m.FileItemId)))
                .ToList();
            return new SharedAlbumSummary(
                x.Id, x.Name, x.Description, x.OwnerDisplayName,
                x.Role, x.AllowOriginalDownload,
                members.Count,
                x.AcceptedAt ?? DateTime.MinValue,
                cover);
        }).ToList();
    }

    public async Task<IReadOnlyList<AlbumInvitationDto>> ListInvitationsAsync(
        Guid actorUserId, CancellationToken cancellationToken = default)
    {
        var rows = await _db.AlbumMemberships
            .AsNoTracking()
            .Where(m => m.MemberUserId == actorUserId
                && m.State == AlbumMembershipStates.Pending
                && m.RevokedAt == null)
            .Join(_db.Albums.AsNoTracking(),
                m => m.AlbumId,
                a => a.Id,
                (m, a) => new { Membership = m, Album = a })
            .Join(_db.Users.AsNoTracking().Where(u => u.DisabledAt == null),
                x => x.Album.OwnerUserId,
                u => u.Id,
                (x, u) => new
                {
                    MembershipId = x.Membership.Id,
                    AlbumId = x.Album.Id,
                    AlbumName = x.Album.Name,
                    AlbumDescription = x.Album.Description,
                    OwnerDisplayName = u.DisplayName,
                    x.Membership.Role,
                    x.Membership.AllowOriginalDownload,
                    x.Membership.InvitedAt,
                })
            .OrderByDescending(x => x.InvitedAt)
            .ToListAsync(cancellationToken);

        if (rows.Count == 0)
        {
            return Array.Empty<AlbumInvitationDto>();
        }

        // The item count an invitation advertises uses the same displayable
        // predicate as the album itself, so "12 items" means 12 openable items.
        var albumIds = rows.Select(x => x.AlbumId).ToList();
        var counts = (await DisplayableMembers()
                .Where(x => albumIds.Contains(x.AlbumId))
                .Select(x => x.AlbumId)
                .ToListAsync(cancellationToken))
            .GroupBy(id => id)
            .ToDictionary(g => g.Key, g => g.Count());

        return rows.Select(x => new AlbumInvitationDto(
            x.MembershipId,
            x.AlbumId,
            x.AlbumName,
            x.AlbumDescription,
            x.OwnerDisplayName,
            x.Role,
            x.AllowOriginalDownload,
            counts.GetValueOrDefault(x.AlbumId),
            x.InvitedAt)).ToList();
    }

    public async Task<AlbumInvitationResponseResult> RespondToInvitationAsync(
        Guid actorUserId, Guid membershipId, bool accept,
        CancellationToken cancellationToken = default)
    {
        // Only the invited user, only while pending, only while unrevoked. All
        // three are in the predicate, so a cancelled invitation cannot be
        // accepted by a client that still has the old id on screen.
        var membership = await _db.AlbumMemberships
            .FirstOrDefaultAsync(
                m => m.Id == membershipId
                    && m.MemberUserId == actorUserId
                    && m.State == AlbumMembershipStates.Pending
                    && m.RevokedAt == null,
                cancellationToken);
        if (membership is null)
        {
            return AlbumInvitationResponseResult.NotFound;
        }

        var now = _time.GetUtcNow().UtcDateTime;
        if (accept)
        {
            membership.State = AlbumMembershipStates.Accepted;
            membership.AcceptedAt = now;
        }
        else
        {
            membership.State = AlbumMembershipStates.Declined;
            membership.DeclinedAt = now;
        }
        membership.UpdatedAt = now;
        await _db.SaveChangesAsync(cancellationToken);

        return AlbumInvitationResponseResult.Ok;
    }

    // ── Shared read model ───────────────────────────────────────────────────

    public async Task<SharedAlbumDetail> GetSharedAlbumAsync(
        AlbumAccessGrant grant, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(grant);

        var album = await _db.Albums
            .AsNoTracking()
            .Where(a => a.Id == grant.AlbumId)
            .Select(a => new { a.Name, a.Description, a.Version })
            .FirstAsync(cancellationToken);

        var ownerDisplayName = await _db.Users
            .AsNoTracking()
            .Where(u => u.Id == grant.AlbumOwnerUserId)
            .Select(u => u.DisplayName)
            .FirstOrDefaultAsync(cancellationToken) ?? string.Empty;

        var count = await DisplayableMembers()
            .Where(x => x.AlbumId == grant.AlbumId)
            .CountAsync(cancellationToken);

        return new SharedAlbumDetail(
            grant.AlbumId, album.Name, album.Description, ownerDisplayName,
            grant.Role, grant.AllowOriginalDownload, count,
            album.Version,
            grant.IsOwner || AlbumRoles.CanEdit(grant.Role));
    }

    public async Task<IReadOnlyList<SharedAlbumItem>> ListSharedItemsAsync(
        AlbumAccessGrant grant, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(grant);

        var rows = await DisplayableMembers()
            .Where(x => x.AlbumId == grant.AlbumId)
            // The album's CURATED order. FileItemId remains the final tie-break
            // so the sequence is stable even if two rows share a SortOrder.
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.FileItemId)
            .Select(x => new
            {
                x.AlbumItemId,
                x.FileItemId,
                x.AddedAt,
                x.MediaCategory,
                x.DetectedContentType,
                x.Width,
                x.Height,
                x.Orientation,
                x.AddedByUserId,
                x.FileOwnerUserId,
            })
            .ToListAsync(cancellationToken);

        return rows.Select(x =>
        {
            var isVideo = x.MediaCategory == MediaCategories.Video;
            var (width, height) = ImageDisplayDimensions.Resolve(x.Width, x.Height, x.Orientation);
            return new SharedAlbumItem(
                x.FileItemId,
                isVideo ? "video" : "image",
                SharedMediaUrls.Thumbnail(grant.AlbumId, x.FileItemId),
                SharedMediaUrls.Preview(grant.AlbumId, x.FileItemId),
                isVideo ? SharedMediaUrls.Poster(grant.AlbumId, x.FileItemId) : null,
                // Playback is offered only for a video whose detected type the
                // server trusts, matching the owner-side /video gate.
                isVideo && SafeContentType.IsTrustedVideo(x.DetectedContentType)
                    ? SharedMediaUrls.Video(grant.AlbumId, x.FileItemId)
                    : null,
                // Advertised only when the grant permits originals. The endpoint
                // re-checks the same permission — this is a UI courtesy, not the
                // control.
                grant.AllowOriginalDownload
                    ? SharedMediaUrls.Content(grant.AlbumId, x.FileItemId)
                    : null,
                x.AlbumItemId,
                width,
                height,
                x.AddedAt,
                // Own contribution only: owns the file AND added it. The same
                // pair WithdrawContributionAsync checks server-side.
                x.AddedByUserId == grant.ActorUserId && x.FileOwnerUserId == grant.ActorUserId);
        }).ToList();
    }

    // SHARE-ALBUM-03: a chosen cover is a preference, not a relation — but a
    // preference pointing at a row that no longer exists is just stale data. It
    // is cleared in the SAME transaction as the removal that invalidated it, so
    // no permanently-dangling reference can accumulate and the DTO never has to
    // explain one.
    //
    // Deliberately NOT cleared for a source that became temporarily unservable
    // without leaving the album (vaulted, soft-deleted, excluded, contributor's
    // membership ended): those are reversible, the item is still a member, and
    // the dynamic fallback already handles them. Erasing the owner's choice for
    // a condition that may end tomorrow would be destroying information.
    private Task ClearCoverIfPointingAtAsync(
        Guid albumId, Guid fileItemId, CancellationToken cancellationToken) =>
        _db.Albums
            .Where(a => a.Id == albumId && a.CoverFileItemId == fileItemId)
            .ExecuteUpdateAsync(
                s => s.SetProperty(a => a.CoverFileItemId, (Guid?)null),
                cancellationToken);

    // Renumber the album to a contiguous 1..n in its current order after a
    // removal. Keeping the sequence dense means a reorder never has to reason
    // about holes, and two albums with the same visible order always have the
    // same stored order.
    private async Task CompactSortOrderAsync(Guid albumId, CancellationToken cancellationToken)
    {
        var ordered = await _db.AlbumItems
            .Where(ai => ai.AlbumId == albumId)
            .OrderBy(ai => ai.SortOrder)
            .ThenBy(ai => ai.FileItemId)
            .ToListAsync(cancellationToken);

        var position = 1;
        var changed = false;
        foreach (var item in ordered)
        {
            if (item.SortOrder != position)
            {
                item.SortOrder = position;
                changed = true;
            }
            position += 1;
        }
        if (changed)
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
    }

    // SHARE-ALBUM-03: content mutations move the album's optimistic-concurrency
    // token. Contribution, withdrawal and the automatic withdrawal a revocation
    // performs all change what the album LOOKS like, so all three bump it.
    // Invitations, role changes and allowDownload deliberately do NOT — they
    // change who may look, not what is there.
    private Task BumpAlbumVersionAsync(Guid albumId, CancellationToken cancellationToken) =>
        _db.Albums
            .Where(a => a.Id == albumId)
            .ExecuteUpdateAsync(
                s => s.SetProperty(a => a.Version, a => a.Version + 1)
                      .SetProperty(a => a.UpdatedAt, _ => _time.GetUtcNow().UtcDateTime),
                cancellationToken);

    // ── Internals ───────────────────────────────────────────────────────────

    // Accepted, unrevoked memberships of one user. The single definition of
    // "shared with me" used by every listing here.
    private IQueryable<AlbumMembership> ActiveMembershipsOf(Guid actorUserId) =>
        _db.AlbumMemberships
            .AsNoTracking()
            .Where(m => m.MemberUserId == actorUserId
                && m.State == AlbumMembershipStates.Accepted
                && m.RevokedAt == null);

    // Album members that are currently servable: the album owner's own media,
    // OR a coherent contribution whose source owner is still an active member.
    // Not soft-deleted, in the media library, detected image/video.
    //
    // This mirrors AlbumAccessResolver.ResolveMediaAsync clause for clause, so a
    // listing can never advertise an item the media routes would refuse — nor
    // hide one they would serve. The two predicates are the single most
    // important thing to keep in step in this slice.
    //
    // Private Vault needs no clause: the global query filter on FileItems
    // removes vaulted rows before this composes.
    private IQueryable<MemberRow> DisplayableMembers() =>
        _db.AlbumItems
            .AsNoTracking()
            .Join(_db.Albums.AsNoTracking(),
                ai => ai.AlbumId,
                a => a.Id,
                (ai, a) => new
                {
                    ai.AlbumId, ai.FileItemId, ai.AddedAt, ai.AddedByUserId, a.OwnerUserId,
                    AlbumItemId = ai.Id, ai.SortOrder,
                })
            .Join(_db.FileItems.AsNoTracking(),
                x => x.FileItemId,
                f => f.Id,
                (x, f) => new
                {
                    x.AlbumId,
                    x.FileItemId,
                    x.AddedAt,
                    x.AddedByUserId,
                    x.AlbumItemId,
                    x.SortOrder,
                    AlbumOwnerUserId = x.OwnerUserId,
                    FileOwnerUserId = f.OwnerUserId,
                    f.BlobObjectId,
                    f.DeletedAt,
                    f.MediaLibraryState,
                })
            .Where(x => x.DeletedAt == null
                && x.MediaLibraryState == MediaLibraryState.Active)
            .Where(x => x.FileOwnerUserId == x.AlbumOwnerUserId
                || (x.AddedByUserId == x.FileOwnerUserId
                    && _db.Users.Any(u => u.Id == x.FileOwnerUserId && u.DisabledAt == null)
                    && _db.AlbumMemberships.Any(m =>
                        m.AlbumId == x.AlbumId
                        && m.MemberUserId == x.FileOwnerUserId
                        && m.State == AlbumMembershipStates.Accepted
                        && m.RevokedAt == null)))
            .Join(_db.BlobMetadata.AsNoTracking(),
                x => x.BlobObjectId,
                m => m.BlobObjectId,
                (x, m) => new MemberRow
                {
                    AlbumId = x.AlbumId,
                    FileItemId = x.FileItemId,
                    AddedAt = x.AddedAt,
                    AlbumItemId = x.AlbumItemId,
                    SortOrder = x.SortOrder,
                    AlbumOwnerUserId = x.AlbumOwnerUserId,
                    AddedByUserId = x.AddedByUserId,
                    FileOwnerUserId = x.FileOwnerUserId,
                    MediaCategory = m.MediaCategory,
                    DetectedContentType = m.DetectedContentType,
                    Width = m.Width,
                    Height = m.Height,
                    Orientation = m.Orientation,
                })
            .Where(x => x.MediaCategory == MediaCategories.Image
                || x.MediaCategory == MediaCategories.Video);

    // Exact, case-insensitive email match against an ACTIVE account other than
    // the caller. Deliberately not a prefix/substring search: over a unique
    // account identifier that is a directory-enumeration primitive.
    private async Task<RecipientRow?> FindInvitableRecipientAsync(
        Guid ownerUserId, string? email, CancellationToken cancellationToken)
    {
        var normalized = NormalizeEmail(email);
        if (normalized is null)
        {
            return null;
        }

        return await _db.Users
            .AsNoTracking()
            .Where(u => u.Email.ToLower() == normalized
                && u.DisabledAt == null
                && u.Id != ownerUserId)
            .Select(u => new RecipientRow(u.Id, u.DisplayName, u.Email))
            .FirstOrDefaultAsync(cancellationToken);
    }

    private Task<AlbumMembership?> LoadOwnedMembershipAsync(
        Guid ownerUserId, Guid albumId, Guid membershipId, CancellationToken cancellationToken) =>
        _db.AlbumMemberships
            .Where(m => m.Id == membershipId && m.AlbumId == albumId)
            .Where(m => _db.Albums.Any(a => a.Id == albumId && a.OwnerUserId == ownerUserId))
            .FirstOrDefaultAsync(cancellationToken);

    private static AlbumMemberDto ToDto(AlbumMembership m, string displayName, string? email) =>
        new(m.Id, displayName, RecipientEmailMask.Mask(email),
            m.Role, m.State, m.AllowOriginalDownload,
            m.InvitedAt, m.AcceptedAt, m.DeclinedAt, m.RevokedAt);

    // Lower-cased, trimmed, and minimally shape-checked. Not an RFC validator:
    // the only thing that matters is that it either matches a stored address
    // exactly or matches nothing.
    private static string? NormalizeEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return null;
        }

        var trimmed = email.Trim();
        if (trimmed.Length > 320)
        {
            return null;
        }

        var at = trimmed.IndexOf('@');
        if (at <= 0 || at != trimmed.LastIndexOf('@') || at == trimmed.Length - 1)
        {
            return null;
        }

        return trimmed.ToLowerInvariant();
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException?.Message.Contains("23505") == true
        || ex.InnerException?.Message.Contains("UNIQUE") == true;

    private sealed record RecipientRow(Guid Id, string DisplayName, string Email);

    // A class (not a positional record) so EF Core can bind its members in the
    // Join result selector and still compose Where/OrderBy/Count over it —
    // the same reason PartyMediaService.MemberRow is one.
    private sealed class MemberRow
    {
        public Guid AlbumId { get; set; }
        public Guid FileItemId { get; set; }
        public Guid AlbumItemId { get; set; }
        public int SortOrder { get; set; }
        public DateTime AddedAt { get; set; }
        public Guid AlbumOwnerUserId { get; set; }
        // SHARE-ALBUM-02 provenance, carried so ListSharedItemsAsync can tell
        // the caller which items are their OWN contribution.
        public Guid AddedByUserId { get; set; }
        public Guid FileOwnerUserId { get; set; }
        public string MediaCategory { get; set; } = string.Empty;
        public string? DetectedContentType { get; set; }
        public int? Width { get; set; }
        public int? Height { get; set; }
        public int? Orientation { get; set; }
    }
}
