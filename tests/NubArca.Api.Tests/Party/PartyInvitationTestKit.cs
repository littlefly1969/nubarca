using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Access;
using NubArca.Api.Auth.Recovery;
using NubArca.Api.Data;
using NubArca.Api.Party;
using NubArca.Api.Tests.Endpoints;

namespace NubArca.Api.Tests.Party;

/// <summary>
/// The steps every guest-list test takes, named once: a host, a party, a group,
/// an invitation actually emailed, and the personal link pulled out of that
/// email exactly as the recipient would.
///
/// <para>The link is taken from the recorded MESSAGE, not derived by the test,
/// wherever a message exists — so a test that passes proves the email carried a
/// link that opens the right group. Only a Draft party, which no email may
/// reach, derives the token directly.</para>
/// </summary>
internal static class PartyInvitationTestKit
{
    /// <summary>The same neutral origin the recovery tests use — never an installation's.</summary>
    internal const string Origin = "https://cloud.example.com";

    internal static readonly string[] EveryPartyPermission =
    [
        Permissions.PartyAccess, Permissions.PartyContributions,
        Permissions.PartyGames, Permissions.PartyPrint, Permissions.PartyFaceSearch,
    ];

    /// <summary>A host with a public origin configured, so invitations can be emailed.</summary>
    internal static SqliteWebApplicationFactory NewFactory()
    {
        var factory = new SqliteWebApplicationFactory(
            new Dictionary<string, string?> { ["Mail:PublicOrigin"] = Origin }, poolHost: true);
        factory.EnsureDatabaseCreated();
        return factory;
    }

    internal static Task<(Guid UserId, HttpClient Client)> NewHostAsync(SqliteWebApplicationFactory factory) =>
        factory.CreatePermissionClientAsync($"host-{Guid.NewGuid():N}@example.com", EveryPartyPermission);

