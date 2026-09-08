using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Domain;

namespace NubArca.Api.Tv;

/// <summary>
/// Reads and writes what a paired television is for.
///
/// <para>Two rules hold everywhere in this file.</para>
///
/// <para>THE CALLER NAMES AN ALBUM, NEVER A LINK. A party link id is an internal
/// identifier, and accepting one from a client would make "which party is this
/// television showing" answerable by anyone who could guess or replay a GUID.
/// The album is what an owner already holds — every other owner Party route is
/// scoped by it — and the server resolves it, under this owner, to the album's
/// own active link.</para>
///
/// <para>OWNERSHIP IS RE-READ, NOT REMEMBERED. Both the television and the
/// party are matched against the calling owner in the same query that finds
/// them, so a session id and an album id from two different accounts can never
/// meet. A missing television, a foreign one, a revoked one and an expired one
/// are the same answer, as everywhere else in this codebase.</para>
/// </summary>
public sealed class TvDisplayAssignmentService : ITvDisplayAssignmentService
{
    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;
    private readonly ILogger<TvDisplayAssignmentService> _logger;

    public TvDisplayAssignmentService(
        AppDbContext db, TimeProvider clock, ILogger<TvDisplayAssignmentService> logger)
    {
        _db = db;
        _clock = clock;
        _logger = logger;
    }

    public async Task<TvAssignmentResult> SetAsync(
        Guid ownerUserId, Guid tvSessionId, string? kind, Guid? albumId,
        CancellationToken cancellationToken = default)
    {
        if (!TvDisplayAssignments.IsKnown(kind))
            return TvAssignmentResult.Fail(TvAssignmentError.UnknownKind);

        var wantsParty = kind == TvDisplayAssignments.Party;
        // A general assignment that named an album is refused rather than
        // silently ignored: the caller believed something about what it was
        // asking for, and it was wrong.
        if (wantsParty == (albumId is null))
            return TvAssignmentResult.Fail(TvAssignmentError.AlbumRequired);

        var now = _clock.GetUtcNow().UtcDateTime;
        var session = await _db.TvSessions.FirstOrDefaultAsync(
            x => x.Id == tvSessionId && x.OwnerUserId == ownerUserId
                && x.RevokedAt == null && x.ExpiresAt > now,
            cancellationToken);
        if (session is null) return TvAssignmentResult.Fail(TvAssignmentError.DeviceNotFound);

        if (!wantsParty)
        {
            // Back to the ordinary television. Both columns move together,
            // because the check constraint refuses a general row that still
            // names a party.
            session.DisplayAssignment = TvDisplayAssignments.General;
            session.AssignedPartyAlbumLinkId = null;
            await _db.SaveChangesAsync(cancellationToken);
            _logger.LogInformation(
                "tv.assignment.set SessionId={SessionId} Kind={Kind}",
                tvSessionId, TvDisplayAssignments.General);
            return TvAssignmentResult.Ok(TvDisplayAssignmentDto.General);
        }

        // The album's own ACTIVE link, resolved under this owner. A foreign
        // album, a missing one and one whose party is off all fall out of this
        // one query, so none of them can be told apart from outside — and the
        // album is matched against the owner too, not only the link, so neither
        // half can be borrowed from another account.
        var party = await _db.PartyAlbumLinks.AsNoTracking()
            .Where(l => l.OwnerUserId == ownerUserId && l.AlbumId == albumId
                && l.Enabled && l.RevokedAt == null)
            .Select(l => new
            {
                LinkId = l.Id,
                // The ALBUM's owner as well as the link's, so neither half can
                // be borrowed from another account.
                Name = _db.Albums.AsNoTracking()
                    .Where(a => a.Id == l.AlbumId && a.OwnerUserId == ownerUserId)
                    .Select(a => a.Name).FirstOrDefault(),
            })
            .Where(x => x.Name != null)
            .FirstOrDefaultAsync(cancellationToken);
        if (party is null) return TvAssignmentResult.Fail(TvAssignmentError.PartyUnavailable);

        session.DisplayAssignment = TvDisplayAssignments.Party;
        session.AssignedPartyAlbumLinkId = party.LinkId;
        await _db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation(
            "tv.assignment.set SessionId={SessionId} Kind={Kind} AlbumId={AlbumId}",
            tvSessionId, TvDisplayAssignments.Party, albumId);
        return TvAssignmentResult.Ok(
            new TvDisplayAssignmentDto(TvDisplayAssignments.Party, albumId, party.Name, true));
    }

