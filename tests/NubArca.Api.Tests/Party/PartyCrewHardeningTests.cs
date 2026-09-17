using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Party;
using NubArca.Api.Tests.Endpoints;
using static NubArca.Api.Tests.Party.PartyCrewTestKit;
using static NubArca.Api.Tests.Party.PartyInvitationTestKit;

namespace NubArca.Api.Tests.Party;

/// <summary>
/// The edges Party Crew has to survive, not the path it takes when everything
/// works.
///
/// <para>Each test here is a way the feature could be true in the happy case
/// and wrong in practice: a mail provider that accepts configuration and
/// refuses messages, a spent challenge cookie nobody cleared, a collaborator
/// enumerating the venue's hardware, a link rotation that half-happened.</para>
/// </summary>
public sealed class PartyCrewHardeningTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory = NewFactory();

    public void Dispose() => _factory.Dispose();

    // ── Mail that is configured and still refuses ───────────────────────────

    [Fact]
    public async Task A_refused_delivery_is_never_a_screen_that_says_check_your_email()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner);
        var (collaboratorId, invite) = await AddCollaboratorAsync(
            owner, partyId, "Sara", "sara@example.com", PartyCrewRoles.Director);

        // CONFIGURED, and refusing. `IsEnabled` says the installation can send
        // mail; it says nothing about whether this message was accepted.
        _factory.EmailSender.FailDelivery = true;
        try
        {
            var started = await _factory.CreateClient().PostAsJsonAsync(
                "/api/party-crew/auth/invite", new { token = TokenOf(invite) });
            Assert.Equal(HttpStatusCode.BadRequest, started.StatusCode);
            Assert.Equal("delivery_failed", await PartyCrewTestKit.ErrorOf(started));
        }
        finally { _factory.EmailSender.FailDelivery = false; }

        // AND NO CHALLENGE SURVIVED IT. A row left behind would be a code
        // somebody could still be handed out of band, and a link that looks
        // spent to the next honest attempt.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Empty(await db.PartyCollaboratorAuthChallenges
            .Where(c => c.PartyCollaboratorId == collaboratorId).ToListAsync());

        // The link is untouched, so trying again once mail works is all it takes.
        var second = await _factory.CreateClient().PostAsJsonAsync(
            "/api/party-crew/auth/invite", new { token = TokenOf(invite) });
        second.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task A_refused_resend_leaves_the_code_that_still_works_alone()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner);
        var (_, invite) = await AddCollaboratorAsync(
            owner, partyId, "Luca", "luca@example.com", PartyCrewRoles.Director);

        var device = _factory.CreateClient();
        (await device.PostAsJsonAsync(
            "/api/party-crew/auth/invite", new { token = TokenOf(invite) })).EnsureSuccessStatusCode();
        var working = CodeFromLastEmail(_factory);

        // Past the interval, so the refusal below is the only thing in the way.
        await AdvancePastResendIntervalAsync();

        _factory.EmailSender.FailDelivery = true;
        try
        {
            var refused = await device.PostAsync("/api/party-crew/auth/resend", null);
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Equal("delivery_failed", await PartyCrewTestKit.ErrorOf(refused));
        }
        finally { _factory.EmailSender.FailDelivery = false; }

        // THE OLD CODE STILL WORKS. Replacing the proof before the send would
        // have killed the only code that existed and delivered nothing to
        // replace it: zero working codes, and no way forward but a new link.
        var verified = await device.PostAsJsonAsync(
            "/api/party-crew/auth/verify", new { code = working });
        verified.EnsureSuccessStatusCode();
        Assert.Equal("Paired", (await verified.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task A_mailbox_cannot_be_used_as_a_channel_by_whoever_holds_the_link()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner);
        var (_, invite) = await AddCollaboratorAsync(
            owner, partyId, "Mira", "mira@example.com", PartyCrewRoles.Director);

        // Re-opening the link is what resets a challenge's own send budget, so
        // the collaborator has a budget too. Without it a leaked link is a way
        // to put an email in somebody's inbox on demand, from any address.
        for (var i = 0; i < PartyCrewLimits.MaxChallengesPerCollaborator; i++)
        {
            (await _factory.CreateClient().PostAsJsonAsync(
                "/api/party-crew/auth/invite",
                new { token = TokenOf(invite) })).EnsureSuccessStatusCode();
        }

        var refused = await _factory.CreateClient().PostAsJsonAsync(
            "/api/party-crew/auth/invite", new { token = TokenOf(invite) });
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
    }

    [Fact]
    public async Task One_challenge_sends_a_bounded_number_of_codes()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner);
        var (_, invite) = await AddCollaboratorAsync(
            owner, partyId, "Gio", "gio@example.com", PartyCrewRoles.Director);

        var device = _factory.CreateClient();
        (await device.PostAsJsonAsync(
            "/api/party-crew/auth/invite", new { token = TokenOf(invite) })).EnsureSuccessStatusCode();

        // The interval spaces them; this bounds them. One link must not be ten
        // emails, a minute apart, for as long as the challenge lives.
        for (var sent = 1; sent < PartyCrewLimits.MaxOtpSendsPerChallenge; sent++)
        {
            await AdvancePastResendIntervalAsync();
            (await device.PostAsync("/api/party-crew/auth/resend", null)).EnsureSuccessStatusCode();
        }

        await AdvancePastResendIntervalAsync();
        var refused = await device.PostAsync("/api/party-crew/auth/resend", null);
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
    }

    // ── The spent challenge cookie ──────────────────────────────────────────

    [Fact]
    public async Task Pairing_a_browser_that_is_already_a_device_still_spends_its_challenge()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var first = await CreatePartyAsync(owner, "La prima");
        var second = await CreatePartyAsync(owner, "La seconda");
        var (_, firstInvite) = await AddCollaboratorAsync(
            owner, first, "Ele", "ele@example.com", PartyCrewRoles.Director);
        var (_, secondInvite) = await AddCollaboratorAsync(
            owner, second, "Ele", "ele@example.com", PartyCrewRoles.Director);

        var phone = await PairAsync(_factory, firstInvite);

        // A browser that already holds a device token mints no new one — the
        // credential is party-agnostic. The CHALLENGE is spent all the same,
        // and tying the clear to the token left one behind in exactly this case.
        (await phone.PostAsJsonAsync(
            "/api/party-crew/auth/invite",
            new { token = TokenOf(secondInvite) })).EnsureSuccessStatusCode();
        var verified = await phone.PostAsJsonAsync(
            "/api/party-crew/auth/verify", new { code = CodeFromLastEmail(_factory) });
        verified.EnsureSuccessStatusCode();
        Assert.Equal("Paired", (await verified.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("outcome").GetString());

        AssertChallengeCookieCleared(verified);

        // Both assignments work, which is the point of not minting a second
        // credential in the first place.
        Assert.Equal(first, (await SessionAsync(phone, first)).GetProperty("partyId").GetGuid());
        Assert.Equal(second, (await SessionAsync(phone, second)).GetProperty("partyId").GetGuid());
    }

    // ── The venue's hardware ────────────────────────────────────────────────

    [Fact]
    public async Task Print_manage_is_this_party_s_printing_and_not_the_installation_s()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner);
        await OpenPublicQrAsync(owner, partyId);
        var (_, invite) = await AddCollaboratorAsync(
            owner, partyId, "Regista", "regista@example.com", PartyCrewRoles.Director);
        var director = await PairAsync(_factory, invite);

        Assert.Contains(
            PartyCrewCapabilities.PrintManage, CapabilitiesOf(await SessionAsync(director, partyId)));

        // The PARTY's print profile: theirs.
        (await director.GetAsync($"/api/party-crew/parties/{partyId}/print-settings"))
            .EnsureSuccessStatusCode();

        // The INSTALLATION's printers: not theirs, and there is no crew route
        // for them at all. Enumerating the venue's hardware is the host
        // administering their own equipment, which outlives this evening.
        AssertAbsent(await director.GetAsync($"/api/party-crew/parties/{partyId}/print-stations"));
        AssertAbsent(await director.GetAsync("/api/party-crew/print-stations"));

        // And the host's own routes refuse a device cookie, as they refuse it
        // everywhere else.
        var stations = await director.GetAsync("/api/print/stations");
        Assert.NotEqual(HttpStatusCode.OK, stations.StatusCode);
        var created = await director.PostAsJsonAsync("/api/print/stations", new { name = "Mia" });
        Assert.NotEqual(HttpStatusCode.Created, created.StatusCode);
    }

    // ── Rotating a link ─────────────────────────────────────────────────────

    [Fact]
    public async Task Rotating_a_link_never_leaves_a_collaborator_without_one()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner);
        var (collaboratorId, first) = await AddCollaboratorAsync(
            owner, partyId, "Rita", "rita@example.com", PartyCrewRoles.Director);

        for (var i = 0; i < 3; i++)
        {
            var minted = await owner.PostAsync(
                $"/api/parties/{partyId}/crew/{collaboratorId}/invite", null);
            minted.EnsureSuccessStatusCode();

            // EXACTLY ONE usable link exists after every rotation. Revoking the
            // old one and inserting the new one are two writes of different
            // kinds — an immediate UPDATE and a tracked insert — so without one
            // transaction a failure between them leaves nobody a way in.
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var usable = await db.PartyCollaboratorInvites
                .CountAsync(x => x.PartyCollaboratorId == collaboratorId
                    && x.RevokedAt == null && x.ConsumedAt == null);
            Assert.Equal(1, usable);
        }

        // And the first link is dead, as "send them a new one" has to mean.
        AssertRefused(await _factory.CreateClient().PostAsJsonAsync(
            "/api/party-crew/auth/invite", new { token = TokenOf(first) }));
    }

    // ── Leaving ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Leaving_one_party_is_not_leaving_the_other()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var first = await CreatePartyAsync(owner, "La prima");
        var second = await CreatePartyAsync(owner, "La seconda");
        var (_, firstInvite) = await AddCollaboratorAsync(
            owner, first, "Dani", "dani@example.com", PartyCrewRoles.Director);
        var (_, secondInvite) = await AddCollaboratorAsync(
            owner, second, "Dani", "dani@example.com", PartyCrewRoles.Director);

        var phone = await PairAsync(_factory, firstInvite);
        (await phone.PostAsJsonAsync(
            "/api/party-crew/auth/invite",
            new { token = TokenOf(secondInvite) })).EnsureSuccessStatusCode();
        (await phone.PostAsJsonAsync(
            "/api/party-crew/auth/verify",
            new { code = CodeFromLastEmail(_factory) })).EnsureSuccessStatusCode();

        // ONE grant goes, and the credential stays because the other still
        // needs it. A product that cleared the cookie here would sign somebody
        // out of a party they never left.
        (await phone.DeleteAsync($"/api/party-crew/parties/{first}/session"))
            .EnsureSuccessStatusCode();
        AssertRefused(await phone.GetAsync($"/api/party-crew/parties/{first}/session"));
        Assert.Equal(second, (await SessionAsync(phone, second)).GetProperty("partyId").GetGuid());

        // DISCONNECTING THE BROWSER is the other decision, and it ends both.
        (await phone.DeleteAsync("/api/party-crew/device")).EnsureSuccessStatusCode();
        AssertRefused(await phone.GetAsync($"/api/party-crew/parties/{second}/session"));
        AssertRefused(await phone.GetAsync("/api/party-crew/me"));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Move this challenge's last send back past the interval.
    ///
    /// <para>The pooled host shares one clock, so the row is aged instead —
    /// which is what the service actually reads, and keeps the test about the
    /// budget rather than about time.</para>
    /// </summary>
    private async Task AdvancePastResendIntervalAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var back = PartyCrewLimits.ResendInterval + TimeSpan.FromSeconds(5);
        // Tracked, not `ExecuteUpdate`: subtracting an interval from a column
        // is not translatable on every provider, and this is a fixture rather
        // than a thing the product does.
        var live = await db.PartyCollaboratorAuthChallenges
            .Where(c => c.CompletedAt == null && c.RevokedAt == null)
            .ToListAsync();
        foreach (var challenge in live) challenge.OtpSentAt -= back;
        await db.SaveChangesAsync();
    }

    private static void AssertChallengeCookieCleared(HttpResponseMessage response)
    {
        var cleared = response.Headers.TryGetValues("Set-Cookie", out var values)
            && values.Any(v => v.StartsWith("NubArca.PartyCrewChallenge=", StringComparison.Ordinal)
                && (v.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase)
                    || v.Contains("max-age=0", StringComparison.OrdinalIgnoreCase)));
        Assert.True(cleared, "A finished pairing left its challenge cookie in the browser.");
    }

    /// <summary>A route that is not part of the Party Crew surface at all.</summary>
    private static void AssertAbsent(HttpResponseMessage response) =>
        Assert.True(
            response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed,
            $"A Party Crew device reached a route that should not exist: {response.StatusCode}");
}
