using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using NubArca.Api.Tests.Endpoints;

namespace NubArca.Api.Tests.Party;

/// <summary>
/// The steps every Party Crew test takes, named once.
///
/// <para>The collaborator's browser is a <b>separate HttpClient with its own
/// cookie jar</b>, because that is the whole point: the challenge and the
/// device are cookies, and a test that reused the host's client would prove
/// nothing about either. The one-time code is pulled out of the RECORDED
/// MESSAGE exactly as the person pulls it out of their inbox, so a passing test
/// proves the email carried a code that works.</para>
/// </summary>
internal static class PartyCrewTestKit
{
    internal static async Task<JsonElement> CrewAsync(HttpClient owner, Guid partyId) =>
        await owner.GetFromJsonAsync<JsonElement>($"/api/parties/{partyId}/crew");

    /// <summary>Adds a collaborator and returns the link the host would hand them.</summary>
    internal static async Task<(Guid CollaboratorId, string InviteUrl)> AddCollaboratorAsync(
        HttpClient owner, Guid partyId, string name, string email, string role)
    {
        var response = await owner.PostAsJsonAsync(
            $"/api/parties/{partyId}/crew",
            new { displayName = name, email, roleKey = role });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("collaboratorId").GetGuid(), body.GetProperty("inviteUrl").GetString()!);
    }

    /// <summary>The raw token out of the link's FRAGMENT, exactly as the page reads it.</summary>
    internal static string TokenOf(string inviteUrl)
    {
        var at = inviteUrl.IndexOf("#token=", StringComparison.Ordinal);
        Assert.True(at > 0, $"The invite link carries no fragment token: {inviteUrl}");
        return inviteUrl[(at + "#token=".Length)..];
    }

    /// <summary>The six digits out of the message that was actually composed.</summary>
    internal static string CodeFromLastEmail(SqliteWebApplicationFactory factory)
    {
        var body = factory.EmailSender.Last!.TextBody;
        var grouped = Regex.Match(body, @"\b(\d{3}) (\d{3})\b");
        Assert.True(grouped.Success, $"No grouped code in the message:\n{body}");
        return grouped.Groups[1].Value + grouped.Groups[2].Value;
    }

    /// <summary>
    /// A whole pairing, from the link to a device cookie the returned client
    /// holds. Fails the test if any step is refused.
    /// </summary>
    internal static async Task<HttpClient> PairAsync(
        SqliteWebApplicationFactory factory, string inviteUrl)
    {
        var device = factory.CreateClient();
        var started = await device.PostAsJsonAsync(
            "/api/party-crew/auth/invite", new { token = TokenOf(inviteUrl) });
        started.EnsureSuccessStatusCode();

        var verified = await device.PostAsJsonAsync(
            "/api/party-crew/auth/verify", new { code = CodeFromLastEmail(factory) });
        verified.EnsureSuccessStatusCode();
        var body = await verified.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Paired", body.GetProperty("outcome").GetString());
        return device;
    }

    /// <summary>Starts a pairing and stops at the verified code, whatever it produced.</summary>
    internal static async Task<(HttpClient Device, JsonElement Result)> VerifyAsync(
        SqliteWebApplicationFactory factory, string inviteUrl)
    {
        var device = factory.CreateClient();
        (await device.PostAsJsonAsync(
            "/api/party-crew/auth/invite", new { token = TokenOf(inviteUrl) })).EnsureSuccessStatusCode();
        var verified = await device.PostAsJsonAsync(
            "/api/party-crew/auth/verify", new { code = CodeFromLastEmail(factory) });
        verified.EnsureSuccessStatusCode();
        return (device, await verified.Content.ReadFromJsonAsync<JsonElement>());
    }

    internal static async Task<JsonElement> SessionAsync(HttpClient device, Guid partyId)
    {
        var response = await device.GetAsync($"/api/party-crew/parties/{partyId}/session");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    internal static string[] CapabilitiesOf(JsonElement session) =>
        [.. session.GetProperty("capabilities").EnumerateArray().Select(c => c.GetString()!)];

    internal static async Task<string> ErrorOf(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.TryGetProperty("error", out var error) ? error.GetString() ?? "" : "";
    }

    /// <summary>Every Party Crew refusal is the same generic nothing.</summary>
    internal static void AssertRefused(HttpResponseMessage response) =>
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
}
