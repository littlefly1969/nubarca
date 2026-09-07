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
    private readonly ILogger<PartyGameService> _logger;

    public PartyGameService(
        AppDbContext db, TimeProvider clock, IPartyLinkService links,
        ILogger<PartyGameService> logger)
    {
        _db = db;
        _clock = clock;
        _links = links;
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
            await RoomAsync(link, cancellationToken));
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
                    await RoomAsync(link, cancellationToken)));
        }

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
            Refused(albumId, command, phase, error);
            return PartyGameCommandResult.Fail(error,
                await BuildOwnerSnapshotAsync(albumId, session, cancellationToken,
                    await RoomAsync(link, cancellationToken)));
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
            Refused(albumId, command, phase, PartyGameCommandError.VersionConflict);
            var current = await _db.PartyGameSessions.AsNoTracking()
                .FirstOrDefaultAsync(x => x.PartyAlbumLinkId == link.Id, cancellationToken);
            return PartyGameCommandResult.Fail(PartyGameCommandError.VersionConflict,
                await BuildOwnerSnapshotAsync(albumId, current, cancellationToken,
                    await RoomAsync(link, cancellationToken)));
        }

        _logger.LogInformation(
            "party.game.command AlbumId={AlbumId} Command={Command} Phase={Phase} Round={Round} Version={Version}",
            albumId, command, session.Phase, session.CurrentRoundNumber, session.Version);

        _db.ChangeTracker.Clear();
        var snapshot = await BuildOwnerSnapshotAsync(albumId,
            await _db.PartyGameSessions.AsNoTracking()
                .FirstAsync(x => x.Id == session.Id, cancellationToken), cancellationToken,
            await RoomAsync(link, cancellationToken));
        return PartyGameCommandResult.Ok(snapshot!);
    }

    public async Task<PartyGamePublicSnapshotDto?> GetPublicSnapshotAsync(
        PartyAccess access, Guid? participantId = null, bool isDisplay = false,
        CancellationToken cancellationToken = default)
    {
        if (access.PartyAlbumLinkId is not Guid linkId) return null;
        var context = await _db.PartyAlbumLinks.AsNoTracking()
            .Where(x => x.Id == linkId && x.AlbumId == access.AlbumId && x.Enabled && x.GameEnabled)
            .Join(_db.Albums.AsNoTracking(), x => x.AlbumId, a => a.Id, (x, a) => new { a.Name })
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
        var total = await EnabledChallengeCountAsync(access.AlbumId, cancellationToken);
        if (session is null)
            return new PartyGamePublicSnapshotDto(context.Name, PartyGameStatuses.Lobby,
                PartyGamePhases.Lobby, 0, 0, total, null, null);

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
                challenge = new PartyChallengePresentationDto(row.Id, row.Title, row.Body, row.Kind,
                    // Token-less sentinel; the endpoint rewrites it against the
                    // caller's own token, exactly as the guest challenge list does.
                    row.MediaFileItemId is null ? null : $"/api/party/challenge-media/{row.Id}",
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
            voting, myVote);
    }

    public async Task<PartyGameVoteResult> VoteAsync(
        PartyAccess access, Guid? participantId, Guid? roundId, string? value,
        CancellationToken cancellationToken = default)
    {
        if (!PartyGameVoteValues.IsKnown(value))
            return PartyGameVoteResult.Fail(PartyGameVoteError.UnknownValue);
        if (access.PartyAlbumLinkId is not Guid linkId)
            return PartyGameVoteResult.Fail(PartyGameVoteError.NotFound);

        // The game switch is re-read on every tap, so turning it off closes
        // voting on the next request rather than on the next deploy. One cheap
        // existence check, ahead of anything that builds a snapshot.
        var open = await _db.PartyAlbumLinks.AsNoTracking().AnyAsync(
            x => x.Id == linkId && x.AlbumId == access.AlbumId && x.Enabled && x.GameEnabled,
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
            "party.game.voted AlbumId={AlbumId} RoundId={RoundId}", access.AlbumId, activeRound);

        return PartyGameVoteResult.Ok(
            (await GetPublicSnapshotAsync(access, participantId, false, cancellationToken))!);
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
        Guid albumId, PartyGameSession? session, CancellationToken ct,
        PartyGameRoomDto? room = null)
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
                PartyGameStateMachine.LegalCommands(PartyGamePhases.Lobby, next is not null),
                null, room?.GuestsPresent ?? 0, room?.DisplaySeenSecondsAgo,
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
            voting, room?.GuestsPresent ?? 0, room?.DisplaySeenSecondsAgo,
            room?.TvUrl, room?.GuestUrl);
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
