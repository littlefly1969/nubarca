using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NubArca.Api.Domain;
using NubArca.Api.Tests.Endpoints;

namespace NubArca.Api.Tests.Party;

// PARTY-GUESTBOOK-01. A guest writes something for the hosts to keep. Not a
// greeting for the television — a dedication for the book.
//
// The properties worth defending, and what would break if each stopped holding:
//   * the book is OPT-IN, so no party acquires one by being upgraded;
//   * a party with the book closed has no book to read and refuses a
//     hand-built dedication with a stable code, storing nothing;
//   * a dedication NEVER reaches the slideshow, the television or the Hero —
//     there is no route that promotes one, and the TV projection has never
//     heard of the table. This is the hard invariant of the whole feature;
//   * only what a manager let in is public, and the public projection is
//     recomputed from the CURRENT state every time;
//   * the book belongs to the PARTY, not to the album, so it survives a
//     re-minted link and an album that was never attached.
public sealed class PartyGuestbookTests : IDisposable
{
    private const string OwnerEmail = "host@example.com";
    private const string StrangerEmail = "stranger@example.com";

    private readonly SqliteWebApplicationFactory _factory = new();

    public PartyGuestbookTests() => _factory.EnsureDatabaseCreated();

    public void Dispose() => _factory.Dispose();

    // ── Closed by default ───────────────────────────────────────────────────

    [Fact]
    public async Task A_party_keeps_no_book_until_its_host_opens_one()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var party = await OpenPartyAsync(owner);

        // The guest surface is told nothing at all: no URL, so no card, no tab,
        // no empty state and no fetch.
        var capabilities = (await GuestContextAsync(party.ViewToken)).GetProperty("capabilities");
        Assert.Equal(JsonValueKind.Null, capabilities.GetProperty("guestbookUrl").ValueKind);

