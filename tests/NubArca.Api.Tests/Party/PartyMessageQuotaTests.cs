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
/// A greeting budget, and the difference between a budget and a speed limit.
///
/// <para>A quota says the host allowed this guest so many greetings and they
/// have sent them — no amount of waiting changes it, so it is a
/// <c>409</c>. Rate limiting says the requests are arriving too fast, which
/// waiting does change, so it stays a <c>429</c>. A client has to be able to
/// tell them apart, and giving both the same code would make that
/// impossible.</para>
///
/// <para>The other rule these hold: a slot is spent by SENDING, not by being
/// approved. Moderation is a judgement about a message, never a refund — or a
/// host declining something would be handing the guest another go at it.</para>
/// </summary>
public sealed class PartyMessageQuotaTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory = new();
    public PartyMessageQuotaTests() => _factory.EnsureDatabaseCreated();
    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Zero_is_unlimited_and_is_what_every_existing_party_has()
    {
        var party = await SetUpAsync(messagesPerGuest: 0);
        var guest = _factory.CreateClient();

        for (var i = 0; i < 25; i++)
        {
            var sent = await SendAsync(guest, party.Upload, $"Auguri {i}");
            Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
            var body = await sent.Content.ReadFromJsonAsync<JsonElement>();
            // Null, never 0: a client must not read "no limit" as "none left".
            Assert.Equal(JsonValueKind.Null, body.GetProperty("messagesRemaining").ValueKind);
        }

        var session = await SessionAsync(guest, party.Upload);
        Assert.Equal(JsonValueKind.Null, session.GetProperty("maxMessages").ValueKind);
        Assert.Equal(JsonValueKind.Null, session.GetProperty("remainingMessages").ValueKind);
        Assert.Equal(25, session.GetProperty("usedMessages").GetInt32());
    }

    [Fact]
    public async Task The_boundary_is_exact_and_the_refusal_is_a_conflict_not_a_rate_limit()
    {
        var party = await SetUpAsync(messagesPerGuest: 3);
        var guest = _factory.CreateClient();

        for (var i = 1; i <= 3; i++)
        {
            var sent = await SendAsync(guest, party.Upload, $"Auguri {i}");
            Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
            Assert.Equal(3 - i,
                (await sent.Content.ReadFromJsonAsync<JsonElement>())
                    .GetProperty("messagesRemaining").GetInt32());
        }

        var refused = await SendAsync(guest, party.Upload, "Uno di troppo");
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var error = await refused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("guest_message_limit_reached", error.GetProperty("error").GetString());
        Assert.Equal(3, error.GetProperty("maxMessages").GetInt32());

        // Exactly three, and the refusal wrote nothing.
        Assert.Equal(3, await MessageCountAsync());
        Assert.Equal(3, (await Participant()).SubmittedMessageCount);
    }

    [Fact]
    public async Task The_last_slot_cannot_be_taken_twice_in_sequence_either()
    {
        // The genuine two-connection race lives in PartyMessageQuotaRaceTests —
        // this host shares one SQLite connection, so simultaneity here would be
        // testing the harness. What this covers is the ordinary case: the second
        // request sees a budget the first has already spent.
        var party = await SetUpAsync(messagesPerGuest: 1);
        var guest = _factory.CreateClient();
        await SessionAsync(guest, party.Upload);

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(guest, party.Upload, "Primo")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(guest, party.Upload, "Secondo")).StatusCode);
        Assert.Equal(1, await MessageCountAsync());
        Assert.Equal(1, (await Participant()).SubmittedMessageCount);
    }

    [Fact]
    public async Task A_pending_greeting_spends_a_slot_just_like_a_visible_one()
    {
        // Approval is about what the room sees, not about what the guest spent.
        var party = await SetUpAsync(messagesPerGuest: 1, requireApproval: true);
        var guest = _factory.CreateClient();

        var sent = await SendAsync(guest, party.Upload, "In attesa");
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        Assert.Equal("pending",
            (await sent.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());

        Assert.Equal(HttpStatusCode.Conflict,
            (await SendAsync(guest, party.Upload, "Un altro")).StatusCode);
    }

    [Theory]
    // Reject is reachable only from pending and hide only from visible, so each
    // runs in the approval mode that makes it a real transition rather than a
    // 400 the assertion below would never notice.
    [InlineData("reject", true)]
    [InlineData("hide", false)]
    public async Task Moderation_is_not_a_refund(string action, bool requireApproval)
    {
        var party = await SetUpAsync(messagesPerGuest: 1, requireApproval: requireApproval);
        var guest = _factory.CreateClient();
        var sent = await SendAsync(guest, party.Upload, "Auguri");
        var id = (await sent.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        (await party.Owner.PostAsync(
            $"/api/albums/{party.Album}/party-messages/{id}/{action}", null))
            .EnsureSuccessStatusCode();

        // The host judged the message. The guest still sent it.
        Assert.Equal(HttpStatusCode.Conflict,
            (await SendAsync(guest, party.Upload, "Ci riprovo")).StatusCode);
        Assert.Equal(1, (await Participant()).SubmittedMessageCount);
    }

    [Theory]
    [InlineData("", "Anna")]
    [InlineData("   ", "Anna")]
    public async Task A_greeting_that_was_never_valid_costs_nothing(string text, string name)
    {
        var party = await SetUpAsync(messagesPerGuest: 1);
        var guest = _factory.CreateClient();
        // Establish the guest, so there is a counter that could have moved.
        await SessionAsync(guest, party.Upload);

        var refused = await SendAsync(guest, party.Upload, text, name);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(0, (await Participant()).SubmittedMessageCount);

        // And the slot is still there to be used.
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(guest, party.Upload, "Auguri")).StatusCode);
    }

    [Fact]
    public async Task An_over_long_name_is_refused_before_the_budget_is_touched()
    {
        var party = await SetUpAsync(messagesPerGuest: 1);
        var guest = _factory.CreateClient();
        await SessionAsync(guest, party.Upload);

        var refused = await SendAsync(guest, party.Upload, "Auguri", new string('n', 200));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(0, (await Participant()).SubmittedMessageCount);
    }

    [Fact]
    public async Task Two_guests_have_two_budgets()
    {
        var party = await SetUpAsync(messagesPerGuest: 1);
        var a = _factory.CreateClient();
        var b = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(a, party.Upload, "Da A")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(a, party.Upload, "Ancora A")).StatusCode);
        // A budget belongs to a guest, not to the party.
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(b, party.Upload, "Da B")).StatusCode);
    }

    [Fact]
    public async Task The_owner_sets_it_where_the_other_guest_quotas_live()
    {
        var party = await SetUpAsync(messagesPerGuest: 0);

        var saved = await party.Owner.PatchAsJsonAsync(
            $"/api/albums/{party.Album}/party-slideshow-settings",
            new { maxMessagesPerParticipant = 4 });
        saved.EnsureSuccessStatusCode();
        Assert.Equal(4, (await saved.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("maxMessagesPerParticipant").GetInt32());

        // Server-authoritative: out of range is refused, and nothing else in the
        // settings changed on the way.
        Assert.Equal(HttpStatusCode.BadRequest, (await party.Owner.PatchAsJsonAsync(
            $"/api/albums/{party.Album}/party-slideshow-settings",
            new { maxMessagesPerParticipant = -1 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await party.Owner.PatchAsJsonAsync(
            $"/api/albums/{party.Album}/party-slideshow-settings",
            new { maxMessagesPerParticipant = 10001 })).StatusCode);

        var status = await (await party.Owner.GetAsync($"/api/albums/{party.Album}/party-settings"))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(4, status.GetProperty("maxMessagesPerParticipant").GetInt32());
    }

    // --- helpers -----------------------------------------------------------

    private sealed record Party(HttpClient Owner, Guid Album, string View, string Upload);

    private static Task<HttpResponseMessage> SendAsync(
        HttpClient guest, string uploadToken, string text, string name = "Anna") =>
        guest.PostAsJsonAsync($"/api/party/{uploadToken}/messages",
            new { displayName = name, text });

    private static async Task<JsonElement> SessionAsync(HttpClient guest, string uploadToken)
    {
        var response = await guest.PostAsync($"/api/party/{uploadToken}/upload-session", null);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<int> MessageCountAsync()
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .PartyMessages.CountAsync();
    }

    private async Task<PartyParticipant> Participant()
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .PartyParticipants.OrderBy(p => p.CreatedAt).FirstAsync();
    }

    private async Task<Party> SetUpAsync(int messagesPerGuest, bool requireApproval = false)
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync($"{Guid.NewGuid():N}@example.com");
        var album = (await (await owner.PostAsJsonAsync("/api/albums",
                new { name = $"Festa {Guid.NewGuid():N}" }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        (await owner.PatchAsJsonAsync($"/api/albums/{album}/party-settings", new
        {
            enabled = true, uploadEnabled = true, requireMessageApproval = requireApproval,
        })).EnsureSuccessStatusCode();
        (await owner.PatchAsJsonAsync($"/api/albums/{album}/party-slideshow-settings",
            new { maxMessagesPerParticipant = messagesPerGuest })).EnsureSuccessStatusCode();

        var status = await (await owner.GetAsync($"/api/albums/{album}/party-settings"))
            .Content.ReadFromJsonAsync<JsonElement>();
        var view = status.GetProperty("partyUrl").GetString()!["/party/".Length..];
        var uploadUrl = status.GetProperty("uploadUrl").GetString()!["/party/".Length..];
        return new Party(owner, album, view,
            uploadUrl[..uploadUrl.IndexOf("/upload", StringComparison.Ordinal)]);
    }
}