    public async Task<IReadOnlyList<TvAssignablePartyDto>> ListAssignablePartiesAsync(
        Guid ownerUserId, CancellationToken cancellationToken = default)
    {
        var rows = await _db.PartyAlbumLinks.AsNoTracking()
            .Where(l => l.OwnerUserId == ownerUserId && l.Enabled && l.RevokedAt == null)
            .Select(l => new
            {
                l.AlbumId,
                l.GameEnabled,
                Name = _db.Albums.AsNoTracking()
                    .Where(a => a.Id == l.AlbumId && a.OwnerUserId == ownerUserId)
                    .Select(a => a.Name).FirstOrDefault(),
            })
            .Where(x => x.Name != null)
            .ToListAsync(cancellationToken);

        // Ordered by the owner's own name for the album, which is how they will
        // look for it in the list.
        return rows
            .OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(x => x.AlbumId)
            .Select(x => new TvAssignablePartyDto(x.AlbumId, x.Name!, x.GameEnabled))
            .ToList();
    }

    public async Task<IReadOnlyDictionary<Guid, TvDisplayAssignmentDto>> DescribeAsync(
        Guid ownerUserId, IReadOnlyCollection<Guid> tvSessionIds,
        CancellationToken cancellationToken = default)
    {
        if (tvSessionIds.Count == 0) return new Dictionary<Guid, TvDisplayAssignmentDto>();
        var ids = tvSessionIds as IList<Guid> ?? tvSessionIds.ToList();
        var rows = await _db.TvSessions.AsNoTracking()
            .Where(x => x.OwnerUserId == ownerUserId && ids.Contains(x.Id))
            .Select(x => new
            {
                x.Id,
                x.DisplayAssignment,
                x.AssignedPartyAlbumLinkId,
                // Left joins, because an assignment naming a party that has been
                // revoked must still describe itself. Reporting nothing would
                // read as "this television is general", which it is not.
                Link = _db.PartyAlbumLinks.AsNoTracking()
                    .Where(l => l.Id == x.AssignedPartyAlbumLinkId)
                    .Select(l => new { l.AlbumId, l.Enabled, l.RevokedAt })
                    .FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        var albumIds = rows.Where(x => x.Link is not null).Select(x => x.Link!.AlbumId).Distinct().ToList();
        var names = albumIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await _db.Albums.AsNoTracking()
                .Where(a => albumIds.Contains(a.Id))
                .ToDictionaryAsync(a => a.Id, a => a.Name, cancellationToken);

        return rows.ToDictionary(x => x.Id, x => Describe(
            x.DisplayAssignment, x.Link?.AlbumId,
            x.Link is null ? null : names.GetValueOrDefault(x.Link.AlbumId),
            available: x.Link is { Enabled: true, RevokedAt: null }));
    }

    public async Task<TvDisplayAssignmentDto> ResolveAsync(
        Guid tvSessionId, CancellationToken cancellationToken = default)
    {
        // Scoped by session id alone: the caller has already proved possession
        // of that session's token, and a session is bound to one owner.
        var row = await _db.TvSessions.AsNoTracking()
            .Where(x => x.Id == tvSessionId)
            .Select(x => new
            {
                x.DisplayAssignment,
                Link = _db.PartyAlbumLinks.AsNoTracking()
                    .Where(l => l.Id == x.AssignedPartyAlbumLinkId)
                    .Select(l => new { l.AlbumId, l.Enabled, l.RevokedAt })
                    .FirstOrDefault(),
            })
            .FirstOrDefaultAsync(cancellationToken);
        if (row is null) return TvDisplayAssignmentDto.General;

        var name = row.Link is null ? null : await _db.Albums.AsNoTracking()
            .Where(a => a.Id == row.Link.AlbumId).Select(a => a.Name)
            .FirstOrDefaultAsync(cancellationToken);
        return Describe(row.DisplayAssignment, row.Link?.AlbumId, name,
            available: row.Link is { Enabled: true, RevokedAt: null });
    }

    /// <summary>
    /// One row's assignment as a DTO. A row whose link has gone — revoked, or
    /// party mode switched off — stays a PARTY assignment and says the party is
    /// not available, because "the party you chose is over" is a different fact
    /// from "this television is a general television".
    /// </summary>
    private static TvDisplayAssignmentDto Describe(
        string kind, Guid? albumId, string? albumName, bool available) =>
        kind == TvDisplayAssignments.Party
            ? new TvDisplayAssignmentDto(TvDisplayAssignments.Party, albumId, albumName, available)
            : TvDisplayAssignmentDto.General;
}
