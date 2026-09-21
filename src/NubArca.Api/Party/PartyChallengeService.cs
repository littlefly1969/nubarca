using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Domain;

namespace NubArca.Api.Party;

public sealed class PartyChallengeService : IPartyChallengeService
{
    private readonly AppDbContext _db;
    private readonly IPartyGameService _game;
    private readonly TimeProvider _clock;

    public PartyChallengeService(
        AppDbContext db,
        IPartyGameService game,
        TimeProvider clock)
    {
        _db = db;
        _game = game;
        _clock = clock;
    }

    public async Task<PartyChallengeListDto?> ListOwnerAsync(Guid ownerId, Guid albumId, CancellationToken ct = default)
    {
        if (!await OwnsAsync(ownerId, albumId, ct)) return null;
        var linkId = await ActiveLinkIdAsync(ownerId, albumId, ct);
        var rows = await _db.PartyChallenges.AsNoTracking()
            .Where(x => x.AlbumId == albumId)
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
            .ToListAsync(ct);
        var counts = linkId is Guid lid
            ? await _db.PartyChallengeVotes.AsNoTracking().Where(x => x.PartyAlbumLinkId == lid)
                .GroupBy(x => x.PartyChallengeId)
                .Select(g => new { Id = g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Id, x => x.Count, ct)
            : [];
        // ONE query for every ballot on the page, grouped in memory. The
        // alternative is an options query per activity, and a deck of twenty is
        // exactly the list a host scrolls.
        var ids = rows.Where(x => PartyChallengeVotingModes.UsesOptions(x.VotingMode))
            .Select(x => x.Id).ToList();
        var ballots = ids.Count == 0
            ? []
            : (await _db.PartyChallengeOptions.AsNoTracking()
                .Where(o => ids.Contains(o.PartyChallengeId))
                .OrderBy(o => o.Position).ToListAsync(ct))
                .GroupBy(o => o.PartyChallengeId)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<PartyChallengeOption>)g.ToList());
        return new PartyChallengeListDto(albumId, rows
            .Select(x => OwnerDto(x, counts.GetValueOrDefault(x.Id), ballots.GetValueOrDefault(x.Id)))
            .ToList());
    }

    public async Task<PartyChallengeDto?> CreateAsync(Guid ownerId, Guid albumId, PartyChallengeWriteRequest request, CancellationToken ct = default)
    {
        if (!await OwnsAsync(ownerId, albumId, ct) || !Valid(request)
            || !await MediaAllowedAsync(ownerId, request.MediaFileItemId, ct)) return null;
        var now = Now;
        var order = (await _db.PartyChallenges.Where(x => x.AlbumId == albumId)
            .Select(x => (int?)x.SortOrder).MaxAsync(ct) ?? -1) + 1;
        var row = new PartyChallenge
        {
            Id = Guid.NewGuid(), AlbumId = albumId, Title = request.Title!.Trim(),
            // KIND IS DORMANT METADATA. The composer no longer asks for one —
            // the product's activities are activities, and a category the room
            // never sees was decoration on a form — so a new one is `custom`.
            // The column, the values and the wire field all stay for the
            // adaptive game they were designed for, and an existing activity
            // keeps whatever it was written with.
            Body = request.Body!.Trim(),
            Kind = request.Kind ?? PartyChallengeKinds.Custom,
            MediaFileItemId = request.MediaFileItemId,
            IsEnabled = request.IsEnabled, SortOrder = order, CreatedAt = now, UpdatedAt = now,
            DurationSeconds = request.DurationSeconds,
            VotingMode = request.VotingMode ?? PartyChallengeVotingModes.Binary,
            VoteQuestion = NormalizeVoteQuestion(request.VoteQuestion),
        };
        _db.PartyChallenges.Add(row);
        await ApplyOptionsAsync(row, request, ct);
        await _db.SaveChangesAsync(ct);
        return OwnerDto(row, 0, await OptionsAsync(row.Id, ct));
    }

    public async Task<PartyChallengeDto?> UpdateAsync(Guid ownerId, Guid albumId, Guid challengeId, PartyChallengeWriteRequest request, CancellationToken ct = default)
    {
        if (!Valid(request) || !await MediaAllowedAsync(ownerId, request.MediaFileItemId, ct)) return null;
        var row = await _db.PartyChallenges
            .Where(x => x.Id == challengeId && x.AlbumId == albumId
                && _db.Albums.Any(a => a.Id == albumId && a.OwnerUserId == ownerId))
            .FirstOrDefaultAsync(ct);
        if (row is null) return null;
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        row.Title = request.Title!.Trim();
        row.Body = request.Body!.Trim();
        // An omitted kind KEEPS the one this activity already has, which is what
        // makes the composer's silence preserve history rather than rewrite it.
        row.Kind = request.Kind ?? row.Kind;
        row.MediaFileItemId = request.MediaFileItemId;
        row.IsEnabled = request.IsEnabled;
        row.DurationSeconds = request.DurationSeconds;
        // An omitted mode keeps whatever the activity already had, so a caller
        // that predates the composer cannot silently reset one.
        row.VotingMode = request.VotingMode ?? row.VotingMode;
        row.VoteQuestion = NormalizeVoteQuestion(request.VoteQuestion);
        row.UpdatedAt = Now;
        if (!row.IsEnabled) await ReleaseVotesForChallengeAsync(row.Id, ct);
        await ApplyOptionsAsync(row, request, ct);
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        var linkId = await ActiveLinkIdAsync(ownerId, albumId, ct);
        var count = linkId is Guid lid
            ? await _db.PartyChallengeVotes.CountAsync(v => v.PartyAlbumLinkId == lid && v.PartyChallengeId == row.Id, ct) : 0;
        return OwnerDto(row, count, await OptionsAsync(row.Id, ct));
    }

    public async Task<bool> DeleteAsync(Guid ownerId, Guid albumId, Guid challengeId, CancellationToken ct = default)
    {
        if (!await OwnsAsync(ownerId, albumId, ct)) return false;
        var exists = await _db.PartyChallenges.AnyAsync(x => x.Id == challengeId && x.AlbumId == albumId, ct);
        if (!exists) return false;
        // The retired hold's "currently held, immutable until NEXT" guard is
        // gone with the hold itself: nothing freezes the slideshow on an
        // activity any more, so a stale legacy row must not block a host from
        // deleting one for ever.
        //
        // For the hosted game the rule stands: a round is the record of what a
        // room was shown. Deleting its activity would rewrite the evening, and
        // the round's restricting foreign key would refuse anyway — this turns
        // that into a clean answer instead of a DbUpdateException.
        if (await _db.PartyGameRounds.AnyAsync(x => x.PartyChallengeId == challengeId, ct)) return false;
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        await ReleaseVotesForChallengeAsync(challengeId, ct);
        await _db.PartyChallengeCompletions.Where(x => x.PartyChallengeId == challengeId).ExecuteDeleteAsync(ct);
        // A host's "not tonight" for an activity that no longer exists is not a
        // decision worth keeping — unlike a round, it records no experience — so
        // it goes with the activity rather than blocking its deletion.
        await _db.PartyGameExclusions.Where(x => x.PartyChallengeId == challengeId).ExecuteDeleteAsync(ct);
        await _db.PartyChallenges.Where(x => x.Id == challengeId && x.AlbumId == albumId).ExecuteDeleteAsync(ct);
        await tx.CommitAsync(ct);
        return true;
    }

    public async Task<bool> ReorderAsync(Guid ownerId, Guid albumId, IReadOnlyList<Guid> ids, CancellationToken ct = default)
    {
        if (!await OwnsAsync(ownerId, albumId, ct)) return false;
        var rows = await _db.PartyChallenges.Where(x => x.AlbumId == albumId).ToListAsync(ct);
        if (ids.Count != rows.Count || ids.Distinct().Count() != ids.Count
            || rows.Any(x => !ids.Contains(x.Id))) return false;
        var byId = rows.ToDictionary(x => x.Id);
        for (var i = 0; i < ids.Count; i++) { byId[ids[i]].SortOrder = i; byId[ids[i]].UpdatedAt = Now; }
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<PartyGuestChallengesDto?> ListGuestAsync(PartyAccess access, Guid participantId, CancellationToken ct = default)
    {
        var linkId = access.PartyAlbumLinkId;
        var state = await GuestStateAsync(access, participantId, linkId, ct);
        if (state is null) return null;
        var voted = await _db.PartyChallengeVotes.AsNoTracking()
            .Where(x => x.PartyAlbumLinkId == linkId && x.PartyParticipantId == participantId)
            .Select(x => x.PartyChallengeId).ToListAsync(ct);
        var rows = await _db.PartyChallenges.AsNoTracking()
            .Where(x => x.AlbumId == access.MainAlbumId && x.IsEnabled)
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
            .Select(x => new { x.Id, x.Title, x.Body, x.Kind, x.MediaFileItemId })
            .ToListAsync(ct);
        // A picture is offered only while its file still qualifies: an activity
        // whose picture went to Trash is listed without one, never with a frame
        // that would fail to load.
        var eligible = await PartyMediaReference.EligibleAmongAsync(_db, access.OwnerUserId,
            rows.Where(x => x.MediaFileItemId is not null).Select(x => x.MediaFileItemId!.Value).ToList(), ct);
        var items = rows
            .Select(x => new PartyGuestChallengeDto(x.Id, x.Title, x.Body, x.Kind,
                x.MediaFileItemId is Guid media && eligible.Contains(media)
                    ? $"/api/party/challenge-media/{x.Id}" : null,
                voted.Contains(x.Id)))
            .ToList();
        return new PartyGuestChallengesDto(state.Value.AlbumName, state.Value.Max,
            state.Value.Used, Math.Max(0, state.Value.Max - state.Value.Used), items);
    }

    /// <summary>
    /// The RETIRED guest vote route, kept for printed QR codes and older
    /// clients, and now one thin adapter over the pre-game preference path.
    ///
    /// <para>It writes nothing of its own. Having two write paths onto one table
    /// is how the budget claim, the uniqueness race and the lobby gate would
    /// come to disagree — so this one asks the game service and translates the
    /// answer back into the shape the old endpoint promised.</para>
    ///
    /// <para>Every refusal collapses to null, which the route answers as the
    /// same generic 404 an unknown token gets: a client that predates the
    /// preference surface has no vocabulary for "the match has started".</para>
    /// </summary>
    public async Task<PartyVoteResultDto?> VoteAsync(
        PartyAccess access, Guid participantId, Guid challengeId, bool voted, CancellationToken ct = default)
    {
        var result = await _game.SetPreferenceAsync(access, participantId, challengeId, voted, ct);
        if (result.Error is PartyGamePreferenceError.NotFound
            or PartyGamePreferenceError.NotJoined
            or PartyGamePreferenceError.UnknownChallenge
            or PartyGamePreferenceError.Closed) return null;
        if (result.Preferences is not PartyGamePreferencesDto preferences) return null;
        var selected = preferences.Items.FirstOrDefault(x => x.Id == challengeId)?.Selected ?? false;
        return new PartyVoteResultDto(selected, preferences.VotesUsed, preferences.VotesRemaining);
    }

    // --- THE RETIRED INTERVAL-DRIVEN HOLD -----------------------------------
    //
    // These three endpoints are what the older Party TV slideshow called on
    // every media boundary: the room's votes chose an activity, the slideshow
    // FROZE on it, and the remote's NEXT released it. The Party Game replaced
    // all of it — the host conducts, the server owns the phase, and the guests'
    // pre-game preferences are ADVISORY — so a preference no longer selects
    // anything and nothing interrupts the slideshow.
    //
    // They answer, inertly, rather than being deleted: an already-installed TV
    // APK calls them on every photograph, and a 404 per boundary is a worse
    // answer than "the slideshow continues". Nothing here writes a row, which is
    // the whole of the retirement — `PartyChallengeSession` survives in the
    // schema and simply stops being reached.
    //
    // The activity MEDIA route below is deliberately still real: it serves the
    // owner's own television, which reaches an activity's picture through its
    // album's game rather than through a hold.

    public async Task<PartyPlaybackSnapshotDto?> GetSnapshotAsync(
        Guid ownerId, Guid albumId, CancellationToken ct = default)
        => await OwnsAsync(ownerId, albumId, ct)
            ? new PartyPlaybackSnapshotDto(PartyPlaybackModes.Media, null, null, 0)
            : null;

    public Task<PartyPlaybackSnapshotDto?> OnMediaBoundaryAsync(
        Guid ownerId, Guid albumId, CancellationToken ct = default)
        => GetSnapshotAsync(ownerId, albumId, ct);

    public Task<PartyPlaybackSnapshotDto?> CompleteActiveAsync(
        Guid ownerId, Guid albumId, CancellationToken ct = default)
        => GetSnapshotAsync(ownerId, albumId, ct);

    public async Task<Guid?> GuestMediaFileAsync(PartyAccess access, Guid challengeId, CancellationToken ct = default)
    {
        // The same gate the guest's challenge list stands behind: without a
        // game on this link there is no deck to reach a picture through.
        var gameOn = await _db.PartyAlbumLinks.AsNoTracking()
            .AnyAsync(x => x.Id == access.PartyAlbumLinkId && x.AlbumId == access.MainAlbumId
                && x.Enabled && x.GameEnabled, ct);
        if (!gameOn) return null;
        var mediaId = await _db.PartyChallenges.AsNoTracking()
            .Where(x => x.Id == challengeId && x.AlbumId == access.MainAlbumId && x.IsEnabled)
            .Select(x => x.MediaFileItemId).FirstOrDefaultAsync(ct);
        return mediaId is Guid id && await PartyMediaReference.IsEligibleAsync(_db, access.OwnerUserId, id, ct)
            ? id : null;
    }

    public async Task<Guid?> TvMediaFileAsync(Guid ownerId, Guid albumId, Guid challengeId, CancellationToken ct = default)
    {
        // The owner's television, through the album's running game. Not gated on
        // IsEnabled: a held activity is presented until NEXT even if the host
        // switched it off meanwhile, and its picture goes with it.
        if (await ActiveGameLinkAsync(ownerId, albumId, ct) is null) return null;
        var mediaId = await _db.PartyChallenges.AsNoTracking()
            .Where(x => x.Id == challengeId && x.AlbumId == albumId)
            .Select(x => x.MediaFileItemId).FirstOrDefaultAsync(ct);
        return mediaId is Guid id && await PartyMediaReference.IsEligibleAsync(_db, ownerId, id, ct)
            ? id : null;
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    private Task<bool> OwnsAsync(Guid ownerId, Guid albumId, CancellationToken ct) =>
        _db.Albums.AsNoTracking().AnyAsync(x => x.Id == albumId && x.OwnerUserId == ownerId, ct);

    private async Task<Guid?> ActiveLinkIdAsync(Guid ownerId, Guid albumId, CancellationToken ct) =>
        await _db.PartyAlbumLinks.AsNoTracking()
            .Where(x => x.OwnerUserId == ownerId && x.AlbumId == albumId && x.Enabled && x.RevokedAt == null)
            .OrderByDescending(x => x.CreatedAt).Select(x => (Guid?)x.Id).FirstOrDefaultAsync(ct);

    private Task<PartyAlbumLink?> ActiveGameLinkAsync(Guid ownerId, Guid albumId, CancellationToken ct) =>
        _db.PartyAlbumLinks.Where(x => x.OwnerUserId == ownerId && x.AlbumId == albumId
            && x.Enabled && x.RevokedAt == null && x.GameEnabled)
            .OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(ct);

    private async Task<(string AlbumName, int Max, int Used)?> GuestStateAsync(
        PartyAccess access, Guid participantId, Guid linkId, CancellationToken ct)
    {
        // The SAME availability the preference surface has — the host asked the
        // room, or nobody is choosing anything. A legacy client reaching this
        // route on a party whose host never switched priority voting on gets the
        // generic 404 rather than a list of activities it could never vote for.
        var row = await _db.PartyAlbumLinks.AsNoTracking()
            .Where(x => x.Id == linkId && x.AlbumId == access.MainAlbumId && x.Enabled
                && x.GameEnabled && x.PriorityVotingEnabled)
            .Join(_db.Albums.AsNoTracking(), x => x.AlbumId, a => a.Id,
                (x, a) => new { a.Name, x.VotesPerGuest }).FirstOrDefaultAsync(ct);
        if (row is null || !access.Capabilities.Games) return null;
        var used = await _db.PartyChallengeVotes.AsNoTracking()
            .CountAsync(x => x.PartyAlbumLinkId == linkId && x.PartyParticipantId == participantId, ct);
        return (row.Name, row.VotesPerGuest, used);
    }

    // An activity's picture is a Party REFERENCE, not an album membership: any of
    // the owner's own eligible images, in the album or not, and choosing one
    // files it nowhere. Ownership of the ALBUM is established by each caller.
    private Task<bool> MediaAllowedAsync(Guid ownerId, Guid? mediaId, CancellationToken ct) =>
        mediaId is Guid id ? PartyMediaReference.IsEligibleAsync(_db, ownerId, id, ct) : Task.FromResult(true);

    private static bool Valid(PartyChallengeWriteRequest r) =>
        !string.IsNullOrWhiteSpace(r.Title) && r.Title.Trim().Length <= PartyChallengeLimits.MaxTitleLength
        && !string.IsNullOrWhiteSpace(r.Body) && r.Body.Trim().Length <= PartyChallengeLimits.MaxBodyLength
        // Null means "the composer did not ask", which is now the ordinary case.
        // A value that IS present still has to be one the domain knows, so an
        // older client cannot write a category nothing can read back.
        && (r.Kind is null || PartyChallengeKinds.IsKnown(r.Kind))
        && PartyChallengeLimits.IsValidDuration(r.DurationSeconds)
        // Null means "keep the default", so only a value that is present has to
        // be a mode the runtime can actually run.
        && (r.VotingMode is null || PartyChallengeVotingModes.IsKnown(r.VotingMode))
        && (r.VoteQuestion is null
            || r.VoteQuestion.Trim().Length <= PartyChallengeLimits.MaxVoteQuestionLength)
        && ValidOptions(r);

    /// <summary>
    /// Whether the ballot this write carries can actually be voted on.
    ///
    /// <para>Options are only meaningful for a `choice` round, and a choice
    /// round is only playable with between two and six answers that each say
    /// something. A write that sends NO options is always valid — null means
    /// unchanged, which is what lets a client that predates choice rounds keep
    /// saving activities it does understand.</para>
    /// </summary>
    private static bool ValidOptions(PartyChallengeWriteRequest r)
    {
        if (r.Options is null) return true;
        var kept = r.Options.Where(o => !string.IsNullOrWhiteSpace(o.Label)).ToList();
        // Sending options for a mode that does not use them is a client bug, not
        // something to quietly store: the row would be unreachable either way.
        if (!PartyChallengeVotingModes.UsesOptions(r.VotingMode)) return kept.Count == 0;
        return PartyChallengeLimits.IsValidOptionCount(kept.Count)
            && kept.All(o => o.Label!.Trim().Length <= PartyChallengeLimits.MaxOptionLabelLength
                && (o.Outcome is null
                    || o.Outcome.Trim().Length <= PartyChallengeLimits.MaxOptionOutcomeLength));
    }

    // A blank question is no question: the room gets the localized default
    // rather than an empty line where one was expected.
    private static string? NormalizeVoteQuestion(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static PartyChallengeDto OwnerDto(
        PartyChallenge x, int votes, IReadOnlyList<PartyChallengeOption>? options = null) =>
        new(x.Id, x.Title, x.Body, x.Kind, x.MediaFileItemId,
            x.MediaFileItemId is Guid id ? $"/api/files/{id}/thumbnail?size=medium" : null,
            x.IsEnabled, x.SortOrder, votes, x.CreatedAt, x.UpdatedAt,
            x.DurationSeconds, x.VotingMode, x.VoteQuestion,
            Ballot(x.VotingMode, options));

    /// The answers, in the order the room sees them — and nothing at all for a
    /// mode that is not answered with them.
    internal static IReadOnlyList<PartyChallengeOptionDto>? Ballot(
        string votingMode, IReadOnlyList<PartyChallengeOption>? options) =>
        !PartyChallengeVotingModes.UsesOptions(votingMode) || options is null
            ? null
            : options.OrderBy(o => o.Position)
                .Select(o => new PartyChallengeOptionDto(o.Id, o.Label, o.Outcome))
                .ToList();

    /// <summary>
    /// Rewrites an activity's ballot to exactly what the host just saved.
    ///
    /// <para>Options are REPLACED rather than merged: the composer sends the
    /// whole ballot because that is what the host was looking at, and matching
    /// rows up by position would make a reorder indistinguishable from an edit.
    /// Replacing means a vote already cast for a removed answer has nothing to
    /// point at — which is correct, and is why a round in flight is not the
    /// moment to rewrite the question.</para>
    ///
    /// <para>A write with no options leaves the stored ballot alone; a mode that
    /// does not use options drops it, because keeping answers to a question
    /// nobody will be asked is how stale data survives.</para>
    /// </summary>
    private async Task ApplyOptionsAsync(
        PartyChallenge row, PartyChallengeWriteRequest request, CancellationToken ct)
    {
        var usesOptions = PartyChallengeVotingModes.UsesOptions(row.VotingMode);
        if (request.Options is null && usesOptions) return;

        var existing = await _db.PartyChallengeOptions
            .Where(o => o.PartyChallengeId == row.Id).ToListAsync(ct);
        if (existing.Count > 0) _db.PartyChallengeOptions.RemoveRange(existing);
        if (!usesOptions || request.Options is null) return;

        var position = 0;
        foreach (var option in request.Options.Where(o => !string.IsNullOrWhiteSpace(o.Label)))
        {
            _db.PartyChallengeOptions.Add(new PartyChallengeOption
            {
                Id = Guid.NewGuid(),
                PartyChallengeId = row.Id,
                Position = position++,
                Label = option.Label!.Trim(),
                Outcome = string.IsNullOrWhiteSpace(option.Outcome) ? null : option.Outcome.Trim(),
            });
        }
    }

    private Task<List<PartyChallengeOption>> OptionsAsync(Guid challengeId, CancellationToken ct) =>
        _db.PartyChallengeOptions.AsNoTracking()
            .Where(o => o.PartyChallengeId == challengeId)
            .OrderBy(o => o.Position).ToListAsync(ct);

    private async Task ReleaseVotesForChallengeAsync(Guid challengeId, CancellationToken ct)
    {
        var participants = await _db.PartyChallengeVotes.AsNoTracking()
            .Where(x => x.PartyChallengeId == challengeId)
            .GroupBy(x => x.PartyParticipantId)
            .Select(g => new { ParticipantId = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        foreach (var guest in participants)
        {
            var count = guest.Count;
            await _db.PartyParticipants.Where(x => x.Id == guest.ParticipantId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(
                    x => x.ChallengeVoteCount,
                    x => x.ChallengeVoteCount > count ? x.ChallengeVoteCount - count : 0), ct);
        }
        await _db.PartyChallengeVotes.Where(x => x.PartyChallengeId == challengeId)
            .ExecuteDeleteAsync(ct);
    }
}
