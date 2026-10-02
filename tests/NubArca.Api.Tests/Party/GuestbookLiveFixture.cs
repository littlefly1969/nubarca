using System.Net.Http.Json;
using System.Text.Json;

namespace NubArca.Api.Tests.Party;

/// <summary>
/// The regia's guest-book live controls, driven through the host's real routes
/// — shared by the tests that need the book open to the room or on the screen.
/// </summary>
internal static class GuestbookLiveFixture
{
    public static Task<JsonElement> ControlAsync(HttpClient owner, Guid albumId) =>
        owner.GetFromJsonAsync<JsonElement>($"/api/albums/{albumId}/party-guestbook-live");

    public static async Task<HttpResponseMessage> CommandAsync(
        HttpClient owner, Guid albumId, string command, int? expectedVersion = null)
    {
        var version = expectedVersion ?? (await ControlAsync(owner, albumId)).GetProperty("version").GetInt32();
        return await owner.PostAsJsonAsync(
            $"/api/albums/{albumId}/party-guestbook-live/commands",
            new { command, expectedVersion = version });
    }

    /// <summary>Lets the whole room read the book while the party is live.</summary>
    public static async Task OpenToTheRoomAsync(HttpClient owner, Guid albumId) =>
        (await CommandAsync(owner, albumId, "enable_viewing")).EnsureSuccessStatusCode();
}
