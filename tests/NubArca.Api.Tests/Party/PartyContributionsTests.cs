using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Tests.Metadata;

namespace NubArca.Api.Tests.Party;

// PARTY-CONTRIBUTIONS-01. A party takes THREE separate things from its guests —
// photographs, greetings for the slideshow, and dedications in the guest book —
// and each is its own decision.
//
// What is defended here, and what would break if it stopped holding:
//   * each switch moves ALONE, so a host who closes the book has not quietly
//     stopped accepting photographs;
//   * greetings default to ON, because every party that existed before the
//     column had a composer and a migration must not take it away;
//   * a party that takes no greetings has no composer ANYWHERE — not in the
//     guest context, not in the upload session — and refuses a hand-built
//     submission with a stable code rather than a shape error;
//   * what guests already wrote SURVIVES the switch: closing a channel hides
//     it, and re-opening brings it back with nothing rewritten;
//   * a client that knows about one switch cannot clear the ones it has never
//     heard of.
public sealed class PartyContributionsTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory = new();

    public PartyContributionsTests() => _factory.EnsureDatabaseCreated();

    public void Dispose() => _factory.Dispose();

    // ── Greetings are on, because they always were ──────────────────────────

    [Fact]
    public async Task A_new_party_takes_greetings_because_every_party_before_the_switch_did()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var albumId = await CreateAlbumAsync(owner);
        var status = await EnablePartyAsync(owner, albumId);

        // The host's own view of the party.
        Assert.True(status.GetProperty("slideshowMessagesEnabled").GetBoolean());
        // …and the book, which is the opposite decision: opt-in.
        Assert.False(status.GetProperty("guestbookEnabled").GetBoolean());

        // The guest's view: a composer to go to.
        var context = await GuestContextAsync(ViewTokenFromStatus(status));
        var capabilities = context.GetProperty("capabilities");
        Assert.EndsWith("?mode=message", capabilities.GetProperty("slideshowMessageUrl").GetString());
        Assert.Equal(JsonValueKind.Null, capabilities.GetProperty("guestbookUrl").ValueKind);

        // And the contribution page's own view, which is what decides whether
        // the written half of that page exists at all.
        var session = await UploadSessionAsync(UploadTokenFromStatus(status));
        Assert.True(session.GetProperty("slideshowMessagesEnabled").GetBoolean());
    }

    [Fact]
    public async Task Turning_greetings_off_removes_the_composer_from_every_surface_that_offers_one()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var albumId = await CreateAlbumAsync(owner);
        var status = await EnablePartyAsync(owner, albumId);

        var after = await SetContributionsAsync(owner, albumId, new { slideshowMessagesEnabled = false });
        Assert.False(after.GetProperty("slideshowMessagesEnabled").GetBoolean());

        // Absent, not present-and-disabled: the guest surface draws a card from
        // a URL, so the absence of the URL is the absence of the card, the tab,
        // the form, the empty state and the fetch behind them.
        var capabilities = (await GuestContextAsync(ViewTokenFromStatus(status))).GetProperty("capabilities");
        Assert.Equal(JsonValueKind.Null, capabilities.GetProperty("slideshowMessageUrl").ValueKind);
        // The photographs are untouched — same page, other half.
        Assert.NotEqual(
            JsonValueKind.Null, capabilities.GetProperty("contributionUrl").ValueKind);

        var session = await UploadSessionAsync(UploadTokenFromStatus(status));
        Assert.False(session.GetProperty("slideshowMessagesEnabled").GetBoolean());
    }

    [Fact]
    public async Task A_greeting_sent_by_hand_to_a_party_that_takes_none_is_refused_with_its_own_code()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var albumId = await CreateAlbumAsync(owner);
        var status = await EnablePartyAsync(owner, albumId);
        var uploadToken = UploadTokenFromStatus(status);
        await SetContributionsAsync(owner, albumId, new { slideshowMessagesEnabled = false });

        // Well-formed, and refused by the party's configuration: a 409 with a
        // stable code, which is what the browser translates. Not a 400 (there
        // is nothing wrong with the text) and not a 404 (the party is right
        // here).
        var refused = await _factory.CreateClient().PostAsJsonAsync(
            $"/api/party/{uploadToken}/messages",
            new { displayName = "Giulia", text = "Auguri!" });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("party_messages_disabled", await ErrorOf(refused));

        // NOTHING WAS STORED. A refusal that quietly kept the text would put a
        // greeting in the host's queue from a party that takes none.
        var queue = await owner.GetFromJsonAsync<JsonElement>($"/api/albums/{albumId}/party-messages");
        Assert.Equal(0, queue.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Greetings_already_written_are_kept_hidden_and_come_back_when_the_host_changes_their_mind()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var albumId = await CreateAlbumAsync(owner);
        var status = await EnablePartyAsync(owner, albumId);
        var tv = await PairTvAsync(owner);
        await SubmitMessageAsync(UploadTokenFromStatus(status), "Ada", "Evviva");
        Assert.Equal(1, (await TvMessagesAsync(tv, albumId)).GetArrayLength());

        await SetContributionsAsync(owner, albumId, new { slideshowMessagesEnabled = false });

        // Off the wall…
        Assert.Equal(0, (await TvMessagesAsync(tv, albumId)).GetArrayLength());

        // …and still in the book the host keeps. The queue stays REACHABLE:
        // closing a channel must never lock a host out of what it collected,
        // and the flag beside it is how the surface says why it went quiet.
        var queue = await owner.GetFromJsonAsync<JsonElement>($"/api/albums/{albumId}/party-messages");
        Assert.False(queue.GetProperty("slideshowMessagesEnabled").GetBoolean());
        Assert.Equal(1, queue.GetProperty("items").GetArrayLength());
        Assert.Equal("visible", queue.GetProperty("items")[0].GetProperty("status").GetString());

        // Re-opening restores it unchanged. Nothing was rewritten on the way
        // out, so nothing has to be repaired on the way back.
        await SetContributionsAsync(owner, albumId, new { slideshowMessagesEnabled = true });
        var messages = await TvMessagesAsync(tv, albumId);
        Assert.Equal(1, messages.GetArrayLength());
        Assert.Equal("Evviva", messages[0].GetProperty("text").GetString());
        Assert.Equal("Ada", messages[0].GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task A_party_that_takes_no_greetings_still_takes_photographs()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var albumId = await CreateAlbumAsync(owner);
        var status = await EnablePartyAsync(owner, albumId);
        var uploadToken = UploadTokenFromStatus(status);
        await SetContributionsAsync(owner, albumId, new { slideshowMessagesEnabled = false });

        // The other two contributions are other decisions. This is the one that
        // would hurt most if the switches were secretly one switch.
        var upload = await UploadGuestPhotoAsync(uploadToken);
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        Assert.True(
            (await owner.GetFromJsonAsync<JsonElement>($"/api/albums/{albumId}/party-settings"))
                .GetProperty("uploadEnabled").GetBoolean());
    }

    // ── Independence ────────────────────────────────────────────────────────

    [Fact]
    public async Task Each_switch_moves_alone()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var albumId = await CreateAlbumAsync(owner);
        await EnablePartyAsync(owner, albumId);

        // Six switches, one request each, every other one asserted unmoved
        // after it. A party is configured over an evening, one decision at a
        // time, and a save that dragged a neighbour along would be found by the
        // host and not by anybody else.
        var book = await SetContributionsAsync(owner, albumId, new { guestbookEnabled = true });
        Assert.True(book.GetProperty("guestbookEnabled").GetBoolean());
        Assert.True(book.GetProperty("slideshowMessagesEnabled").GetBoolean());
        Assert.True(book.GetProperty("uploadEnabled").GetBoolean());
        Assert.False(book.GetProperty("requireGuestbookApproval").GetBoolean());

        var greetings = await SetContributionsAsync(owner, albumId, new { slideshowMessagesEnabled = false });
        Assert.False(greetings.GetProperty("slideshowMessagesEnabled").GetBoolean());
        Assert.True(greetings.GetProperty("guestbookEnabled").GetBoolean());
        Assert.True(greetings.GetProperty("uploadEnabled").GetBoolean());

        var photos = await SetContributionsAsync(owner, albumId, new { uploadEnabled = false });
        Assert.False(photos.GetProperty("uploadEnabled").GetBoolean());
        Assert.True(photos.GetProperty("guestbookEnabled").GetBoolean());
        Assert.False(photos.GetProperty("slideshowMessagesEnabled").GetBoolean());

        var moderation = await SetContributionsAsync(
            owner, albumId, new { requireGuestbookApproval = true, requireMessageApproval = true });
        Assert.True(moderation.GetProperty("requireGuestbookApproval").GetBoolean());
        Assert.True(moderation.GetProperty("requireMessageApproval").GetBoolean());
        // Approving is not accepting: turning moderation on does not re-open
        // a channel the host closed.
        Assert.False(moderation.GetProperty("slideshowMessagesEnabled").GetBoolean());
        Assert.False(moderation.GetProperty("uploadEnabled").GetBoolean());
    }

    [Fact]
    public async Task An_older_client_cannot_switch_off_what_it_has_never_heard_of()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var albumId = await CreateAlbumAsync(owner);
        await EnablePartyAsync(owner, albumId);
        await SetContributionsAsync(
            owner, albumId, new { guestbookEnabled = true, requireGuestbookApproval = true });

        // A form that knows only about photographs, saved. Every field is
        // nullable for exactly this: absence means "no opinion", never "off".
        var after = await SetContributionsAsync(owner, albumId, new { uploadEnabled = true });
        Assert.True(after.GetProperty("guestbookEnabled").GetBoolean());
        Assert.True(after.GetProperty("requireGuestbookApproval").GetBoolean());
        Assert.True(after.GetProperty("slideshowMessagesEnabled").GetBoolean());
    }

    [Fact]
    public async Task An_empty_save_changes_nothing_and_is_not_an_error()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var albumId = await CreateAlbumAsync(owner);
        await EnablePartyAsync(owner, albumId);
        await SetContributionsAsync(owner, albumId, new { guestbookEnabled = true });

        var after = await SetContributionsAsync(owner, albumId, new { });
        Assert.True(after.GetProperty("guestbookEnabled").GetBoolean());
        Assert.True(after.GetProperty("slideshowMessagesEnabled").GetBoolean());
        Assert.True(after.GetProperty("uploadEnabled").GetBoolean());
    }

    // ── None of the three depends on another ────────────────────────────────

    [Fact]
    public async Task Closing_photographs_leaves_the_other_two_reachable()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var albumId = await CreateAlbumAsync(owner);
        var status = await EnablePartyAsync(owner, albumId);
        var uploadToken = UploadTokenFromStatus(status);
        await SetContributionsAsync(owner, albumId, new { guestbookEnabled = true });

        var closed = await SetContributionsAsync(owner, albumId, new { uploadEnabled = false });

        // THE PAGE SURVIVES. Its address used to hang off the photo switch, so
        // closing photographs took the composer and the book away with them —
        // three independent switches in the card and one switch in the product.
        Assert.NotEqual(JsonValueKind.Null, closed.GetProperty("uploadUrl").ValueKind);
        var session = await UploadSessionAsync(uploadToken);
        Assert.False(session.GetProperty("uploadEnabled").GetBoolean());
        Assert.True(session.GetProperty("slideshowMessagesEnabled").GetBoolean());
        Assert.True(session.GetProperty("guestbookEnabled").GetBoolean());

        // A greeting still lands.
        (await SubmitMessageAsync(uploadToken, "Ada", "Auguri")).GetProperty("id").GetGuid();

        // A dedication still lands, written on the contribution page's own
        // token — the book is read on the QR's token and written from here.
        var dedication = await _factory.CreateClient().PostAsJsonAsync(
            $"/api/party/{uploadToken}/guestbook",
            new { authorDisplayName = "Ada", body = "Da conservare" });
        dedication.EnsureSuccessStatusCode();

        // And the photographs really are closed: the refusal moved from the
        // token to the action, and says which.
        var upload = await UploadGuestPhotoAsync(uploadToken);
        Assert.Equal(HttpStatusCode.Conflict, upload.StatusCode);
        Assert.Equal("party_uploads_disabled", await ErrorOf(upload));
    }

    [Fact]
    public async Task With_every_channel_closed_there_is_nowhere_to_contribute()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var albumId = await CreateAlbumAsync(owner);
        await EnablePartyAsync(owner, albumId);

        // The address is conditional still; the condition is now all three
        // rather than one of them.
        var closed = await SetContributionsAsync(
            owner, albumId,
            new { uploadEnabled = false, slideshowMessagesEnabled = false, guestbookEnabled = false });
        Assert.Equal(JsonValueKind.Null, closed.GetProperty("uploadUrl").ValueKind);
        // The party itself is untouched: guests still see it.
        Assert.NotEqual(JsonValueKind.Null, closed.GetProperty("partyUrl").ValueKind);
    }

    // ── Whose decision it is ────────────────────────────────────────────────

    [Fact]
    public async Task Somebody_else_s_party_takes_what_its_own_host_says_it_takes()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync("host@example.com");
        var (_, stranger) = await _factory.CreateAuthenticatedClientAsync("stranger@example.com");
        var albumId = await CreateAlbumAsync(owner);
        await EnablePartyAsync(owner, albumId);

        var refused = await stranger.PatchAsJsonAsync(
            $"/api/albums/{albumId}/party-contributions", new { guestbookEnabled = true });
        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);

        Assert.False(
            (await owner.GetFromJsonAsync<JsonElement>($"/api/albums/{albumId}/party-settings"))
                .GetProperty("guestbookEnabled").GetBoolean());
    }

    [Fact]
    public async Task An_album_with_no_party_has_no_contributions_to_configure()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var albumId = await CreateAlbumAsync(owner);

        // No active capability: the host opens the party before deciding what
        // it takes, rather than writing switches onto a revoked row that a
        // later party would inherit.
        var refused = await owner.PatchAsJsonAsync(
            $"/api/albums/{albumId}/party-contributions", new { guestbookEnabled = true });
        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
    }

    // ── The migration's own promise ─────────────────────────────────────────

    [Fact]
    public void The_migration_gives_every_existing_party_the_composer_it_already_had()
    {
        // This asserts the GENERATED MIGRATION, not the model: the model's
        // default governs rows this build creates, while the column's default
        // governs every row that existed before the deploy. They are different
        // promises and only one of them is about the parties already running.
        var migration = File.ReadAllText(MigrationPath("20260919175103_AddPartyContributionsAndGuestbook.cs"));

        Assert.Contains("name: \"SlideshowMessagesEnabled\"", migration);
        Assert.Matches(
            @"name: ""SlideshowMessagesEnabled"",(?s:.*?)defaultValue: true",
            migration);
        // The book is the other way round on purpose: nobody's party silently
        // acquires a guest book because they upgraded.
        Assert.Matches(@"name: ""GuestbookEnabled"",(?s:.*?)defaultValue: false", migration);
        Assert.Matches(@"name: ""RequireGuestbookApproval"",(?s:.*?)defaultValue: false", migration);
    }

    private static string MigrationPath(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "NubArca.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "NubArca.Api", "Data", "Migrations", fileName);
    }

    // ── Fixture ─────────────────────────────────────────────────────────────

    private async Task<Guid> CreateAlbumAsync(HttpClient owner, string name = "Festa")
    {
        var response = await owner.PostAsJsonAsync("/api/albums", new { name });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> EnablePartyAsync(HttpClient owner, Guid albumId)
    {
        (await owner.PatchAsJsonAsync($"/api/albums/{albumId}/tv-settings", new { showOnTv = true }))
            .EnsureSuccessStatusCode();
        var response = await owner.PatchAsJsonAsync(
            $"/api/albums/{albumId}/party-settings", new { enabled = true });
        response.EnsureSuccessStatusCode();
        var settings = await response.Content.ReadFromJsonAsync<JsonElement>();
        await PartyTestHost.StartAsync(owner, settings);
        return settings;
    }

    private static async Task<JsonElement> SetContributionsAsync(
        HttpClient owner, Guid albumId, object body)
    {
        var response = await owner.PatchAsJsonAsync(
            $"/api/albums/{albumId}/party-contributions", body);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private Task<JsonElement> GuestContextAsync(string viewToken) =>
        _factory.CreateClient().GetFromJsonAsync<JsonElement>($"/api/party/{viewToken}");

    private async Task<JsonElement> UploadSessionAsync(string uploadToken)
    {
        var response = await _factory.CreateClient()
            .PostAsync($"/api/party/{uploadToken}/upload-session", null);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<JsonElement> SubmitMessageAsync(string uploadToken, string? displayName, string text)
    {
        var response = await _factory.CreateClient().PostAsJsonAsync(
            $"/api/party/{uploadToken}/messages", new { displayName, text });
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<HttpResponseMessage> UploadGuestPhotoAsync(string uploadToken)
    {
        var part = new ByteArrayContent(ImageFixtures.PlainPng());
        part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        return await _factory.CreateClient().PostAsync(
            $"/api/party/{uploadToken}/upload",
            new MultipartFormDataContent { { part, "file", "guest.png" } });
    }

    private static async Task<string> ErrorOf(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.TryGetProperty("error", out var error) ? error.GetString() ?? "" : "";
    }

    private static string ViewTokenFromStatus(JsonElement status) =>
        status.GetProperty("partyUrl").GetString()!["/party/".Length..];

    private static string UploadTokenFromStatus(JsonElement status)
    {
        var url = status.GetProperty("uploadUrl").GetString()!;
        var rest = url["/party/".Length..];
        return rest[..rest.IndexOf("/upload", StringComparison.Ordinal)];
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
