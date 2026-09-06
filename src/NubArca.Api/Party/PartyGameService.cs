using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Domain;

namespace NubArca.Api.Party;

/// <summary>
/// Applies the Party Game state machine to persisted state.
///
/// Three rules hold everywhere in this file.
///
/// A READ NEVER WRITES. A game that has not started has no row; the snapshot is
/// synthesized at version 0. This is the same shape as an implicit-pending AI
/// artifact status, and it is what keeps a television polling every second from
/// silently starting somebody's party.
///
/// A COMMAND IS A TRANSITION, and the transition comes from
/// <see cref="PartyGameStateMachine"/>. Nothing here decides what is legal; it
/// decides what a legal transition does to rows.
///
/// A REFUSAL IS INFORMATIVE. Every refusal that can carry the current snapshot
/// does, because the owner is holding a phone in front of a room full of people
/// and the useful answer to "that was stale" is the truth, not an apology.
/// </summary>
public sealed class PartyGameService : IPartyGameService
{
    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;
    private readonly ILogger<PartyGameService> _logger;

    public PartyGameService(AppDbContext db, TimeProvider clock, ILogger<PartyGameService> logger)
    {
        _db = db;
        _clock = clock;
        _logger = logger;
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    public async Task<PartyGameSnapshotDto?> GetOwnerSnapshotAsync(
        Guid ownerUserId, Guid albumId, CancellationToken cancellationToken = default)
    {
        var link = await ActiveLinkAsync(ownerUserId, albumId, cancellationToken);
        if (link is null || !link.GameEnabled) return null;
        var session = await _db.PartyGameSessions.AsNoTracking()
            .FirstOrDefaultAsync(x => x.PartyAlbumLinkId == link.Id, cancellationToken);
        return await BuildOwnerSnapshotAsync(albumId, session, cancellationToken);
    }

    public async Task<PartyGameCommandResult> ExecuteAsync(
        Guid ownerUserId, Guid albumId, string? command, int expectedVersion,
        CancellationToken cancellationToken = default)
    {
        var link = await ActiveLinkAsync(ownerUserId, albumId, cancellationToken);
        if (link is null) return PartyGameCommandResult.Fail(PartyGameCommandError.NotFound);
        if (!link.GameEnabled) return PartyGameCommandResult.Fail(PartyGameCommandError.GameDisabled);
        if (!PartyGameCommands.IsKnown(command))
            return PartyGameCommandResult.Fail(PartyGameCommandError.UnknownCommand);

        var session = await _db.PartyGameSessions
            .FirstOrDefaultAsync(x => x.PartyAlbumLinkId == link.Id, cancellationToken);

        // Version 0 is the lobby that has not been written yet. Quoting it is how
        // an owner says "I believe nobody has started this game".
        var currentVersion = session?.Version ?? 0;
        if (expectedVersion != currentVersion)
            return PartyGameCommandResult.Fail(PartyGameCommandError.VersionConflict,
                await BuildOwnerSnapshotAsync(albumId, session, cancellationToken));

        var phase = session?.Phase ?? PartyGamePhases.Lobby;
        var playedIds = session is null
            ? new List<Guid>()
            : await _db.PartyGameRounds.AsNoTracking()
                .Where(x => x.PartyGameSessionId == session.Id)
                .Select(x => x.PartyChallengeId).ToListAsync(cancellationToken);
        var next = await NextChallengeAsync(albumId, playedIds, cancellationToken);
        var currentChallenge = await CurrentChallengeAsync(session, cancellationToken);

        var transition = PartyGameStateMachine.Resolve(
            phase, command!, next is not null, Votes(currentChallenge));
        if (transition is null)
        {
            var error = command == PartyGameCommands.Start && phase == PartyGamePhases.Lobby
                ? PartyGameCommandError.NoChallenges
                : PartyGameCommandError.IllegalTransition;
            return PartyGameCommandResult.Fail(error,
                await BuildOwnerSnapshotAsync(albumId, session, cancellationToken));
        }

        var now = Now;
        if (session is null)
        {
            session = NewSession(albumId, link.Id, now);
            _db.PartyGameSessions.Add(session);
        }
        var round = session.CurrentRoundId is Guid roundId
            ? await _db.PartyGameRounds.FirstOrDefaultAsync(x => x.Id == roundId, cancellationToken)
            : null;

        if (round is not null && transition.Effect.HasFlag(PartyGameRoundEffect.CompleteRound))
        {
            round.Status = PartyGameRoundStatuses.Completed;
            round.CompletedAt = now;
        }
        else if (round is not null && transition.Effect.HasFlag(PartyGameRoundEffect.AbandonRound))
        {
            round.Status = PartyGameRoundStatuses.Abandoned;
            round.CompletedAt = now;
        }

        if (transition.Effect.HasFlag(PartyGameRoundEffect.StartRound))
        {
            // Resolve() only returns StartRound when there IS a next activity.
            var started = new PartyGameRound
            {
                Id = Guid.NewGuid(),
                PartyGameSessionId = session.Id,
                PartyChallengeId = next!.Id,
                Sequence = session.CurrentRoundNumber + 1,
                Status = PartyGameRoundStatuses.Active,
                StartedAt = now,
                PhaseStartedAt = now,
            };
            currentChallenge = next;
            _db.PartyGameRounds.Add(started);
            session.CurrentRoundId = started.Id;
            session.CurrentRoundNumber = started.Sequence;
        }
        else if (transition.Phase == PartyGamePhases.Finished)
        {
            session.CurrentRoundId = null;
        }
        else if (round is not null)
        {
            // An ordinary phase change inside the same round restarts the phase
            // clock. Only the activity phase carries the host's time limit: a
            // reveal, a vote and a result each last exactly as long as the host
            // leaves them on screen.
            round.PhaseStartedAt = now;
            round.PhaseEndsAt = command == PartyGameCommands.StartChallenge
                && currentChallenge?.DurationSeconds is int seconds
                ? now.AddSeconds(seconds)
                : null;
        }

        if (session.StartedAt is null && transition.Status == PartyGameStatuses.Live) session.StartedAt = now;
        if (transition.Status == PartyGameStatuses.Finished) session.FinishedAt ??= now;
        session.Phase = transition.Phase;
        session.Status = transition.Status;
        session.Version = currentVersion + 1;
        session.UpdatedAt = now;

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is DbUpdateConcurrencyException or DbUpdateException)
        {
            // Two owner surfaces commanded the same game in the same instant, or
            // both tried to create it. The database elected one; the loser is
            // told it was stale and handed the winner's state.
            _db.ChangeTracker.Clear();
            var current = await _db.PartyGameSessions.AsNoTracking()
                .FirstOrDefaultAsync(x => x.PartyAlbumLinkId == link.Id, cancellationToken);
            return PartyGameCommandResult.Fail(PartyGameCommandError.VersionConflict,
                await BuildOwnerSnapshotAsync(albumId, current, cancellationToken));
        }

        _logger.LogInformation(
            "party.game.command AlbumId={AlbumId} Command={Command} Phase={Phase} Round={Round} Version={Version}",
            albumId, command, session.Phase, session.CurrentRoundNumber, session.Version);

        _db.ChangeTracker.Clear();
        var snapshot = await BuildOwnerSnapshotAsync(albumId,
            await _db.PartyGameSessions.AsNoTracking()
                .FirstAsync(x => x.Id == session.Id, cancellationToken), cancellationToken);
        return PartyGameCommandResult.Ok(snapshot!);
    }

