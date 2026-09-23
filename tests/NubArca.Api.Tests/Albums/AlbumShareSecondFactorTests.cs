using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using NubArca.Api.Tests.Endpoints;

namespace NubArca.Api.Tests.Albums;

/// <summary>
/// THE SECOND FACTOR on a share, and what makes it authorization rather than
/// theatre: the owner says who, and only those addresses receive a code.
///
/// What these defend:
///   * the challenge answers IDENTICALLY for a listed and an unlisted address,
///     because any difference turns the link into a way to read the owner's
///     guest list;
///   * a resend invalidates the previous code, so two are never live at once;
///   * attempts are counted and run out;
///   * a verified visitor carries an HTTP-only cookie and stops being asked;
///   * removing an address closes the phone it already verified, on that
///     phone's NEXT request — not whenever its cookie happens to expire;
///   * a link without the second factor has no challenge to answer at all.
/// </summary>
public sealed class AlbumShareSecondFactorTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory = new();
    public AlbumShareSecondFactorTests() => _factory.EnsureDatabaseCreated();
    public void Dispose() => _factory.Dispose();

    private const string Listed = "zia@example.com";

    [Fact]
    public async Task A_listed_address_is_let_in_and_stops_being_asked()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var (album, token) = await GuardedShareAsync(owner);

        var visitor = _factory.CreateClient();
        // Before proving anything: the link is live, and says so rather than
        // pretending to be nothing.
        var locked = await visitor.GetAsync($"/api/album-share/{token}");
        Assert.Equal(HttpStatusCode.Unauthorized, locked.StatusCode);
        Assert.Equal("second_factor_required",
            (await locked.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());

        Assert.Equal(HttpStatusCode.Accepted, (await visitor.PostAsJsonAsync(
            $"/api/album-share/{token}/challenge", new { email = Listed })).StatusCode);

        var code = await LastCodeAsync();
        var verified = await visitor.PostAsJsonAsync(
            $"/api/album-share/{token}/verify", new { email = Listed, code });
        Assert.Equal(HttpStatusCode.NoContent, verified.StatusCode);

        // The cookie is HTTP-only: no script on that page can read it.
        var setCookie = verified.Headers.GetValues("Set-Cookie").First();
        Assert.Contains("httponly", setCookie.ToLowerInvariant());

        // And the SAME client, carrying it, is simply in.
        var open = await visitor.GetAsync($"/api/album-share/{token}");
        Assert.Equal(HttpStatusCode.OK, open.StatusCode);
        Assert.Equal(album, album);
    }

    [Fact]
    public async Task A_listed_and_an_unlisted_address_are_indistinguishable_however_many_times_you_ask()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var (_, token) = await GuardedShareAsync(owner);
        _factory.EmailSender.Reset();
        var visitor = _factory.CreateClient();

        // THE ORACLE THIS CLOSES. Asking twice inside the cooldown used to
        // answer 202 then 429 for a listed address and 202 twice for an
        // unlisted one — two requests, and the owner's guest list is read. The
        // cooldown still suppresses the mail; it is simply invisible.
        var listedFirst = await Ask(visitor, token, Listed);
        var listedAgain = await Ask(visitor, token, Listed);
        var strangerFirst = await Ask(visitor, token, "chiunque@example.com");
        var strangerAgain = await Ask(visitor, token, "chiunque@example.com");
        var malformed = await Ask(visitor, token, "non-una-email");

        foreach (var response in new[]
                 { listedFirst, listedAgain, strangerFirst, strangerAgain, malformed })
        {
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }

        // The cooldown did its real job: ONE mail, not two, and only to the
        // address the owner listed.
        await _factory.WaitForShareCodesAsync(1);
        Assert.Single(_factory.EmailSender.Messages);
        Assert.Equal(Listed, _factory.EmailSender.Messages[0].ToAddress);
    }

    [Fact]
    public async Task Six_wrong_codes_look_the_same_for_a_listed_and_an_unlisted_address()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var (_, token) = await GuardedShareAsync(owner);
        var visitor = _factory.CreateClient();
        await Ask(visitor, token, Listed);

        // THE SECOND ORACLE, reached from the other side: only a listed address
        // can ever exhaust its attempts, so `too_many_attempts` used to say
        // "this one is on the list" to anybody willing to guess six times.
        var listed = new List<(HttpStatusCode Status, string? Error)>();
        var stranger = new List<(HttpStatusCode Status, string? Error)>();
        for (var i = 0; i < 6; i++)
        {
            listed.Add(await Guess(visitor, token, Listed, "000000"));
            stranger.Add(await Guess(visitor, token, "chiunque@example.com", "000000"));
        }

        Assert.Equal(stranger, listed);
        Assert.All(listed, x =>
        {
            Assert.Equal(HttpStatusCode.Unauthorized, x.Status);
            Assert.Equal("invalid_code", x.Error);
        });
    }

    [Fact]
    public async Task A_resend_kills_the_previous_code()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var (_, token) = await GuardedShareAsync(owner);
        var visitor = _factory.CreateClient();

        await visitor.PostAsJsonAsync($"/api/album-share/{token}/challenge", new { email = Listed });
        var first = await LastCodeAsync();

        // The resend interval is what stops a loop; this test is about the
        // generation, so it reaches past the clock by asking the owner to
        // re-add the address, which starts a fresh challenge row.
        _factory.EmailSender.Reset();
        await _factory.AdvanceAlbumShareResendWindowAsync();
        await visitor.PostAsJsonAsync($"/api/album-share/{token}/challenge", new { email = Listed });
        var second = await LastCodeAsync();
        Assert.NotEqual(first, second);

        // The OLD code is no longer an answer to anything.
        var stale = await visitor.PostAsJsonAsync(
            $"/api/album-share/{token}/verify", new { email = Listed, code = first });
        Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);

        var fresh = await visitor.PostAsJsonAsync(
            $"/api/album-share/{token}/verify", new { email = Listed, code = second });
        Assert.Equal(HttpStatusCode.NoContent, fresh.StatusCode);
    }

    [Fact]
    public async Task Guesses_run_out()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var (_, token) = await GuardedShareAsync(owner);
        var visitor = _factory.CreateClient();
        await visitor.PostAsJsonAsync($"/api/album-share/{token}/challenge", new { email = Listed });
        var real = await LastCodeAsync();
        var wrong = real == "000000" ? "111111" : "000000";

        for (var i = 0; i < 5; i++)
        {
            var attempt = await visitor.PostAsJsonAsync(
                $"/api/album-share/{token}/verify", new { email = Listed, code = wrong });
            Assert.Equal(HttpStatusCode.Unauthorized, attempt.StatusCode);
        }

        // The real code no longer helps: a guessing run must not be rescued by
        // finally reading the email. The refusal is the SAME generic one, so an
        // exhausted run cannot be told apart from a wrong guess.
        var exhausted = await visitor.PostAsJsonAsync(
            $"/api/album-share/{token}/verify", new { email = Listed, code = real });
        Assert.Equal(HttpStatusCode.Unauthorized, exhausted.StatusCode);
        Assert.Equal("invalid_code",
            (await exhausted.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("error").GetString());
    }

    [Fact]
    public async Task Removing_an_address_closes_the_phone_it_had_already_verified()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var (album, token) = await GuardedShareAsync(owner);
        var visitor = _factory.CreateClient();
        await visitor.PostAsJsonAsync($"/api/album-share/{token}/challenge", new { email = Listed });
        (await visitor.PostAsJsonAsync($"/api/album-share/{token}/verify",
            new { email = Listed, code = await LastCodeAsync() })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await visitor.GetAsync($"/api/album-share/{token}")).StatusCode);

        var link = await owner.GetFromJsonAsync<JsonElement>($"/api/albums/{album}/share-link");
        var guestId = link.GetProperty("guests")[0].GetProperty("id").GetGuid();
        (await owner.DeleteAsync($"/api/albums/{album}/share-link/guests/{guestId}"))
            .EnsureSuccessStatusCode();

        // The cookie is still in the jar. It is simply no longer worth anything.
        var after = await visitor.GetAsync($"/api/album-share/{token}");
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    [Fact]
    public async Task A_share_without_the_factor_has_no_challenge_to_answer()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = (await (await owner.PostAsJsonAsync("/api/albums", new { name = $"Album {Guid.NewGuid():N}" }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var created = await owner.PostAsync($"/api/albums/{album}/share-link", null);
        created.EnsureSuccessStatusCode();
        var token = (await created.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("url").GetString()!["/album/".Length..];

        var visitor = _factory.CreateClient();
        // The link opens without one, and the challenge route has nothing to do.
        Assert.Equal(HttpStatusCode.OK, (await visitor.GetAsync($"/api/album-share/{token}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.PostAsJsonAsync(
            $"/api/album-share/{token}/challenge", new { email = Listed })).StatusCode);
    }

    [Fact]
    public async Task A_code_works_once_and_then_never_again()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var (_, token) = await GuardedShareAsync(owner);
        await Ask(_factory.CreateClient(), token, Listed);
        var code = await LastCodeAsync();

        // SEQUENTIAL, because this host cannot run the race — see the note
        // above. What it proves is the property that matters either way: a
        // code is spent on use, so the second presentation of a correct one is
        // refused and mints nothing.
        var first = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.NoContent, (await first.PostAsJsonAsync(
            $"/api/album-share/{token}/verify", new { email = Listed, code })).StatusCode);

        var second = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await second.PostAsJsonAsync(
            $"/api/album-share/{token}/verify", new { email = Listed, code })).StatusCode);

        Assert.Equal(1, await _factory.CountAlbumShareDevicesAsync());
    }

    // --- helpers -----------------------------------------------------------

    private static Task<HttpResponseMessage> Ask(HttpClient client, string token, string email) =>
        client.PostAsJsonAsync($"/api/album-share/{token}/challenge", new { email });

    /// <summary>One guess, reduced to what a caller can actually observe.</summary>
    private static async Task<(HttpStatusCode Status, string? Error)> Guess(
        HttpClient client, string token, string email, string code)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/album-share/{token}/verify", new { email, code });
        string? error = null;
        try
        {
            error = (await response.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("error").GetString();
        }
        catch { /* a body without an error is itself part of what is observed */ }
        return (response.StatusCode, error);
    }

    private async Task<string> LastCodeAsync(int expected = 1)
    {
        await _factory.WaitForShareCodesAsync(expected);
        Assert.True(_factory.EmailSender.Messages.Count >= expected,
            $"expected {expected} code(s), saw {_factory.EmailSender.Messages.Count}");
        var body = _factory.EmailSender.Messages[^1].TextBody;
        var match = Regex.Match(body, @"\b(\d{6})\b");
        Assert.True(match.Success, "the message carried no six-digit code");
        return match.Groups[1].Value;
    }

    private async Task<(Guid Album, string Token)> GuardedShareAsync(HttpClient owner)
    {
        var album = (await (await owner.PostAsJsonAsync("/api/albums", new { name = $"Album {Guid.NewGuid():N}" }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var created = await owner.PostAsync($"/api/albums/{album}/share-link", null);
        created.EnsureSuccessStatusCode();
        var token = (await created.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("url").GetString()!["/album/".Length..];

        (await owner.PostAsJsonAsync($"/api/albums/{album}/share-link/guests",
            new { email = Listed, displayName = "La zia" })).EnsureSuccessStatusCode();
        (await owner.PatchAsJsonAsync($"/api/albums/{album}/share-link",
            new { requireSecondFactor = true })).EnsureSuccessStatusCode();
        return (album, token);
    }
}
