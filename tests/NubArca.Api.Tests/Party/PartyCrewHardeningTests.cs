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

    // ── One identity per browser per party ──────────────────────────────────

    [Fact]
    public async Task A_browser_cannot_become_a_second_person_at_the_same_party()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner, "La stessa festa");
        var (_, lauraInvite) = await AddCollaboratorAsync(
            owner, partyId, "Laura", "laura@example.com", PartyCrewRoles.CoOrganizer);
        var (_, marcoInvite) = await AddCollaboratorAsync(
            owner, partyId, "Marco", "marco@example.com", PartyCrewRoles.Director);

        var browser = await PairAsync(_factory, lauraInvite);

        // A DEVICE MAY HELP AT MANY PARTIES, BUT IS ONE PERSON AT EACH. Two
        // identities on one browser at one party would leave the resolver
        // choosing which of them is acting, and "who did this" would be decided
        // by whichever grant came back first.
        (await browser.PostAsJsonAsync(
            "/api/party-crew/auth/invite",
            new { token = TokenOf(marcoInvite) })).EnsureSuccessStatusCode();
        var refused = await browser.PostAsJsonAsync(
            "/api/party-crew/auth/verify", new { code = CodeFromLastEmail(_factory) });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("device_already_assigned", await PartyCrewTestKit.ErrorOf(refused));

        // Laura is untouched, and Marco got nothing.
        var session = await SessionAsync(browser, partyId);
        Assert.Equal("Laura", session.GetProperty("displayName").GetString());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var marco = await db.PartyCollaborators
            .SingleAsync(c => c.PartyId == partyId && c.DisplayName == "Marco");
        Assert.Empty(await db.PartyCollaboratorDeviceGrants
            .Where(g => g.PartyCollaboratorId == marco.Id && g.RevokedAt == null).ToListAsync());
    }

    [Fact]
    public async Task Leaving_the_party_frees_the_browser_for_somebody_else()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner);
        var (_, lauraInvite) = await AddCollaboratorAsync(
            owner, partyId, "Laura", "laura@example.com", PartyCrewRoles.CoOrganizer);
        var (_, marcoInvite) = await AddCollaboratorAsync(
            owner, partyId, "Marco", "marco@example.com", PartyCrewRoles.Director);

        var browser = await PairAsync(_factory, lauraInvite);

        // The way out is a decision the person makes, not one the pairing makes
        // for them: leave the other assignment, then pair.
        (await browser.DeleteAsync($"/api/party-crew/parties/{partyId}/session"))
            .EnsureSuccessStatusCode();

        (await browser.PostAsJsonAsync(
            "/api/party-crew/auth/invite",
            new { token = TokenOf(marcoInvite) })).EnsureSuccessStatusCode();
        var paired = await browser.PostAsJsonAsync(
            "/api/party-crew/auth/verify", new { code = CodeFromLastEmail(_factory) });
        paired.EnsureSuccessStatusCode();
        Assert.Equal("Marco", (await SessionAsync(browser, partyId))
            .GetProperty("displayName").GetString());
    }

    // ── A rotated link takes its pairings with it ───────────────────────────

    [Fact]
    public async Task A_new_link_kills_the_pairing_already_in_flight_from_the_old_one()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner);
        var (collaboratorId, first) = await AddCollaboratorAsync(
            owner, partyId, "Ines", "ines@example.com", PartyCrewRoles.Director);

        // A pairing is underway: the link has been opened and a code sent.
        var device = _factory.CreateClient();
        (await device.PostAsJsonAsync(
            "/api/party-crew/auth/invite", new { token = TokenOf(first) })).EnsureSuccessStatusCode();
        var code = CodeFromLastEmail(_factory);

        // The host sends a new link before it finishes.
        (await owner.PostAsync($"/api/parties/{partyId}/crew/{collaboratorId}/invite", null))
            .EnsureSuccessStatusCode();

        // EVERY DOOR THE OLD LINK OPENED IS SHUT. Revoking the invite and
        // leaving its challenge alive would mean the host revoked nothing: the
        // pairing would simply finish a minute later.
        AssertRefused(await device.PostAsJsonAsync("/api/party-crew/auth/verify", new { code }));
        AssertRefused(await device.GetAsync("/api/party-crew/auth/challenge"));
        AssertRefused(await device.GetAsync("/api/party-crew/auth/devices"));
        AssertRefused(await device.PostAsync("/api/party-crew/auth/complete", null));
        AssertRefused(await device.PostAsync("/api/party-crew/auth/resend", null));
    }

    // ── The party's photographs, and no others ──────────────────────────────

    [Fact]
    public async Task A_collaborator_names_the_party_s_own_photographs_and_cannot_reach_past_them()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner);
        var (albumId, _, _) = await OpenPublicQrAsync(owner, partyId);

        // One photograph in the party's album, one only in the host's library.
        var inAlbum = await UploadPhotoAsync(owner, "in-album.png");
        var elsewhere = await UploadPhotoAsync(owner, "elsewhere.png");
        (await owner.PostAsJsonAsync(
            $"/api/albums/{albumId}/items", new { fileItemId = inAlbum }))
            .EnsureSuccessStatusCode();

        var (_, invite) = await AddCollaboratorAsync(
            owner, partyId, "Co", "co@example.com", PartyCrewRoles.CoOrganizer);
        var co = await PairAsync(_factory, invite);

        // THE ALBUM'S PHOTOGRAPH IS THEIRS to name, and to see: a picker that
        // listed rows a browser cannot render is the visible half of having no
        // session at all.
        var accepted = await co.PutAsJsonAsync(
            $"/api/party-crew/parties/{partyId}/guest-content/invitation",
            new
            {
                enabled = true, visibleBefore = true, visibleLive = false, visibleAfter = false,
                content = new { headline = "Vieni" }, mediaFileItemId = inAlbum,
                mediaPresentation = "inline", version = 0,
            });
        accepted.EnsureSuccessStatusCode();
        (await co.GetAsync($"/api/party-crew/parties/{partyId}/media/{inAlbum}/thumbnail"))
            .EnsureSuccessStatusCode();

        // THE HOST'S OTHER PHOTOGRAPH IS NOT. The picker never offers it, but
        // the picker is not the boundary — a request naming it directly is
        // refused, and its bytes are not served either.
        var refused = await co.PutAsJsonAsync(
            $"/api/party-crew/parties/{partyId}/guest-content/menu",
            new
            {
                enabled = true, visibleBefore = true, visibleLive = true, visibleAfter = false,
                content = new { headline = "Menu" }, mediaFileItemId = elsewhere,
                mediaPresentation = "inline", version = 0,
            });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("invalid_media", await PartyCrewTestKit.ErrorOf(refused));
        AssertRefused(await co.GetAsync(
            $"/api/party-crew/parties/{partyId}/media/{elsewhere}/thumbnail"));

        // And the host's own file routes stay shut to a device cookie.
        var direct = await co.GetAsync($"/api/files/{inAlbum}/thumbnail");
        Assert.NotEqual(HttpStatusCode.OK, direct.StatusCode);
    }

    // ── The venue's hardware, again — this time by request ──────────────────

    [Fact]
    public async Task A_hand_built_request_cannot_re_point_the_printer()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner);
        var (albumId, _, _) = await OpenPublicQrAsync(owner, partyId);
        var (_, invite) = await AddCollaboratorAsync(
            owner, partyId, "Regista", "regista@example.com", PartyCrewRoles.Director);
        var director = await PairAsync(_factory, invite);

        var before = await owner.GetFromJsonAsync<JsonElement>(
            $"/api/albums/{albumId}/party-print-settings");

        // The crew request has no hardware field at all, so these are simply
        // not read. What the collaborator MAY change is changed.
        var saved = await director.PatchAsJsonAsync(
            $"/api/party-crew/parties/{partyId}/print-settings",
            new
            {
                enabled = false,
                photoEnabled = true,
                photoMaxPrints = 7,
                printStationId = Guid.NewGuid(),
                printerDeviceId = Guid.NewGuid(),
            });
        saved.EnsureSuccessStatusCode();

        var after = await owner.GetFromJsonAsync<JsonElement>(
            $"/api/albums/{albumId}/party-print-settings");
        Assert.Equal(
            before.GetProperty("printStationId").GetRawText(),
            after.GetProperty("printStationId").GetRawText());
        Assert.Equal(
            before.GetProperty("printerDeviceId").GetRawText(),
            after.GetProperty("printerDeviceId").GetRawText());
        Assert.Equal(7, after.GetProperty("photo").GetProperty("maxPrints").GetInt32());
    }

    [Fact]
    public async Task A_refused_resend_gives_the_attempt_back()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner);
        var (_, invite) = await AddCollaboratorAsync(
            owner, partyId, "Rea", "rea@example.com", PartyCrewRoles.Director);

        var device = _factory.CreateClient();
        (await device.PostAsJsonAsync(
            "/api/party-crew/auth/invite", new { token = TokenOf(invite) })).EnsureSuccessStatusCode();

        // The slot is claimed before the send, so a refusal has already spent
        // one — and is handed back, because a provider's bad day is not the
        // person's fault and must not cost them one of three.
        await AdvancePastResendIntervalAsync();
        _factory.EmailSender.FailDelivery = true;
        try
        {
            var refused = await device.PostAsync("/api/party-crew/auth/resend", null);
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        }
        finally { _factory.EmailSender.FailDelivery = false; }

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var challenge = await db.PartyCollaboratorAuthChallenges
                .SingleAsync(c => c.RevokedAt == null && c.CompletedAt == null);
            Assert.Equal(1, challenge.OtpSendCount);
            Assert.Equal(1, challenge.OtpGeneration);
        }

        // So the full budget of resends is still there to use.
        for (var sent = 1; sent < PartyCrewLimits.MaxOtpSendsPerChallenge; sent++)
        {
            await AdvancePastResendIntervalAsync();
            (await device.PostAsync("/api/party-crew/auth/resend", null)).EnsureSuccessStatusCode();
        }
    }

    [Fact]
    public async Task The_crew_print_profile_never_names_the_venue_s_hardware()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner);
        await OpenPublicQrAsync(owner, partyId);
        var (_, invite) = await AddCollaboratorAsync(
            owner, partyId, "Regista", "regista@example.com", PartyCrewRoles.Director);
        var director = await PairAsync(_factory, invite);

        var read = await director.GetAsync($"/api/party-crew/parties/{partyId}/print-settings");
        read.EnsureSuccessStatusCode();
        var wire = await read.Content.ReadAsStringAsync();

        // WHETHER printing is set up is a fact about tonight; WHICH machine
        // does it identifies the installation's hardware, which outlives this
        // evening and is not the party's data. Not changeable, and not visible.
        Assert.Contains("printerConfigured", wire);
        Assert.DoesNotContain("printStationId", wire, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("printerDeviceId", wire, StringComparison.OrdinalIgnoreCase);

        // The write answers through the same projection, or it would hand back
        // what the read is careful not to.
        var saved = await director.PatchAsJsonAsync(
            $"/api/party-crew/parties/{partyId}/print-settings",
            new { enabled = false, photoEnabled = true, photoMaxPrints = 4 });
        saved.EnsureSuccessStatusCode();
        var savedWire = await saved.Content.ReadAsStringAsync();
        Assert.DoesNotContain("printStationId", savedWire, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("printerDeviceId", savedWire, StringComparison.OrdinalIgnoreCase);
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