    public async Task<PartyGamePublicSnapshotDto?> GetPublicSnapshotAsync(
        PartyAccess access, CancellationToken cancellationToken = default)
    {
        if (access.PartyAlbumLinkId is not Guid linkId) return null;
        var context = await _db.PartyAlbumLinks.AsNoTracking()
            .Where(x => x.Id == linkId && x.AlbumId == access.AlbumId && x.Enabled && x.GameEnabled)
            .Join(_db.Albums.AsNoTracking(), x => x.AlbumId, a => a.Id, (x, a) => new { a.Name })
            .FirstOrDefaultAsync(cancellationToken);
        if (context is null) return null;

        var session = await _db.PartyGameSessions.AsNoTracking()
            .FirstOrDefaultAsync(x => x.PartyAlbumLinkId == linkId, cancellationToken);
        var total = await EnabledChallengeCountAsync(access.AlbumId, cancellationToken);
        if (session is null)
            return new PartyGamePublicSnapshotDto(context.Name, PartyGameStatuses.Lobby,
                PartyGamePhases.Lobby, 0, 0, total, null, null);

        PartyChallengePresentationDto? challenge = null;
        DateTime? phaseEndsAt = null;
        if (PartyGamePhases.ShowsChallenge(session.Phase) && session.CurrentRoundId is Guid roundId)
        {
            var row = await _db.PartyGameRounds.AsNoTracking()
                .Where(x => x.Id == roundId)
                .Join(_db.PartyChallenges.AsNoTracking(), r => r.PartyChallengeId, c => c.Id,
                    (r, c) => new
                    {
                        r.PhaseEndsAt, c.Id, c.Title, c.Body, c.Kind, c.MediaFileItemId,
                        c.DurationSeconds, c.VotingMode, c.VoteQuestion,
                    })
                .FirstOrDefaultAsync(cancellationToken);
            if (row is not null)
            {
                phaseEndsAt = row.PhaseEndsAt;
                challenge = new PartyChallengePresentationDto(row.Id, row.Title, row.Body, row.Kind,
                    // Token-less sentinel; the endpoint rewrites it against the
                    // caller's own token, exactly as the guest challenge list does.
                    row.MediaFileItemId is null ? null : $"/api/party/challenge-media/{row.Id}",
                    row.DurationSeconds, row.VotingMode, row.VoteQuestion);
            }
        }

        return new PartyGamePublicSnapshotDto(context.Name, session.Status, session.Phase,
            session.Version, session.CurrentRoundNumber, total, phaseEndsAt, challenge);
    }

    // --- internals ---------------------------------------------------------

