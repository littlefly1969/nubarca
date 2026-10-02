using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Tv;

namespace NubArca.Api.Party;

/// <inheritdoc cref="IPartyGuestbookLiveService"/>
public sealed class PartyGuestbookLiveService : IPartyGuestbookLiveService
{
    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;
    private readonly ITvPartyPresentationService _presentation;
    private readonly ILogger<PartyGuestbookLiveService> _logger;

    public PartyGuestbookLiveService(
        AppDbContext db,
        TimeProvider clock,
        ITvPartyPresentationService presentation,
        ILogger<PartyGuestbookLiveService> logger)
    {
        _db = db;
        _clock = clock;
        _presentation = presentation;
        _logger = logger;
    }

    public async Task<PartyGuestbookLiveControlDto?> GetAsync(
        Guid ownerUserId, Guid albumId, PartyGuestbookLiveRights rights,
        CancellationToken cancellationToken = default)
    {
        var linkId = await ActiveLinkIdAsync(ownerUserId, albumId, cancellationToken);
        if (linkId is not Guid id) return null;
        var link = await LinkAsync(id, cancellationToken);
        return link is null ? null : await BuildAsync(link, rights, cancellationToken);
    }

    public async Task<PartyGuestbookLiveResult> ExecuteAsync(
        Guid ownerUserId, Guid albumId, string? command, int expectedVersion,
        PartyGuestbookLiveRights rights, CancellationToken cancellationToken = default)
    {
        if (!PartyGuestbookLiveCommands.IsKnown(command))
            return PartyGuestbookLiveResult.Fail(PartyGuestbookLiveError.UnknownCommand);

        var linkId = await ActiveLinkIdAsync(ownerUserId, albumId, cancellationToken);
        if (linkId is not Guid id) return PartyGuestbookLiveResult.Fail(PartyGuestbookLiveError.NotFound);

        if (!rights.Allows(command!))
        {
            var current = await LinkAsync(id, cancellationToken);
            return PartyGuestbookLiveResult.Fail(PartyGuestbookLiveError.Forbidden,
                current is null ? null : await BuildAsync(current, rights, cancellationToken));
        }

        PartyGuestbookLiveError? refusal;
        // THE BOUNDARY IS THE PARTY LINK'S ROW — the same one every game command
        // that takes the screen locks. The transaction opens with an update that
        // assigns a column to itself: it changes nothing and holds the row's
        // write lock, so a game command and this one are ordered, and everything
        // read after it (this row, the game, the book) is what the winner left.
        var owned = _db.Database.CurrentTransaction is null;
        var tx = owned ? await _db.Database.BeginTransactionAsync(cancellationToken) : null;
        try
        {
            var locked = await _db.PartyAlbumLinks
                .Where(l => l.Id == id)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(l => l.GuestbookControlVersion, l => l.GuestbookControlVersion),
                    cancellationToken);
            var link = locked == 0 ? null : await LinkAsync(id, cancellationToken);
            if (link is null)
            {
                if (owned) await tx!.RollbackAsync(cancellationToken);
                return PartyGuestbookLiveResult.Fail(PartyGuestbookLiveError.NotFound);
            }

            refusal = await RefusalAsync(link, command!, expectedVersion, cancellationToken);
            if (refusal is null)
            {
                var applied = await ApplyAsync(link, command!, expectedVersion, cancellationToken);
                if (applied == 1)
                {
                    if (owned) await tx!.CommitAsync(cancellationToken);
                    Log(link, command!);
                    var after = await LinkAsync(id, cancellationToken);
                    return PartyGuestbookLiveResult.Ok(await BuildAsync(after!, rights, cancellationToken));
                }

                // Unreachable under the row lock, and answered honestly if it
                // ever is: somebody else spent the version.
                refusal = PartyGuestbookLiveError.VersionConflict;
            }

            if (owned) await tx!.RollbackAsync(cancellationToken);
        }
        finally
        {
            if (tx is not null) await tx.DisposeAsync();
        }

        if (!PartyGuestbookLiveCommands.IsViewing(command!))
        {
            _logger.LogInformation(
                "party.guestbook.tv.refused LinkId={LinkId} Command={Command} Code={Code}",
                id, command, Code(refusal.Value));
        }

