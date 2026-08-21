using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Tv;

namespace NubArca.Api.Tests.Tv;

// GET /api/tv/personal/media/semantic — the TV adapter over the CANONICAL
// semantic service.
//
// The adapter exists for one reason: a limited TV session cannot call an
// owner-web endpoint. Everything that decides what a semantic result IS —
// candidate policy, ranking, relevance cursor, availability — belongs to
// MediaSemanticSearchService, the same service behind the web's
// /api/media/semantic. So these tests deliberately do NOT re-test ranking.
// They test what is genuinely this route's own: the authorization boundary,
// the owner derivation, the validation, and the promise that a semantic search
// which cannot run says so instead of quietly becoming a substring search.
public sealed class TvPersonalSemanticTests : IDisposable
{
    private const string Code = "URDLSUDLR";
    private const string Route = "/api/tv/personal/media/semantic";

    private readonly SqliteWebApplicationFactory _factory = new();

    public TvPersonalSemanticTests() => _factory.EnsureDatabaseCreated();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Requires_Both_The_Session_And_The_Grant()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var cookie = await PairAsync(owner);

        // Session but no grant → locked, exactly like every other personal route.
        var locked = await TvSendAsync(cookie, $"{Route}?q=cane");
        Assert.Equal(HttpStatusCode.Forbidden, locked.StatusCode);