    private PartyGameSession NewSession(Guid albumId, Guid linkId, DateTime now) => new()
    {
        Id = Guid.NewGuid(),
        AlbumId = albumId,
        PartyAlbumLinkId = linkId,
        Status = PartyGameStatuses.Lobby,
        Phase = PartyGamePhases.Lobby,
        Version = 0,
        CreatedAt = now,
        UpdatedAt = now,
    };

    private Task<PartyAlbumLink?> ActiveLinkAsync(Guid ownerUserId, Guid albumId, CancellationToken ct) =>
        _db.PartyAlbumLinks.AsNoTracking()
            .Where(x => x.OwnerUserId == ownerUserId && x.AlbumId == albumId
                && x.Enabled && x.RevokedAt == null)
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(ct);

    private Task<int> EnabledChallengeCountAsync(Guid albumId, CancellationToken ct) =>
        _db.PartyChallenges.AsNoTracking().CountAsync(x => x.AlbumId == albumId && x.IsEnabled, ct);

    /// The deck is played in the owner's own order. Vote-driven selection belongs
    /// to the older interval-based hold, where the room chose what happened next;
    /// in a hosted game the host chose, in the composer, before the party.
    private Task<PartyChallenge?> NextChallengeAsync(
        Guid albumId, List<Guid> playedIds, CancellationToken ct) =>
        _db.PartyChallenges.AsNoTracking()
            .Where(x => x.AlbumId == albumId && x.IsEnabled && !playedIds.Contains(x.Id))
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
            .FirstOrDefaultAsync(ct);

    private async Task<PartyGameSnapshotDto?> BuildOwnerSnapshotAsync(
        Guid albumId, PartyGameSession? session, CancellationToken ct)
    {
        var total = await EnabledChallengeCountAsync(albumId, ct);
        var playedIds = session is null
            ? new List<Guid>()
            : await _db.PartyGameRounds.AsNoTracking()
                .Where(x => x.PartyGameSessionId == session.Id)
                .Select(x => x.PartyChallengeId).ToListAsync(ct);
        var next = await NextChallengeAsync(albumId, playedIds, ct);

        if (session is null)
            return new PartyGameSnapshotDto(albumId, null, PartyGameStatuses.Lobby, PartyGamePhases.Lobby,
                0, 0, total, 0, null, null, null, null, null, Challenge(next),
                PartyGameStateMachine.LegalCommands(PartyGamePhases.Lobby, next is not null));

        PartyGameChallengeDto? current = null;
        DateTime? phaseStartedAt = null;
        DateTime? phaseEndsAt = null;
        if (session.CurrentRoundId is Guid roundId)
        {
            var row = await _db.PartyGameRounds.AsNoTracking()
                .Where(x => x.Id == roundId)
                .Join(_db.PartyChallenges.AsNoTracking(), r => r.PartyChallengeId, c => c.Id,
                    (r, c) => new { r.PhaseStartedAt, r.PhaseEndsAt, Challenge = c })
                .FirstOrDefaultAsync(ct);
            if (row is not null)
            {
                phaseStartedAt = row.PhaseStartedAt;
                phaseEndsAt = row.PhaseEndsAt;
                current = Challenge(row.Challenge);
            }
        }

        var played = await _db.PartyGameRounds.AsNoTracking()
            .CountAsync(x => x.PartyGameSessionId == session.Id
                && x.Status != PartyGameRoundStatuses.Active, ct);

        return new PartyGameSnapshotDto(albumId, session.Id, session.Status, session.Phase,
            session.Version, session.CurrentRoundNumber, total, played,
            session.StartedAt, session.FinishedAt, phaseStartedAt, phaseEndsAt,
            current, Challenge(next),
            PartyGameStateMachine.LegalCommands(
                session.Phase, next is not null,
                current is null || PartyChallengeVotingModes.CollectsVotes(current.VotingMode)));
    }

    private static PartyGameChallengeDto? Challenge(PartyChallenge? row) => row is null ? null
        : new PartyGameChallengeDto(row.Id, row.Title, row.Body, row.Kind,
            row.MediaFileItemId is Guid id ? $"/api/files/{id}/thumbnail?size=medium" : null,
            row.DurationSeconds, row.VotingMode, row.VoteQuestion);

    /// Whether the running activity is one the room votes on. No activity at all
    /// (the lobby) answers true, so the lobby's own commands are unaffected.
    private static bool Votes(PartyChallenge? challenge) =>
        challenge is null || PartyChallengeVotingModes.CollectsVotes(challenge.VotingMode);

    private async Task<PartyChallenge?> CurrentChallengeAsync(
        PartyGameSession? session, CancellationToken ct)
    {
        if (session?.CurrentRoundId is not Guid roundId) return null;
        return await _db.PartyGameRounds.AsNoTracking()
            .Where(x => x.Id == roundId)
            .Join(_db.PartyChallenges.AsNoTracking(), r => r.PartyChallengeId, c => c.Id,
                (r, c) => c)
            .FirstOrDefaultAsync(ct);
    }
}
