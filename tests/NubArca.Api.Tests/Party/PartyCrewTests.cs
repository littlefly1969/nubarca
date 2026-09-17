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
/// PARTY CREW — an accountless person who runs one evening, and nothing else.
///
/// <para>What this file pins down is the boundary, from both sides. A
/// collaborator can do the job the host gave them, on the party the host gave
/// them, from at most two devices — and they cannot become a user, cannot reach
/// another party, cannot outlive a revoke, and cannot exceed what the host
/// themselves is still permitted to do.</para>
/// </summary>
public sealed class PartyCrewTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory = NewFactory();

    public void Dispose() => _factory.Dispose();

    // ── The two factors ─────────────────────────────────────────────────────

    [Fact]
    public async Task A_link_and_a_code_make_a_device_and_neither_alone_does()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner, "Compleanno di Lia");
        var (_, invite) = await AddCollaboratorAsync(
            owner, partyId, "Marco", "marco@example.com", PartyCrewRoles.CoOrganizer);

        // The link is on the operator's configured origin and carries its token
        // in the FRAGMENT — which no server ever sees.
        Assert.StartsWith($"{Origin}/party/crew/invite#token=", invite);
        Assert.Equal(43, TokenOf(invite).Length);

        // THE LINK ALONE opens nothing. It starts a challenge and says only
        // what the party is called, what the role is, and a masked address.
        var device = _factory.CreateClient();
        var started = await device.PostAsJsonAsync(
            "/api/party-crew/auth/invite", new { token = TokenOf(invite) });
        started.EnsureSuccessStatusCode();
        var view = await started.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Compleanno di Lia", view.GetProperty("partyTitle").GetString());
        Assert.Equal("m••••@example.com", view.GetProperty("maskedEmail").GetString());
        // Not the address, not an owner, not an album, not a party id.
        var wire = view.GetRawText();
        Assert.DoesNotContain("marco@example.com", wire);
        Assert.DoesNotContain(partyId.ToString(), wire);
        // And nothing is authorized yet.
        AssertRefused(await device.GetAsync($"/api/party-crew/parties/{partyId}/session"));

        // THE CODE ALONE is not enough either: a second browser holding the
        // same six digits has no challenge to type them into.
        var elsewhere = _factory.CreateClient();
        var stolen = await elsewhere.PostAsJsonAsync(
            "/api/party-crew/auth/verify", new { code = CodeFromLastEmail(_factory) });
        AssertRefused(stolen);

        // BOTH TOGETHER pair the browser that asked.
        var verified = await device.PostAsJsonAsync(
            "/api/party-crew/auth/verify", new { code = CodeFromLastEmail(_factory) });
        verified.EnsureSuccessStatusCode();
        Assert.Equal("Paired", (await verified.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("outcome").GetString());

        var session = await SessionAsync(device, partyId);
        Assert.Equal(partyId, session.GetProperty("partyId").GetGuid());
        Assert.Equal("Marco", session.GetProperty("displayName").GetString());
        Assert.Equal(PartyCrewRoles.CoOrganizer, session.GetProperty("roleKey").GetString());
    }

    [Fact]
    public async Task The_email_carries_six_digits_and_nothing_that_could_be_used_without_them()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner, "Cena di classe");
        var (collaboratorId, invite) = await AddCollaboratorAsync(
            owner, partyId, "Sara", "sara@example.com", PartyCrewRoles.Director);

        var device = _factory.CreateClient();
        (await device.PostAsJsonAsync(
            "/api/party-crew/auth/invite", new { token = TokenOf(invite) })).EnsureSuccessStatusCode();

        var message = _factory.EmailSender.Last!;
        Assert.Equal("sara@example.com", message.ToAddress);
        Assert.Contains("Cena di classe", message.TextBody);
        // No link, no token, no ids, nothing that works on its own.
        Assert.DoesNotContain("http", message.TextBody);
        Assert.DoesNotContain(TokenOf(invite), message.TextBody);
        Assert.DoesNotContain(partyId.ToString(), message.TextBody);
        Assert.DoesNotContain(collaboratorId.ToString(), message.TextBody);
    }

    [Fact]
    public async Task A_wrong_code_says_only_that_and_five_of_them_spend_the_challenge()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner);
        var (_, invite) = await AddCollaboratorAsync(
            owner, partyId, "Luca", "luca@example.com", PartyCrewRoles.Director);

        var device = _factory.CreateClient();
        (await device.PostAsJsonAsync(
            "/api/party-crew/auth/invite", new { token = TokenOf(invite) })).EnsureSuccessStatusCode();
        var right = CodeFromLastEmail(_factory);
        var wrong = right == "000000" ? "111111" : "000000";

        // Every wrong code up to the last says the same thing, and never how
        // many are left: that is a counter for a guesser.
        for (var attempt = 1; attempt < PartyCrewLimits.MaxOtpAttempts; attempt++)
        {
            var refused = await device.PostAsJsonAsync("/api/party-crew/auth/verify", new { code = wrong });
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Equal("invalid_code", await PartyCrewTestKit.ErrorOf(refused));
        }

        // The last one spends the challenge as it is charged.
        var spent = await device.PostAsJsonAsync("/api/party-crew/auth/verify", new { code = wrong });
        Assert.Equal(HttpStatusCode.TooManyRequests, spent.StatusCode);

        // Spent. Even the RIGHT code no longer works on this challenge.
        var afterwards = await device.PostAsJsonAsync("/api/party-crew/auth/verify", new { code = right });
        Assert.Equal(HttpStatusCode.TooManyRequests, afterwards.StatusCode);
        AssertRefused(await device.GetAsync($"/api/party-crew/parties/{partyId}/session"));
    }

    [Fact]
    public async Task A_resend_inside_the_interval_is_refused_and_the_next_code_replaces_the_last()
    {
        // Its OWN host: a controllable clock cannot share a pooled one.
        var clock = new MovableClock(DateTimeOffset.Parse("2027-06-12T18:00:00Z"));
        using var factory = new SqliteWebApplicationFactory(
            new Dictionary<string, string?> { ["Mail:PublicOrigin"] = Origin }, clock);
        factory.EnsureDatabaseCreated();
        var (_, owner) = await NewHostAsync(factory);
        var partyId = await CreatePartyAsync(owner);
        var (_, invite) = await AddCollaboratorAsync(
            owner, partyId, "Gio", "gio@example.com", PartyCrewRoles.CoOrganizer);

        var device = factory.CreateClient();
        (await device.PostAsJsonAsync(
            "/api/party-crew/auth/invite", new { token = TokenOf(invite) })).EnsureSuccessStatusCode();
        var first = CodeFromLastEmail(factory);

        var tooSoon = await device.PostAsync("/api/party-crew/auth/resend", null);
        Assert.Equal(HttpStatusCode.TooManyRequests, tooSoon.StatusCode);

        clock.Advance(PartyCrewLimits.ResendInterval + TimeSpan.FromSeconds(1));
        (await device.PostAsync("/api/party-crew/auth/resend", null)).EnsureSuccessStatusCode();
        var second = CodeFromLastEmail(factory);

        // The OLD code is dead the moment a new one is sent.
        var stale = await device.PostAsJsonAsync("/api/party-crew/auth/verify", new { code = first });
        Assert.Equal(HttpStatusCode.BadRequest, stale.StatusCode);
        var fresh = await device.PostAsJsonAsync("/api/party-crew/auth/verify", new { code = second });
        fresh.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task A_used_link_cannot_start_a_second_pairing_and_a_new_link_kills_the_old_one()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner);
        var (collaboratorId, invite) = await AddCollaboratorAsync(
            owner, partyId, "Ale", "ale@example.com", PartyCrewRoles.Director);

        await PairAsync(_factory, invite);

        // CONSUMED: the same link is not a second way in.
        var again = _factory.CreateClient();
        AssertRefused(await again.PostAsJsonAsync(
            "/api/party-crew/auth/invite", new { token = TokenOf(invite) }));

        // A NEW link works, and asking for it is also how the previous one
        // stops working — which is what "send them a new link" has to mean.
        var minted = await owner.PostAsync(
            $"/api/parties/{partyId}/crew/{collaboratorId}/invite", null);
        minted.EnsureSuccessStatusCode();
        var second = (await minted.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("inviteUrl").GetString()!;
        Assert.NotEqual(TokenOf(invite), TokenOf(second));

        var third = await owner.PostAsync($"/api/parties/{partyId}/crew/{collaboratorId}/invite", null);
        third.EnsureSuccessStatusCode();
        var stale = _factory.CreateClient();
        AssertRefused(await stale.PostAsJsonAsync(
            "/api/party-crew/auth/invite", new { token = TokenOf(second) }));
    }

    // ── The two-device limit ────────────────────────────────────────────────

    [Fact]
    public async Task A_third_device_is_offered_the_other_two_and_finishes_without_a_second_code()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner);
        var (collaboratorId, first) = await AddCollaboratorAsync(
            owner, partyId, "Vale", "vale@example.com", PartyCrewRoles.CoOrganizer);

        await PairAsync(_factory, first);
        await PairAsync(_factory, await NewLinkAsync(owner, partyId, collaboratorId));
        Assert.Equal(2, await LiveGrantsAsync(collaboratorId));

        // THE THIRD is not an error: the code was right, and the person is who
        // they said. They are shown their own two devices instead.
        var (third, result) = await VerifyAsync(
            _factory, await NewLinkAsync(owner, partyId, collaboratorId));
        Assert.Equal("DeviceLimitReached", result.GetProperty("outcome").GetString());
        Assert.Equal(2, result.GetProperty("devices").GetArrayLength());
        Assert.Equal(2, await LiveGrantsAsync(collaboratorId));
        // Not paired yet: the session is still nothing.
        AssertRefused(await third.GetAsync($"/api/party-crew/parties/{partyId}/session"));

        // Freeing a slot and finishing — WITHOUT typing a second code.
        var listed = await third.GetAsync("/api/party-crew/auth/devices");
        listed.EnsureSuccessStatusCode();
        var devices = await listed.Content.ReadFromJsonAsync<JsonElement>();
        var drop = devices[0].GetProperty("grantId").GetGuid();
        (await third.DeleteAsync($"/api/party-crew/auth/devices/{drop}")).EnsureSuccessStatusCode();

        var completed = await third.PostAsync("/api/party-crew/auth/complete", null);
        completed.EnsureSuccessStatusCode();
        Assert.Equal("Paired", (await completed.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("outcome").GetString());
        Assert.Equal(2, await LiveGrantsAsync(collaboratorId));
        Assert.Equal(partyId, (await SessionAsync(third, partyId)).GetProperty("partyId").GetGuid());
    }

    [Fact]
    public async Task The_same_device_pairing_twice_does_not_spend_a_second_slot()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner);
        var (collaboratorId, invite) = await AddCollaboratorAsync(
            owner, partyId, "Nina", "nina@example.com", PartyCrewRoles.Director);

        var device = await PairAsync(_factory, invite);
        Assert.Equal(1, await LiveGrantsAsync(collaboratorId));

        // The SAME browser follows a new link. It already holds this
        // collaborator's grant, so it is re-confirmed rather than counted again.
        var again = await NewLinkAsync(owner, partyId, collaboratorId);
        (await device.PostAsJsonAsync(
            "/api/party-crew/auth/invite", new { token = TokenOf(again) })).EnsureSuccessStatusCode();
        var verified = await device.PostAsJsonAsync(
            "/api/party-crew/auth/verify", new { code = CodeFromLastEmail(_factory) });
        verified.EnsureSuccessStatusCode();
        Assert.Equal("Paired", (await verified.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("outcome").GetString());
        Assert.Equal(1, await LiveGrantsAsync(collaboratorId));
    }

    // ── What a role may do ──────────────────────────────────────────────────

    [Fact]
    public async Task A_director_runs_the_evening_and_never_sees_a_guest()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner);
        await OpenPublicQrAsync(owner, partyId);
        var (_, invite) = await AddCollaboratorAsync(
            owner, partyId, "Regista", "regista@example.com", PartyCrewRoles.Director);
        var director = await PairAsync(_factory, invite);

        var capabilities = CapabilitiesOf(await SessionAsync(director, partyId));
        Assert.Contains(PartyCrewCapabilities.ActivitiesControl, capabilities);
        Assert.Contains(PartyCrewCapabilities.ContributionsModerate, capabilities);
        Assert.Contains(PartyCrewCapabilities.ScreensManage, capabilities);
        // MODERATING IS NOT CONFIGURING. A director decides what stays up
        // tonight; whether guests may upload at all, and whether what they
        // upload needs approving, is the host's standing decision about their
        // own party and their own library.
        Assert.DoesNotContain(PartyCrewCapabilities.ContributionsConfigure, capabilities);
        // The guest list is not theirs, and neither is sending anything.
        Assert.DoesNotContain(PartyCrewCapabilities.GuestsRead, capabilities);
        Assert.DoesNotContain(PartyCrewCapabilities.InvitationsManage, capabilities);
        Assert.DoesNotContain(PartyCrewCapabilities.AttendanceManage, capabilities);

        // The party itself, the greetings and the photographs: yes.
        (await director.GetAsync($"/api/party-crew/parties/{partyId}/party")).EnsureSuccessStatusCode();
        (await director.GetAsync($"/api/party-crew/parties/{partyId}/uploads")).EnsureSuccessStatusCode();
        (await director.GetAsync($"/api/party-crew/parties/{partyId}/messages")).EnsureSuccessStatusCode();

        // The contribution CONFIGURATION is not theirs either, and the routes
        // say so rather than the surface merely hiding the switch.
        AssertRefused(await director.PatchAsJsonAsync(
            $"/api/party-crew/parties/{partyId}/album-settings",
            new { enabled = true, requireUploadApproval = true }));
        AssertRefused(await director.PatchAsJsonAsync(
            $"/api/party-crew/parties/{partyId}/slideshow-settings",
            new { maxPhotoUploadsPerParticipant = 3 }));

        // But the party's own lifecycle IS: opening it to guests is what they
        // are there for, and it carries no configuration.
        (await director.PatchAsJsonAsync(
            $"/api/party-crew/parties/{partyId}/album-settings",
            new { enabled = true })).EnsureSuccessStatusCode();

        // A guest's NAME: not a 403 that confirms the surface exists — nothing.
        AssertRefused(await director.PostAsJsonAsync(
            $"/api/party-crew/parties/{partyId}/guest-directory/query", new { take = 50 }));
        AssertRefused(await director.GetAsync($"/api/party-crew/parties/{partyId}/attendance"));
        AssertRefused(await director.GetAsync($"/api/party-crew/parties/{partyId}/rsvp-questions"));
        AssertRefused(await director.PostAsJsonAsync(
            $"/api/party-crew/parties/{partyId}/invitation-groups", new { label = "Famiglia", maxAdditionalGuests = 0 }));
    }

    [Fact]
    public async Task A_co_organizer_runs_the_guest_list_but_never_the_party_itself()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner);
        await OpenPublicQrAsync(owner, partyId);
        var (_, invite) = await AddCollaboratorAsync(
            owner, partyId, "Co", "co@example.com", PartyCrewRoles.CoOrganizer);
        var co = await PairAsync(_factory, invite);

        (await co.PostAsJsonAsync(
            $"/api/party-crew/parties/{partyId}/guest-directory/query", new { take = 50 })).EnsureSuccessStatusCode();
        (await co.GetAsync($"/api/party-crew/parties/{partyId}/attendance")).EnsureSuccessStatusCode();

        // OWNER-ONLY, for the life of the party: adding another collaborator,
        // re-pointing the album, duplicating, tearing down. None of them exist
        // on this family of routes at all.
        AssertAbsent(await co.PostAsJsonAsync(
            $"/api/party-crew/parties/{partyId}/crew", new { displayName = "X", email = "x@example.com", roleKey = "director" }));
        AssertAbsent(await co.PutAsJsonAsync($"/api/party-crew/parties/{partyId}/party/media/main", new { }));
        AssertAbsent(await co.PostAsync($"/api/party-crew/parties/{partyId}/party/duplicate", null));
        AssertAbsent(await co.DeleteAsync($"/api/party-crew/parties/{partyId}/party"));

        // And the host's own routes refuse a device cookie: it is not a session.
        AssertRefusedOrUnauthorized(await co.GetAsync($"/api/parties/{partyId}"));
        AssertRefusedOrUnauthorized(await co.GetAsync("/api/parties"));
        AssertRefusedOrUnauthorized(await co.GetAsync("/api/albums"));
    }

    [Fact]
    public async Task Delegation_can_never_exceed_what_the_host_is_still_permitted_to_do()
    {
        // A host WITHOUT party.games. Their director's preset still names
        // activities.control; the intersection takes it away, because the host
        // cannot run a game either.
        var (_, owner) = await _factory.CreatePermissionClientAsync(
            $"host-{Guid.NewGuid():N}@example.com",
            NubArca.Api.Access.Permissions.PartyAccess,
            NubArca.Api.Access.Permissions.PartyContributions);
        var partyId = await CreatePartyAsync(owner);
        await OpenPublicQrAsync(owner, partyId);
        var (_, invite) = await AddCollaboratorAsync(
            owner, partyId, "Regista", "r@example.com", PartyCrewRoles.Director);
        var director = await PairAsync(_factory, invite);

        var capabilities = CapabilitiesOf(await SessionAsync(director, partyId));
        Assert.DoesNotContain(PartyCrewCapabilities.ActivitiesControl, capabilities);
        Assert.DoesNotContain(PartyCrewCapabilities.ActivitiesManage, capabilities);
        Assert.DoesNotContain(PartyCrewCapabilities.PrintManage, capabilities);
        // Moderation needs only party.access, which the host still holds.
        Assert.Contains(PartyCrewCapabilities.ContributionsModerate, capabilities);

        AssertRefused(await director.GetAsync($"/api/party-crew/parties/{partyId}/game"));
        AssertRefused(await director.GetAsync($"/api/party-crew/parties/{partyId}/challenges"));
        (await director.GetAsync($"/api/party-crew/parties/{partyId}/uploads")).EnsureSuccessStatusCode();
    }

    // ── Revoking ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Everything_the_host_takes_away_takes_effect_on_the_next_request()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner);
        var (collaboratorId, invite) = await AddCollaboratorAsync(
            owner, partyId, "Temp", "temp@example.com", PartyCrewRoles.CoOrganizer);
        var device = await PairAsync(_factory, invite);
        (await device.GetAsync($"/api/party-crew/parties/{partyId}/party")).EnsureSuccessStatusCode();

        // A ROLE CHANGE replaces the whole capability set, with nobody signing
        // out and no token rotated.
        var before = await CrewAsync(owner, partyId);
        var version = before.GetProperty("collaborators")[0].GetProperty("version").GetInt32();
        (await owner.PutAsJsonAsync(
            $"/api/parties/{partyId}/crew/{collaboratorId}",
            new
            {
                displayName = "Temp", email = "temp@example.com",
                roleKey = PartyCrewRoles.Director, version,
            })).EnsureSuccessStatusCode();
        Assert.DoesNotContain(
            PartyCrewCapabilities.GuestsRead, CapabilitiesOf(await SessionAsync(device, partyId)));

        // A REVOKE ends it, on the very next request.
        (await owner.DeleteAsync($"/api/parties/{partyId}/crew/{collaboratorId}"))
            .EnsureSuccessStatusCode();
        AssertRefused(await device.GetAsync($"/api/party-crew/parties/{partyId}/session"));
        AssertRefused(await device.GetAsync($"/api/party-crew/parties/{partyId}/party"));
    }

    [Fact]
    public async Task Changing_the_address_revokes_every_device_because_it_may_be_a_different_person()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner);
        var (collaboratorId, invite) = await AddCollaboratorAsync(
            owner, partyId, "Chi", "prima@example.com", PartyCrewRoles.CoOrganizer);
        var device = await PairAsync(_factory, invite);
        (await device.GetAsync($"/api/party-crew/parties/{partyId}/session")).EnsureSuccessStatusCode();

        var version = (await CrewAsync(owner, partyId))
            .GetProperty("collaborators")[0].GetProperty("version").GetInt32();
        (await owner.PutAsJsonAsync(
            $"/api/parties/{partyId}/crew/{collaboratorId}",
            new
            {
                displayName = "Chi", email = "dopo@example.com",
                roleKey = PartyCrewRoles.CoOrganizer, version,
            })).EnsureSuccessStatusCode();

        // The device was verified against a mailbox the host has replaced.
        AssertRefused(await device.GetAsync($"/api/party-crew/parties/{partyId}/session"));
        Assert.Equal(0, await LiveGrantsAsync(collaboratorId));
    }

    [Fact]
    public async Task A_person_can_sign_their_own_device_out_and_drop_the_other_one()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner);
        var (collaboratorId, first) = await AddCollaboratorAsync(
            owner, partyId, "Due", "due@example.com", PartyCrewRoles.Director);
        var phone = await PairAsync(_factory, first);
        var tablet = await PairAsync(_factory, await NewLinkAsync(owner, partyId, collaboratorId));

        var mine = await phone.GetAsync($"/api/party-crew/parties/{partyId}/devices");
        mine.EnsureSuccessStatusCode();
        var listed = await mine.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, listed.GetArrayLength());
        var other = listed.EnumerateArray().Single(d => !d.GetProperty("isCurrent").GetBoolean());

        (await phone.DeleteAsync($"/api/party-crew/parties/{partyId}/devices/{other.GetProperty("grantId").GetGuid()}"))
            .EnsureSuccessStatusCode();
        AssertRefused(await tablet.GetAsync($"/api/party-crew/parties/{partyId}/session"));
        (await phone.GetAsync($"/api/party-crew/parties/{partyId}/session")).EnsureSuccessStatusCode();

        (await phone.DeleteAsync($"/api/party-crew/parties/{partyId}/session")).EnsureSuccessStatusCode();
        AssertRefused(await phone.GetAsync($"/api/party-crew/parties/{partyId}/session"));
        Assert.Equal(0, await LiveGrantsAsync(collaboratorId));
    }

    // ── Isolation ───────────────────────────────────────────────────────────

    [Fact]
    public async Task A_device_reaches_the_party_it_was_asked_for_and_no_other()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var mine = await CreatePartyAsync(owner, "La mia festa");
        var theirs = await CreatePartyAsync(owner, "L'altra festa");
        var (_, invite) = await AddCollaboratorAsync(
            owner, mine, "Uno", "uno@example.com", PartyCrewRoles.CoOrganizer);
        var device = await PairAsync(_factory, invite);

        var party = await device.GetFromJsonAsync<JsonElement>(
            $"/api/party-crew/parties/{mine}/party");
        Assert.Equal(mine, party.GetProperty("id").GetGuid());

        // THE ID IN THE URL IS A SELECTOR, NOT AN AUTHORITY. Naming the other
        // party — which this device holds no grant for — is the same nothing as
        // holding no device at all, and it is emphatically not a fallback to
        // the one party this device does have.
        AssertRefused(await device.GetAsync($"/api/party-crew/parties/{theirs}/party"));
        AssertRefused(await device.GetAsync($"/api/party-crew/parties/{theirs}/session"));
        AssertRefused(await device.GetAsync($"/api/party-crew/parties/{Guid.NewGuid()}/party"));
    }

    [Fact]
    public async Task One_browser_at_two_parties_gets_the_right_one_each_time()
    {
        // The whole reason the party is named. Laura helps at two parties from
        // one phone: one device, two grants, two roles. Nothing may decide
        // which evening she is running by insertion order.
        var (_, owner) = await NewHostAsync(_factory);
        var first = await CreatePartyAsync(owner, "Il matrimonio");
        var second = await CreatePartyAsync(owner, "Il compleanno");
        var (_, firstInvite) = await AddCollaboratorAsync(
            owner, first, "Laura", "laura@example.com", PartyCrewRoles.CoOrganizer);
        var (_, secondInvite) = await AddCollaboratorAsync(
            owner, second, "Laura", "laura@example.com", PartyCrewRoles.Director);

        var phone = await PairAsync(_factory, firstInvite);
        (await phone.PostAsJsonAsync(
            "/api/party-crew/auth/invite",
            new { token = TokenOf(secondInvite) })).EnsureSuccessStatusCode();
        (await phone.PostAsJsonAsync(
            "/api/party-crew/auth/verify",
            new { code = CodeFromLastEmail(_factory) })).EnsureSuccessStatusCode();

        var atFirst = await SessionAsync(phone, first);
        Assert.Equal(first, atFirst.GetProperty("partyId").GetGuid());
        Assert.Equal("Il matrimonio", atFirst.GetProperty("partyTitle").GetString());
        Assert.Equal(PartyCrewRoles.CoOrganizer, atFirst.GetProperty("roleKey").GetString());
        Assert.Contains(PartyCrewCapabilities.GuestsRead, CapabilitiesOf(atFirst));

        var atSecond = await SessionAsync(phone, second);
        Assert.Equal(second, atSecond.GetProperty("partyId").GetGuid());
        Assert.Equal("Il compleanno", atSecond.GetProperty("partyTitle").GetString());
        Assert.Equal(PartyCrewRoles.Director, atSecond.GetProperty("roleKey").GetString());
        // The role at the OTHER party does not leak into this one.
        Assert.DoesNotContain(PartyCrewCapabilities.GuestsRead, CapabilitiesOf(atSecond));

        // And the capability check follows the party, not the device: the guest
        // list is open at the first and absent at the second.
        (await phone.PostAsJsonAsync(
            $"/api/party-crew/parties/{first}/guest-directory/query",
            new { take = 50 })).EnsureSuccessStatusCode();
        AssertRefused(await phone.PostAsJsonAsync(
            $"/api/party-crew/parties/{second}/guest-directory/query", new { take = 50 }));
    }

    [Fact]
    public async Task Every_party_this_browser_may_operate_and_nothing_of_the_owner_s()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var helping = await CreatePartyAsync(owner, "Dove aiuto");
        var elsewhere = await CreatePartyAsync(owner, "Dove non aiuto");
        var (_, invite) = await AddCollaboratorAsync(
            owner, helping, "Laura", "laura@example.com", PartyCrewRoles.Director);
        var phone = await PairAsync(_factory, invite);

        var me = await phone.GetFromJsonAsync<JsonElement>("/api/party-crew/me");
        var assignments = me.GetProperty("assignments");
        Assert.Equal(1, assignments.GetArrayLength());
        Assert.Equal(helping, assignments[0].GetProperty("partyId").GetGuid());
        Assert.Equal("Dove aiuto", assignments[0].GetProperty("partyTitle").GetString());

        // The owner's OTHER party is not this device's business, and neither is
        // anything else about them.
        var wire = me.GetRawText();
        Assert.DoesNotContain(elsewhere.ToString(), wire);
        Assert.DoesNotContain("Dove non aiuto", wire);
        Assert.DoesNotContain("laura@example.com", wire);
    }

    [Fact]
    public async Task One_host_cannot_see_or_touch_another_host_s_collaborators()
    {
        var (_, alice) = await NewHostAsync(_factory);
        var (_, bob) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(alice);
        var (collaboratorId, _) = await AddCollaboratorAsync(
            alice, partyId, "Suo", "suo@example.com", PartyCrewRoles.Director);

        AssertRefused(await bob.GetAsync($"/api/parties/{partyId}/crew"));
        AssertRefused(await bob.PostAsJsonAsync(
            $"/api/parties/{partyId}/crew",
            new { displayName = "X", email = "x@example.com", roleKey = "director" }));
        AssertRefused(await bob.DeleteAsync($"/api/parties/{partyId}/crew/{collaboratorId}"));
        AssertRefused(await bob.PostAsync($"/api/parties/{partyId}/crew/{collaboratorId}/invite", null));
    }

    [Fact]
    public async Task A_collaborator_s_address_is_owner_private_and_a_link_is_returned_once()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner);
        var (_, invite) = await AddCollaboratorAsync(
            owner, partyId, "Privata", "privata@example.com", PartyCrewRoles.Director);
        var device = await PairAsync(_factory, invite);

        // The HOST sees the address they chose — nobody else does, and no read
        // ever hands back a link.
        var crew = await CrewAsync(owner, partyId);
        Assert.Contains("privata@example.com", crew.GetRawText());
        Assert.DoesNotContain("inviteUrl", crew.GetRawText());
        Assert.DoesNotContain(TokenOf(invite), crew.GetRawText());

        // The collaborator's OWN session says who they are and nothing about
        // where the code went.
        var session = await SessionAsync(device, partyId);
        Assert.DoesNotContain("privata@example.com", session.GetRawText());

        // And the audit names the collaborator by ID, never by name or address.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var entries = await db.AuditLogs
            .Where(a => a.Action.StartsWith("party.crew."))
            .ToListAsync();
        Assert.NotEmpty(entries);
        foreach (var entry in entries)
        {
            Assert.DoesNotContain("privata@example.com", entry.MetadataJson ?? "");
            Assert.DoesNotContain("Privata", entry.MetadataJson ?? "");
            Assert.DoesNotContain(TokenOf(invite), entry.MetadataJson ?? "");
        }
    }

    [Fact]
    public async Task What_a_collaborator_does_is_audited_as_the_collaborator_and_never_as_the_host()
    {
        var (ownerId, owner) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner);
        var (albumId, _, _) = await OpenPublicQrAsync(owner, partyId);
        var (collaboratorId, invite) = await AddCollaboratorAsync(
            owner, partyId, "Regia", "regia@example.com", PartyCrewRoles.CoOrganizer);
        var device = await PairAsync(_factory, invite);

        var party = await device.GetFromJsonAsync<JsonElement>($"/api/party-crew/parties/{partyId}/party");
        (await device.PostAsJsonAsync(
            $"/api/party-crew/parties/{partyId}/party/start-live",
            new { version = party.GetProperty("version").GetInt32() })).EnsureSuccessStatusCode();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var started = await db.AuditLogs
            .Where(a => a.Action == NubArca.Api.Audit.AuditActions.PartyStartLive && a.EntityId == partyId)
            .SingleAsync();
        // The line names WHO, and it is not the host.
        Assert.Equal(collaboratorId, started.PartyCollaboratorId);
        Assert.Null(started.UserId);
        Assert.NotEqual(ownerId, started.PartyCollaboratorId);
        Assert.DoesNotContain(albumId.ToString(), started.MetadataJson ?? "");
    }

    // ── Teardown ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Tearing_the_party_down_takes_its_crew_with_it()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner);
        var (collaboratorId, invite) = await AddCollaboratorAsync(
            owner, partyId, "Andrà", "andra@example.com", PartyCrewRoles.CoOrganizer);
        var device = await PairAsync(_factory, invite);

        var version = (await GetPartyAsync(owner, partyId)).GetProperty("version").GetInt32();
        var torn = await owner.DeleteAsync($"/api/parties/{partyId}?version={version}");
        torn.EnsureSuccessStatusCode();

        AssertRefused(await device.GetAsync($"/api/party-crew/parties/{partyId}/session"));
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Empty(await db.PartyCollaborators.Where(c => c.PartyId == partyId).ToListAsync());
        Assert.Empty(await db.PartyCollaboratorGrants
            .Where(g => g.PartyCollaboratorId == collaboratorId).ToListAsync());
        Assert.Empty(await db.PartyCollaboratorDeviceGrants
            .Where(g => g.PartyCollaboratorId == collaboratorId).ToListAsync());
        Assert.Empty(await db.PartyCollaboratorInvites
            .Where(i => i.PartyCollaboratorId == collaboratorId).ToListAsync());
        Assert.Empty(await db.PartyCollaboratorAuthChallenges
            .Where(c => c.PartyCollaboratorId == collaboratorId).ToListAsync());
        // The device itself held nothing else, so it went too.
        Assert.Empty(await db.PartyCrewDevices.ToListAsync());
    }

    [Fact]
    public async Task A_device_helping_at_two_parties_keeps_the_one_that_still_exists()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var doomed = await CreatePartyAsync(owner, "Quella che sparisce");
        var surviving = await CreatePartyAsync(owner, "Quella che resta");
        var (_, firstInvite) = await AddCollaboratorAsync(
            owner, doomed, "Ovunque", "ovunque@example.com", PartyCrewRoles.CoOrganizer);
        var (_, secondInvite) = await AddCollaboratorAsync(
            owner, surviving, "Ovunque", "ovunque@example.com", PartyCrewRoles.CoOrganizer);

        // ONE browser, two parties: one device, two grants.
        var device = await PairAsync(_factory, firstInvite);
        (await device.PostAsJsonAsync(
            "/api/party-crew/auth/invite",
            new { token = TokenOf(secondInvite) })).EnsureSuccessStatusCode();
        (await device.PostAsJsonAsync(
            "/api/party-crew/auth/verify",
            new { code = CodeFromLastEmail(_factory) })).EnsureSuccessStatusCode();

        var version = (await GetPartyAsync(owner, doomed)).GetProperty("version").GetInt32();
        (await owner.DeleteAsync($"/api/parties/{doomed}?version={version}")).EnsureSuccessStatusCode();

        // Still signed in — at the party that still exists, and nowhere else.
        var session = await SessionAsync(device, surviving);
        Assert.Equal(surviving, session.GetProperty("partyId").GetGuid());
        AssertRefused(await device.GetAsync($"/api/party-crew/parties/{doomed}/session"));
    }

    // ── Validation ──────────────────────────────────────────────────────────

    [Fact]
    public async Task The_host_may_only_hand_out_the_two_roles_this_release_offers()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner);

        var overview = await CrewAsync(owner, partyId);
        Assert.Equal(
            [PartyCrewRoles.CoOrganizer, PartyCrewRoles.Director],
            overview.GetProperty("assignableRoles").EnumerateArray().Select(r => r.GetString()).ToArray());

        foreach (var role in new[] { PartyCrewRoles.Dj, PartyCrewRoles.Reception, PartyCrewRoles.Honoree })
        {
            var refused = await owner.PostAsJsonAsync(
                $"/api/parties/{partyId}/crew",
                new { displayName = "X", email = $"x-{role}@example.com", roleKey = role });
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Equal("role_not_assignable", await PartyCrewTestKit.ErrorOf(refused));
        }

        var nonsense = await owner.PostAsJsonAsync(
            $"/api/parties/{partyId}/crew",
            new { displayName = "X", email = "x@example.com", roleKey = "superuser" });
        Assert.Equal(HttpStatusCode.BadRequest, nonsense.StatusCode);
        Assert.Equal("invalid_role", await PartyCrewTestKit.ErrorOf(nonsense));
    }

    [Fact]
    public async Task One_live_collaborator_per_address_per_party_and_a_revoked_one_frees_it()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner);
        var (collaboratorId, _) = await AddCollaboratorAsync(
            owner, partyId, "Primo", "stesso@example.com", PartyCrewRoles.Director);

        // The same inbox twice would mean two codes to one person and four
        // devices where the product promises two. Case-folded, like every other
        // address in the product.
        var duplicate = await owner.PostAsJsonAsync(
            $"/api/parties/{partyId}/crew",
            new { displayName = "Secondo", email = "STESSO@example.com", roleKey = "director" });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("email_in_use", await PartyCrewTestKit.ErrorOf(duplicate));

        (await owner.DeleteAsync($"/api/parties/{partyId}/crew/{collaboratorId}")).EnsureSuccessStatusCode();
        var again = await owner.PostAsJsonAsync(
            $"/api/parties/{partyId}/crew",
            new { displayName = "Secondo", email = "stesso@example.com", roleKey = "director" });
        again.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Party_crew_says_plainly_when_the_installation_cannot_send_email()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner);
        _factory.EmailSender.Enabled = false;
        try
        {
            var overview = await CrewAsync(owner, partyId);
            Assert.False(overview.GetProperty("mailAvailable").GetBoolean());

            // Minting a link nobody could finish using would be worse than
            // saying so: the second factor IS the email.
            var refused = await owner.PostAsJsonAsync(
                $"/api/parties/{partyId}/crew",
                new { displayName = "Nessuno", email = "n@example.com", roleKey = "director" });
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Equal("mail_unavailable", await PartyCrewTestKit.ErrorOf(refused));
        }
        finally { _factory.EmailSender.Enabled = true; }
    }

    [Fact]
    public async Task A_stale_form_cannot_overwrite_a_decision_it_never_saw()
    {
        var (_, owner) = await NewHostAsync(_factory);
        var partyId = await CreatePartyAsync(owner);
        var (collaboratorId, _) = await AddCollaboratorAsync(
            owner, partyId, "Uno", "uno@example.com", PartyCrewRoles.Director);

        var stale = (await CrewAsync(owner, partyId))
            .GetProperty("collaborators")[0].GetProperty("version").GetInt32();
        (await owner.PutAsJsonAsync(
            $"/api/parties/{partyId}/crew/{collaboratorId}",
            new
            {
                displayName = "Uno", email = "uno@example.com",
                roleKey = PartyCrewRoles.CoOrganizer, version = stale,
            })).EnsureSuccessStatusCode();

        var refused = await owner.PutAsJsonAsync(
            $"/api/parties/{partyId}/crew/{collaboratorId}",
            new
            {
                displayName = "Uno", email = "uno@example.com",
                roleKey = PartyCrewRoles.Director, version = stale,
            });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("version_conflict", await PartyCrewTestKit.ErrorOf(refused));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static async Task<string> NewLinkAsync(HttpClient owner, Guid partyId, Guid collaboratorId)
    {
        var minted = await owner.PostAsync($"/api/parties/{partyId}/crew/{collaboratorId}/invite", null);
        minted.EnsureSuccessStatusCode();
        return (await minted.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("inviteUrl").GetString()!;
    }

    private async Task<int> LiveGrantsAsync(Guid collaboratorId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.PartyCollaboratorDeviceGrants
            .CountAsync(g => g.PartyCollaboratorId == collaboratorId && g.RevokedAt == null);
    }

    /// <summary>
    /// A route that is not part of the Party Crew surface at all.
    ///
    /// <para>404 or 405 — the framework picks one depending on whether some
    /// other method shares the path. Both mean the same thing: there is no such
    /// operation here. A CAPABILITY refusal is stricter and must be exactly 404,
    /// which is what <c>AssertRefused</c> checks.</para>
    /// </summary>
    private static void AssertAbsent(HttpResponseMessage response) =>
        Assert.True(
            response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed,
            $"A Party Crew device reached a route that should not exist: {response.StatusCode}");

    /// <summary>A clock a test can push forward, for the intervals the product enforces.</summary>
    private sealed class MovableClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }

    /// <summary>
    /// An authenticated route met by a device cookie. Either answer is correct
    /// — the point is that it is never a success.
    /// </summary>
    private static void AssertRefusedOrUnauthorized(HttpResponseMessage response) =>
        Assert.True(
            response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                or HttpStatusCode.NotFound,
            $"A Party Crew device reached an authenticated route: {response.StatusCode}");
}
