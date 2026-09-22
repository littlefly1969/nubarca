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

        var code = LastCode();
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
    public async Task An_unlisted_address_gets_the_same_answer_and_no_code()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var (_, token) = await GuardedShareAsync(owner);
        _factory.EmailSender.Reset();

        var visitor = _factory.CreateClient();
        var listed = await visitor.PostAsJsonAsync(
            $"/api/album-share/{token}/challenge", new { email = Listed });
        var stranger = await visitor.PostAsJsonAsync(
            $"/api/album-share/{token}/challenge", new { email = "chiunque@example.com" });
        var malformed = await visitor.PostAsJsonAsync(
            $"/api/album-share/{token}/challenge", new { email = "non-una-email" });

        // ONE answer for all three. The difference is invisible from outside,
        // which is the whole property.
        Assert.Equal(HttpStatusCode.Accepted, listed.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, stranger.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, malformed.StatusCode);

        // Exactly one message went out, and it went to the listed address.
        Assert.Single(_factory.EmailSender.Messages);
        Assert.Equal(Listed, _factory.EmailSender.Messages[0].ToAddress);
    }

    [Fact]
    public async Task A_resend_kills_the_previous_code()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var (_, token) = await GuardedShareAsync(owner);
        var visitor = _factory.CreateClient();

        await visitor.PostAsJsonAsync($"/api/album-share/{token}/challenge", new { email = Listed });
        var first = LastCode();

        // The resend interval is what stops a loop; this test is about the
        // generation, so it reaches past the clock by asking the owner to
        // re-add the address, which starts a fresh challenge row.
        _factory.EmailSender.Reset();
        await _factory.AdvanceAlbumShareResendWindowAsync();
        await visitor.PostAsJsonAsync($"/api/album-share/{token}/challenge", new { email = Listed });
        var second = LastCode();
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
        var real = LastCode();
        var wrong = real == "000000" ? "111111" : "000000";

        for (var i = 0; i < 5; i++)
        {
            var attempt = await visitor.PostAsJsonAsync(
                $"/api/album-share/{token}/verify", new { email = Listed, code = wrong });
            Assert.Equal(HttpStatusCode.Unauthorized, attempt.StatusCode);
        }

        // The sixth is refused as EXHAUSTED, and the real code no longer helps:
        // a guessing run must not be rescued by finally reading the email.
        var exhausted = await visitor.PostAsJsonAsync(
            $"/api/album-share/{token}/verify", new { email = Listed, code = real });
        Assert.Equal(HttpStatusCode.Unauthorized, exhausted.StatusCode);
        Assert.Equal("too_many_attempts",
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
            new { email = Listed, code = LastCode() })).EnsureSuccessStatusCode();
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
        var album = (await (await owner.PostAsJsonAsync("/api/albums", new { name = "Album" }))
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

    // --- helpers -----------------------------------------------------------

    private string LastCode()
    {
        var body = _factory.EmailSender.Messages[^1].TextBody;
        var match = Regex.Match(body, @"\b(\d{6})\b");
        Assert.True(match.Success, "the message carried no six-digit code");
        return match.Groups[1].Value;
    }

    private async Task<(Guid Album, string Token)> GuardedShareAsync(HttpClient owner)
    {
        var album = (await (await owner.PostAsJsonAsync("/api/albums", new { name = "Album" }))
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
