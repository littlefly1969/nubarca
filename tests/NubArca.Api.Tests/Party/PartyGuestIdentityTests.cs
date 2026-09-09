using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Print;
using NubArca.Api.Tests.Endpoints;

namespace NubArca.Api.Tests.Party;

/// <summary>
/// One anonymous guest per browser per party link, shared by every capability.
///
/// <para>A party's tokens say WHAT a browser may do; they are printed on a QR
/// and identify nobody. The participant says WHO, anonymously, is doing it.
/// Before this, the identity cookie was scoped to the path of the capability
/// token that minted it — so the same phone reading the album, uploading a
/// photograph and printing a keepsake arrived as three guests with three
/// allowances, not because anyone chose that but because a cookie path said
/// so.</para>
///
/// <para>These tests are the invariant from both directions: one browser at one
/// party is one guest however it got there, and one browser at two parties is
/// two guests with nothing in common.</para>
/// </summary>
public sealed class PartyGuestIdentityTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory = new();
    public PartyGuestIdentityTests() => _factory.EnsureDatabaseCreated();
    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task One_browser_walking_the_whole_party_is_one_guest()
    {
        var party = await SetUpAsync(messagesPerGuest: 5);
        // ONE client, so one cookie jar — a browser, not a test harness trick.
        var browser = _factory.CreateClient();

        // 1. Opens the party surface (view token).
        (await browser.GetAsync($"/api/party/{party.View}/challenges")).EnsureSuccessStatusCode();
        // 2. Contributes a photograph (upload token).
        (await browser.PostAsync($"/api/party/{party.Upload}/upload-session", null))
            .EnsureSuccessStatusCode();
        // 3. Sends a greeting (upload token).
        (await browser.PostAsJsonAsync($"/api/party/{party.Upload}/messages",
            new { displayName = "Anna", text = "Auguri!" })).EnsureSuccessStatusCode();
        // 4. Joins the game and 5. votes (view token).
        var joined = await browser.PostAsync($"/api/party/{party.View}/game/join", null);
        joined.EnsureSuccessStatusCode();
        var round = (await joined.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("roundId").GetGuid();
        (await browser.PostAsJsonAsync($"/api/party/{party.View}/game/vote",
            new { roundId = round, value = "yes" })).EnsureSuccessStatusCode();
        // 6. Opens print and 7. submits one (print token).
        (await browser.GetAsync($"/api/party/{party.Print}/print")).EnsureSuccessStatusCode();
        await TouchAsync(browser, party, "print");
        (await browser.PostAsJsonAsync($"/api/party/{party.Upload}/messages",
            new { displayName = "Anna", text = "Ancora auguri!" })).EnsureSuccessStatusCode();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // ONE participant, whichever capability created it.
        var participant = await db.PartyParticipants.SingleAsync();
        Assert.Null(participant.RetiredAt);

        // And every action that has an actor points at that one guest.
        Assert.All(await db.PartyMessages.ToListAsync(),
            m => Assert.Equal(participant.Id, m.PartyParticipantId));
        Assert.All(await db.PartyGameVotes.ToListAsync(),
            v => Assert.Equal(participant.Id, v.PartyParticipantId));
        // The greetings it sent were counted against it, not against three
        // different guests who each thought they were the first.
        Assert.Equal(2, participant.SubmittedMessageCount);
    }

    [Theory]
    // Every ordered pair the requirement names, plus the reverse of each: the
    // invariant must not depend on which capability the guest reached first.
    [InlineData("view", "upload")]
    [InlineData("upload", "view")]
    [InlineData("upload", "print")]
    [InlineData("print", "upload")]
    [InlineData("print", "view")]
    [InlineData("view", "print")]
    public async Task Two_capabilities_of_one_browser_are_one_guest(string first, string second)
    {
        var party = await SetUpAsync();
        var browser = _factory.CreateClient();

        await TouchAsync(browser, party, first);
        // Proved, not assumed: a capability that established nothing would make
        // the assertion below pass for the wrong reason.
        Assert.Equal(1, await ParticipantCountAsync());

        await TouchAsync(browser, party, second);
        Assert.Equal(1, await ParticipantCountAsync());
    }

    private async Task<int> ParticipantCountAsync()
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .PartyParticipants.CountAsync();
    }

    [Fact]
    public async Task One_browser_at_two_parties_is_two_guests_with_nothing_in_common()
    {
        var a = await SetUpAsync(messagesPerGuest: 1);
        var b = await SetUpAsync(messagesPerGuest: 1);
        var browser = _factory.CreateClient();

        // The same browser spends its one greeting at party A.
        (await browser.PostAsJsonAsync($"/api/party/{a.Upload}/messages",
            new { displayName = "Anna", text = "Auguri A!" })).EnsureSuccessStatusCode();
        var exhausted = await browser.PostAsJsonAsync($"/api/party/{a.Upload}/messages",
            new { displayName = "Anna", text = "Ancora?" });
        Assert.Equal(HttpStatusCode.Conflict, exhausted.StatusCode);

        // Party B is a different evening: its own guest, its own budget.
        var atB = await browser.PostAsJsonAsync($"/api/party/{b.Upload}/messages",
            new { displayName = "Anna", text = "Auguri B!" });
        Assert.Equal(HttpStatusCode.OK, atB.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var participants = await db.PartyParticipants.ToListAsync();
        Assert.Equal(2, participants.Count);
        // Two rows, two links, and two DIFFERENT keys: the derivation puts the
        // link inside the message, so one browser's two identities are unrelated.
        Assert.Equal(2, participants.Select(p => p.PartyAlbumLinkId).Distinct().Count());
        Assert.Equal(2, participants.Select(p => p.TokenHash).Distinct().Count());
        Assert.All(participants, p => Assert.Equal(1, p.SubmittedMessageCount));
    }

    [Fact]
    public async Task The_raw_browser_identity_is_never_persisted()
    {
        var party = await SetUpAsync();
        var browser = _factory.CreateClient();
        var response = await browser.PostAsync($"/api/party/{party.View}/game/join", null);
        response.EnsureSuccessStatusCode();

        var raw = BrowserCookie(response);
        Assert.NotNull(raw);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var participant = await db.PartyParticipants.SingleAsync();

        // Not the value, and not a plain hash of it either: the stored key is a
        // per-link HMAC, so a database read yields nothing anybody can present
        // and nothing that links the two parties of one browser.
        Assert.DoesNotContain(raw!, participant.TokenHash, StringComparison.Ordinal);
        Assert.NotEqual(Sha256Hex(raw!), participant.TokenHash);
        Assert.Equal(64, participant.TokenHash.Length);
    }

    [Fact]
    public async Task The_identity_cookie_covers_the_whole_party_surface_and_hides_from_scripts()
    {
        var party = await SetUpAsync();
        var response = await _factory.CreateClient()
            .PostAsync($"/api/party/{party.View}/game/join", null);
        var header = response.Headers.GetValues("Set-Cookie")
            .Single(x => x.StartsWith("NubArca.PartyBrowser=", StringComparison.Ordinal));

        // The one line that makes one browser one guest: the path is the party
        // surface, not the capability token that happened to mint it.
        Assert.Contains("path=/api/party", header, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(party.View, header, StringComparison.Ordinal);
        Assert.Contains("httponly", header, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", header, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_cookie_from_another_party_carries_no_allowance_into_this_one()
    {
        var a = await SetUpAsync(messagesPerGuest: 1);
        var b = await SetUpAsync(messagesPerGuest: 1);

        var atA = _factory.CreateClient();
        // The first request is the one that issues the cookie; a later response
        // carries no Set-Cookie because the browser is already holding it.
        var firstAtA = await atA.PostAsJsonAsync($"/api/party/{a.Upload}/messages",
            new { displayName = "Anna", text = "Auguri!" });
        firstAtA.EnsureSuccessStatusCode();
        var cookie = BrowserCookie(firstAtA);
        Assert.NotNull(cookie);

        // Deliberately replayed at party B. It derives a DIFFERENT key there, so
        // it neither impersonates anybody nor arrives already spent.
        var replay = new HttpRequestMessage(HttpMethod.Post, $"/api/party/{b.Upload}/messages")
        {
            Content = JsonContent.Create(new { displayName = "Anna", text = "Auguri B!" }),
        };
        replay.Headers.Add("Cookie", $"NubArca.PartyBrowser={cookie}");
        Assert.Equal(HttpStatusCode.OK, (await _factory.CreateClient().SendAsync(replay)).StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(2, await db.PartyParticipants.CountAsync());
        Assert.All(await db.PartyParticipants.ToListAsync(),
            p => Assert.Equal(1, p.SubmittedMessageCount));
    }

    // --- helpers -----------------------------------------------------------

    private sealed record Party(HttpClient Owner, Guid Album, string View, string Upload, string Print);

    /// <summary>
    /// Reach one capability as this browser would.
    ///
    /// The print leg is a real submission with an idempotency key, because the
    /// GET side never establishes a guest — a print touch that 404'd would make
    /// this test pass by doing nothing at all, which is the failure mode a
    /// capability-pair assertion is most exposed to. It is expected to be
    /// refused afterwards on its own merits (no slots); establishing the guest
    /// happens first, and is what is being proved.
    /// </summary>
    private static async Task TouchAsync(HttpClient browser, Party party, string capability)
    {
        if (capability == "print")
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"/api/party/{party.Print}/print")
            {
                Content = JsonContent.Create(new
                {
                    product = "photo", theme = "pure", slots = Array.Empty<object>(),
                }),
            };
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
            var printed = await browser.SendAsync(request);
            Assert.NotEqual(HttpStatusCode.NotFound, printed.StatusCode);
            return;
        }

        var response = capability switch
        {
            "view" => await browser.GetAsync($"/api/party/{party.View}/challenges"),
            "upload" => await browser.PostAsync($"/api/party/{party.Upload}/upload-session", null),
            _ => throw new ArgumentOutOfRangeException(nameof(capability)),
        };
        response.EnsureSuccessStatusCode();
    }

    private static string? BrowserCookie(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.FirstOrDefault(x => x.StartsWith("NubArca.PartyBrowser=", StringComparison.Ordinal))
                ?.Split(';', 2)[0]["NubArca.PartyBrowser=".Length..]
            : null;

    private static string Sha256Hex(string value) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(value)));

    private async Task<Party> SetUpAsync(int messagesPerGuest = 0)
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync($"{Guid.NewGuid():N}@example.com");
        var album = (await (await owner.PostAsJsonAsync("/api/albums",
                new { name = $"Festa {Guid.NewGuid():N}" }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        (await owner.PatchAsJsonAsync($"/api/albums/{album}/party-settings",
            new { enabled = true, uploadEnabled = true })).EnsureSuccessStatusCode();
        await StartPartyAsync(owner, album);
        (await owner.PatchAsJsonAsync($"/api/albums/{album}/party-slideshow-settings",
            new { maxMessagesPerParticipant = messagesPerGuest })).EnsureSuccessStatusCode();
        (await owner.PatchAsJsonAsync($"/api/albums/{album}/party-game-settings", new
        {
            gameEnabled = true, minChallengeIntervalSeconds = 30, maxChallengeIntervalSeconds = 60,
            votesPerGuest = 3, maxChallengesPerSession = (int?)null,
        })).EnsureSuccessStatusCode();
        (await owner.PostAsJsonAsync($"/api/albums/{album}/party-challenges", new
        {
            title = "Canta", body = "Sali sul tavolo.", kind = "dare",
            mediaFileItemId = (Guid?)null, isEnabled = true, votingMode = "binary",
        })).EnsureSuccessStatusCode();

        var status = await (await owner.GetAsync($"/api/albums/{album}/party-settings"))
            .Content.ReadFromJsonAsync<JsonElement>();
        var view = status.GetProperty("partyUrl").GetString()!["/party/".Length..];
        var uploadUrl = status.GetProperty("uploadUrl").GetString()!["/party/".Length..];
        var upload = uploadUrl[..uploadUrl.IndexOf("/upload", StringComparison.Ordinal)];

        // Printing only hands out a token when the whole chain holds, so the
        // test builds a real one: a station, a printer that does 10x15, and a
        // budget. Anything less and the print leg would be proving a 404.
        var ownerId = await OwnerIdAsync(album);
        var (stationId, deviceId) = await SeedPrinterAsync(ownerId);
        (await owner.PatchAsJsonAsync($"/api/albums/{album}/party-print-settings", new
        {
            enabled = true, printStationId = stationId, printerDeviceId = deviceId,
            photoEnabled = true, photoMaxPrints = 10, stripEnabled = true, stripMaxPrints = 10,
        })).EnsureSuccessStatusCode();
        // The print token is minted the first time the hub reports printing as
        // open, which is how a guest reaches it at all. Read the hub once, as a
        // guest would, before deriving it.
        (await _factory.CreateClient().GetAsync($"/api/party/{view}")).EnsureSuccessStatusCode();
        var print = await PrintTokenAsync(album);
        // Printing is a chain, and a test that silently loses it proves nothing:
        // if the token does not resolve, say so here rather than three
        // assertions later.
        using (var probe = _factory.Services.CreateScope())
        {
            var resolver = probe.ServiceProvider
                .GetRequiredService<NubArca.Api.Print.IPartyPrintAccessResolver>();
            Assert.True(await resolver.ResolveAsync(print, CancellationToken.None) is not null,
                "the print capability did not resolve — the setup chain is incomplete");
        }

        var version = 0;
        foreach (var command in new[] { "start", "start_challenge", "open_voting" })
            version = (await (await owner.PostAsJsonAsync(
                    $"/api/albums/{album}/party-game/commands",
                    new { command, expectedVersion = version }))
                .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetInt32();

        return new Party(owner, album, view, upload, print);
    }

    private async Task<string> PrintTokenAsync(Guid album)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var link = await db.PartyAlbumLinks.AsNoTracking()
            .Where(x => x.AlbumId == album && x.Enabled && x.RevokedAt == null)
            .OrderByDescending(x => x.CreatedAt).FirstAsync();
        return ((NubArca.Api.Party.PartyLinkService)scope.ServiceProvider
            .GetRequiredService<NubArca.Api.Party.IPartyLinkService>()).DerivePrintToken(link.Id);
    }

    private async Task<Guid> OwnerIdAsync(Guid album)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Albums.AsNoTracking().Where(a => a.Id == album)
            .Select(a => a.OwnerUserId).FirstAsync();
    }

    private async Task<(Guid StationId, Guid DeviceId)> SeedPrinterAsync(Guid ownerUserId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stationId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        db.PrintStations.Add(new PrintStation
        {
            Id = stationId, OwnerUserId = ownerUserId, Name = "Postazione",
            Enabled = true, CreatedAt = DateTime.UtcNow,
        });
        db.PrinterDevices.Add(new PrinterDevice
        {
            Id = deviceId, PrintStationId = stationId, DeviceKey = "d1",
            DisplayName = "DS620", AdapterKind = "fake",
            CapabilitiesJson = "{\"formats\":[\"10x15\"]}",
            LastObservedState = PrintDeviceStates.Ready, LastSeenAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return (stationId, deviceId);
    }

    // Enabling guest access PUBLISHES the party — an invitation, which is
    // deliberately not the party itself. These tests exercise the party, so they
    // start it, exactly as a host does.
    private static async Task StartPartyAsync(HttpClient owner, Guid album)
    {
        var settings = await (await owner.GetAsync($"/api/albums/{album}/party-settings"))
            .Content.ReadFromJsonAsync<JsonElement>();
        await NubArca.Api.Tests.Party.PartyTestHost.StartAsync(owner, settings);
    }
}
