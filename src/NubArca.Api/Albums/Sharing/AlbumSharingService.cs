using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Albums;
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
                    m.CanManagePartyMessages,
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
            x.InvitedAt, x.AcceptedAt, x.DeclinedAt, x.RevokedAt,
            x.CanManagePartyMessages)).ToList();
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
            // A REVOKED DELEGATION MUST NOT RISE AGAIN. Reusing the row is what
            // keeps the unique index plain, but it also means every per-member
            // grant on it outlives the membership that carried it unless it is
            // reset here. `AllowOriginalDownload` is reset because the request
            // carries it; `CanManagePartyMessages` has no field on an invite at
            // all, so without this line an owner who revoked somebody's party
            // moderation and later re-invited them would silently hand it back.
            // A new invitation lifecycle always requires a new explicit decision.
            existing.CanManagePartyMessages = false;
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
        bool? canManagePartyMessages = null,
        CancellationToken cancellationToken = default)
    {
        var membership = await LoadOwnedMembershipAsync(ownerUserId, albumId, membershipId, cancellationToken);
        if (membership is null)
        {
            return (AlbumMemberMutationResult.NotFound, null);
        }

        membership.AllowOriginalDownload = allowOriginalDownload;
        // Absent means "leave it alone". Only the OWNER reaches this method
        // (LoadOwnedMembershipAsync scopes by album ownership), which is what
        // makes "only the owner may grant or revoke the delegation" true without
        // a second check: a delegate has no route here at all.
        if (canManagePartyMessages.HasValue)
        {
            membership.CanManagePartyMessages = canManagePartyMessages.Value;
        }
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
            // Clear the party-message delegation at the moment it stops being
            // exercisable, rather than leaving a true flag on a dead row for a
            // later reinvite to find. This is defence in depth, NOT the
            // enforcement: PartyMessageAccessResolver requires an accepted,
            // unrevoked membership AND the flag, so revocation already ends the
            // delegation on the very next request whatever this column says.
            membership.CanManagePartyMessages = false;
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

    // May this actor add media to this album at all? ONE definition, shared by
    // the single-item and bulk paths, so the two can never drift on who is
    // allowed to contribute.
    //
    // (1) + (2) The actor's own live grant on this album, which also establishes
    // that the album exists and its owner is active. Reusing the resolver keeps
    // "may I act on this album" in one place.
    private async Task<AlbumContributionResult> ResolveContributionAuthorityAsync(
        Guid actorUserId, Guid albumId, CancellationToken cancellationToken)
    {
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

        return AlbumRoles.CanContribute(grant.Role)
            ? AlbumContributionResult.Ok
            : AlbumContributionResult.RoleNotPermitted;
    }

    // (3) + (4) + (5) The ONE definition of "contributable media": the ACTOR's
    // own, servable, displayable file. Returns the subset of the requested ids
    // that qualifies — everything else simply does not come back, which is what
    // makes "missing", "foreign", "deleted", "excluded", "vaulted" and "not
    // media" indistinguishable to the caller. _db.FileItems carries the global
    // Private-Vault filter, so a vaulted file is invisible here for free.
    private async Task<HashSet<Guid>> ContributableFileIdsAsync(
        Guid actorUserId, List<Guid> fileItemIds, CancellationToken cancellationToken)
    {
        var ids = await _db.FileItems
            .AsNoTracking()
            .Where(f => fileItemIds.Contains(f.Id)
                && f.OwnerUserId == actorUserId
                && f.DeletedAt == null
                && f.MediaLibraryState == MediaLibraryState.Active)
            .Join(_db.BlobMetadata.AsNoTracking(),
                f => f.BlobObjectId,
                m => m.BlobObjectId,
                (f, m) => new { f.Id, m.MediaCategory })
            .Where(x => x.MediaCategory == MediaCategories.Image
                || x.MediaCategory == MediaCategories.Video)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);
        return [.. ids];
    }

    public async Task<AlbumContributionResult> ContributeAsync(
        Guid actorUserId, Guid albumId, Guid fileItemId,
        CancellationToken cancellationToken = default)
    {
        var authority = await ResolveContributionAuthorityAsync(actorUserId, albumId, cancellationToken);
        if (authority != AlbumContributionResult.Ok)
        {
            return authority;
        }

        var contributable = await ContributableFileIdsAsync(
            actorUserId, [fileItemId], cancellationToken);
        if (!contributable.Contains(fileItemId))
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
            // A contribution appends to the album's append order (SortOrder).
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

    public async Task<(AlbumBulkContributionResult Result, BulkContributionOutcome? Outcome)> ContributeManyAsync(
        Guid actorUserId, Guid albumId, IReadOnlyList<Guid> fileItemIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileItemIds);

        // The role gate is answered ONCE for the whole batch, before any file is
        // looked at: a Viewer must be refused outright rather than told that
        // every one of their items was "skipped".
        var authority = await ResolveContributionAuthorityAsync(actorUserId, albumId, cancellationToken);
        if (authority == AlbumContributionResult.AlbumNotAccessible)
        {
            return (AlbumBulkContributionResult.AlbumNotAccessible, null);
        }
        if (authority != AlbumContributionResult.Ok)
        {
            return (AlbumBulkContributionResult.RoleNotPermitted, null);
        }

        // `Requested` is the RAW count, exactly like the owner's bulk add: a
        // caller sending the same id twice sees the duplicate as skipped rather
        // than as a second success.
        var requested = fileItemIds.Count;
        var distinct = fileItemIds.Distinct().ToList();
        if (distinct.Count == 0)
        {
            return (AlbumBulkContributionResult.Ok,
                new BulkContributionOutcome(new BulkAlbumItemsResult(requested, 0, requested), []));
        }

        var contributable = await ContributableFileIdsAsync(actorUserId, distinct, cancellationToken);

        // Already in this album — by anyone. Skipped, never an error: one item a
        // second device added between the picker opening and the request landing
        // must not discard the rest of the batch.
        var already = await _db.AlbumItems
            .Where(ai => ai.AlbumId == albumId && distinct.Contains(ai.FileItemId))
            .Select(ai => ai.FileItemId)
            .ToListAsync(cancellationToken);
        var alreadySet = already.ToHashSet();

        // Request order preserved, so the append order (SortOrder) follows the
        // order the contributor selected in; the album itself is shown by date.
        var toAdd = distinct
            .Where(id => contributable.Contains(id) && !alreadySet.Contains(id))
            .ToList();

        if (toAdd.Count > 0)
        {
            var now = _time.GetUtcNow().UtcDateTime;
            var next = (await _db.AlbumItems
                .Where(ai => ai.AlbumId == albumId)
                .Select(ai => (int?)ai.SortOrder)
                .MaxAsync(cancellationToken) ?? 0) + 1;
            foreach (var id in toAdd)
            {
                _db.AlbumItems.Add(new AlbumItem
                {
                    Id = Guid.NewGuid(),
                    AlbumId = albumId,
                    FileItemId = id,
                    AddedAt = now,
                    // The invariant the resolver verifies: whoever added it owns it.
                    AddedByUserId = actorUserId,
                    SortOrder = next++,
                });
            }

            await _db.SaveChangesAsync(cancellationToken);
            await BumpAlbumVersionAsync(albumId, cancellationToken);
        }

        return (AlbumBulkContributionResult.Ok,
            new BulkContributionOutcome(
                new BulkAlbumItemsResult(requested, toAdd.Count, requested - toAdd.Count),
                toAdd));
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
        await BumpAlbumVersionAsync(albumId, cancellationToken);

        return (AlbumItemRemovalResult.Ok, new RemovedAlbumItem(
            provenance.SourceOwnerUserId ?? provenance.AddedByUserId,
            provenance.AddedByUserId));
    }

    public async Task<AlbumContentReadResult> ListAlbumContentAsync(
        Guid actorUserId, Guid albumId, AlbumContentQuery? query,
        CancellationToken cancellationToken = default)
    {
        // SHARE-ALBUM-03: the moderation view is reachable by the OWNER and by
        // an EDITOR, through the same grant the mutations use — so what a
        // curator can see and what they can act on cannot drift apart.
        var grant = await _access.ResolveAsync(albumId, actorUserId, cancellationToken);
        if (grant is null || (!grant.IsOwner && !AlbumRoles.CanEdit(grant.Role)))
        {
            return new AlbumContentReadResult(AlbumContentReadOutcome.NotFound);
        }

        // The version, the total and the page come from ONE snapshot, so a page
        // can never describe a state other than the version it is stamped with.
        // REPEATABLE READ is a snapshot on PostgreSQL; SQLite promotes it to
        // serializable. Read-only, so disposing it is its whole lifecycle.
        await using var snapshot = await _db.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.RepeatableRead, cancellationToken);

        var album = await _db.Albums.AsNoTracking()
            .Where(a => a.Id == albumId)
            .Select(a => new { a.Version, a.CoverFileItemId, a.OwnerUserId, a.SortOldestFirst })
            .FirstAsync(cancellationToken);
        if (query?.ExpectedVersion is int expected && expected != album.Version)
        {
            return new AlbumContentReadResult(
                AlbumContentReadOutcome.VersionConflict, CurrentVersion: album.Version);
        }
        var ownerUserId = album.OwnerUserId;

        var members = _db.AlbumItems.AsNoTracking().Where(ai => ai.AlbumId == albumId);
        var totalCount = await members.CountAsync(cancellationToken);

        // The album's sequence — date taken in the owner's direction,
        // FileItemId the tie-break — read from the source even when it has
        // since been vaulted, so every row of the moderation view has its place.
        var page = members.Join(
            _db.FileItems.IgnoreQueryFilters().AsNoTracking(),
            ai => ai.FileItemId,
            f => f.Id,
            (ai, f) => new { Item = ai, TakenAt = f.EffectiveDateTaken });

        // A continuation resumes after the caller's last row. `(date taken,
        // FileItemId)` is exactly the order below, so the page starts where the
        // previous one ended: nothing skipped, nothing repeated.
        if (query?.After is Guid after)
        {
            var anchor = await page
                .Where(x => x.Item.Id == after)
                .Select(x => new { x.TakenAt, x.Item.FileItemId })
                .FirstOrDefaultAsync(cancellationToken);
            if (anchor is null)
            {
                return new AlbumContentReadResult(AlbumContentReadOutcome.InvalidCursor);
            }
            var anchorAt = anchor.TakenAt;
            var anchorFileId = anchor.FileItemId;
            page = album.SortOldestFirst
                ? page.Where(x => x.TakenAt > anchorAt
                    || (x.TakenAt == anchorAt && x.Item.FileItemId.CompareTo(anchorFileId) > 0))
                : page.Where(x => x.TakenAt < anchorAt
                    || (x.TakenAt == anchorAt && x.Item.FileItemId.CompareTo(anchorFileId) < 0));
        }

        var ordered = album.SortOldestFirst
            ? page.OrderBy(x => x.TakenAt).ThenBy(x => x.Item.FileItemId)
            : page.OrderByDescending(x => x.TakenAt).ThenByDescending(x => x.Item.FileItemId);
        // One more than asked for: whether a further page exists is a fact
        // about the data, never a guess from a full page.
        IQueryable<AlbumItem> bounded = (query is null ? ordered : ordered.Take(query.Limit + 1))
            .Select(x => x.Item);

        // Every row of the album, including ones whose source is no longer
        // servable — this is the moderation view, so a row the owner needs to
        // clear must be visible rather than silently filtered out.
        // IgnoreQueryFilters reaches a source that has since been vaulted; it
        // reports it as unavailable and never yields a URL for it.
        var rows = await bounded
            .Select(ai => new
            {
                AlbumItemId = ai.Id,
                ai.FileItemId,
                ai.AddedAt,
                ai.AddedByUserId,
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
            .ToListAsync(cancellationToken);

        var hasMore = query is not null && rows.Count > query.Limit;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

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
                // Every row is drawn as a ~56 px icon, so every item — photo or
                // video — is addressed at the Micro size; the route answers a
                // video with an icon of its poster. Owner-scoped URLs for the
                // owner's own media; album-scoped for a contribution, since the
                // owner does not own those bytes and /api/files/{id}/* would
                // (correctly) refuse them.
                isOwnerItem
                    ? $"/api/files/{x.FileItemId}/thumbnail?size={NubArca.Api.Files.ThumbnailSizes.Micro}"
                    : SharedMediaUrls.MicroThumbnail(albumId, x.FileItemId),
                isOwnerItem ? AlbumContentOrigins.Owner : AlbumContentOrigins.Contribution,
                isOwnerItem ? null : x.ContributorDisplayName,
                isOwnerItem ? null : RecipientEmailMask.Mask(x.ContributorEmail),
                servable ? AlbumContentSourceStates.Available : AlbumContentSourceStates.Unavailable,
                x.AddedAt,
                album.CoverFileItemId == x.FileItemId);
        }).ToList();

        return new AlbumContentReadResult(AlbumContentReadOutcome.Ok, new AlbumContentResponse(
            album.Version,
            album.CoverFileItemId,
            grant.IsOwner || AlbumRoles.CanEdit(grant.Role),
            items,
            totalCount,
            hasMore ? rows[^1].AlbumItemId.ToString() : null));
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
                    x.Album.CoverFileItemId,
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
            .Select(x => new
            {
                x.AlbumId, x.FileItemId, x.AlbumOwnerUserId, x.MediaCategory, x.TakenAt, x.SortOldestFirst,
            })
            .ToListAsync(cancellationToken);

        // The DERIVED cover follows the album's sequence (date taken in the
        // owner's direction), so turning the album around also turns the card.
        var byAlbum = facts.GroupBy(x => x.AlbumId).ToDictionary(
            g => g.Key,
            g => (g.First().SortOldestFirst
                ? g.OrderBy(x => x.TakenAt).ThenBy(x => x.FileItemId)
                : g.OrderByDescending(x => x.TakenAt).ThenByDescending(x => x.FileItemId)).ToList());

        return rows.Select(x =>
        {
            var members = byAlbum.GetValueOrDefault(x.Id) ?? [];
            // Led by the chosen cover while it is still shown.
            var cover = members
                .OrderBy(m => m.FileItemId == x.CoverFileItemId ? 0 : 1)
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
            .Select(a => new { a.Name, a.Description, a.Version, a.CoverFileItemId, a.SortOldestFirst })
            .FirstAsync(cancellationToken);

        var ownerDisplayName = await _db.Users
            .AsNoTracking()
            .Where(u => u.Id == grant.AlbumOwnerUserId)
            .Select(u => u.DisplayName)
            .FirstOrDefaultAsync(cancellationToken) ?? string.Empty;

        var members = DisplayableMembers().Where(x => x.AlbumId == grant.AlbumId);
        var count = await members.CountAsync(cancellationToken);
        // The chosen cover, only while this member is shown it: an id the album
        // no longer serves is never handed out.
        var cover = album.CoverFileItemId is Guid chosen
            && await members.AnyAsync(x => x.FileItemId == chosen, cancellationToken)
                ? chosen
                : (Guid?)null;

        return new SharedAlbumDetail(
            grant.AlbumId, album.Name, album.Description, ownerDisplayName,
            grant.Role, grant.AllowOriginalDownload, count,
            album.Version,
            grant.IsOwner || AlbumRoles.CanEdit(grant.Role),
            cover,
            album.SortOldestFirst ? AlbumSortDirections.OldestFirst : AlbumSortDirections.NewestFirst);
    }

    // One page of a shared album, in its date order.
    //
    // Three things are deliberate. The counts describe the whole album, not the
    // filtered slice, so a tab's label does not change meaning with the tab that
    // is open. The page is a KEYSET over `(date taken, FileItemId)` in the
    // owner's direction — the very order the album is served in — so an item added or removed while the
    // recipient is scrolling shifts nothing already read. And the kind filter is
    // applied to the SAME query the counts come from, so "Videos" can never
    // include something "All" would not.
    //
    // Returns null for a cursor that does not parse, was issued for another kind
    // or direction, or names an item that is not in this album; the endpoint
    // turns that into one 400.
    public async Task<SharedAlbumItemsPage?> ListSharedItemsAsync(
        AlbumAccessGrant grant,
        SharedAlbumItemQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentNullException.ThrowIfNull(query);

        SharedAlbumItemCursor? cursor = null;
        if (!string.IsNullOrWhiteSpace(query.Cursor))
        {
            if (!SharedAlbumItemCursor.TryParse(query.Cursor, out var parsed)
                || !parsed.MatchesKind(query.Kind))
            {
                return null;
            }

            cursor = parsed;
        }

        var members = DisplayableMembers().Where(x => x.AlbumId == grant.AlbumId);
        var oldestFirst = await _db.Albums.AsNoTracking()
            .Where(a => a.Id == grant.AlbumId)
            .Select(a => a.SortOldestFirst)
            .FirstOrDefaultAsync(cancellationToken);
        // A cursor issued before the owner turned the album around belongs to a
        // sequence that no longer exists: the client starts again (one 400).
        if (cursor is not null && cursor.OldestFirst != oldestFirst)
        {
            return null;
        }

        // Counts first, over the unfiltered, unseeked set: one round trip that
        // answers every tab.
        var byCategory = await members
            .GroupBy(x => x.MediaCategory)
            .Select(g => new { Category = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);
        var photoCount = byCategory
            .Where(x => x.Category == MediaCategories.Image).Sum(x => x.Count);
        var videoCount = byCategory
            .Where(x => x.Category == MediaCategories.Video).Sum(x => x.Count);

        var page = query.Kind switch
        {
            SharedAlbumItemKinds.Image => members.Where(x => x.MediaCategory == MediaCategories.Image),
            SharedAlbumItemKinds.Video => members.Where(x => x.MediaCategory == MediaCategories.Video),
            _ => members,
        };

        if (cursor is not null)
        {
            // The boundary's date is read here, never taken from the cursor. It
            // must be an item of THIS album (even one no longer shown), so a
            // cursor cannot be pointed at an arbitrary file to probe its date.
            var boundaryId = cursor.FileItemId;
            var boundary = await _db.AlbumItems.AsNoTracking()
                .Where(ai => ai.AlbumId == grant.AlbumId && ai.FileItemId == boundaryId)
                .Join(_db.FileItems.IgnoreQueryFilters().AsNoTracking(),
                    ai => ai.FileItemId, f => f.Id, (ai, f) => new { f.EffectiveDateTaken })
                .FirstOrDefaultAsync(cancellationToken);
            if (boundary is null)
            {
                return null;
            }
            var boundaryAt = boundary.EffectiveDateTaken;
            page = oldestFirst
                ? page.Where(x => x.TakenAt > boundaryAt
                    || (x.TakenAt == boundaryAt && x.FileItemId.CompareTo(boundaryId) > 0))
                : page.Where(x => x.TakenAt < boundaryAt
                    || (x.TakenAt == boundaryAt && x.FileItemId.CompareTo(boundaryId) < 0));
        }

        // The album's sequence: date taken in the owner's direction, FileItemId
        // the stable tie-break — the same order the owner, the TV and the party
        // wall see.
        var ordered = oldestFirst
            ? page.OrderBy(x => x.TakenAt).ThenBy(x => x.FileItemId)
            : page.OrderByDescending(x => x.TakenAt).ThenByDescending(x => x.FileItemId);
        var rows = await ordered
            // One more than asked for: whether a further page exists is a fact
            // about the data, never a guess from a full page.
            .Take(query.Limit + 1)
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

        var hasMore = rows.Count > query.Limit;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        var items = rows.Select(x =>
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

        var last = rows.Count > 0 ? rows[^1] : null;
        var nextCursor = hasMore && last is not null
            ? new SharedAlbumItemCursor(query.Kind, last.FileItemId, oldestFirst).Encode()
            : null;

        return new SharedAlbumItemsPage(
            items, nextCursor, photoCount + videoCount, photoCount, videoCount);
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

    // No renumbering after a removal. The album's visible sequence is
    // (EffectiveDateTaken, FileItemId) in the owner's direction, so SortOrder is
    // only the append order and a hole in it changes nothing anyone sees. The
    // old compaction loaded and rewrote every row of the album per removal.

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
                    AlbumItemId = ai.Id, ai.SortOrder, a.SortOldestFirst,
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
                    x.SortOldestFirst,
                    TakenAt = f.EffectiveDateTaken,
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
                    SortOldestFirst = x.SortOldestFirst,
                    TakenAt = x.TakenAt,
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
            m.InvitedAt, m.AcceptedAt, m.DeclinedAt, m.RevokedAt,
            m.CanManagePartyMessages);

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
        // The album's sequence: date taken (never null; the upload time when a
        // file carries no date), in the direction the owner chose.
        public DateTime TakenAt { get; set; }
        public bool SortOldestFirst { get; set; }
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