    internal static async Task<Guid> CreatePartyAsync(
        HttpClient owner, string title = "Matrimonio di Marta", string? eventStartsAt = "2027-06-12T12:00:00Z")
    {
        var response = await owner.PostAsJsonAsync("/api/parties", new { title, eventStartsAt });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    internal static async Task<JsonElement> GetPartyAsync(HttpClient owner, Guid partyId) =>
        await owner.GetFromJsonAsync<JsonElement>($"/api/parties/{partyId}");

    internal static async Task<JsonElement> AdvanceAsync(HttpClient owner, Guid partyId, string action)
    {
        var version = (await GetPartyAsync(owner, partyId)).GetProperty("version").GetInt32();
        var response = await owner.PostAsJsonAsync($"/api/parties/{partyId}/{action}", new { version });
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>
    /// Gives the party a main album and opens its public QR — which publishes a
    /// Draft, exactly as the workspace does. Returns the album and the QR token.
    /// </summary>
    internal static async Task<(Guid AlbumId, string ViewToken, string? UploadToken)> OpenPublicQrAsync(
        HttpClient owner, Guid partyId, string albumName = "Album della festa")
    {
        var album = await owner.PostAsJsonAsync("/api/albums", new { name = albumName });
        album.EnsureSuccessStatusCode();
        var albumId = (await album.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var party = await GetPartyAsync(owner, partyId);
        (await owner.PutAsJsonAsync(
            $"/api/parties/{partyId}/media/main",
            new { albumId, version = party.GetProperty("version").GetInt32() })).EnsureSuccessStatusCode();
        var settings = await owner.PatchAsJsonAsync($"/api/albums/{albumId}/party-settings", new { enabled = true });
        settings.EnsureSuccessStatusCode();
        var status = await settings.Content.ReadFromJsonAsync<JsonElement>();
        var viewToken = status.GetProperty("partyUrl").GetString()!["/party/".Length..];
        var uploadUrl = status.GetProperty("uploadUrl").GetString();
        var uploadToken = uploadUrl?["/party/".Length..^"/upload".Length];
        return (albumId, viewToken, uploadToken);
    }

    /// <summary>One of the owner's own photographs, in no album.</summary>
    internal static async Task<Guid> UploadPhotoAsync(HttpClient owner, string name = "photo.png")
    {
        var part = new ByteArrayContent(NubArca.Api.Tests.Metadata.ImageFixtures.PlainPng());
        part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        var upload = await owner.PostAsync("/api/files", new MultipartFormDataContent { { part, "file", name } });
        upload.EnsureSuccessStatusCode();
        return (await upload.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    internal static object GroupBody(
        string label, string email, int maxAdditionalGuests, IEnumerable<object> guests,
        int version = 0, string? phone = null) =>
        new { label, recipientEmail = email, phone, maxAdditionalGuests, guests, version };

    /// <summary>Adds a group of named guests and returns its row from the list the server answered.</summary>
    internal static async Task<JsonElement> AddGroupAsync(
        HttpClient owner, Guid partyId, string label, string email, int maxAdditionalGuests = 0,
        params string[] names)
    {
        var response = await owner.PostAsJsonAsync(
            $"/api/parties/{partyId}/invitation-groups",
            GroupBody(label, email, maxAdditionalGuests, names.Select(n => (object)new { name = n })));
        response.EnsureSuccessStatusCode();
        return Group(await response.Content.ReadFromJsonAsync<JsonElement>(), label);
    }

    internal static JsonElement Group(JsonElement guestList, string label) =>
        guestList.GetProperty("groups").EnumerateArray()
            .Single(g => g.GetProperty("label").GetString() == label);

    internal static async Task<JsonElement> GuestListAsync(HttpClient owner, Guid partyId) =>
        await owner.GetFromJsonAsync<JsonElement>($"/api/parties/{partyId}/guest-list");

    internal static Task<HttpResponseMessage> SendAsync(
        HttpClient owner, Guid partyId, Guid groupId, Guid? requestId = null, int? partyVersion = null,
        string action = "send") =>
        owner.PostAsJsonAsync(
            $"/api/parties/{partyId}/invitation-groups/{groupId}/{action}",
            new { clientRequestId = requestId ?? Guid.NewGuid(), partyVersion });

    /// <summary>
    /// Emails the group its invitation — publishing a Draft on the way, as the
    /// first send does — and returns the personal token from that email.
    /// </summary>
    internal static async Task<string> InviteAsync(
        SqliteWebApplicationFactory factory, HttpClient owner, Guid partyId, Guid groupId)
    {
        var version = (await GetPartyAsync(owner, partyId)).GetProperty("version").GetInt32();
        var response = await SendAsync(owner, partyId, groupId, partyVersion: version);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("sent", body.GetProperty("delivery").GetProperty("status").GetString());
        return TokenFrom(factory.EmailSender.Last!);
    }

    /// <summary>The personal token, read out of the email the way its recipient reads it.</summary>
    internal static string TokenFrom(EmailMessage message)
    {
        const string marker = "/party/invite/";
        var start = message.TextBody.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, "The message carries no personal invitation link.");
        start += marker.Length;
        var end = start;
        while (end < message.TextBody.Length && !char.IsWhiteSpace(message.TextBody[end])) end++;
        return message.TextBody[start..end];
    }

    /// <summary>
    /// The CURRENT generation's raw token, derived as the server derives it.
    /// Only for a Draft party, which no email can reach.
    /// </summary>
    internal static string CurrentToken(SqliteWebApplicationFactory factory, Guid groupId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var capabilityId = db.PartyInvitationGroups.Where(g => g.Id == groupId).Select(g => g.CapabilityId).Single();
        return scope.ServiceProvider.GetRequiredService<PartyInvitationTokens>().Derive(capabilityId);
    }

    internal static async Task<JsonElement> ViewAsync(HttpClient guest, string token) =>
        await guest.GetFromJsonAsync<JsonElement>($"/api/party-invitations/{token}");

    internal static Task<HttpResponseMessage> RsvpAsync(HttpClient guest, string token, object body) =>
        guest.PutAsJsonAsync($"/api/party-invitations/{token}/rsvp", body);

    internal static Guid GuestId(JsonElement view, string name) =>
        view.GetProperty("invitation").GetProperty("guests").EnumerateArray()
            .Single(g => g.GetProperty("name").GetString() == name)
            .GetProperty("id").GetGuid();

    internal static JsonElement GuestRow(JsonElement view, string name) =>
        view.GetProperty("invitation").GetProperty("guests").EnumerateArray()
            .Single(g => g.GetProperty("name").GetString() == name);

    internal static int Version(JsonElement view) => view.GetProperty("invitation").GetProperty("version").GetInt32();

    /// <summary>One person as the HOST's list shows them.</summary>
    internal static JsonElement OwnerGuest(JsonElement group, string name) =>
        group.GetProperty("guests").EnumerateArray().Single(g => g.GetProperty("name").GetString() == name);

    internal static JsonElement Question(JsonElement guestList, Guid questionId) =>
        guestList.GetProperty("questions").EnumerateArray().Single(q => q.GetProperty("id").GetGuid() == questionId);

    /// <summary>A reply naming every named guest with one status, and nothing else.</summary>
    internal static object Reply(
        JsonElement view, IReadOnlyDictionary<string, string> statuses,
        IEnumerable<object>? additionalGuests = null, IEnumerable<object>? answers = null) =>
        new
        {
            version = Version(view),
            guests = statuses.Select(s => new { guestId = GuestId(view, s.Key), status = s.Value, dietaryNotes = (string?)null }),
            additionalGuests = additionalGuests ?? [],
            answers = answers ?? [],
        };

    internal static async Task<Guid> AddQuestionAsync(
        HttpClient owner, Guid partyId, string prompt, string kind, bool required = false,
        string[]? options = null)
    {
        var response = await owner.PostAsJsonAsync(
            $"/api/parties/{partyId}/rsvp-questions", new { prompt, kind, required, options });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("questions")
            .EnumerateArray().Single(q => q.GetProperty("prompt").GetString() == prompt)
            .GetProperty("id").GetGuid();
    }

    internal static async Task<string> ErrorOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()!;
}