        // And the routes behind it are the same generic nothing every absent
        // Party capability is. A guest has no business learning that the
        // surface exists elsewhere.
        var read = await _factory.CreateClient().GetAsync($"/api/party/{party.ViewToken}/guestbook");
        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
    }

    [Fact]
    public async Task A_dedication_sent_by_hand_to_a_party_with_no_book_is_refused_and_stored_nowhere()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var party = await OpenPartyAsync(owner);

        var refused = await SubmitRawAsync(party.ViewToken, new { authorDisplayName = "Ada", body = "Auguri" });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("guestbook_disabled", await ErrorOf(refused));

        // Refused BEFORE the text was looked at, and nothing kept: the host's
        // own queue is empty, so a closed book cannot be filled in advance.
        var queue = await ManagerListAsync(owner, party.PartyId);
        Assert.Equal(0, queue.GetProperty("entries").GetArrayLength());
        Assert.False(queue.GetProperty("guestbookEnabled").GetBoolean());
    }

    // ── An open book ────────────────────────────────────────────────────────

    [Fact]
    public async Task An_opened_book_is_offered_to_the_guest_read_and_written()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var party = await OpenPartyAsync(owner, guestbook: true);

        var capabilities = (await GuestContextAsync(party.ViewToken)).GetProperty("capabilities");
        Assert.Equal(
            $"/party/{party.ViewToken}/guestbook",
            capabilities.GetProperty("guestbookUrl").GetString());

        var submitted = await SubmitAsync(party.ViewToken, "Giulia", "Che serata. Grazie di tutto.");
        Assert.Equal(PartyMessageStatuses.Visible, submitted.GetProperty("status").GetString());

        var page = await PublicBookAsync(party.ViewToken);
        Assert.True(page.GetProperty("canWrite").GetBoolean());
        Assert.Equal(PartyGuestbookLimits.MaxBodyLength, page.GetProperty("maxBodyLength").GetInt32());
        Assert.Equal(
            PartyGuestbookLimits.MaxAuthorDisplayNameLength,
            page.GetProperty("maxAuthorDisplayNameLength").GetInt32());

        var entry = page.GetProperty("entries")[0];
        Assert.Equal("Giulia", entry.GetProperty("authorDisplayName").GetString());
        Assert.Equal("Che serata. Grazie di tutto.", entry.GetProperty("body").GetString());
    }

    [Fact]
    public async Task A_dedication_without_a_signature_carries_null_rather_than_an_empty_string()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var party = await OpenPartyAsync(owner, guestbook: true);

        await SubmitAsync(party.ViewToken, "   ", "Anonimo, ma sincero.");

        // One shape for "unsigned", so the page has one case to render.
        var entry = (await PublicBookAsync(party.ViewToken)).GetProperty("entries")[0];
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("authorDisplayName").ValueKind);
    }

    [Fact]
    public async Task The_book_reads_newest_first_because_that_is_what_somebody_just_wrote()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var party = await OpenPartyAsync(owner, guestbook: true);

        await SubmitAsync(party.ViewToken, null, "Prima");
        await SubmitAsync(party.ViewToken, null, "Seconda");

        var bodies = (await PublicBookAsync(party.ViewToken)).GetProperty("entries")
            .EnumerateArray().Select(e => e.GetProperty("body").GetString()).ToArray();
        Assert.Equal(["Seconda", "Prima"], bodies);
    }

    // ── What a dedication may be ────────────────────────────────────────────

    [Fact]
    public async Task A_blank_dedication_is_not_a_dedication()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var party = await OpenPartyAsync(owner, guestbook: true);

        foreach (var body in new string?[] { null, "", "   ", "\n\t ​" })
        {
            var refused = await SubmitRawAsync(party.ViewToken, new { authorDisplayName = "Ada", body });
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Equal("guestbook_invalid_body", await ErrorOf(refused));
        }

        Assert.Equal(0, (await PublicBookAsync(party.ViewToken)).GetProperty("entries").GetArrayLength());
    }

    [Fact]
    public async Task The_limits_are_measured_on_what_would_be_stored_not_on_what_was_typed()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var party = await OpenPartyAsync(owner, guestbook: true);

        // Exactly at the limit, buried in whitespace that normalisation
        // removes: accepted, because the limit is about the keepsake and not
        // about how somebody's keyboard behaved.
        var atLimit = new string('a', PartyGuestbookLimits.MaxBodyLength);
        var accepted = await SubmitRawAsync(
            party.ViewToken, new { authorDisplayName = (string?)null, body = $"   {atLimit}   " });
        accepted.EnsureSuccessStatusCode();

        var tooLong = await SubmitRawAsync(
            party.ViewToken,
            new { authorDisplayName = (string?)null, body = new string('a', PartyGuestbookLimits.MaxBodyLength + 1) });
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Equal("guestbook_invalid_body", await ErrorOf(tooLong));

        var longSignature = await SubmitRawAsync(
            party.ViewToken,
            new
            {
                authorDisplayName = new string('n', PartyGuestbookLimits.MaxAuthorDisplayNameLength + 1),
                body = "Auguri",
            });
        Assert.Equal(HttpStatusCode.BadRequest, longSignature.StatusCode);
        var refusal = await longSignature.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("guestbook_invalid_author", refusal.GetProperty("error").GetString());

        // The refusal states the limits, so a client that has drifted from the
        // shared contract can still say something true.
        Assert.Equal(
            PartyGuestbookLimits.MaxAuthorDisplayNameLength,
            refusal.GetProperty("maxAuthorDisplayNameLength").GetInt32());
        Assert.Equal(PartyGuestbookLimits.MaxBodyLength, refusal.GetProperty("maxBodyLength").GetInt32());

        // Only the one that was accepted is in the book.
        Assert.Equal(1, (await PublicBookAsync(party.ViewToken)).GetProperty("entries").GetArrayLength());
    }

    [Fact]
    public async Task A_dedication_is_kept_as_plain_text_with_its_whitespace_collapsed()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var party = await OpenPartyAsync(owner, guestbook: true);

        await SubmitAsync(party.ViewToken, "  Ada   Lovelace  ", "  Grazie\t\tdi   tutto.  ");

        // No markup was interpreted and no formatting survived: the book stores
        // what somebody wrote, as text, and the page renders it as text.
        var entry = (await PublicBookAsync(party.ViewToken)).GetProperty("entries")[0];
        Assert.Equal("Ada Lovelace", entry.GetProperty("authorDisplayName").GetString());
        Assert.Equal("Grazie di tutto.", entry.GetProperty("body").GetString());
    }

    [Fact]
    public async Task A_guest_cannot_fill_the_book_faster_than_the_message_limit_allows()
    {
        // The book rides the SAME bucket greetings do — it is the same act,
        // written on a different page — so a party under an open link has one
        // budget for written contributions rather than two that add up.
        using var factory = new SqliteWebApplicationFactory(new Dictionary<string, string?>
        {
            ["RateLimits:PartyMessage:PermitLimit"] = "2",
            ["RateLimits:PartyMessage:WindowSeconds"] = "60",
        });
        factory.EnsureDatabaseCreated();
        var (_, owner) = await factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var party = await OpenPartyAsync(owner, guestbook: true);

        var guest = factory.CreateClient();
        for (var i = 0; i < 2; i++)
        {
            var accepted = await guest.PostAsJsonAsync(
                $"/api/party/{party.ViewToken}/guestbook",
                new { authorDisplayName = (string?)null, body = $"Dedica {i}" });
            accepted.EnsureSuccessStatusCode();
        }

        var refused = await guest.PostAsJsonAsync(
            $"/api/party/{party.ViewToken}/guestbook",
            new { authorDisplayName = (string?)null, body = "Una di troppo" });
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
    }

    // ── Moderation ──────────────────────────────────────────────────────────

    [Fact]
    public async Task With_approval_on_a_dedication_waits_and_the_book_does_not_show_it()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var party = await OpenPartyAsync(owner, guestbook: true, requireApproval: true);

        var submitted = await SubmitAsync(party.ViewToken, "Marco", "In attesa");
        Assert.Equal(PartyMessageStatuses.Pending, submitted.GetProperty("status").GetString());
        var entryId = submitted.GetProperty("id").GetGuid();

        // Nowhere in the book…
        Assert.Equal(0, (await PublicBookAsync(party.ViewToken)).GetProperty("entries").GetArrayLength());

        // …and in the host's queue, which is a different surface with a
        // different rule.
        var queue = await ManagerListAsync(owner, party.PartyId);
        Assert.True(queue.GetProperty("requireGuestbookApproval").GetBoolean());
        Assert.True(queue.GetProperty("isOwner").GetBoolean());
        Assert.Equal(PartyMessageStatuses.Pending, queue.GetProperty("entries")[0].GetProperty("status").GetString());

        Assert.Equal(HttpStatusCode.NoContent, await ModerateAsync(owner, party.PartyId, entryId, "approve"));
        Assert.Equal(1, (await PublicBookAsync(party.ViewToken)).GetProperty("entries").GetArrayLength());
    }

    [Fact]
    public async Task Hiding_takes_a_dedication_out_of_the_book_and_restoring_puts_it_back()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var party = await OpenPartyAsync(owner, guestbook: true);
        var entryId = (await SubmitAsync(party.ViewToken, "Ada", "Evviva")).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.NoContent, await ModerateAsync(owner, party.PartyId, entryId, "hide"));
        Assert.Equal(0, (await PublicBookAsync(party.ViewToken)).GetProperty("entries").GetArrayLength());
        // Hidden, not deleted: the host can change their mind, and the row was
        // never rewritten.
        var hidden = (await ManagerListAsync(owner, party.PartyId)).GetProperty("entries")[0];
        Assert.Equal(PartyMessageStatuses.Hidden, hidden.GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, hidden.GetProperty("moderatedAt").ValueKind);

        Assert.Equal(HttpStatusCode.NoContent, await ModerateAsync(owner, party.PartyId, entryId, "restore"));
        Assert.Equal(1, (await PublicBookAsync(party.ViewToken)).GetProperty("entries").GetArrayLength());
    }

    [Fact]
    public async Task A_rejected_dedication_stays_out_and_a_refused_transition_is_not_a_missing_entry()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var party = await OpenPartyAsync(owner, guestbook: true, requireApproval: true);
        var entryId = (await SubmitAsync(party.ViewToken, null, "No")).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.NoContent, await ModerateAsync(owner, party.PartyId, entryId, "reject"));
        Assert.Equal(0, (await PublicBookAsync(party.ViewToken)).GetProperty("entries").GetArrayLength());

        // A real entry, a real manager, a refused transition → 400. The caller
        // is entitled to know the difference between "not allowed from here"
        // and "no such thing".
        Assert.Equal(HttpStatusCode.BadRequest, await ModerateAsync(owner, party.PartyId, entryId, "hide"));
    }

    [Fact]
    public async Task Closing_the_book_hides_it_and_keeps_every_dedication_for_when_it_re_opens()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var party = await OpenPartyAsync(owner, guestbook: true);
        await SubmitAsync(party.ViewToken, "Ada", "Da conservare");

        await SetContributionsAsync(owner, party.AlbumId, new { guestbookEnabled = false });

        // Closed to the guest…
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await _factory.CreateClient().GetAsync($"/api/party/{party.ViewToken}/guestbook")).StatusCode);

        // …and intact for the host, who is the person the book is for.
        var queue = await ManagerListAsync(owner, party.PartyId);
        Assert.False(queue.GetProperty("guestbookEnabled").GetBoolean());
        Assert.Equal(1, queue.GetProperty("entries").GetArrayLength());

        await SetContributionsAsync(owner, party.AlbumId, new { guestbookEnabled = true });
        var reopened = await PublicBookAsync(party.ViewToken);
        Assert.Equal("Da conservare", reopened.GetProperty("entries")[0].GetProperty("body").GetString());
    }

    // ── The book's own budget ───────────────────────────────────────────────

    [Fact]
    public async Task A_guest_writes_what_the_host_allowed_and_is_then_refused_with_its_own_code()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var party = await OpenPartyAsync(owner, guestbook: true);
        (await owner.PatchAsJsonAsync(
            $"/api/albums/{party.AlbumId}/party-slideshow-settings",
            new { maxGuestbookEntriesPerParticipant = 2 })).EnsureSuccessStatusCode();

        // ONE browser, so one budget: the guest session is a cookie, and the
        // client carries it exactly as a phone does.
        var guest = _factory.CreateClient();
        for (var i = 1; i <= 2; i++)
        {
            var accepted = await guest.PostAsJsonAsync(
                $"/api/party/{party.ViewToken}/guestbook",
                new { authorDisplayName = (string?)null, body = $"Dedica {i}" });
            accepted.EnsureSuccessStatusCode();
            var body = await accepted.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(2 - i, body.GetProperty("remaining").GetInt32());
        }

        var refused = await guest.PostAsJsonAsync(
            $"/api/party/{party.ViewToken}/guestbook",
            new { authorDisplayName = (string?)null, body = "Una di troppo" });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        // Its OWN code: "you have written your allowance" is a different thing
        // to tell somebody than "there is no book here" or "you are going too
        // fast", and a guest can act on only one of the three.
        Assert.Equal("guestbook_limit_reached", await ErrorOf(refused));

        // Nothing was stored for the refusal.
        Assert.Equal(2, (await PublicBookAsync(party.ViewToken)).GetProperty("entries").GetArrayLength());
    }

    [Fact]
    public async Task Hiding_a_dedication_does_not_hand_the_slot_back()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var party = await OpenPartyAsync(owner, guestbook: true);
        (await owner.PatchAsJsonAsync(
            $"/api/albums/{party.AlbumId}/party-slideshow-settings",
            new { maxGuestbookEntriesPerParticipant = 1 })).EnsureSuccessStatusCode();

        var guest = _factory.CreateClient();
        var written = await guest.PostAsJsonAsync(
            $"/api/party/{party.ViewToken}/guestbook",
            new { authorDisplayName = "Ada", body = "Scritta" });
        written.EnsureSuccessStatusCode();
        var entryId = (await written.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        // Moderation is a judgement about the dedication, not a refund. A host
        // who takes one down has not handed its author another go at sending
        // it — otherwise declining something would be an invitation to resend.
        Assert.Equal(HttpStatusCode.NoContent, await ModerateAsync(owner, party.PartyId, entryId, "hide"));

        var again = await guest.PostAsJsonAsync(
            $"/api/party/{party.ViewToken}/guestbook",
            new { authorDisplayName = "Ada", body = "Ancora" });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("guestbook_limit_reached", await ErrorOf(again));
    }

    // ── The hard invariant ──────────────────────────────────────────────────

    [Fact]
    public async Task A_dedication_never_reaches_the_slideshow_the_television_or_the_hero()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var party = await OpenPartyAsync(owner, guestbook: true);
        var tv = await PairTvAsync(owner);

        var entryId = (await SubmitAsync(party.ViewToken, "Ada", "Per il libro")).GetProperty("id").GetGuid();
        await _factory.CreateClient().PostAsJsonAsync(
            $"/api/party/{party.UploadToken}/messages",
            new { displayName = "Giulia", text = "Per la TV" });

        // The television sees the GREETING and has never heard of the book.
        var messages = await TvMessagesAsync(tv, party.AlbumId);
        Assert.Equal(1, messages.GetArrayLength());
        Assert.Equal("Per la TV", messages[0].GetProperty("text").GetString());

        // The host's greeting queue is equally unaware: the two are separate
        // tables, not one table with a flag.
        var greetings = await owner.GetFromJsonAsync<JsonElement>(
            $"/api/albums/{party.AlbumId}/party-messages");
        Assert.Equal(1, greetings.GetProperty("items").GetArrayLength());
        Assert.Equal("Per la TV", greetings.GetProperty("items")[0].GetProperty("text").GetString());

        // AND THERE IS NO WAY TO PROMOTE ONE. Not a refusal — an absence: no
        // route exists, so no host, collaborator or client can invent the act.
        foreach (var action in new[] { "promote-hero", "demote-hero" })
        {
            var missing = await owner.PostAsync(
                $"/api/parties/{party.PartyId}/guestbook/{entryId}/{action}", null);
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }
    }

    // ── Whose book it is ────────────────────────────────────────────────────

    [Fact]
    public async Task A_stranger_reads_nothing_and_moderates_nothing()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, stranger) = await _factory.CreateAuthenticatedClientAsync(StrangerEmail);
        var party = await OpenPartyAsync(owner, guestbook: true);
        var entryId = (await SubmitAsync(party.ViewToken, "Ada", "Privata")).GetProperty("id").GetGuid();

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await stranger.GetAsync($"/api/parties/{party.PartyId}/guestbook")).StatusCode);
        foreach (var action in new[] { "approve", "reject", "hide", "restore" })
        {
            Assert.Equal(HttpStatusCode.NotFound, await ModerateAsync(stranger, party.PartyId, entryId, action));
        }

        // Untouched.
        Assert.Equal(
            PartyMessageStatuses.Visible,
            (await ManagerListAsync(owner, party.PartyId)).GetProperty("entries")[0]
                .GetProperty("status").GetString());
    }

    [Fact]
    public async Task An_entry_id_from_another_party_is_the_same_nothing_as_one_that_never_existed()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var first = await OpenPartyAsync(owner, guestbook: true, albumName: "Prima");
        var second = await OpenPartyAsync(owner, guestbook: true, albumName: "Seconda");
        var entryId = (await SubmitAsync(first.ViewToken, null, "Della prima")).GetProperty("id").GetGuid();

        // The SAME host, holding both parties — so this is not an authorization
        // test but a scoping one: a route somebody legitimately holds must not
        // become a way to reach across their other parties' books by id.
        Assert.Equal(HttpStatusCode.NotFound, await ModerateAsync(owner, second.PartyId, entryId, "hide"));
        Assert.Equal(
            PartyMessageStatuses.Visible,
            (await ManagerListAsync(owner, first.PartyId)).GetProperty("entries")[0]
                .GetProperty("status").GetString());
    }

    [Fact]
    public async Task The_public_book_carries_no_identity_beyond_the_signature_somebody_chose()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var party = await OpenPartyAsync(owner, guestbook: true);
        await SubmitAsync(party.ViewToken, "Ada", "Auguri");

        // Provenance is recorded and never published: no owner, no participant,
        // no link, no party id and no token anywhere in what a guest receives.
        var raw = await _factory.CreateClient().GetStringAsync($"/api/party/{party.ViewToken}/guestbook");
        foreach (var forbidden in new[]
                 {
                     "ownerUserId", "partyId", "partyAlbumLinkId", "partyParticipantId",
                     "tokenHash", "storageKey", "moderatedByUserId",
                 })
        {
            Assert.DoesNotContain(forbidden, raw, StringComparison.OrdinalIgnoreCase);
        }

        var entry = (await PublicBookAsync(party.ViewToken)).GetProperty("entries")[0];
        Assert.Equal(
            ["id", "authorDisplayName", "body", "createdAt"],
            entry.EnumerateObject().Select(p => p.Name).ToArray());
    }

    // ── Fixture ─────────────────────────────────────────────────────────────

    private sealed record OpenParty(Guid AlbumId, Guid PartyId, string ViewToken, string UploadToken);

    private async Task<OpenParty> OpenPartyAsync(
        HttpClient owner,
        bool guestbook = false,
        bool requireApproval = false,
        string albumName = "Festa")
    {
        var album = await owner.PostAsJsonAsync("/api/albums", new { name = albumName });
        album.EnsureSuccessStatusCode();
        var albumId = (await album.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        (await owner.PatchAsJsonAsync($"/api/albums/{albumId}/tv-settings", new { showOnTv = true }))
            .EnsureSuccessStatusCode();
        var settings = await owner.PatchAsJsonAsync(
            $"/api/albums/{albumId}/party-settings", new { enabled = true });
        settings.EnsureSuccessStatusCode();
        var status = await settings.Content.ReadFromJsonAsync<JsonElement>();
        await PartyTestHost.StartAsync(owner, status);

        if (guestbook || requireApproval)
        {
            status = await SetContributionsAsync(
                owner, albumId,
                new { guestbookEnabled = guestbook, requireGuestbookApproval = requireApproval });
        }

        var uploadUrl = status.GetProperty("uploadUrl").GetString()!;
        var rest = uploadUrl["/party/".Length..];
        return new OpenParty(
            albumId,
            status.GetProperty("partyId").GetGuid(),
            status.GetProperty("partyUrl").GetString()!["/party/".Length..],
            rest[..rest.IndexOf("/upload", StringComparison.Ordinal)]);
    }

    private static async Task<JsonElement> SetContributionsAsync(
        HttpClient owner, Guid albumId, object body)
    {
        var response = await owner.PatchAsJsonAsync($"/api/albums/{albumId}/party-contributions", body);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private Task<JsonElement> GuestContextAsync(string viewToken) =>
        _factory.CreateClient().GetFromJsonAsync<JsonElement>($"/api/party/{viewToken}");

    private Task<JsonElement> PublicBookAsync(string viewToken) =>
        _factory.CreateClient().GetFromJsonAsync<JsonElement>($"/api/party/{viewToken}/guestbook");

    private Task<HttpResponseMessage> SubmitRawAsync(string viewToken, object payload) =>
        _factory.CreateClient().PostAsJsonAsync($"/api/party/{viewToken}/guestbook", payload);

    private async Task<JsonElement> SubmitAsync(string viewToken, string? author, string body)
    {
        var response = await SubmitRawAsync(viewToken, new { authorDisplayName = author, body });
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static Task<JsonElement> ManagerListAsync(HttpClient client, Guid partyId) =>
        client.GetFromJsonAsync<JsonElement>($"/api/parties/{partyId}/guestbook");

    private static async Task<HttpStatusCode> ModerateAsync(
        HttpClient client, Guid partyId, Guid entryId, string action) =>
        (await client.PostAsync($"/api/parties/{partyId}/guestbook/{entryId}/{action}", null)).StatusCode;

    private static async Task<string> ErrorOf(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.TryGetProperty("error", out var error) ? error.GetString() ?? "" : "";
    }

    private async Task<string> PairTvAsync(HttpClient owner)
    {
        var tvClient = _factory.CreateClient();
        var start = await tvClient.PostAsync("/api/tv/pairing/start", null);
        start.EnsureSuccessStatusCode();
        var started = (await start.Content.ReadFromJsonAsync<NubArca.Api.Tv.TvPairingStartedDto>())!;
        (await owner.PostAsJsonAsync(
            $"/api/tv/pairing/{started.PublicCode}/approve",
            new
            {
                pairingSecret = started.PairingSecret,
                personalCode = "URDLSUDLR",
                personalCodeConfirmation = "URDLSUDLR",
            })).EnsureSuccessStatusCode();
        var poll = new HttpRequestMessage(
            HttpMethod.Get, $"/api/tv/pairing/{started.PublicCode}/status");
        poll.Headers.Add(NubArca.Api.Tv.TvPairingService.PairingSecretHeader, started.PairingSecret);
        var response = await tvClient.SendAsync(poll);
        response.EnsureSuccessStatusCode();
        return response.Headers.GetValues("Set-Cookie").Single();
    }

    private async Task<JsonElement> TvMessagesAsync(string tvCookie, Guid albumId)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Get, $"/api/tv/albums/{albumId}/party-messages");
        var cookie = tvCookie.Split(';', 2)[0];
        request.Headers.Add(
            "Cookie", $"{NubArca.Api.Tv.TvPairingService.CookieName}={cookie[(cookie.IndexOf('=') + 1)..]}");
        var response = await _factory.CreateClient().SendAsync(request);
        response.EnsureSuccessStatusCode();
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
        return body.GetProperty("messages");
    }
}
