using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Domain;

namespace NubArca.Api.Albums;

public class AlbumService : IAlbumService
{
    private readonly AppDbContext _db;
    private readonly TimeProvider _time;
    // SEARCH-SEM-01: album membership decides what the "Solo da organizzare"
    // filter shows, so changing it must drop this owner's cached semantic
    // rankings. Optional: legacy direct-construction test sites pass null and
    // simply get the previous (uncached-invalidating) behaviour.
    private readonly Media.Semantic.SemanticRankingCache? _semanticRankings;

    public AlbumService(
        AppDbContext db,
        TimeProvider time,
        Media.Semantic.SemanticRankingCache? semanticRankings = null)
    {
        _semanticRankings = semanticRankings;
        _db = db;
        _time = time;
    }

    public async Task<AlbumDetail> CreateAsync(
        Guid ownerUserId, string name, string? description,
        CancellationToken cancellationToken = default)
    {
        name = name.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 255)
            throw new ArgumentException("Album name must be between 1 and 255 characters.", nameof(name));
        if (description is not null && description.Length > 1000)
            throw new ArgumentException("Description must be 1000 characters or fewer.", nameof(description));

        var now = _time.GetUtcNow().UtcDateTime;
        var album = new Album
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerUserId,
            Name = name,
            Description = description,
            CreatedAt = now,
            UpdatedAt = now
        };

        _db.Albums.Add(album);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("23505") == true
            || ex.InnerException?.Message.Contains("UNIQUE") == true)
        {
            throw new DuplicateAlbumNameException(name);
        }

        return new AlbumDetail(album.Id, album.Name, album.Description, album.ShowOnTv, album.CreatedAt, album.UpdatedAt);
    }

    public async Task<IReadOnlyList<AlbumSummary>> ListAsync(
        Guid ownerUserId,
        CancellationToken cancellationToken = default)
    {
        // Album rows + the raw membership count (backward compatible).
        var albums = await _db.Albums
            .Where(a => a.OwnerUserId == ownerUserId)
            .OrderBy(a => a.Name)
            .Select(a => new
            {
                a.Id,
                a.Name,
                a.Description,
                ItemCount = _db.AlbumItems.Count(ai => ai.AlbumId == a.Id),
                a.ShowOnTv,
                a.CreatedAt,
                a.UpdatedAt,
            })
            .ToListAsync(cancellationToken);

        if (albums.Count == 0)
        {
            return Array.Empty<AlbumSummary>();
        }

        // ONE query for every album's member facts (no N+1). Per-kind counts +
        // the cover mosaic are computed in memory from this — consistent with
        // ListItemsAsync, which already materialises full album membership.
        // Private Vault content is excluded by the global FileItems query filter.
        var albumIds = albums.Select(a => a.Id).ToList();
        var facts = await (
            from ai in _db.AlbumItems
            where albumIds.Contains(ai.AlbumId)
            join f in _db.FileItems on ai.FileItemId equals f.Id
            where f.OwnerUserId == ownerUserId && f.DeletedAt == null
            orderby ai.AddedAt
            select new
            {
                ai.AlbumId,
                ai.FileItemId,
                f.MediaLibraryState,
                DetectedType = _db.BlobMetadata
                    .Where(m => m.BlobObjectId == f.BlobObjectId)
                    .Select(m => m.DetectedContentType)
                    .FirstOrDefault(),
                f.MimeType,
            })
            .ToListAsync(cancellationToken);

        static bool IsVideo(string? detected, string mime) =>
            detected != null ? detected.StartsWith("video/", StringComparison.Ordinal)
                : mime.StartsWith("video/", StringComparison.Ordinal);

        var byAlbum = facts.GroupBy(x => x.AlbumId).ToDictionary(g => g.Key, g => g.ToList());

        return albums.Select(a =>
        {
            var members = byAlbum.GetValueOrDefault(a.Id) ?? [];
            var active = members.Where(m => m.MediaLibraryState == MediaLibraryState.Active).ToList();
            var photoCount = active.Count(m => !IsVideo(m.DetectedType, m.MimeType));
            var videoCount = active.Count(m => IsVideo(m.DetectedType, m.MimeType));
            var excludedCount = members.Count(m => m.MediaLibraryState == MediaLibraryState.Excluded);
            var cover = active
                .Take(4)
                .Select(m =>
                {
                    var video = IsVideo(m.DetectedType, m.MimeType);
                    return new AlbumCoverItem(
                        m.FileItemId,
                        video ? "video" : "image",
                        video
                            ? $"/api/files/{m.FileItemId}/poster"
                            : $"/api/files/{m.FileItemId}/thumbnail?size=small");
                })
                .ToList();
            return new AlbumSummary(
                a.Id, a.Name, a.Description, a.ItemCount, a.ShowOnTv, a.CreatedAt, a.UpdatedAt,
                photoCount, videoCount, excludedCount, cover);
        }).ToList();
    }

    public async Task<AlbumDetail?> GetByIdAsync(
        Guid albumId, Guid ownerUserId,
        CancellationToken cancellationToken = default)
    {
        return await _db.Albums
            .Where(a => a.Id == albumId && a.OwnerUserId == ownerUserId)
            .Select(a => new AlbumDetail(a.Id, a.Name, a.Description, a.ShowOnTv, a.CreatedAt, a.UpdatedAt))
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<AlbumDetail?> UpdateAsync(
        Guid albumId, Guid ownerUserId, string name, string? description,
        CancellationToken cancellationToken = default)
    {
        name = name.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 255)
            throw new ArgumentException("Album name must be between 1 and 255 characters.", nameof(name));
        if (description is not null && description.Length > 1000)
            throw new ArgumentException("Description must be 1000 characters or fewer.", nameof(description));

        var album = await _db.Albums
            .Where(a => a.Id == albumId && a.OwnerUserId == ownerUserId)
            .FirstOrDefaultAsync(cancellationToken);
        if (album is null)
            return null;

        album.Name = name;
        album.Description = description;
        album.UpdatedAt = _time.GetUtcNow().UtcDateTime;
        // The LEGACY owner-only route does not require expectedVersion — that
        // would break every existing caller — but it must still move the token,
        // or a collaborator's stale version would silently look current.
        album.Version += 1;

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("23505") == true
            || ex.InnerException?.Message.Contains("UNIQUE") == true)
        {
            throw new DuplicateAlbumNameException(name);
        }

        return new AlbumDetail(album.Id, album.Name, album.Description, album.ShowOnTv, album.CreatedAt, album.UpdatedAt);
    }

    public async Task<AlbumDetail?> SetTvVisibilityAsync(
        Guid albumId, Guid ownerUserId, bool showOnTv,
        CancellationToken cancellationToken = default)
    {
        var album = await _db.Albums
            .Where(a => a.Id == albumId && a.OwnerUserId == ownerUserId)
            .FirstOrDefaultAsync(cancellationToken);
        if (album is null)
            return null;

        var now = _time.GetUtcNow().UtcDateTime;
        album.ShowOnTv = showOnTv;
        album.UpdatedAt = now;

        // Party is deliberately NOT touched. Show-on-TV is the owner's decision
        // about their own television; a party is an event they are hosting, and
        // one is held with no screen in the room all the time. Turning this off
        // used to revoke every live party QR on the album — which is why party
        // mode had to turn it back on, and why the two switches could never be
        // set independently. Party access is revoked by revoking the party.
        await _db.SaveChangesAsync(cancellationToken);

        return new AlbumDetail(album.Id, album.Name, album.Description, album.ShowOnTv, album.CreatedAt, album.UpdatedAt);
    }

    // The next append position for a new item. Album membership is small, so a
    // MAX is cheaper than maintaining a counter and cannot drift from reality.
    private async Task<int> NextSortOrderAsync(Guid albumId, CancellationToken cancellationToken)
    {
        var max = await _db.AlbumItems
            .Where(ai => ai.AlbumId == albumId)
            .Select(ai => (int?)ai.SortOrder)
            .MaxAsync(cancellationToken);
        return (max ?? 0) + 1;
    }

    // SHARE-ALBUM-03: every change to what the album LOOKS like moves the
    // content version. Membership changes (invite, role, allowDownload) do not
    // — they change who may look, not what is there — and are documented as
    // outside the content version on IAlbumEditingService.
    private Task BumpVersionAsync(Guid albumId, CancellationToken cancellationToken) =>
        _db.Albums
            .Where(a => a.Id == albumId)
            .ExecuteUpdateAsync(
                s => s.SetProperty(a => a.Version, a => a.Version + 1)
                      .SetProperty(a => a.UpdatedAt, _ => _time.GetUtcNow().UtcDateTime),
                cancellationToken);

    // Called whenever this owner's album membership changes, so a filtered
    // semantic view cannot keep showing media that has just been filed (or keep
    // hiding media that has just been unfiled).
    private void InvalidateSemanticRankings(Guid ownerUserId)
        => _semanticRankings?.InvalidateOwner(ownerUserId);

    public async Task<bool> DeleteAsync(
        Guid albumId, Guid ownerUserId,
        CancellationToken cancellationToken = default)
    {
        var album = await _db.Albums
            .Where(a => a.Id == albumId && a.OwnerUserId == ownerUserId)
            .FirstOrDefaultAsync(cancellationToken);
        if (album is null)
            return false;

        // ONE UNIT OF WORK. Every statement below is an ExecuteDelete or an
        // ExecuteUpdate, and each one commits on its own unless a transaction
        // says otherwise — so a failure partway through used to leave everything
        // before it committed. That is bad for rows and worse for the
        // television: an album delete that failed after the assignment reset
        // would leave a screen in somebody's room silently showing something
        // else, with the party it was pointed at still there.
        //
        // It PARTICIPATES in a caller's transaction when there is one, rather
        // than demanding its own — the same shape the party vote and restart
        // paths use, so an album delete can be one step of a larger unit of work.
        var owned = _db.Database.CurrentTransaction is null;
        var transaction = owned
            ? await _db.Database.BeginTransactionAsync(cancellationToken)
            : null;
        try
        {
            await RemoveAlbumAndItsPartyStateAsync(album, albumId, cancellationToken);
            if (owned) await transaction!.CommitAsync(cancellationToken);
        }
        catch
        {
            // Nothing partially deleted, and no television left pointing
            // somewhere it was never moved to.
            if (owned) await transaction!.RollbackAsync(cancellationToken);
            throw;
        }
        finally
        {
            if (transaction is not null) await transaction.DisposeAsync();
        }

        // Only once the delete is durable: a cache invalidation cannot be rolled
        // back, so it must not happen for a delete that did not commit.
        InvalidateSemanticRankings(ownerUserId);
        return true;
    }

    /// <summary>
    /// Everything deleting an album destroys, in the order its foreign keys
    /// demand. Called inside the caller's transaction, never on its own.
    ///
    /// <para>The order is not stylistic. Every table here reaches the album
    /// through a RESTRICTING key, so each one has to be gone before the thing it
    /// names — which is why the sequence reads bottom-up: the rows that point at
    /// the most things go first, and the album goes last.</para>
    /// </summary>
    private async Task RemoveAlbumAndItsPartyStateAsync(
        Album album, Guid albumId, CancellationToken cancellationToken)
    {

        // Delete item memberships first (FK Restrict → Album).
        await _db.AlbumItems
            .Where(ai => ai.AlbumId == albumId)
            .ExecuteDeleteAsync(cancellationToken);

        // SHARE-ALBUM-01: and the SHARES, which carry the same FK Restrict.
        // Without this, deleting an album that has ever been shared — including
        // one whose shares were all revoked, since a revoke keeps the row for
        // the audit trail — fails on the constraint instead of deleting.
        //
        // Hard-deleted rather than revoked: the album is going away, so there is
        // no grant left to describe. Who was invited, and by whom, survives in
        // the audit log, which is where that question belongs.
        await _db.AlbumMemberships
            .Where(m => m.AlbumId == albumId)
            .ExecuteDeleteAsync(cancellationToken);

        // …and the PARTY state, for exactly the same reason and on exactly the
        // same terms. Every one of these tables carries FK Restrict to the
        // album, so an album that has ever had Party enabled could not be
        // deleted at all: the constraint failed instead.
        //
        // None of them is the audit trail. A party link is a capability over an
        // album that is going away; a participant row is a quota counter for an
        // event that no longer exists; an upload row is a VISIBILITY control on
        // a party surface that is about to stop existing (the guest's file
        // itself is untouched and stays in the owner's library). What actually
        // happened — party enabled, revoked, uploaded, moderated — is in the
        // audit log, which is where that question belongs, as it already is for
        // shares.
        //
        // Order follows the foreign keys: messages reference the link AND the
        // participant, upload rows reference the participant, participants
        // reference the link. Face-search sessions are absent deliberately —
        // they cascade from the album already, and their results cascade from
        // the session.
        await _db.PartyMessages
            .Where(m => m.AlbumId == albumId)
            .ExecuteDeleteAsync(cancellationToken);

        var partyLinkIds = _db.PartyAlbumLinks.Where(l => l.AlbumId == albumId).Select(l => l.Id);

        // The HOSTED GAME's runtime, and it has to go before four of the deletes
        // below rather than one. Its restricting foreign keys reach further than
        // the party link: a vote names a PARTICIPANT, a round names a CHALLENGE,
        // and the session names both the LINK and the ALBUM. An album that had
        // ever run a Party Game therefore could not be deleted at all — the
        // constraint failed instead, in four different places.
        //
        // Deleted explicitly, in key order, rather than left to the cascade the
        // session already carries. The cascade would work, but "what this
        // aggregate owns" is a statement this method should make out loud: the
        // next table added under the session must be deleted here too, and a
        // silent cascade is exactly what stops anyone noticing.
        //
        // This is not a restart. The album itself is going away, so the game's
        // whole history goes with it — see PartyGameService.RestartAsync for the
        // other case, where the session deliberately survives.
        var partyGameSessionIds = _db.PartyGameSessions
            .Where(g => g.AlbumId == albumId).Select(g => g.Id);
        await _db.PartyGameVotes
            .Where(v => partyGameSessionIds.Contains(v.PartyGameSessionId))
            .ExecuteDeleteAsync(cancellationToken);
        await _db.PartyGameRounds
            .Where(r => partyGameSessionIds.Contains(r.PartyGameSessionId))
            .ExecuteDeleteAsync(cancellationToken);
        await _db.PartyGameSessions
            .Where(g => g.AlbumId == albumId)
            .ExecuteDeleteAsync(cancellationToken);

        await _db.PartyChallengeVotes
            .Where(v => partyLinkIds.Contains(v.PartyAlbumLinkId))
            .ExecuteDeleteAsync(cancellationToken);
        await _db.PartyChallengeCompletions
            .Where(c => partyLinkIds.Contains(c.PartyAlbumLinkId))
            .ExecuteDeleteAsync(cancellationToken);
        await _db.PartyChallengeSessions
            .Where(s => partyLinkIds.Contains(s.PartyAlbumLinkId))
            .ExecuteDeleteAsync(cancellationToken);

        await _db.PartyUploadItems
            .Where(u => u.AlbumId == albumId)
            .ExecuteDeleteAsync(cancellationToken);

        await _db.PartyParticipants
            .Where(p => _db.PartyAlbumLinks
                .Where(l => l.AlbumId == albumId)
                .Select(l => l.Id)
                .Contains(p.PartyAlbumLinkId))
            .ExecuteDeleteAsync(cancellationToken);

        // A paired television pointed at one of this album's parties holds a
        // RESTRICTING foreign key to the link, so it would block the delete the
        // same way every table above used to. It is returned to the general
        // NubArca TV experience rather than unpaired: the party is what is going
        // away, not the television. Both columns move in one statement — the
        // check constraint refuses a general row that still names a party.
        await _db.TvSessions
            .Where(t => t.AssignedPartyAlbumLinkId != null
                && partyLinkIds.Contains(t.AssignedPartyAlbumLinkId.Value))
            .ExecuteUpdateAsync(u => u
                .SetProperty(t => t.DisplayAssignment, TvDisplayAssignments.General)
                .SetProperty(t => t.AssignedPartyAlbumLinkId, (Guid?)null), cancellationToken);

        await _db.PartyAlbumLinks
            .Where(l => l.AlbumId == albumId)
            .ExecuteDeleteAsync(cancellationToken);

        await _db.PartyChallenges
            .Where(c => c.AlbumId == albumId)
            .ExecuteDeleteAsync(cancellationToken);

        // The Party ROOT, last, because everything above holds a restricting
        // foreign key to it or to the album it draws on. A media source is the
        // party's claim on this album, so it goes with the album; a party left
        // with no source at all has nothing to show and no way to acquire one
        // in this slice, so it goes too — while a party that still draws on
        // another album survives, which is the whole point of the table.
        var partyIds = await _db.PartyMediaSources
            .Where(s => s.AlbumId == albumId)
            .Select(s => s.PartyId)
            .Distinct()
            .ToListAsync(cancellationToken);
        await _db.PartyMediaSources
            .Where(s => s.AlbumId == albumId)
            .ExecuteDeleteAsync(cancellationToken);
        await _db.Parties
            .Where(p => partyIds.Contains(p.Id)
                && !_db.PartyMediaSources.Any(s => s.PartyId == p.Id))
            .ExecuteDeleteAsync(cancellationToken);

        _db.Albums.Remove(album);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AlbumItemSummary>?> ListItemsAsync(
        Guid albumId, Guid ownerUserId,
        CancellationToken cancellationToken = default)
    {
        var albumExists = await _db.Albums
            .AnyAsync(a => a.Id == albumId && a.OwnerUserId == ownerUserId, cancellationToken);
        if (!albumExists)
            return null;

        // Hidden soft-deleted files from album item views. Slice 3: a file the
        // owner moved out of the media library (Excluded) keeps its AlbumItem
        // row but is suppressed from the album's CONTENT — it reappears here the
        // moment it is restored. (Private Vault content is already hidden by the
        // global query filter on _db.FileItems.)
        return await _db.AlbumItems
            .Where(ai => ai.AlbumId == albumId)
            .Join(_db.FileItems,
                ai => ai.FileItemId,
                f => f.Id,
                (ai, f) => new { ai, f })
            .Where(x => x.f.OwnerUserId == ownerUserId
                && x.f.DeletedAt == null
                && x.f.MediaLibraryState == MediaLibraryState.Active)
            .OrderBy(x => x.ai.AddedAt)
            .Select(x => new AlbumItemSummary(
                x.f.Id,
                x.f.Name,
                x.f.MimeType,
                x.f.SizeBytes,
                x.ai.AddedAt,
                "/api/files/" + x.f.Id.ToString() + "/thumbnail?size=small"))
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> AddItemAsync(
        Guid albumId, Guid ownerUserId, Guid fileItemId,
        CancellationToken cancellationToken = default)
    {
        // Verify album ownership.
        var albumExists = await _db.Albums
            .AnyAsync(a => a.Id == albumId && a.OwnerUserId == ownerUserId, cancellationToken);
        if (!albumExists)
            return false;

        // Verify file ownership (active files only; FileItem is always a file, not a folder).
        var fileExists = await _db.FileItems
            .AnyAsync(f => f.Id == fileItemId && f.OwnerUserId == ownerUserId && f.DeletedAt == null,
                cancellationToken);
        if (!fileExists)
            return false;

        // Idempotent: ignore if already a member.
        var alreadyAdded = await _db.AlbumItems
            .AnyAsync(ai => ai.AlbumId == albumId && ai.FileItemId == fileItemId, cancellationToken);
        if (alreadyAdded)
            return true;

        _db.AlbumItems.Add(new AlbumItem
        {
            Id = Guid.NewGuid(),
            AlbumId = albumId,
            FileItemId = fileItemId,
            AddedAt = _time.GetUtcNow().UtcDateTime,
            // This path is owner-only (the file ownership check above), so the
            // adder is always the album owner. A Contributor adds through
            // IAlbumSharingService, never here.
            AddedByUserId = ownerUserId,
            // New items APPEND to the album's curated order.
            SortOrder = await NextSortOrderAsync(albumId, cancellationToken),
        });
        // Adding an item changes the album's representation, so the content
        // version moves — a collaborator holding the old one must re-read.
        await BumpVersionAsync(albumId, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        InvalidateSemanticRankings(ownerUserId);
        return true;
    }

    public async Task<bool> RemoveItemAsync(
        Guid albumId, Guid ownerUserId, Guid fileItemId,
        CancellationToken cancellationToken = default)
    {
        var albumExists = await _db.Albums
            .AnyAsync(a => a.Id == albumId && a.OwnerUserId == ownerUserId, cancellationToken);
        if (!albumExists)
            return false;

        var removed = await _db.AlbumItems
            .Where(ai => ai.AlbumId == albumId && ai.FileItemId == fileItemId)
            .ExecuteDeleteAsync(cancellationToken);
        if (removed > 0)
        {
            await BumpVersionAsync(albumId, cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
            InvalidateSemanticRankings(ownerUserId);
        }
        return true;
    }

    public async Task<BulkAlbumItemsResult?> AddItemsAsync(
        Guid albumId, Guid ownerUserId, IReadOnlyList<Guid> fileItemIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileItemIds);

        var albumExists = await _db.Albums
            .AnyAsync(a => a.Id == albumId && a.OwnerUserId == ownerUserId, cancellationToken);
        if (!albumExists)
            return null;

        // Distinct requested ids; the raw request count is what we report as
        // "requested" so a caller sending duplicates sees them as skipped.
        var requested = fileItemIds.Count;
        var distinct = fileItemIds.Distinct().ToList();
        if (distinct.Count == 0)
            return new BulkAlbumItemsResult(requested, 0, requested);

        // Only the owner's own active files are eligible. Foreign/missing/
        // soft-deleted ids are silently dropped (no existence leak).
        var eligible = await _db.FileItems
            .Where(f => distinct.Contains(f.Id) && f.OwnerUserId == ownerUserId && f.DeletedAt == null)
            .Select(f => f.Id)
            .ToListAsync(cancellationToken);

        // Existing memberships are skipped (idempotent).
        var already = await _db.AlbumItems
            .Where(ai => ai.AlbumId == albumId && eligible.Contains(ai.FileItemId))
            .Select(ai => ai.FileItemId)
            .ToListAsync(cancellationToken);
        var alreadySet = already.ToHashSet();

        var toAdd = eligible.Where(id => !alreadySet.Contains(id)).ToList();
        var now = _time.GetUtcNow().UtcDateTime;
        // One query for the append position, then a deterministic run: a bulk
        // add shares AddedAt, which is exactly the case that made the old
        // implicit ordering ambiguous.
        var next = await NextSortOrderAsync(albumId, cancellationToken);
        foreach (var id in toAdd)
        {
            _db.AlbumItems.Add(new AlbumItem
            {
                Id = Guid.NewGuid(),
                AlbumId = albumId,
                FileItemId = id,
                AddedAt = now,
                AddedByUserId = ownerUserId,
                SortOrder = next++,
            });
        }
        if (toAdd.Count > 0)
        {
            await BumpVersionAsync(albumId, cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
            InvalidateSemanticRankings(ownerUserId);
        }

        return new BulkAlbumItemsResult(requested, toAdd.Count, requested - toAdd.Count);
    }

    public async Task<BulkAlbumItemsResult?> RemoveItemsAsync(
        Guid albumId, Guid ownerUserId, IReadOnlyList<Guid> fileItemIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileItemIds);

        var albumExists = await _db.Albums
            .AnyAsync(a => a.Id == albumId && a.OwnerUserId == ownerUserId, cancellationToken);
        if (!albumExists)
            return null;

        var requested = fileItemIds.Count;
        var distinct = fileItemIds.Distinct().ToList();
        if (distinct.Count == 0)
            return new BulkAlbumItemsResult(requested, 0, requested);

        // Remove only current memberships of THIS album. Removing membership
        // never touches the FileItem/blob — the file stays in the library.
        var removed = await _db.AlbumItems
            .Where(ai => ai.AlbumId == albumId && distinct.Contains(ai.FileItemId))
            .ExecuteDeleteAsync(cancellationToken);
        if (removed > 0)
        {
            await BumpVersionAsync(albumId, cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
            InvalidateSemanticRankings(ownerUserId);
        }

        return new BulkAlbumItemsResult(requested, removed, requested - removed);
    }
}