        var state = await LinkAsync(id, cancellationToken);
        return PartyGuestbookLiveResult.Fail(refusal.Value,
            state is null ? null : await BuildAsync(state, rights, cancellationToken));
    }

    /// <summary>The stable machine code of a refusal, as the HTTP layer sends it.</summary>
    public static string Code(PartyGuestbookLiveError error) => error switch
    {
        PartyGuestbookLiveError.NotFound => "not_found",
        PartyGuestbookLiveError.UnknownCommand => "unknown_command",
        PartyGuestbookLiveError.Forbidden => "forbidden",
        PartyGuestbookLiveError.PartyNotLive => "party_not_live",
        PartyGuestbookLiveError.GuestbookNotAvailable => "guestbook_not_available",
        PartyGuestbookLiveError.GuestbookEmpty => "guestbook_empty",
        PartyGuestbookLiveError.GameActive => "game_active",
        PartyGuestbookLiveError.VersionConflict => "version_conflict",
        _ => "illegal_transition",
    };

    // ── Deciding ────────────────────────────────────────────────────────────

    /// <summary>
    /// Why this command may not run NOW, or null when it may — read from what
    /// is committed after the lock, never from what the caller saw.
    /// </summary>
    private async Task<PartyGuestbookLiveError?> RefusalAsync(
        LinkRow link, string command, int expectedVersion, CancellationToken ct)
    {
        if (link.Version != expectedVersion) return PartyGuestbookLiveError.VersionConflict;
        if (!await PartyIsLiveAsync(link.PartyId, ct)) return PartyGuestbookLiveError.PartyNotLive;

        switch (command)
        {
            case PartyGuestbookLiveCommands.EnableViewing:
                if (!link.GuestbookEnabled) return PartyGuestbookLiveError.GuestbookNotAvailable;
                return link.ViewingEnabled ? PartyGuestbookLiveError.IllegalTransition : null;
            case PartyGuestbookLiveCommands.DisableViewing:
                return link.ViewingEnabled ? null : PartyGuestbookLiveError.IllegalTransition;
            case PartyGuestbookLiveCommands.ReturnToSlideshow:
                return link.TvActive ? null : PartyGuestbookLiveError.IllegalTransition;
            default: // show_on_tv
                if (link.TvActive) return PartyGuestbookLiveError.IllegalTransition;
                var state = await _presentation.ProjectAsync(link.Id, ct);
                var visible = await CountAsync(link.PartyId, PartyMessageStatuses.Visible, ct);
                return ShowOnTvRefusal(true, link.GuestbookEnabled, state, visible);
        }
    }

    /// <summary>
    /// Whether the book may take the television: the party live and showable,
    /// the book open, a memory to show, and the GAME not holding the screen —
    /// asked through the same projection the television is told, so "the game
    /// is on the TV" means exactly one thing (finished dwell included).
    /// </summary>
    private static PartyGuestbookLiveError? ShowOnTvRefusal(
        bool live, bool guestbookEnabled, TvPartyState state, int visible)
    {
        if (!live) return PartyGuestbookLiveError.PartyNotLive;
        if (!guestbookEnabled || !state.Showable) return PartyGuestbookLiveError.GuestbookNotAvailable;
        if (state.Presentation == TvPartyPresentations.Game) return PartyGuestbookLiveError.GameActive;
        return visible == 0 ? PartyGuestbookLiveError.GuestbookEmpty : null;
    }

    /// <summary>
    /// The one write: the command's flag, and the version it spends — guarded
    /// by the version the decision was made against.
    /// </summary>
    private Task<int> ApplyAsync(LinkRow link, string command, int expectedVersion, CancellationToken ct)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var row = _db.PartyAlbumLinks.Where(l => l.Id == link.Id && l.GuestbookControlVersion == expectedVersion);
        return command switch
        {
            PartyGuestbookLiveCommands.EnableViewing or PartyGuestbookLiveCommands.DisableViewing =>
                row.ExecuteUpdateAsync(s => s
                    .SetProperty(l => l.GuestbookViewingEnabled, command == PartyGuestbookLiveCommands.EnableViewing)
                    .SetProperty(l => l.GuestbookControlVersion, expectedVersion + 1)
                    .SetProperty(l => l.UpdatedAt, now), ct),
            _ =>
                row.ExecuteUpdateAsync(s => s
                    .SetProperty(l => l.GuestbookTvActive, command == PartyGuestbookLiveCommands.ShowOnTv)
                    .SetProperty(l => l.GuestbookControlVersion, expectedVersion + 1)
                    .SetProperty(l => l.UpdatedAt, now), ct),
        };
    }

    private void Log(LinkRow link, string command)
    {
        var name = command switch
        {
            PartyGuestbookLiveCommands.EnableViewing => "party.guestbook.viewing.enabled",
            PartyGuestbookLiveCommands.DisableViewing => "party.guestbook.viewing.disabled",
            PartyGuestbookLiveCommands.ShowOnTv => "party.guestbook.tv.started",
            _ => "party.guestbook.tv.stopped",
        };
        // Ids and the new version only: never a memory, a name or a token.
        _logger.LogInformation("{Event} LinkId={LinkId} Version={Version}", name, link.Id, link.Version + 1);
    }

    // ── The read model ──────────────────────────────────────────────────────

    private async Task<PartyGuestbookLiveControlDto> BuildAsync(
        LinkRow link, PartyGuestbookLiveRights rights, CancellationToken ct)
    {
        var state = await _presentation.ProjectAsync(link.Id, ct);
        var live = await PartyIsLiveAsync(link.PartyId, ct);
        var visible = await CountAsync(link.PartyId, PartyMessageStatuses.Visible, ct);
        var pending = await CountAsync(link.PartyId, PartyMessageStatuses.Pending, ct);
        var tvRefusal = link.TvActive ? null : ShowOnTvRefusal(live, link.GuestbookEnabled, state, visible);

        var commands = new List<string>(2);
        if (rights.Viewing && live && (link.ViewingEnabled || link.GuestbookEnabled))
        {
            commands.Add(link.ViewingEnabled
                ? PartyGuestbookLiveCommands.DisableViewing
                : PartyGuestbookLiveCommands.EnableViewing);
        }
        if (rights.Tv)
        {
            if (link.TvActive) commands.Add(PartyGuestbookLiveCommands.ReturnToSlideshow);
            else if (tvRefusal is null) commands.Add(PartyGuestbookLiveCommands.ShowOnTv);
        }

        return new PartyGuestbookLiveControlDto(
            link.Version, link.ViewingEnabled, link.TvActive, state.Presentation,
            visible, pending, commands,
            tvRefusal is PartyGuestbookLiveError reason ? Code(reason) : null,
            live, link.GuestbookEnabled);
    }

    // ── Reads ───────────────────────────────────────────────────────────────

    private sealed record LinkRow(
        Guid Id, Guid PartyId, bool GuestbookEnabled, bool ViewingEnabled, bool TvActive, int Version);

    /// <summary>The album's ACTIVE party link — the one every live setting is written to.</summary>
    private async Task<Guid?> ActiveLinkIdAsync(Guid ownerUserId, Guid albumId, CancellationToken ct)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        return await _db.PartyAlbumLinks.AsNoTracking()
            .Where(l => l.AlbumId == albumId && l.OwnerUserId == ownerUserId
                && l.Enabled && l.RevokedAt == null
                && (l.ExpiresAt == null || l.ExpiresAt > now))
            .OrderByDescending(l => l.CreatedAt)
            .Select(l => (Guid?)l.Id)
            .FirstOrDefaultAsync(ct);
    }

    private Task<LinkRow?> LinkAsync(Guid linkId, CancellationToken ct) =>
        _db.PartyAlbumLinks.AsNoTracking()
            .Where(l => l.Id == linkId)
            .Select(l => new LinkRow(
                l.Id, l.PartyId, l.GuestbookEnabled, l.GuestbookViewingEnabled,
                l.GuestbookTvActive, l.GuestbookControlVersion))
            .FirstOrDefaultAsync(ct);

    private Task<bool> PartyIsLiveAsync(Guid partyId, CancellationToken ct) =>
        _db.Parties.AsNoTracking().AnyAsync(p => p.Id == partyId && p.Status == PartyStatuses.Live, ct);

    /// <summary>The book is the PARTY's, whichever link the request came through.</summary>
    private Task<int> CountAsync(Guid partyId, string status, CancellationToken ct) =>
        _db.PartyGuestbookEntries.AsNoTracking().CountAsync(e => e.PartyId == partyId && e.Status == status, ct);
}
