using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Tv;

namespace NubArca.Api.Tests.Tv;

// Authorization + behaviour of the LOCAL natural-language interpret endpoint.
// Mirrors the TV Personal Area gate: TV session cookie + live unlock grant, no
// owner-web auth, no-store, POST body only, safe audit. Also pins deterministic
// interpretation + STRICTLY owner-scoped person resolution (no cross-owner leak).
public sealed class TvInterpretCommandTests : IDisposable
{
    private const string Pin = "123456";
    private const string Url = "/api/tv/personal/gallery/interpret-command";

    private readonly SqliteWebApplicationFactory _factory = new();

    public TvInterpretCommandTests() => _factory.EnsureDatabaseCreated();
    public void Dispose() => _factory.Dispose();

    private static object Request(string command, object? currentFilters = null) => new
    {
        command,
        locale = "it-IT",
        timeZone = "Europe/Rome",
        currentDate = "2026-07-12T12:00:00Z",
        currentFilters = currentFilters ?? new { },
    };

    [Fact]
    public async Task Requires_A_Tv_Session()
    {
        var anon = _factory.CreateClient();
        var response = await anon.PostAsJsonAsync(Url, Request("Mostrami le preferite"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Owner_Web_Auth_Alone_Cannot_Interpret()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var response = await owner.PostAsJsonAsync(Url, Request("Mostrami le preferite"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Paired_Session_Without_Grant_Is_Locked()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var cookie = await PairTvAsync(owner);
        var response = await SendAsync(cookie, Url, grant: null, json: Request("Mostrami le preferite"));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Grant_From_Another_Session_Is_Rejected()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var cookieA = await PairTvAsync(owner);
        var tokenA = await UnlockTokenAsync(cookieA);
        var cookieB = await PairTvAsync(owner);
        // Present session B with session A's grant → rejected.
        var response = await SendAsync(cookieB, Url, tokenA, Request("preferite"));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Valid_Session_And_Grant_Returns_Draft_No_Store()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var cookie = await PairTvAsync(owner);
        var token = await UnlockTokenAsync(cookie);

        var response = await SendAsync(cookie, Url, token, Request("Mostrami solo le preferite"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString() ?? "");

        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var draft = root.GetProperty("draft");
        Assert.Equal("replace", draft.GetProperty("operation").GetString());
        Assert.True(draft.GetProperty("favorite").GetBoolean());
        Assert.False(root.GetProperty("requiresClarification").GetBoolean());
    }

    [Fact]
    public async Task Empty_Command_Is_Unsupported()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var cookie = await PairTvAsync(owner);
        var token = await UnlockTokenAsync(cookie);
        var response = await SendAsync(cookie, Url, token, Request("   "));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Person_Resolution_Is_Owner_Scoped()
    {
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync();
        await SeedPersonAsync(ownerId, "Anna");
        var cookie = await PairTvAsync(owner);
        var token = await UnlockTokenAsync(cookie);

        var response = await SendAsync(cookie, Url, token, Request("Foto di Anna al mare"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        var resolved = root.GetProperty("resolvedPeople");
        Assert.Equal(1, resolved.GetArrayLength());
        Assert.Equal("Anna", resolved[0].GetProperty("name").GetString());
        var include = root.GetProperty("draft").GetProperty("peopleInclude");
        Assert.Equal(1, include.GetArrayLength());
        // The visual residual is a semantic query, not a person or metadata term.
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("draft").GetProperty("semanticQuery").GetString()));
    }

    [Fact]
    public async Task Ambiguous_Person_Requires_Clarification()
    {
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync();
        await SeedPersonAsync(ownerId, "Marco Rossi");
        await SeedPersonAsync(ownerId, "Marco Bianchi");
        var cookie = await PairTvAsync(owner);
        var token = await UnlockTokenAsync(cookie);

        var response = await SendAsync(cookie, Url, token, Request("Foto di Marco"));
        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.True(root.GetProperty("requiresClarification").GetBoolean());
        var ambiguities = root.GetProperty("ambiguities");
        Assert.Equal(1, ambiguities.GetArrayLength());
        Assert.Equal(2, ambiguities[0].GetProperty("candidates").GetArrayLength());
        // The ambiguous person is NOT silently added to the draft.
        Assert.Equal(0, root.GetProperty("draft").GetProperty("peopleInclude").GetArrayLength());
    }

    [Fact]
    public async Task Foreign_Owner_Person_Does_Not_Resolve()
    {
        // Owner A has a person "Anna"; owner B (the caller) does not.
        var (ownerAId, _) = await _factory.CreateAuthenticatedClientAsync("a@example.com");
        await SeedPersonAsync(ownerAId, "Anna");
        var (_, ownerB) = await _factory.CreateAuthenticatedClientAsync("b@example.com");
        var cookie = await PairTvAsync(ownerB);
        var token = await UnlockTokenAsync(cookie);

        var response = await SendAsync(cookie, Url, token, Request("Foto di Anna"));
        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(0, root.GetProperty("resolvedPeople").GetArrayLength());
        Assert.Equal(0, root.GetProperty("draft").GetProperty("peopleInclude").GetArrayLength());
    }

    [Fact]
    public async Task Gallery_Endpoint_Wires_Semantic_Query()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var cookie = await PairTvAsync(owner);
        var token = await UnlockTokenAsync(cookie);
        // No active profile seeded → semantic path is cleanly unavailable, but the
        // endpoint must still ROUTE the semantic query (semanticActive true).
        var response = await SendGetAsync(cookie,
            "/api/tv/personal/gallery?semanticQuery=mare%20al%20tramonto&semanticTopK=300", token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.True(root.GetProperty("semanticActive").GetBoolean());
        Assert.Equal(0, root.GetProperty("totalCount").GetInt32());
    }

    // ── helpers (mirrors TvPersonalGalleryTests) ─────────────────────────────

    private async Task SeedPersonAsync(Guid ownerUserId, string name)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.People.Add(new Person
        {
            Id = Guid.NewGuid(), OwnerUserId = ownerUserId, DisplayName = name,
            IsArchived = false, CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private async Task<string> PairTvAsync(HttpClient owner)
    {
        var tvClient = _factory.CreateClient();
        var start = await tvClient.PostAsync("/api/tv/pairing/start", null);
        start.EnsureSuccessStatusCode();
        var started = (await start.Content.ReadFromJsonAsync<TvPairingStartedDto>())!;
        (await owner.PostAsJsonAsync(
            $"/api/tv/pairing/{started.PublicCode}/approve",
            new { pairingSecret = started.PairingSecret, personalPin = Pin, personalPinConfirmation = Pin }))
            .EnsureSuccessStatusCode();
        var pollRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/tv/pairing/{started.PublicCode}/status");
        pollRequest.Headers.Add(TvPairingService.PairingSecretHeader, started.PairingSecret);
        var poll = await tvClient.SendAsync(pollRequest);
        poll.EnsureSuccessStatusCode();
        return poll.Headers.GetValues("Set-Cookie").Single();
    }

    private async Task<string> UnlockTokenAsync(string setCookie)
    {
        var response = await SendAsync(setCookie, "/api/tv/personal/unlock", grant: null, json: new { pin = Pin });
        response.EnsureSuccessStatusCode();
        var dto = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        return dto.GetProperty("unlockToken").GetString()!;
    }

    private Task<HttpResponseMessage> SendAsync(string setCookie, string url, string? grant, object? json)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("Cookie", $"{TvPairingService.CookieName}={CookieValue(setCookie)}");
        if (grant is not null) request.Headers.Add(TvPersonalAreaService.UnlockHeader, grant);
        if (json is not null) request.Content = JsonContent.Create(json);
        return _factory.CreateClient().SendAsync(request);
    }

    private Task<HttpResponseMessage> SendGetAsync(string setCookie, string url, string grant)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Cookie", $"{TvPairingService.CookieName}={CookieValue(setCookie)}");
        request.Headers.Add(TvPersonalAreaService.UnlockHeader, grant);
        return _factory.CreateClient().SendAsync(request);
    }

    private static string CookieValue(string setCookie)
    {
        var value = setCookie.Split(';', 2)[0];
        return value[(value.IndexOf('=') + 1)..];
    }
}