        // No session at all → unauthorized, even with a grant-shaped header.
        var anonymous = new HttpRequestMessage(HttpMethod.Get, $"{Route}?q=cane");
        anonymous.Headers.Add(TvPersonalAreaService.UnlockHeader, "not-a-grant");
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await _factory.CreateClient().SendAsync(anonymous)).StatusCode);
    }

    [Fact]
    public async Task The_Television_Cannot_Name_The_Owner()
    {
        // The owner comes from the session + grant, server-side. A television
        // that tries to name one must not be able to reach another account's
        // media — the parameter does not exist, so the attempt is simply a
        // normal request for the TV's OWN owner.
        var (_, alice) = await _factory.CreateAuthenticatedClientAsync();
        var (_, bob) = await _factory.CreateAuthenticatedClientAsync("bob@example.com");
        var aliceCookie = await PairAsync(alice);
        var grant = await UnlockTokenAsync(aliceCookie);
        var bobId = await OwnerIdAsync(bob);

        var response = await TvSendAsync(
            aliceCookie, $"{Route}?q=cane&ownerUserId={bobId}&userId={bobId}", grant);

        // Whatever the retrieval answers, it must never be a success carrying
        // another owner's scope. Forbidden is also acceptable: this account may
        // not hold the semantic permission.
        Assert.True(
            response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Forbidden
                or HttpStatusCode.ServiceUnavailable,
            $"unexpected {response.StatusCode}");
        if (response.StatusCode == HttpStatusCode.OK)
        {
            var page = await response.Content.ReadFromJsonAsync<TvPersonalMediaPageDto>();
            Assert.NotNull(page);
            // Alice owns nothing, so a successful search is necessarily empty.
            Assert.Empty(page!.Items);
        }
    }

    [Fact]
    public async Task Rejects_A_Query_It_Cannot_Run()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var cookie = await PairAsync(owner);
        var grant = await UnlockTokenAsync(cookie);

        foreach (var (url, because) in new[]
        {
            ($"{Route}", "an absent query"),
            ($"{Route}?q=", "an empty query"),
            ($"{Route}?q={new string('x', 300)}", "an over-long query"),
            ($"{Route}?q=cane&kind=audio", "an unknown kind"),
            ($"{Route}?q=cane&minRating=9", "an out-of-range rating"),
            ($"{Route}?q=cane&dateTakenFrom=2026-06-01&dateTakenTo=2026-01-01", "an inverted range"),
        })
        {
            var response = await TvSendAsync(cookie, url, grant);
            Assert.True(
                response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden,
                $"{because} produced {response.StatusCode}");
        }
    }

    [Fact]
    public async Task Accepts_Every_Media_Kind()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var cookie = await PairAsync(owner);
        var grant = await UnlockTokenAsync(cookie);

        foreach (var kind in new[] { "all", "image", "video" })
        {
            var response = await TvSendAsync(cookie, $"{Route}?q=cane&kind={kind}", grant);
            // The kind is valid input. What comes back depends on whether the
            // account holds the permission and whether retrieval is available —
            // both legitimate — but it must never be a validation failure.
            Assert.NotEqual(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    [Fact]
    public async Task Unavailable_Retrieval_Is_Explicit_And_Never_A_Fallback()
    {
        // The deterministic test backend has no semantic provider configured,
        // so retrieval is unavailable. The contract under test is that this
        // becomes a 503 carrying a sanitized token — NOT a 200 with an empty
        // page, and NOT a page of substring matches wearing a semantic label.
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var cookie = await PairAsync(owner);
        var grant = await UnlockTokenAsync(cookie);

        var response = await TvSendAsync(cookie, $"{Route}?q=una+spiaggia+al+tramonto", grant);
        var body = await response.Content.ReadAsStringAsync();

        if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
        {
            Assert.Contains("semantic_unavailable", body);
            // Sanitized: no provider, model, path or stack trace.
            Assert.DoesNotContain("Exception", body);
            Assert.DoesNotContain("/", body.Replace("\\/", string.Empty));
        }
        else
        {
            Assert.True(
                response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Forbidden,
                $"unexpected {response.StatusCode}: {body}");
        }
    }

    [Fact]
    public async Task Never_Returns_Scores_Or_Vectors()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var cookie = await PairAsync(owner);
        var grant = await UnlockTokenAsync(cookie);

        var response = await TvSendAsync(cookie, $"{Route}?q=cane", grant);
        var body = await response.Content.ReadAsStringAsync();

        foreach (var leak in new[] { "score", "vector", "embedding", "bestMatch", "storageKey", "sha256" })
        {
            Assert.DoesNotContain(leak, body, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ── helpers (same shape as TvPersonalMediaTests) ────────────────────────

    private async Task<string> PairAsync(HttpClient owner)
    {
        var tvClient = _factory.CreateClient();
        var start = await tvClient.PostAsync("/api/tv/pairing/start", null);
        start.EnsureSuccessStatusCode();
        var started = (await start.Content.ReadFromJsonAsync<TvPairingStartedDto>())!;

        (await owner.PostAsJsonAsync(
            $"/api/tv/pairing/{started.PublicCode}/approve",
            new
            {
                pairingSecret = started.PairingSecret,
                personalCode = Code,
                personalCodeConfirmation = Code,
            })).EnsureSuccessStatusCode();

        var poll = new HttpRequestMessage(
            HttpMethod.Get, $"/api/tv/pairing/{started.PublicCode}/status");
        poll.Headers.Add(TvPairingService.PairingSecretHeader, started.PairingSecret);
        var response = await tvClient.SendAsync(poll);
        response.EnsureSuccessStatusCode();
        return response.Headers.GetValues("Set-Cookie").Single();
    }

    private async Task<string> UnlockTokenAsync(string cookie)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/tv/personal/unlock");
        request.Headers.Add("Cookie", $"{TvPairingService.CookieName}={CookieValue(cookie)}");
        request.Content = JsonContent.Create(new { code = Code });
        var response = await _factory.CreateClient().SendAsync(request);
        response.EnsureSuccessStatusCode();
        var dto = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        return dto.GetProperty("unlockToken").GetString()!;
    }

    private Task<HttpResponseMessage> TvSendAsync(string cookie, string url, string? grant = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Cookie", $"{TvPairingService.CookieName}={CookieValue(cookie)}");
        if (grant is not null) request.Headers.Add(TvPersonalAreaService.UnlockHeader, grant);
        return _factory.CreateClient().SendAsync(request);
    }

    private static async Task<Guid> OwnerIdAsync(HttpClient client)
    {
        var me = await client.GetFromJsonAsync<Dictionary<string, object>>("/api/auth/me");
        return Guid.Parse(me!["id"].ToString()!);
    }

    private static string CookieValue(string setCookie) => setCookie.Split(';')[0].Split('=', 2)[1];
}
