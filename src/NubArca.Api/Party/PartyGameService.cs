using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
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
    private readonly IPartyLinkService _links;
    private readonly IPartyParticipantService _participants;
    private readonly ILogger<PartyGameService> _logger;

    public PartyGameService(
        AppDbContext db, TimeProvider clock, IPartyLinkService links,
        IPartyParticipantService participants,
        ILogger<PartyGameService> logger)
    {
        _db = db;
        _clock = clock;
        _links = links;
        _participants = participants;
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
        return await BuildOwnerSnapshotAsync(albumId, session, cancellationToken,
            await RoomAsync(link, cancellationToken), link);
    }

    /// <summary>
    /// The room, as only the server can describe it: how many guests are in it,
    /// how long ago a screen last looked, and where both surfaces live.
    ///
    /// The elapsed time is computed HERE rather than sent as a timestamp,
    /// because a control room open on a laptop with a drifting clock would
    /// otherwise decide for itself that the television has been dead for an hour.
    /// </summary>
    private async Task<PartyGameRoomDto> RoomAsync(PartyAlbumLink link, CancellationToken ct)
    {
        var now = Now;
        var since = now.AddSeconds(-PartyGamePresence.WindowSeconds);
        // Retired aliases are excluded, and that is not tidiness. A guest whose
        // pre-upgrade row was folded would otherwise be counted TWICE in the
        // room — the split this identity exists to end, showing up in the one
        // number a host reads out loud.
        var guests = await _db.PartyParticipants.AsNoTracking()
            .CountAsync(x => x.PartyAlbumLinkId == link.Id
                && x.RetiredAt == null && x.LastSeenAt >= since, ct);
        var displayAge = link.LastDisplaySeenAt is DateTime seen
            ? (int?)Math.Max(0, (int)Math.Round((now - seen).TotalSeconds))
            : null;
        // The owner is authorized to hold this party's tokens — that is what the
        // settings panel already shows them — so the two surfaces are named as
        // URLs rather than left for the client to assemble.
        var token = _links.DeriveViewToken(link.Id);
        return new PartyGameRoomDto(guests, displayAge,
            PartyLinkService.BuildTvStageUrl(token), PartyLinkService.BuildGameUrl(token));
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
        {
            Refused(albumId, command, session?.Phase ?? PartyGamePhases.Lobby,
                PartyGameCommandError.VersionConflict);
            return PartyGameCommandResult.Fail(PartyGameCommandError.VersionConflict,
                await BuildOwnerSnapshotAsync(albumId, session, cancellationToken,
                    await RoomAsync(link, cancellationToken), link));
        }

        var phase = session?.Phase ?? PartyGamePhases.Lobby;
        var playedIds = session is null
            ? new List<Guid>()
            : await _db.PartyGameRounds.AsNoTracking()
                .Where(x => x.PartyGameSessionId == session.Id)
                .Select(x => x.PartyChallengeId).ToListAsync(cancellationToken);
        var excludedIds = await ExcludedIdsAsync(session, cancellationToken);
        var next = await NextChallengeAsync(albumId, playedIds, excludedIds, cancellationToken);
        var currentChallenge = await CurrentChallengeAsync(session, cancellationToken);

        var transition = PartyGameStateMachine.Resolve(
            phase, command!, next is not null, Votes(currentChallenge));
        if (transition is null)
        {
            var error = command == PartyGameCommands.Start && phase == PartyGamePhases.Lobby
                ? PartyGameCommandError.NoChallenges
                : PartyGameCommandError.IllegalTransition;
            Refused(albumId, command, phase, error);
            return PartyGameCommandResult.Fail(error,
                await BuildOwnerSnapshotAsync(albumId, session, cancellationToken,
                    await RoomAsync(link, cancellationToken), link));
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

        // Playing the same party again. The rows the finished match wrote go;
        // the session, its link and its version stay, so a command written for
        // the game that just ended is stale for ever rather than for a while.
        if (transition.Effect.HasFlag(PartyGameRoundEffect.ResetGame))
        {
            var restarted = await RestartAsync(session, currentVersion, now, cancellationToken);
            if (!restarted)
            {
                _db.ChangeTracker.Clear();
                Refused(albumId, command, phase, PartyGameCommandError.VersionConflict);
                var winner = await _db.PartyGameSessions.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.PartyAlbumLinkId == link.Id, cancellationToken);
                return PartyGameCommandResult.Fail(PartyGameCommandError.VersionConflict,
                    await BuildOwnerSnapshotAsync(albumId, winner, cancellationToken,
                        await RoomAsync(link, cancellationToken), link));
            }

            _logger.LogInformation(
                "party.game.command AlbumId={AlbumId} Command={Command} Phase={Phase} Round={Round} Version={Version}",
                albumId, command, session.Phase, session.CurrentRoundNumber, session.Version);

            _db.ChangeTracker.Clear();
            return PartyGameCommandResult.Ok((await BuildOwnerSnapshotAsync(albumId,
                await _db.PartyGameSessions.AsNoTracking()
                    .FirstAsync(x => x.Id == session.Id, cancellationToken), cancellationToken,
                await RoomAsync(link, cancellationToken), link))!);
        }

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
        else if (transition.Phase is PartyGamePhases.Finished or PartyGamePhases.Intermission)
        {
            // Nothing is on the screen in either phase, so nothing is current.
            // An intermission that kept pointing at the round it just completed
            // would leave the control room describing an activity the room is no
            // longer looking at, and would offer its vote count as if it were
            // still being collected.
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
            Refused(albumId, command, phase, PartyGameCommandError.VersionConflict);
            var current = await _db.PartyGameSessions.AsNoTracking()
                .FirstOrDefaultAsync(x => x.PartyAlbumLinkId == link.Id, cancellationToken);
            return PartyGameCommandResult.Fail(PartyGameCommandError.VersionConflict,
                await BuildOwnerSnapshotAsync(albumId, current, cancellationToken,
                    await RoomAsync(link, cancellationToken), link));
        }

        _logger.LogInformation(
            "party.game.command AlbumId={AlbumId} Command={Command} Phase={Phase} Round={Round} Version={Version}",
            albumId, command, session.Phase, session.CurrentRoundNumber, session.Version);

        _db.ChangeTracker.Clear();
        var snapshot = await BuildOwnerSnapshotAsync(albumId,
            await _db.PartyGameSessions.AsNoTracking()
                .FirstAsync(x => x.Id == session.Id, cancellationToken), cancellationToken,
            await RoomAsync(link, cancellationToken), link);
        return PartyGameCommandResult.Ok(snapshot!);
    }

    public async Task<PartyGamePublicSnapshotDto?> GetPublicSnapshotAsync(
        PartyAccess access, Guid? participantId = null, bool isDisplay = false,
        CancellationToken cancellationToken = default)
    {
        var linkId = access.PartyAlbumLinkId;
        var context = await _db.PartyAlbumLinks.AsNoTracking()
            .Where(x => x.Id == linkId && x.AlbumId == access.MainAlbumId && x.Enabled && x.GameEnabled)
            .Join(_db.Albums.AsNoTracking(), x => x.AlbumId, a => a.Id,
                (x, a) => new { a.Name, x.PriorityVotingEnabled, x.VotesPerGuest })
            .FirstOrDefaultAsync(cancellationToken);
        if (context is null) return null;

        if (isDisplay)
        {
            // One column, by id, outside the change tracker: a television polling
            // every couple of seconds must never contend with an owner command
            // for the session's concurrency token.
            await _db.PartyAlbumLinks.Where(x => x.Id == linkId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastDisplaySeenAt, Now), cancellationToken);
        }

        var session = await _db.PartyGameSessions.AsNoTracking()
            .FirstOrDefaultAsync(x => x.PartyAlbumLinkId == linkId, cancellationToken);
        var total = await EnabledChallengeCountAsync(access.MainAlbumId, cancellationToken);

        // The pre-game surface, resolved from the SAME session row the phase
        // comes from, so "the game has begun" and "preferences are closed" can
        // never be two different answers inside one response.
        var preferences = await BuildPreferencesAsync(
            access, participantId, context.PriorityVotingEnabled, context.VotesPerGuest,
            session?.Phase, cancellationToken);

        if (session is null)
            return new PartyGamePublicSnapshotDto(context.Name, PartyGameStatuses.Lobby,
                PartyGamePhases.Lobby, 0, 0, total, null, null,
                Preferences: preferences);

        PartyChallengePresentationDto? challenge = null;
        DateTime? phaseEndsAt = null;
        string? myVote = null;
        PartyGameVotingDto? voting = null;
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
                // Offered only while the picture still qualifies as a Party
                // reference — the stage shows an activity whose picture went to
                // Trash without one, never with a frame that fails to load.
                var mediaOk = row.MediaFileItemId is Guid mediaId
                    && await PartyMediaReference.IsEligibleAsync(
                        _db, access.OwnerUserId, mediaId, cancellationToken);
                challenge = new PartyChallengePresentationDto(row.Id, row.Title, row.Body, row.Kind,
                    // Token-less sentinel; the endpoint rewrites it against the
                    // caller's own token, exactly as the guest challenge list does.
                    mediaOk ? $"/api/party/challenge-media/{row.Id}" : null,
                    row.DurationSeconds, row.VotingMode, row.VoteQuestion);

                if (PartyChallengeVotingModes.CollectsVotes(row.VotingMode))
                {
                    // A television and a guest learn the RESULT only once it has
                    // been revealed. Between closing and revealing the owner
                    // knows and the room does not, which is the whole point of
                    // having a host.
                    voting = await VotingAsync(session, roundId,
                        includeResult: session.Phase == PartyGamePhases.Result, cancellationToken);
                    if (participantId is Guid guest)
                        myVote = await _db.PartyGameVotes.AsNoTracking()
                            .Where(x => x.PartyGameRoundId == roundId && x.PartyParticipantId == guest)
                            .Select(x => x.Value).FirstOrDefaultAsync(cancellationToken);
                }
            }
        }

        return new PartyGamePublicSnapshotDto(context.Name, session.Status, session.Phase,
            session.Version, session.CurrentRoundNumber, total, phaseEndsAt, challenge,
            PartyGamePhases.ShowsChallenge(session.Phase) ? session.CurrentRoundId : null,
            voting, myVote, preferences);
    }

    public async Task<PartyGameVoteResult> VoteAsync(
        PartyAccess access, Guid? participantId, Guid? roundId, string? value,
        CancellationToken cancellationToken = default)
    {
        if (!PartyGameVoteValues.IsKnown(value))
            return PartyGameVoteResult.Fail(PartyGameVoteError.UnknownValue);
        var linkId = access.PartyAlbumLinkId;

        // The game switch is re-read on every tap, so turning it off closes
        // voting on the next request rather than on the next deploy. One cheap
        // existence check, ahead of anything that builds a snapshot.
        var open = await _db.PartyAlbumLinks.AsNoTracking().AnyAsync(
            x => x.Id == linkId && x.AlbumId == access.MainAlbumId && x.Enabled && x.GameEnabled,
            cancellationToken);
        if (!open) return PartyGameVoteResult.Fail(PartyGameVoteError.NotFound);

        var session = await _db.PartyGameSessions.AsNoTracking()
            .FirstOrDefaultAsync(x => x.PartyAlbumLinkId == linkId, cancellationToken);

        // The snapshot a refusal carries is built ON the refusal path, not
        // before the checks: a successful tap would otherwise pay for two of
        // them, and voting is exactly where a party's load arrives.
        async Task<PartyGameVoteResult> RefuseAsync(PartyGameVoteError error) =>
            PartyGameVoteResult.Fail(error,
                await GetPublicSnapshotAsync(access, participantId, false, cancellationToken));

        // No identity, no vote — and nothing written on the way to saying so.
        // The caller resolved this against THIS link and did not create it; a
        // cookie the server never issued resolves to nothing and stops here,
        // before any row of any kind is touched.
        if (participantId is not Guid voter) return await RefuseAsync(PartyGameVoteError.NotJoined);

        // These reads are a FAST PATH, not the authority. They answer the
        // ordinary refusals without opening a transaction; the boundary itself
        // is decided below, by the database.
        if (session is null || session.Phase != PartyGamePhases.VotingOpen
            || session.CurrentRoundId is not Guid activeRound)
            return await RefuseAsync(PartyGameVoteError.VotingClosed);
        if (roundId is null || roundId != activeRound)
            return await RefuseAsync(PartyGameVoteError.StaleRound);

        var challenge = await CurrentChallengeAsync(session, cancellationToken);
        if (!PartyChallengeVotingModes.CollectsVotes(challenge?.VotingMode))
            return await RefuseAsync(PartyGameVoteError.VotingClosed);

        var accepted = await TryRecordAsync(session.Id, activeRound, voter, value!, cancellationToken);
        if (!accepted) return await RefuseAsync(PartyGameVoteError.VotingClosed);

        // A vote is worth a line — voting is where a party's load is — but the
        // line carries the round and nothing about the person or their answer.
        _logger.LogInformation(
            "party.game.voted AlbumId={AlbumId} RoundId={RoundId}", access.MainAlbumId, activeRound);

        return PartyGameVoteResult.Ok(
            (await GetPublicSnapshotAsync(access, participantId, false, cancellationToken))!);
    }

    // --- The plan ----------------------------------------------------------

    public async Task<PartyGameCommandResult> PlanAsync(
        Guid ownerUserId, Guid albumId, PartyGamePlanRequest request,
        CancellationToken cancellationToken = default)
    {
        var link = await ActiveLinkAsync(ownerUserId, albumId, cancellationToken);
        if (link is null) return PartyGameCommandResult.Fail(PartyGameCommandError.NotFound);
        if (!link.GameEnabled) return PartyGameCommandResult.Fail(PartyGameCommandError.GameDisabled);
        if (!PartyGamePlanActions.IsKnown(request.Action) || request.ChallengeId is not Guid challengeId
            || request.ExpectedVersion is not int expectedVersion)
            return PartyGameCommandResult.Fail(PartyGameCommandError.UnknownCommand);

        var session = await _db.PartyGameSessions
            .FirstOrDefaultAsync(x => x.PartyAlbumLinkId == link.Id, cancellationToken);
        var currentVersion = session?.Version ?? 0;

        async Task<PartyGameCommandResult> RefuseAsync(PartyGameCommandError error)
        {
            _db.ChangeTracker.Clear();
            Refused(albumId, request.Action, session?.Phase ?? PartyGamePhases.Lobby, error);
            var reread = await _db.PartyGameSessions.AsNoTracking()
                .FirstOrDefaultAsync(x => x.PartyAlbumLinkId == link.Id, cancellationToken);
            return PartyGameCommandResult.Fail(error,
                await BuildOwnerSnapshotAsync(albumId, reread, cancellationToken,
                    await RoomAsync(link, cancellationToken), link));
        }

        if (expectedVersion != currentVersion)
            return await RefuseAsync(PartyGameCommandError.VersionConflict);

        var target = await _db.PartyChallenges.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == challengeId && x.AlbumId == albumId, cancellationToken);
        if (target is null) return await RefuseAsync(PartyGameCommandError.InvalidPlan);

        // WHAT MAY BE PLANNED. Everything the room has already seen, and
        // whatever is on the screen right now, is history and the present — the
        // plan describes the FUTURE, and refusing out loud is better than
        // silently reordering around a round that cannot move.
        var playedIds = session is null
            ? new List<Guid>()
            : await _db.PartyGameRounds.AsNoTracking()
                .Where(x => x.PartyGameSessionId == session.Id)
                .Select(x => x.PartyChallengeId).ToListAsync(cancellationToken);
        if (playedIds.Contains(challengeId)) return await RefuseAsync(PartyGameCommandError.InvalidPlan);

        var now = Now;
        var created = session is null;
        if (session is null)
        {
            // Planning before the first `start` is an ordinary thing to do, and
            // it is a COMMAND, so it may create the row a read never would. The
            // next command quotes the version this one spends, exactly as it
            // would have quoted 0.
            session = NewSession(albumId, link.Id, now);
            _db.PartyGameSessions.Add(session);
        }

        var changed = request.Action switch
        {
            PartyGamePlanActions.Exclude => await SetExclusionAsync(session.Id, challengeId, true, now, cancellationToken),
            PartyGamePlanActions.Include => await SetExclusionAsync(session.Id, challengeId, false, now, cancellationToken),
            _ => await MoveAsync(albumId, session, playedIds, challengeId, request.Position ?? 0, now, cancellationToken),
        };
        if (changed is null) return await RefuseAsync(PartyGameCommandError.InvalidPlan);

        // A NO-OP WRITES NOTHING AT ALL. An exclusion that was already set, or a
        // move to the position an activity already holds, is not a decision: it
        // spends no version — charging one would invalidate the host's other
        // phone for nothing — and, before the first `start`, it does not
        // materialise the session either. A plan edit is a command and may
        // create that row; a plan edit that changes nothing is a read wearing a
        // POST, and a read never writes.
        if (!changed.Value)
        {
            _db.ChangeTracker.Clear();
            var unchanged = created
                ? null
                : await _db.PartyGameSessions.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.PartyAlbumLinkId == link.Id, cancellationToken);
            return PartyGameCommandResult.Ok((await BuildOwnerSnapshotAsync(
                albumId, unchanged, cancellationToken,
                await RoomAsync(link, cancellationToken), link))!);
        }

        session.Version = currentVersion + 1;
        session.UpdatedAt = now;

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is DbUpdateConcurrencyException or DbUpdateException)
        {
            return await RefuseAsync(PartyGameCommandError.VersionConflict);
        }

        _logger.LogInformation(
            "party.game.planned AlbumId={AlbumId} Action={Action} Version={Version}",
            albumId, request.Action, session.Version);

        _db.ChangeTracker.Clear();
        var latest = await _db.PartyGameSessions.AsNoTracking()
            .FirstAsync(x => x.Id == session.Id, cancellationToken);
        return PartyGameCommandResult.Ok((await BuildOwnerSnapshotAsync(
            albumId, latest, cancellationToken, await RoomAsync(link, cancellationToken), link))!);
    }

    /// <summary>
    /// Adds or removes one exclusion. Null is never returned — an exclusion is
    /// always a legal thing to ask for on a plannable activity — and false means
    /// it was already in the state asked for.
    /// </summary>
    private async Task<bool?> SetExclusionAsync(
        Guid sessionId, Guid challengeId, bool excluded, DateTime now, CancellationToken ct)
    {
        var existing = await _db.PartyGameExclusions
            .FirstOrDefaultAsync(x => x.PartyGameSessionId == sessionId
                && x.PartyChallengeId == challengeId, ct);
        if (excluded && existing is null)
        {
            _db.PartyGameExclusions.Add(new PartyGameExclusion
            {
                Id = Guid.NewGuid(),
                PartyGameSessionId = sessionId,
                PartyChallengeId = challengeId,
                CreatedAt = now,
            });
            return true;
        }
        if (!excluded && existing is not null)
        {
            _db.PartyGameExclusions.Remove(existing);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Moves one activity to a position among the REMAINING ones.
    ///
    /// <para>It renumbers the remaining activities into the <c>SortOrder</c>
    /// slots they already occupy, so every played activity keeps the number it
    /// had: the deck order is one sequence, and the past is a prefix of it that
    /// planning may not rewrite. The target position is clamped rather than
    /// refused — a control room asking for "last" by sending a large number is
    /// asking for something the host can see, not making a mistake.</para>
    /// </summary>
    private async Task<bool?> MoveAsync(
        Guid albumId, PartyGameSession session, List<Guid> playedIds,
        Guid challengeId, int position, DateTime now, CancellationToken ct)
    {
        var currentId = session.CurrentRoundId is Guid roundId
            ? await _db.PartyGameRounds.AsNoTracking().Where(r => r.Id == roundId)
                .Select(r => (Guid?)r.PartyChallengeId).FirstOrDefaultAsync(ct)
            : null;
        if (challengeId == currentId) return null;

        var deck = await _db.PartyChallenges
            .Where(x => x.AlbumId == albumId)
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
            .ToListAsync(ct);
        var fixedIds = playedIds.ToHashSet();
        if (currentId is Guid live) fixedIds.Add(live);

        var remaining = deck.Where(x => !fixedIds.Contains(x.Id)).ToList();
        var from = remaining.FindIndex(x => x.Id == challengeId);
        if (from < 0) return null;

        var to = Math.Clamp(position, 0, remaining.Count - 1);
        if (to == from) return false;

        var moved = remaining[from];
        remaining.RemoveAt(from);
        remaining.Insert(to, moved);

        // The slots the remaining activities held between them, reused in order.
        // Nothing a played round points at changes value.
        var slots = deck.Where(x => !fixedIds.Contains(x.Id))
            .Select(x => x.SortOrder).OrderBy(x => x).ToList();
        for (var i = 0; i < remaining.Count; i++)
        {
            if (remaining[i].SortOrder == slots[i]) continue;
            remaining[i].SortOrder = slots[i];
            remaining[i].UpdatedAt = now;
        }
        return true;
    }

    // --- Pre-game preferences ----------------------------------------------

    public async Task<PartyGamePreferencesDto?> GetPreferencesAsync(
        PartyAccess access, Guid? participantId, CancellationToken cancellationToken = default)
    {
        var link = await _db.PartyAlbumLinks.AsNoTracking()
            .Where(x => x.Id == access.PartyAlbumLinkId && x.AlbumId == access.MainAlbumId && x.Enabled)
            .Select(x => new { x.GameEnabled, x.PriorityVotingEnabled, x.VotesPerGuest })
            .FirstOrDefaultAsync(cancellationToken);
        if (link is null || !link.GameEnabled) return null;
        var phase = await _db.PartyGameSessions.AsNoTracking()
            .Where(x => x.PartyAlbumLinkId == access.PartyAlbumLinkId)
            .Select(x => x.Phase).FirstOrDefaultAsync(cancellationToken);
        return await BuildPreferencesAsync(
            access, participantId, link.PriorityVotingEnabled, link.VotesPerGuest,
            phase, cancellationToken);
    }

    public async Task<PartyGamePreferenceResult> SetPreferenceAsync(
        PartyAccess access, Guid? participantId, Guid? challengeId, bool selected,
        CancellationToken cancellationToken = default)
    {
        var link = await _db.PartyAlbumLinks.AsNoTracking()
            .Where(x => x.Id == access.PartyAlbumLinkId && x.AlbumId == access.MainAlbumId && x.Enabled)
            .Select(x => new { x.GameEnabled, x.PriorityVotingEnabled, x.VotesPerGuest })
            .FirstOrDefaultAsync(cancellationToken);
        if (link is null || !PartyGamePreferencePolicy.IsOffered(
                access.Capabilities.Games, link.GameEnabled, link.PriorityVotingEnabled))
            return PartyGamePreferenceResult.Fail(PartyGamePreferenceError.NotFound);

        async Task<PartyGamePreferencesDto?> CurrentAsync() =>
            await GetPreferencesAsync(access, participantId, cancellationToken);

        // No identity, no preference — the same rule the live vote obeys, and
        // for the same reason: a cookie the server never issued is a claim, and
        // a claim must not become a say in what the party plays.
        if (participantId is not Guid guest)
            return PartyGamePreferenceResult.Fail(
                PartyGamePreferenceError.NotJoined, await CurrentAsync());

        var phase = await _db.PartyGameSessions.AsNoTracking()
            .Where(x => x.PartyAlbumLinkId == access.PartyAlbumLinkId)
            .Select(x => x.Phase).FirstOrDefaultAsync(cancellationToken);
        if (!PartyGamePreferencePolicy.IsOpen(
                access.Capabilities.Games, link.GameEnabled, link.PriorityVotingEnabled, phase))
            return PartyGamePreferenceResult.Fail(
                PartyGamePreferenceError.Closed, await CurrentAsync());

        if (challengeId is not Guid target || !await _db.PartyChallenges.AsNoTracking()
                .AnyAsync(x => x.Id == target && x.AlbumId == access.MainAlbumId && x.IsEnabled,
                    cancellationToken))
            return PartyGamePreferenceResult.Fail(
                PartyGamePreferenceError.UnknownChallenge, await CurrentAsync());

        var linkId = access.PartyAlbumLinkId;
        var owned = _db.Database.CurrentTransaction is null;
        var tx = owned ? await _db.Database.BeginTransactionAsync(cancellationToken) : null;
        var refused = false;
        try
        {
            var existing = await _db.PartyChallengeVotes.FirstOrDefaultAsync(
                x => x.PartyAlbumLinkId == linkId && x.PartyParticipantId == guest
                    && x.PartyChallengeId == target, cancellationToken);
            if (selected && existing is null)
            {
                // THE BUDGET IS A CONDITIONAL UPDATE, not a count-then-insert:
                // two phones spending a guest's last preference both read "one
                // free" and only one can win a row lock.
                if (!await _participants.TryClaimChallengeVoteAsync(
                        guest, link.VotesPerGuest, cancellationToken))
                {
                    refused = true;
                }
                else
                {
                    _db.PartyChallengeVotes.Add(new PartyChallengeVote
                    {
                        Id = Guid.NewGuid(),
                        PartyAlbumLinkId = linkId,
                        PartyParticipantId = guest,
                        PartyChallengeId = target,
                        CreatedAt = Now,
                    });
                    await _db.SaveChangesAsync(cancellationToken);
                }
            }
            else if (!selected && existing is not null)
            {
                _db.PartyChallengeVotes.Remove(existing);
                await _db.SaveChangesAsync(cancellationToken);
                await _participants.ReleaseChallengeVoteAsync(guest, cancellationToken);
            }

            if (refused && owned) await tx!.RollbackAsync(cancellationToken);
            else if (owned) await tx!.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException) when (owned)
        {
            // A concurrent identical tap won the unique (link, participant,
            // challenge) index. The guest's preference is recorded either way,
            // so the counter claim rolls back with it and the surface below
            // reports what the rows actually say.
            await tx!.RollbackAsync(cancellationToken);
            _db.ChangeTracker.Clear();
        }
        finally
        {
            if (tx is not null) await tx.DisposeAsync();
        }

        var current = await CurrentAsync();
        return refused
            ? PartyGamePreferenceResult.Fail(PartyGamePreferenceError.LimitReached, current)
            : PartyGamePreferenceResult.Ok(current!);
    }

    /// <summary>
    /// The preference surface for one caller. Null when the host never asked the
    /// room — absence IS the answer, exactly as it is for every other Party
    /// capability, so no client ever renders a disabled preference list.
    /// </summary>
    private async Task<PartyGamePreferencesDto?> BuildPreferencesAsync(
        PartyAccess access, Guid? participantId, bool priorityVotingEnabled, int votesPerGuest,
        string? sessionPhase, CancellationToken ct)
    {
        if (!PartyGamePreferencePolicy.IsOffered(
                access.Capabilities.Games, gameEnabled: true, priorityVotingEnabled))
            return null;

        var linkId = access.PartyAlbumLinkId;
        var mine = participantId is Guid guest
            ? (await _db.PartyChallengeVotes.AsNoTracking()
                .Where(x => x.PartyAlbumLinkId == linkId && x.PartyParticipantId == guest)
                .Select(x => x.PartyChallengeId).ToListAsync(ct)).ToHashSet()
            : [];

        var rows = await _db.PartyChallenges.AsNoTracking()
            .Where(x => x.AlbumId == access.MainAlbumId && x.IsEnabled)
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
            .Select(x => new { x.Id, x.Title, x.Body, x.MediaFileItemId })
            .ToListAsync(ct);

        // A picture is offered only while its file still qualifies as a Party
        // reference, on the same terms as everywhere else: an activity whose
        // photograph went to Trash is listed without one.
        var eligible = await PartyMediaReference.EligibleAmongAsync(_db, access.OwnerUserId,
            rows.Where(x => x.MediaFileItemId is not null)
                .Select(x => x.MediaFileItemId!.Value).ToList(), ct);

        var used = mine.Count;
        return new PartyGamePreferencesDto(
            PartyGamePreferencePolicy.IsOpen(
                access.Capabilities.Games, gameEnabled: true, priorityVotingEnabled, sessionPhase),
            votesPerGuest, used, Math.Max(0, votesPerGuest - used),
            rows.Select(x => new PartyGamePreferenceItemDto(
                x.Id, x.Title, x.Body,
                // Token-less sentinel; the endpoint rewrites it against the
                // caller's own token, exactly as the activity on stage does.
                x.MediaFileItemId is Guid media && eligible.Contains(media)
                    ? $"/api/party/challenge-media/{x.Id}" : null,
                mine.Contains(x.Id))).ToList());
    }

    /// <summary>
    /// Records one answer, or reports that the host closed voting first.
    ///
    /// <para>THE BOUNDARY IS THE SESSION ROW. The transaction's FIRST statement
    /// is a conditional update of that row whose WHERE clause is the whole
    /// authority: "this session is still in voting_open, on this round". The
    /// statement changes nothing — it assigns <c>UpdatedAt</c> to itself — and
    /// exists to take the row's write lock and to have its predicate evaluated
    /// under it. The vote is written inside the same transaction, so a vote and
    /// a close can only be ordered, never interleaved.</para>
    ///
    /// <para>That is what makes the dangerous ordering impossible. If
    /// <c>close_voting</c> is committing, this statement BLOCKS on its lock;
    /// when the close commits, the predicate is re-evaluated against the row the
    /// close left behind, matches nothing, and the vote is refused with no row
    /// written. If this commits first, the close's own update then finds the
    /// session exactly as it expected. Checking the phase again after writing
    /// would not do: by then the row exists.</para>
    ///
    /// <para>It deliberately does NOT touch <c>Version</c>. That is the owner's
    /// optimistic-concurrency token; bumping it here would make every vote
    /// during a round reject the host's next command.</para>
    ///
    /// <para>The transaction opens with a WRITE, before any read, so it never
    /// upgrades a shared lock to an exclusive one — the shape SQLite refuses to
    /// wait on, and the one that would turn a busy database into an error
    /// instead of a queue.</para>
    ///
    /// <para>It PARTICIPATES in a caller's transaction when there is one, rather
    /// than demanding its own — the same shape as the participant quota claim,
    /// so a vote can be one step of a larger unit of work. A caller that owns
    /// the transaction owns the recovery with it: the duplicate-insert race is
    /// swallowed only on the path that can roll back to a clean point.</para>
    /// </summary>
    private async Task<bool> TryRecordAsync(
        Guid sessionId, Guid roundId, Guid participantId, string value, CancellationToken ct)
    {
        var owned = _db.Database.CurrentTransaction is null;
        var tx = owned ? await _db.Database.BeginTransactionAsync(ct) : null;
        try
        {
            return await RecordInsideTransactionAsync(
                sessionId, roundId, participantId, value, owned, tx, ct);
        }
        finally
        {
            if (tx is not null) await tx.DisposeAsync();
        }
    }

    private async Task<bool> RecordInsideTransactionAsync(
        Guid sessionId, Guid roundId, Guid participantId, string value,
        bool owned, IDbContextTransaction? tx, CancellationToken ct)
    {
        var open = await _db.PartyGameSessions
            .Where(x => x.Id == sessionId
                && x.Phase == PartyGamePhases.VotingOpen
                && x.CurrentRoundId == roundId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.UpdatedAt, x => x.UpdatedAt), ct);
        if (open == 0)
        {
            if (owned) await tx!.RollbackAsync(ct);
            return false;
        }

        var now = Now;
        var existing = await _db.PartyGameVotes.FirstOrDefaultAsync(
            x => x.PartyGameRoundId == roundId && x.PartyParticipantId == participantId, ct);
        if (existing is null)
        {
            _db.PartyGameVotes.Add(new PartyGameVote
            {
                Id = Guid.NewGuid(),
                PartyGameSessionId = sessionId,
                PartyGameRoundId = roundId,
                PartyParticipantId = participantId,
                Value = value,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
        else if (existing.Value != value)
        {
            // Changing your mind while voting is open replaces the answer; there
            // is no history of what somebody thought thirty seconds ago.
            existing.Value = value;
            existing.UpdatedAt = now;
        }
        else
        {
            // The same tap twice. Nothing to write, and nothing to complain
            // about: the guest already said this.
            if (owned) await tx!.CommitAsync(ct);
            return true;
        }

        try
        {
            await _db.SaveChangesAsync(ct);
            if (owned) await tx!.CommitAsync(ct);
        }
        catch (DbUpdateException) when (owned)
        {
            // Two taps arrived together and the unique index elected one. The
            // guest's answer is recorded either way, so this is not an error —
            // roll back and let the caller report what the row actually says.
            // Only swallowed on the path that owns the transaction: rolling back
            // somebody else's unit of work would be a worse answer than the
            // exception.
            await tx!.RollbackAsync(ct);
            _db.ChangeTracker.Clear();
        }
        return true;
    }

    /// <summary>
    /// Plays the same party again: discards what the finished match wrote and
    /// leaves the session in its own lobby.
    ///
    /// <para>THE SESSION ROW SURVIVES, and that is the whole design. Deleting it
    /// and letting the next <c>start</c> create another would reset the version
    /// to 0, and a command the host's other tab wrote during the previous game
    /// would become quotable again — the exact thing the concurrency token
    /// exists to prevent. Keeping the row keeps <c>Version</c> monotonic across
    /// the restart, so version 27 is spent for ever.</para>
    ///
    /// <para>THE BOUNDARY IS THE SESSION ROW, the same authority the vote path
    /// uses. The transaction's first statement is a conditional update whose
    /// WHERE clause is the whole check — "still finished, still at the version
    /// this caller quoted" — and it takes the row's write lock before a single
    /// round is deleted. Two restarts racing on one version therefore cannot
    /// both delete: the loser blocks, re-evaluates against the row the winner
    /// left behind, matches nothing, and returns false having written nothing.
    /// Deleting first and checking afterwards would mean the loser had already
    /// destroyed the winner's fresh lobby.</para>
    ///
    /// <para>Votes go before rounds because a vote names the round it answers.
    /// Nothing outside the game is touched: the party link and its token, the
    /// participants, their photographs, greetings and prints, the display
    /// heartbeat and the deck of activities all belong to the party rather than
    /// to the match, and a host restarting a game is not asking to lose any of
    /// them.</para>
    /// </summary>
    private async Task<bool> RestartAsync(
        PartyGameSession session, int expectedVersion, DateTime now, CancellationToken ct)
    {
        var owned = _db.Database.CurrentTransaction is null;
        var tx = owned ? await _db.Database.BeginTransactionAsync(ct) : null;
        try
        {
            var claimed = await _db.PartyGameSessions
                .Where(x => x.Id == session.Id
                    && x.Version == expectedVersion
                    && x.Phase == PartyGamePhases.Finished)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.UpdatedAt, x => x.UpdatedAt), ct);
            if (claimed == 0)
            {
                if (owned) await tx!.RollbackAsync(ct);
                return false;
            }

            await _db.PartyGameVotes.Where(x => x.PartyGameSessionId == session.Id)
                .ExecuteDeleteAsync(ct);
            await _db.PartyGameRounds.Where(x => x.PartyGameSessionId == session.Id)
                .ExecuteDeleteAsync(ct);
            // The host's exclusions go too: they said which activities THIS
            // match would skip, and there is no longer a this match. The guests'
            // PREFERENCES deliberately stay — they are the party's, cast before
            // the evening began, and a replay starts from what the room already
            // said rather than asking everybody again.
            await _db.PartyGameExclusions.Where(x => x.PartyGameSessionId == session.Id)
                .ExecuteDeleteAsync(ct);

            // The lobby this session started in, at the next version. The two
            // match timestamps are cleared because they bounded a game that no
            // longer exists; CreatedAt is not, because the session was created
            // once and the party has not changed.
            session.Phase = PartyGamePhases.Lobby;
            session.Status = PartyGameStatuses.Lobby;
            session.CurrentRoundId = null;
            session.CurrentRoundNumber = 0;
            session.StartedAt = null;
            session.FinishedAt = null;
            session.Version = expectedVersion + 1;
            session.UpdatedAt = now;
            await _db.SaveChangesAsync(ct);
            if (owned) await tx!.CommitAsync(ct);
            return true;
        }
        catch (Exception ex) when (owned && ex is DbUpdateConcurrencyException or DbUpdateException)
        {
            // The concurrency token disagreed after the predicate matched. Only
            // recoverable on the path that owns the transaction; rolling back
            // somebody else's unit of work would be a worse answer than the
            // exception.
            await tx!.RollbackAsync(ct);
            return false;
        }
        finally
        {
            if (tx is not null) await tx.DisposeAsync();
        }
    }

    /// <summary>
    /// How much of the room has answered, and — only when the caller is allowed
    /// to know — what it answered.
    /// </summary>
    private async Task<PartyGameVotingDto> VotingAsync(
        PartyGameSession session, Guid roundId, bool includeResult, CancellationToken ct)
    {
        var tallies = await _db.PartyGameVotes.AsNoTracking()
            .Where(x => x.PartyGameRoundId == roundId)
            .GroupBy(x => x.Value)
            .Select(g => new { Value = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        var yes = tallies.FirstOrDefault(x => x.Value == PartyGameVoteValues.Yes)?.Count ?? 0;
        var no = tallies.FirstOrDefault(x => x.Value == PartyGameVoteValues.No)?.Count ?? 0;
        var received = yes + no;

        var since = Now.AddSeconds(-PartyGamePresence.WindowSeconds);
        var present = await _db.PartyParticipants.AsNoTracking()
            .CountAsync(x => x.PartyAlbumLinkId == session.PartyAlbumLinkId
                && x.RetiredAt == null && x.LastSeenAt >= since, ct);

        // Whoever voted is in the room, whatever their last heartbeat says.
        var eligible = Math.Max(present, received);

        return includeResult
            ? new PartyGameVotingDto(received, eligible, yes, no, yes > no)
            : new PartyGameVotingDto(received, eligible);
    }

    /// <summary>
    /// A refused command, on the same structured line as an accepted one.
    ///
    /// Refusals are the interesting half of an evening's telemetry: a burst of
    /// version conflicts is two owner surfaces fighting, and an illegal
    /// transition is a client that has drifted from the machine. The line names
    /// the album, never a guest — nothing here identifies a person.
    /// </summary>
    private void Refused(Guid albumId, string? command, string phase, PartyGameCommandError error) =>
        _logger.LogInformation(
            "party.game.refused AlbumId={AlbumId} Command={Command} Phase={Phase} Reason={Reason}",
            albumId, command, phase, error);

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

    /// <summary>
    /// Activities the host has taken out of THIS match. Empty before a session
    /// exists, which is the truth rather than a shortcut: an exclusion belongs to
    /// a match, and there is no match yet.
    /// </summary>
    private async Task<List<Guid>> ExcludedIdsAsync(PartyGameSession? session, CancellationToken ct) =>
        session is null
            ? []
            : await _db.PartyGameExclusions.AsNoTracking()
                .Where(x => x.PartyGameSessionId == session.Id)
                .Select(x => x.PartyChallengeId).ToListAsync(ct);

    /// The deck is played in the owner's own order, minus whatever the host has
    /// set aside for tonight. Vote-driven selection belongs to the retired
    /// interval-based hold, where the room chose what happened next; here the
    /// guests' preferences inform the host and the HOST decides — which is the
    /// whole difference between an advisory preference and a ballot.
    private Task<PartyChallenge?> NextChallengeAsync(
        Guid albumId, List<Guid> playedIds, List<Guid> excludedIds, CancellationToken ct) =>
        _db.PartyChallenges.AsNoTracking()
            .Where(x => x.AlbumId == albumId && x.IsEnabled
                && !playedIds.Contains(x.Id) && !excludedIds.Contains(x.Id))
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
            .FirstOrDefaultAsync(ct);

    private async Task<PartyGameSnapshotDto?> BuildOwnerSnapshotAsync(
        Guid albumId, PartyGameSession? session, CancellationToken ct,
        PartyGameRoomDto? room = null, PartyAlbumLink? link = null)
    {
        var total = await EnabledChallengeCountAsync(albumId, ct);
        var playedIds = session is null
            ? new List<Guid>()
            : await _db.PartyGameRounds.AsNoTracking()
                .Where(x => x.PartyGameSessionId == session.Id)
                .Select(x => x.PartyChallengeId).ToListAsync(ct);
        var excludedIds = await ExcludedIdsAsync(session, ct);
        var next = await NextChallengeAsync(albumId, playedIds, excludedIds, ct);
        var plan = await PlanAsync(albumId, session, playedIds, excludedIds, link, ct);
        var priorityVoting = link?.PriorityVotingEnabled ?? false;
        // `gamesPermitted` is true by construction on this path: the owner route
        // stands behind RequirePartyGames and this snapshot is only ever built
        // for the caller's own active link. The guest path asks the resolved
        // capability instead, which is where the phase fold lives.
        var preferencesOpen = PartyGamePreferencePolicy.IsOpen(
            gamesPermitted: true, gameEnabled: link?.GameEnabled ?? false,
            priorityVotingEnabled: priorityVoting, sessionPhase: session?.Phase);

        if (session is null)
            return new PartyGameSnapshotDto(albumId, null, PartyGameStatuses.Lobby, PartyGamePhases.Lobby,
                0, 0, total, 0, null, null, null, null, null, Challenge(next),
                PartyGameStateMachine.LegalCommands(PartyGamePhases.Lobby, next is not null),
                null, plan, priorityVoting, preferencesOpen,
                room?.GuestsPresent ?? 0, room?.DisplaySeenSecondsAgo,
                room?.TvUrl, room?.GuestUrl);

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

        // The host may see the result from the moment voting closes, because the
        // host is the one who decides when to reveal it. Nothing else may.
        PartyGameVotingDto? voting = null;
        if (session.CurrentRoundId is Guid votingRound && current is not null
            && PartyChallengeVotingModes.CollectsVotes(current.VotingMode)
            && PartyGamePhases.ShowsChallenge(session.Phase))
        {
            voting = await VotingAsync(session, votingRound,
                includeResult: session.Phase is PartyGamePhases.VotingClosed or PartyGamePhases.Result,
                ct);
        }

        return new PartyGameSnapshotDto(albumId, session.Id, session.Status, session.Phase,
            session.Version, session.CurrentRoundNumber, total, played,
            session.StartedAt, session.FinishedAt, phaseStartedAt, phaseEndsAt,
            current, Challenge(next),
            PartyGameStateMachine.LegalCommands(
                session.Phase, next is not null,
                current is null || PartyChallengeVotingModes.CollectsVotes(current.VotingMode)),
            voting, plan, priorityVoting, preferencesOpen,
            room?.GuestsPresent ?? 0, room?.DisplaySeenSecondsAgo,
            room?.TvUrl, room?.GuestUrl);
    }

    /// <summary>
    /// The whole deck as the host plans it: play order, what the room asked for,
    /// and which of the three states each activity is in.
    ///
    /// <para>Ordered by the deck's own <c>SortOrder</c> rather than by what has
    /// happened, so the list a host reorders is the list the game will walk. The
    /// preference counts come from <see cref="Domain.PartyChallengeVote"/> — the
    /// pre-game vote, on the party link — and are reported for EVERY entry
    /// including excluded and played ones, because they record what the room
    /// wanted and that does not stop being true.</para>
    /// </summary>
    private async Task<IReadOnlyList<PartyGamePlanEntryDto>> PlanAsync(
        Guid albumId, PartyGameSession? session, List<Guid> playedIds, List<Guid> excludedIds,
        PartyAlbumLink? link, CancellationToken ct)
    {
        var rows = await _db.PartyChallenges.AsNoTracking()
            .Where(x => x.AlbumId == albumId)
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
            .Select(x => new { x.Id, x.Title, x.MediaFileItemId, x.IsEnabled })
            .ToListAsync(ct);
        if (rows.Count == 0) return [];

        var preferences = link is null
            ? new Dictionary<Guid, int>()
            : await _db.PartyChallengeVotes.AsNoTracking()
                .Where(v => v.PartyAlbumLinkId == link.Id)
                .GroupBy(v => v.PartyChallengeId)
                .Select(g => new { Id = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.Id, x => x.Count, ct);

        var currentId = session?.CurrentRoundId is Guid roundId
            ? await _db.PartyGameRounds.AsNoTracking().Where(r => r.Id == roundId)
                .Select(r => (Guid?)r.PartyChallengeId).FirstOrDefaultAsync(ct)
            : null;

        var played = playedIds.ToHashSet();
        var excluded = excludedIds.ToHashSet();
        var entries = new List<PartyGamePlanEntryDto>(rows.Count);
        var position = 0;
        foreach (var row in rows)
        {
            var state = row.Id == currentId ? PartyGamePlanStates.Current
                : played.Contains(row.Id) ? PartyGamePlanStates.Played
                : PartyGamePlanStates.Remaining;
            // Only a remaining activity that would actually be played has a
            // place in the queue. An excluded or switched-off one is remaining —
            // the host can still bring it back — but numbering it would promise
            // a turn the game will never take.
            int? at = state == PartyGamePlanStates.Remaining
                && row.IsEnabled && !excluded.Contains(row.Id)
                ? ++position
                : null;
            entries.Add(new PartyGamePlanEntryDto(
                row.Id, row.Title,
                row.MediaFileItemId is Guid media ? $"/api/files/{media}/thumbnail?size=small" : null,
                state, at, row.IsEnabled, excluded.Contains(row.Id),
                preferences.GetValueOrDefault(row.Id)));
        }
        return entries;
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
