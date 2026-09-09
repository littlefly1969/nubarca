using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Tests.Endpoints;

namespace NubArca.Api.Tests.Party;

/// <summary>
/// What a fold has to move BESIDES the counters.
///
/// <para><c>PartyParticipantId</c> means "the guest who did this". Folding a
/// pre-upgrade row while leaving its activity behind would satisfy the letter
/// of "one identity per browser" and break the point of it: a challenge the
/// guest already voted would look unvoted to the identity that now represents
/// them, so they could vote it AGAIN. One browser, two votes — the exact thing
/// an anonymous identity exists to prevent, reintroduced by the migration meant
/// to end it.</para>
///
/// <para>So these tests are written from the outside, through the API a phone
/// actually uses, and they ask the question a guest would: is this still voted?
/// The reconciliation and the actor invariant are checked at the service, where
/// two identities can be made to collide deliberately.</para>
/// </summary>
public sealed class PartyGuestFoldActorTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory = new();
    public PartyGuestFoldActorTests() => _factory.EnsureDatabaseCreated();
    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task A_challenge_voted_before_the_upgrade_is_still_voted_after_it()
    {
        var party = await SetUpAsync();
        var challenge = await SeedChallengeAsync(party.Album);

        // The guest as they were mid-party: a capability-scoped row that has
        // already spent one of its three votes on this challenge.
        var legacy = FakeToken();
        var legacyId = await SeedLegacyAsync(party.LinkId, legacy);
        await SeedChallengeVoteAsync(party.LinkId, legacyId, challenge);

        // Their next request, carrying the old cookie. This is the fold.
        var browser = _factory.CreateClient();
        var list = await ReadAsync(await WithLegacyAsync(
            browser, HttpMethod.Get, $"/api/party/{party.View}/challenges", legacy));

        // The guest's own screen: still voted, and still two votes left.
        Assert.True(list.GetProperty("items")[0].GetProperty("voted").GetBoolean());
        Assert.Equal(1, list.GetProperty("votesUsed").GetInt32());
        Assert.Equal(2, list.GetProperty("votesRemaining").GetInt32());

        // One vote, cast by the canonical guest, and a budget that agrees.
        var canonical = await CanonicalAsync(party.LinkId);
        var vote = await WithDbAsync(db => db.PartyChallengeVotes.SingleAsync());
        Assert.Equal(canonical, vote.PartyParticipantId);
        Assert.Equal(1, (await GuestAsync(canonical)).ChallengeVoteCount);

        // Voting it AGAIN changes nothing — which is only true because the vote
        // moved. Left on the alias, this would have been a second vote.
        var again = await browser.PutAsync(
            $"/api/party/{party.View}/challenges/{challenge}/vote", null);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(1, (await ReadAsync(again)).GetProperty("votesUsed").GetInt32());
        Assert.Equal(1, await CountAsync(db => db.PartyChallengeVotes.CountAsync()));
        Assert.Equal(1, await TallyAsync(challenge));

        // And taking it back gives the guest their vote back, rather than
        // failing to find a vote that was recorded under another identity.
        var removed = await browser.DeleteAsync(
            $"/api/party/{party.View}/challenges/{challenge}/vote");
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        Assert.Equal(0, (await ReadAsync(removed)).GetProperty("votesUsed").GetInt32());
        Assert.Equal(0, await CountAsync(db => db.PartyChallengeVotes.CountAsync()));
        Assert.Equal(0, (await GuestAsync(canonical)).ChallengeVoteCount);
        Assert.Equal(0, await TallyAsync(challenge));
    }

    [Fact]
    public async Task A_game_vote_cast_before_the_upgrade_belongs_to_the_canonical_guest()
    {
        var party = await SetUpAsync();
        await SeedChallengeAsync(party.Album);
        var round = await OpenVotingAsync(party);

        var legacy = FakeToken();
        var legacyId = await SeedLegacyAsync(party.LinkId, legacy);
        await SeedGameVoteAsync(round, legacyId, PartyGameVoteValues.Yes);

        // The phone's first request after the upgrade: it joins, which folds.
        var browser = _factory.CreateClient();
        var joined = await ReadAsync(await WithLegacyAsync(
            browser, HttpMethod.Post, $"/api/party/{party.View}/game/join", legacy));

        // The vote the guest cast is THEIR vote, on their own screen.
        Assert.Equal("yes", joined.GetProperty("myVote").GetString());
        Assert.Equal(1, joined.GetProperty("voting").GetProperty("received").GetInt32());
        // One guest in the room, not two: the alias is retired, so the count the
        // host reads out loud does not include the identity that was replaced.
        Assert.Equal(1, joined.GetProperty("voting").GetProperty("eligible").GetInt32());

        // Changing their mind CHANGES that vote rather than adding a second one.
        var changed = await ReadAsync(await browser.PostAsJsonAsync(
            $"/api/party/{party.View}/game/vote",
            new { roundId = round, value = "no" }));
        Assert.Equal("no", changed.GetProperty("myVote").GetString());
        Assert.Equal(1, changed.GetProperty("voting").GetProperty("received").GetInt32());
        Assert.Equal(1, await CountAsync(db => db.PartyGameVotes.CountAsync()));

        var vote = await WithDbAsync(db => db.PartyGameVotes.SingleAsync());
        Assert.Equal(await CanonicalAsync(party.LinkId), vote.PartyParticipantId);
    }

    [Fact]
    public async Task Where_both_identities_answered_the_same_question_the_canonical_answer_wins()
    {
        // The awkward middle of an upgrade: the guest voted, then their browser
        // established the new identity and voted again from it, and only THEN
        // did a surface still holding the old cookie come back. Both rows exist,
        // and the uniqueness rule says only one may survive.
        //
        // The canonical vote wins because it is the later one — the answer the
        // guest gave after the new session existed. A fold must never overwrite
        // what a guest did with what they did earlier.
        var party = await SetUpAsync();
        await SeedChallengeAsync(party.Album);
        var round = await OpenVotingAsync(party);
        var challenge = await WithDbAsync(db => db.PartyChallenges.Select(c => c.Id).FirstAsync());

        var legacy = FakeToken();
        var legacyId = await SeedLegacyAsync(party.LinkId, legacy);
        await SeedChallengeVoteAsync(party.LinkId, legacyId, challenge);
        await SeedGameVoteAsync(round, legacyId, PartyGameVoteValues.Yes);

        // The browser's new identity, with its own answer to both questions.
        var browser = _factory.CreateClient();
        (await browser.PostAsync($"/api/party/{party.View}/game/join", null))
            .EnsureSuccessStatusCode();
        (await browser.PutAsync(
            $"/api/party/{party.View}/challenges/{challenge}/vote", null))
            .EnsureSuccessStatusCode();
        (await browser.PostAsJsonAsync($"/api/party/{party.View}/game/vote",
            new { roundId = round, value = "no" })).EnsureSuccessStatusCode();

        // Now the old cookie arrives. Two votes want to become one.
        var folded = await WithLegacyAsync(
            browser, HttpMethod.Get, $"/api/party/{party.View}/challenges", legacy);
        // Not a 500. The reconciliation happens before the re-parent, so the
        // unique index is never reached — it stays a safety net.
        Assert.Equal(HttpStatusCode.OK, folded.StatusCode);

        var canonical = await CanonicalAsync(party.LinkId);
        var challengeVote = await WithDbAsync(db => db.PartyChallengeVotes.SingleAsync());
        Assert.Equal(canonical, challengeVote.PartyParticipantId);

        var gameVote = await WithDbAsync(db => db.PartyGameVotes.SingleAsync());
        Assert.Equal(canonical, gameVote.PartyParticipantId);
        // The canonical answer, not the alias's older one.
        Assert.Equal(PartyGameVoteValues.No, gameVote.Value);

        // ONE vote, so ONE spent from the budget. Summing the counters would
        // have charged this guest twice for a vote they hold once, and the
        // budget is what they can still spend tonight.
        Assert.Equal(1, (await GuestAsync(canonical)).ChallengeVoteCount);
        Assert.Equal(1, await TallyAsync(challenge));
        Assert.Equal(1, (await ReadAsync(folded)).GetProperty("votesUsed").GetInt32());
    }

    [Fact]
    public async Task After_a_fold_nothing_a_feature_reads_still_points_at_the_alias()
    {
        // The invariant, checked against the whole list rather than against the
        // two records that happened to be found first. Four foreign keys name a
        // participant; every one of them must name the surviving guest.
        var party = await SetUpAsync();
        var challenge = await SeedChallengeAsync(party.Album);
        var round = await OpenVotingAsync(party);

        var legacy = FakeToken();
        var legacyId = await SeedLegacyAsync(party.LinkId, legacy);
        await SeedChallengeVoteAsync(party.LinkId, legacyId, challenge);
        await SeedGameVoteAsync(round, legacyId, PartyGameVoteValues.Yes);
        await SeedMessageAsync(party.LinkId, legacyId);
        await SeedUploadAsync(party.Album, party.LinkId, legacyId);

        var browser = _factory.CreateClient();
        (await WithLegacyAsync(
            browser, HttpMethod.Get, $"/api/party/{party.View}/challenges", legacy))
            .EnsureSuccessStatusCode();

        var canonical = await CanonicalAsync(party.LinkId);
        Assert.NotEqual(legacyId, canonical);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var actors = new List<(string Table, Guid? Actor)>();
        actors.AddRange((await db.PartyChallengeVotes.AsNoTracking().ToListAsync())
            .Select(x => ("party_challenge_votes", (Guid?)x.PartyParticipantId)));
        actors.AddRange((await db.PartyGameVotes.AsNoTracking().ToListAsync())
            .Select(x => ("party_game_votes", (Guid?)x.PartyParticipantId)));
        actors.AddRange((await db.PartyMessages.AsNoTracking().ToListAsync())
            .Select(x => ("party_messages", x.PartyParticipantId)));
        actors.AddRange((await db.PartyUploadItems.AsNoTracking().ToListAsync())
            .Select(x => ("party_upload_items", x.PartyParticipantId)));

        // Four records, one per referencing table — so this cannot pass by
        // finding nothing.
        Assert.Equal(4, actors.Count);
        Assert.Equal(4, actors.Select(a => a.Table).Distinct().Count());
        Assert.All(actors, a => Assert.Equal(canonical, a.Actor));

        // And the alias keeps neither activity nor allowance.
        var alias = await GuestAsync(legacyId);
        Assert.NotNull(alias.RetiredAt);
        Assert.Equal(0, alias.SubmittedMessageCount);
        Assert.Equal(0, alias.AcceptedPhotoCount);
        Assert.Equal(0, alias.ChallengeVoteCount);
    }

    // --- helpers -----------------------------------------------------------

    private sealed record Party(HttpClient Owner, Guid Album, Guid LinkId, string View);

    private static string FakeToken() =>
        Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Sha256Hex(string value) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(value)));

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static Task<HttpResponseMessage> WithLegacyAsync(
        HttpClient client, HttpMethod method, string url, string legacyToken)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("Cookie", $"NubArca.PartyGuest={legacyToken}");
        return client.SendAsync(request);
    }

    private async Task<T> WithDbAsync<T>(Func<AppDbContext, Task<T>> read)
    {
        using var scope = _factory.Services.CreateScope();
        return await read(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    private Task<int> CountAsync(Func<AppDbContext, Task<int>> count) => WithDbAsync(count);

    private Task<Guid> CanonicalAsync(Guid linkId) => WithDbAsync(db => db.PartyParticipants
        .AsNoTracking()
        .Where(p => p.PartyAlbumLinkId == linkId && p.RetiredAt == null)
        .Select(p => p.Id).SingleAsync());

    private Task<PartyParticipant> GuestAsync(Guid id) => WithDbAsync(db =>
        db.PartyParticipants.AsNoTracking().SingleAsync(p => p.Id == id));

    /// The public tally, counted the way the product counts it: from the rows.
    /// It and the guest's own budget are the same fact recorded twice, so a fold
    /// that got one right and the other wrong would still be wrong.
    private Task<int> TallyAsync(Guid challengeId) => WithDbAsync(db =>
        db.PartyChallengeVotes.AsNoTracking()
            .CountAsync(v => v.PartyChallengeId == challengeId));

    /// A participant exactly as the pre-change code wrote one: keyed by a plain
    /// hash of a capability-scoped token.
    private async Task<Guid> SeedLegacyAsync(Guid linkId, string token)
    {
        var id = Guid.NewGuid();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.PartyParticipants.Add(new PartyParticipant
        {
            Id = id, PartyAlbumLinkId = linkId, TokenHash = Sha256Hex(token),
            CreatedAt = DateTime.UtcNow, LastSeenAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return id;
    }

    /// A vote as the vote path leaves it: the row AND the budget it spent, which
    /// share one transaction in production and so can never disagree.
    private async Task SeedChallengeVoteAsync(Guid linkId, Guid guestId, Guid challengeId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.PartyChallengeVotes.Add(new PartyChallengeVote
        {
            Id = Guid.NewGuid(), PartyAlbumLinkId = linkId,
            PartyParticipantId = guestId, PartyChallengeId = challengeId,
            CreatedAt = DateTime.UtcNow,
        });
        await db.PartyParticipants.Where(p => p.Id == guestId)
            .ExecuteUpdateAsync(s => s.SetProperty(
                p => p.ChallengeVoteCount, p => p.ChallengeVoteCount + 1));
        await db.SaveChangesAsync();
    }

    private async Task SeedGameVoteAsync(Guid roundId, Guid guestId, string value)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var sessionId = await db.PartyGameRounds.AsNoTracking()
            .Where(r => r.Id == roundId).Select(r => r.PartyGameSessionId).SingleAsync();
        db.PartyGameVotes.Add(new PartyGameVote
        {
            Id = Guid.NewGuid(), PartyGameSessionId = sessionId, PartyGameRoundId = roundId,
            PartyParticipantId = guestId, Value = value,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedMessageAsync(Guid linkId, Guid guestId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (albumId, ownerId) = await AlbumOfAsync(db, linkId);
        db.PartyMessages.Add(new PartyMessage
        {
            Id = Guid.NewGuid(), PartyAlbumLinkId = linkId, PartyParticipantId = guestId,
            AlbumId = albumId, OwnerUserId = ownerId,
            DisplayName = "Anna", Body = "Auguri", Status = PartyMessageStatuses.Visible,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await db.PartyParticipants.Where(p => p.Id == guestId)
            .ExecuteUpdateAsync(s => s.SetProperty(
                p => p.SubmittedMessageCount, p => p.SubmittedMessageCount + 1));
        await db.SaveChangesAsync();
    }

    private async Task SeedUploadAsync(Guid albumId, Guid linkId, Guid guestId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (_, ownerId) = await AlbumOfAsync(db, linkId);
        // A real file, through the real service. A stand-in id would fail the
        // foreign key the moment the fold re-parented the row, which is the
        // opposite of what this test is trying to observe.
        var file = await scope.ServiceProvider.GetRequiredService<NubArca.Api.Files.IFileItemService>()
            .CreateAsync(ownerId, null, $"{Guid.NewGuid():N}.jpg", "image/jpeg",
                new MemoryStream([0xFF, 0xD8, 0xFF, 0xD9]));
        db.PartyUploadItems.Add(new PartyUploadItem
        {
            Id = Guid.NewGuid(), AlbumId = albumId, OwnerUserId = ownerId,
            PartyAlbumLinkId = linkId, PartyParticipantId = guestId,
            FileItemId = file.Id, Status = PartyUploadStatuses.Approved,
            UploadedAt = DateTime.UtcNow,
        });
        await db.PartyParticipants.Where(p => p.Id == guestId)
            .ExecuteUpdateAsync(s => s.SetProperty(
                p => p.AcceptedPhotoCount, p => p.AcceptedPhotoCount + 1));
        await db.SaveChangesAsync();
    }

    private static async Task<(Guid AlbumId, Guid OwnerId)> AlbumOfAsync(
        AppDbContext db, Guid linkId) =>
        await db.PartyAlbumLinks.AsNoTracking().Where(l => l.Id == linkId)
            .Select(l => new ValueTuple<Guid, Guid>(l.AlbumId, l.OwnerUserId))
            .SingleAsync();

    private async Task<Guid> SeedChallengeAsync(Guid albumId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var id = Guid.NewGuid();
        db.PartyChallenges.Add(new PartyChallenge
        {
            Id = id, AlbumId = albumId, Title = "Canta", Body = "Sali sul tavolo.",
            Kind = PartyChallengeKinds.Dare, IsEnabled = true,
            VotingMode = PartyChallengeVotingModes.Binary,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return id;
    }

    /// Runs the host's opening moves and returns the round a guest would vote on.
    private async Task<Guid> OpenVotingAsync(Party party)
    {
        var version = 0;
        foreach (var command in new[] { "start", "start_challenge", "open_voting" })
        {
            version = (await ReadAsync(await party.Owner.PostAsJsonAsync(
                $"/api/albums/{party.Album}/party-game/commands",
                new { command, expectedVersion = version })))
                .GetProperty("version").GetInt32();
        }
        return await WithDbAsync(db => db.PartyGameSessions.AsNoTracking()
            .Where(s => s.PartyAlbumLinkId == party.LinkId)
            .Select(s => s.CurrentRoundId!.Value).SingleAsync());
    }

    private async Task<Party> SetUpAsync()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(
            $"{Guid.NewGuid():N}@example.com");
        var album = (await ReadAsync(await owner.PostAsJsonAsync("/api/albums",
            new { name = $"Festa {Guid.NewGuid():N}" }))).GetProperty("id").GetGuid();
        (await owner.PatchAsJsonAsync($"/api/albums/{album}/party-settings",
            new { enabled = true, uploadEnabled = true })).EnsureSuccessStatusCode();
        await StartPartyAsync(owner, album);
        (await owner.PatchAsJsonAsync($"/api/albums/{album}/party-slideshow-settings",
            new { maxMessagesPerParticipant = 10 })).EnsureSuccessStatusCode();
        (await owner.PatchAsJsonAsync($"/api/albums/{album}/party-game-settings", new
        {
            gameEnabled = true, minChallengeIntervalSeconds = 30,
            maxChallengeIntervalSeconds = 60, votesPerGuest = 3,
            maxChallengesPerSession = (int?)null,
        })).EnsureSuccessStatusCode();

        var status = await ReadAsync(await owner.GetAsync($"/api/albums/{album}/party-settings"));
        var view = status.GetProperty("partyUrl").GetString()!["/party/".Length..];
        var linkId = await WithDbAsync(db => db.PartyAlbumLinks.AsNoTracking()
            .Where(x => x.AlbumId == album && x.Enabled && x.RevokedAt == null)
            .OrderByDescending(x => x.CreatedAt).Select(x => x.Id).FirstAsync());
        return new Party(owner, album, linkId, view);
    }

    // Enabling guest access PUBLISHES the party — an invitation, which is
    // deliberately not the party itself. These tests exercise the party, so they
    // start it, exactly as a host does.
    private static async Task StartPartyAsync(HttpClient owner, Guid album)
    {
        var settings = await (await owner.GetAsync($"/api/albums/{album}/party-settings"))
            .Content.ReadFromJsonAsync<JsonElement>();
        await NubArca.Api.Tests.Party.PartyTestHost.StartAsync(owner, settings);
    }
}
