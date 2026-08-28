using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NubArca.Api.Domain;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Tv;

namespace NubArca.Api.Tests.Party;

// PARTY-GUEST-MESSAGES-01. A guest writes a short greeting; it reaches the
// television as a ribbon, and the host (or one narrowly delegated member) may
// approve, take down, or promote it to a full-screen Hero card.
//
// The properties worth defending, and what would break if each stopped holding:
//   * the approval mode is per-party and governs NEW submissions only, so
//     turning it off never publishes a backlog somebody declined to approve;
//   * `owner || CanManagePartyMessages` is the WHOLE authorization rule, so an
//     `editor` gains nothing and a delegate gains nothing beyond messages;
//   * every projection is recomputed from the party's CURRENT state, so hiding,
//     revoking or re-enabling removes content with no stale resurrection and no
//     row rewriting;
//   * TvAlbumItem is untouched, so an older TV APK keeps working.
public sealed class PartyGuestMessageTests : IDisposable
{
    private const string OwnerEmail = "owner@example.com";
    private const string DelegateEmail = "delegate@example.com";
    private const string EditorEmail = "editor@example.com";

    private readonly SqliteWebApplicationFactory _factory = new();

    public PartyGuestMessageTests() => _factory.EnsureDatabaseCreated();

    public void Dispose() => _factory.Dispose();

    // ── Auto-publish ────────────────────────────────────────────────────────

    [Fact]
    public async Task With_Approval_Off_A_Message_Is_Born_Visible_And_Reaches_The_Tv()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var albumId = await CreateAlbumAsync(owner, "Festa");
        var status = await EnablePartyAsync(owner, albumId);
        var tv = await PairTvAsync(owner);

        // Approval is off by default — the low-friction party behaviour.
        Assert.False(status.GetProperty("requireMessageApproval").GetBoolean());

        var submitted = await SubmitMessageAsync(
            UploadTokenFromStatus(status), "Giulia", "Serata fantastica! Auguri ragazzi ❤️");
        Assert.Equal("visible", submitted.GetProperty("status").GetString());

        var messages = await TvMessagesAsync(tv, albumId);
        Assert.Equal(1, messages.GetArrayLength());
        Assert.Equal("Giulia", messages[0].GetProperty("displayName").GetString());
        Assert.Equal("Serata fantastica! Auguri ragazzi ❤️", messages[0].GetProperty("text").GetString());
        Assert.False(messages[0].GetProperty("isHero").GetBoolean());
    }

    [Fact]
    public async Task A_Message_Without_A_Name_Carries_Null_Rather_Than_An_Empty_String()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var albumId = await CreateAlbumAsync(owner, "Festa");
        var status = await EnablePartyAsync(owner, albumId);
        var tv = await PairTvAsync(owner);

        await SubmitMessageAsync(UploadTokenFromStatus(status), "   ", "Auguri!");

        // One shape for "unsigned", so the TV has one case to render.
        var messages = await TvMessagesAsync(tv, albumId);
        Assert.Equal(JsonValueKind.Null, messages[0].GetProperty("displayName").ValueKind);
    }

    // ── Approval ────────────────────────────────────────────────────────────

    [Fact]
    public async Task With_Approval_On_A_Message_Is_Pending_And_Invisible_Until_Approved()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var albumId = await CreateAlbumAsync(owner, "Festa");
        var status = await EnablePartyAsync(owner, albumId, requireMessageApproval: true);
        var tv = await PairTvAsync(owner);
        Assert.True(status.GetProperty("requireMessageApproval").GetBoolean());

        var submitted = await SubmitMessageAsync(UploadTokenFromStatus(status), "Marco", "Auguri!");
        Assert.Equal("pending", submitted.GetProperty("status").GetString());
        var messageId = submitted.GetProperty("id").GetGuid();

        // Nowhere near the television.
        Assert.Equal(0, (await TvMessagesAsync(tv, albumId)).GetArrayLength());

        // But in the host's queue.
        var queue = await ListMessagesAsync(owner, albumId);
        Assert.True(queue.GetProperty("requireMessageApproval").GetBoolean());
        Assert.Equal("pending", queue.GetProperty("items")[0].GetProperty("status").GetString());

        Assert.Equal(HttpStatusCode.NoContent, await ApproveAsync(owner, albumId, messageId));

        var messages = await TvMessagesAsync(tv, albumId);
        Assert.Equal(1, messages.GetArrayLength());
        Assert.Equal("Auguri!", messages[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task Message_Approval_Is_Independent_Of_Upload_Approval()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var albumId = await CreateAlbumAsync(owner, "Festa");

        // Photos straight through, greetings read first. The two switches are
        // separate decisions and neither may move the other.
        var status = await EnablePartyAsync(
            owner, albumId, requireUploadApproval: false, requireMessageApproval: true);
        Assert.False(status.GetProperty("requireUploadApproval").GetBoolean());
        Assert.True(status.GetProperty("requireMessageApproval").GetBoolean());

        var flipped = await EnablePartyAsync(
            owner, albumId, requireUploadApproval: true, requireMessageApproval: false);
        Assert.True(flipped.GetProperty("requireUploadApproval").GetBoolean());
        Assert.False(flipped.GetProperty("requireMessageApproval").GetBoolean());
    }

    [Fact]
    public async Task Turning_Approval_Off_Does_Not_Publish_The_Existing_Pending_Backlog()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var albumId = await CreateAlbumAsync(owner, "Festa");
        var status = await EnablePartyAsync(owner, albumId, requireMessageApproval: true);
        var uploadToken = UploadTokenFromStatus(status);
        var tv = await PairTvAsync(owner);

        await SubmitMessageAsync(uploadToken, "Marco", "Waiting");

        var after = await EnablePartyAsync(owner, albumId, requireMessageApproval: false);
        Assert.False(after.GetProperty("requireMessageApproval").GetBoolean());

        // The setting governs NEW submissions. The one the host has not read is
        // still not on the wall.
        Assert.Equal(0, (await TvMessagesAsync(tv, albumId)).GetArrayLength());
        var queue = await ListMessagesAsync(owner, albumId);
        Assert.Equal("pending", queue.GetProperty("items")[0].GetProperty("status").GetString());

        // The next one is live immediately.
        await SubmitMessageAsync(uploadToken, "Ada", "Live");
        var messages = await TvMessagesAsync(tv, albumId);
        Assert.Equal(1, messages.GetArrayLength());
        Assert.Equal("Live", messages[0].GetProperty("text").GetString());
    }

    // ── Owner moderation ────────────────────────────────────────────────────

    [Fact]
    public async Task Owner_Can_Hide_And_Restore_A_Live_Message()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var albumId = await CreateAlbumAsync(owner, "Festa");
        var status = await EnablePartyAsync(owner, albumId);
        var tv = await PairTvAsync(owner);

        var messageId = (await SubmitMessageAsync(UploadTokenFromStatus(status), "Giulia", "Ciao"))
            .GetProperty("id").GetGuid();
        Assert.Equal(1, (await TvMessagesAsync(tv, albumId)).GetArrayLength());

        Assert.Equal(HttpStatusCode.NoContent, await PostAsync(owner, albumId, messageId, "hide"));
        Assert.Equal(0, (await TvMessagesAsync(tv, albumId)).GetArrayLength());
        Assert.Equal("hidden", (await ListMessagesAsync(owner, albumId))
            .GetProperty("items")[0].GetProperty("status").GetString());

        Assert.Equal(HttpStatusCode.NoContent, await PostAsync(owner, albumId, messageId, "restore"));
        Assert.Equal(1, (await TvMessagesAsync(tv, albumId)).GetArrayLength());
    }

    [Fact]
    public async Task Owner_Can_Reject_A_Pending_Message_And_It_Stays_Off_The_Tv()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var albumId = await CreateAlbumAsync(owner, "Festa");
        var status = await EnablePartyAsync(owner, albumId, requireMessageApproval: true);
        var tv = await PairTvAsync(owner);

        var messageId = (await SubmitMessageAsync(UploadTokenFromStatus(status), null, "Nope"))
            .GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.NoContent, await PostAsync(owner, albumId, messageId, "reject"));
        Assert.Equal("rejected", (await ListMessagesAsync(owner, albumId))
            .GetProperty("items")[0].GetProperty("status").GetString());
        Assert.Equal(0, (await TvMessagesAsync(tv, albumId)).GetArrayLength());
    }

    [Fact]
    public async Task The_Manager_Queue_Marks_The_Owner_As_Owner()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var albumId = await CreateAlbumAsync(owner, "Festa");
        await EnablePartyAsync(owner, albumId);

        var queue = await ListMessagesAsync(owner, albumId);
        Assert.True(queue.GetProperty("isOwner").GetBoolean());
        Assert.True(queue.GetProperty("partyActive").GetBoolean());
    }

    [Fact]
    public async Task An_Album_With_No_Party_Is_An_Empty_Queue_Not_A_Missing_One()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var albumId = await CreateAlbumAsync(owner, "Nessuna festa");

        // "No party running" and "no such album" are different answers, because
        // the owner UI has different things to say about them.
        var queue = await ListMessagesAsync(owner, albumId);
        Assert.False(queue.GetProperty("partyActive").GetBoolean());
        Assert.Equal(0, queue.GetProperty("items").GetArrayLength());
    }

    // ── Hero ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Owner_Can_Promote_A_Visible_Message_To_Hero_And_Demote_It_Again()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var albumId = await CreateAlbumAsync(owner, "Festa");
        var status = await EnablePartyAsync(owner, albumId);
        var tv = await PairTvAsync(owner);

        var messageId = (await SubmitMessageAsync(UploadTokenFromStatus(status), "Ada", "Evviva"))
            .GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.NoContent, await PostAsync(owner, albumId, messageId, "promote-hero"));
        var hero = (await TvMessagesAsync(tv, albumId))[0];
        Assert.True(hero.GetProperty("isHero").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, hero.GetProperty("heroPromotedAt").ValueKind);

        Assert.Equal(HttpStatusCode.NoContent, await PostAsync(owner, albumId, messageId, "demote-hero"));
        var plain = (await TvMessagesAsync(tv, albumId))[0];
        Assert.False(plain.GetProperty("isHero").GetBoolean());
        Assert.Equal(JsonValueKind.Null, plain.GetProperty("heroPromotedAt").ValueKind);
    }

    [Fact]
    public async Task A_Pending_Hidden_Or_Rejected_Message_Cannot_Be_Promoted()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var albumId = await CreateAlbumAsync(owner, "Festa");
        var status = await EnablePartyAsync(owner, albumId, requireMessageApproval: true);
        var uploadToken = UploadTokenFromStatus(status);

        var pending = (await SubmitMessageAsync(uploadToken, null, "Pending"))
            .GetProperty("id").GetGuid();
        // Real message, real manager, refused transition → 400, not a 404. The
        // caller is entitled to know the difference.
        Assert.Equal(HttpStatusCode.BadRequest, await PostAsync(owner, albumId, pending, "promote-hero"));

        var rejected = (await SubmitMessageAsync(uploadToken, null, "Rejected"))
            .GetProperty("id").GetGuid();
        await PostAsync(owner, albumId, rejected, "reject");
        Assert.Equal(HttpStatusCode.BadRequest, await PostAsync(owner, albumId, rejected, "promote-hero"));

        var hidden = (await SubmitMessageAsync(uploadToken, null, "Hidden"))
            .GetProperty("id").GetGuid();
        await PostAsync(owner, albumId, hidden, "approve");
        await PostAsync(owner, albumId, hidden, "hide");
        Assert.Equal(HttpStatusCode.BadRequest, await PostAsync(owner, albumId, hidden, "promote-hero"));
    }

    [Fact]
    public async Task Hiding_A_Hero_Removes_It_From_The_Tv_Entirely_And_Restoring_Brings_It_Back()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var albumId = await CreateAlbumAsync(owner, "Festa");
        var status = await EnablePartyAsync(owner, albumId);
        var tv = await PairTvAsync(owner);

        var messageId = (await SubmitMessageAsync(UploadTokenFromStatus(status), "Ada", "Evviva"))
            .GetProperty("id").GetGuid();
        await PostAsync(owner, albumId, messageId, "promote-hero");

        // A hidden Hero is not a quieter Hero: it is gone from the ribbon AND
        // from the Hero rotation, because the projection filters on Visible
        // before it ever looks at the promotion.
        await PostAsync(owner, albumId, messageId, "hide");
        Assert.Equal(0, (await TvMessagesAsync(tv, albumId)).GetArrayLength());

        // The owner's own queue still shows it, and stops calling it a Hero
        // while it is down.
        var hiddenRow = (await ListMessagesAsync(owner, albumId)).GetProperty("items")[0];
        Assert.Equal("hidden", hiddenRow.GetProperty("status").GetString());
        Assert.False(hiddenRow.GetProperty("isHero").GetBoolean());

        // Restoring returns it as a Hero: the promotion was never revoked, only
        // outranked by the message being invisible.
        await PostAsync(owner, albumId, messageId, "restore");
        Assert.True((await TvMessagesAsync(tv, albumId))[0].GetProperty("isHero").GetBoolean());
    }

    [Fact]
    public async Task Re_Promoting_Keeps_The_Original_Place_In_The_Rotation()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var albumId = await CreateAlbumAsync(owner, "Festa");
        var status = await EnablePartyAsync(owner, albumId);
        var tv = await PairTvAsync(owner);
        var uploadToken = UploadTokenFromStatus(status);

        var first = (await SubmitMessageAsync(uploadToken, null, "First")).GetProperty("id").GetGuid();
        var second = (await SubmitMessageAsync(uploadToken, null, "Second")).GetProperty("id").GetGuid();
        await PostAsync(owner, albumId, first, "promote-hero");
        await PostAsync(owner, albumId, second, "promote-hero");

        var before = HeroPromotedAt(await TvMessagesAsync(tv, albumId), "First");
        // Promoting something that is already a Hero must not jump it to the end
        // of the queue, or a host clicking twice reshuffles the rotation.
        await PostAsync(owner, albumId, first, "promote-hero");
        Assert.Equal(before, HeroPromotedAt(await TvMessagesAsync(tv, albumId), "First"));
    }

    // ── Delegation ──────────────────────────────────────────────────────────

    [Fact]
    public async Task A_Delegate_Can_Do_Every_Message_Action_The_Owner_Can()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, helper) = await _factory.CreateAuthenticatedClientAsync(DelegateEmail);
        var albumId = await CreateAlbumAsync(owner, "Festa");
        var status = await EnablePartyAsync(owner, albumId, requireMessageApproval: true);
        var uploadToken = UploadTokenFromStatus(status);
        await GrantMessageDelegationAsync(owner, helper, albumId, DelegateEmail);

        var queue = await ListMessagesAsync(helper, albumId);
        // Authorised to moderate, and told plainly that they are not the owner —
        // which is how the UI knows to hide the owner-only party settings.
        Assert.False(queue.GetProperty("isOwner").GetBoolean());

        var approve = (await SubmitMessageAsync(uploadToken, null, "One")).GetProperty("id").GetGuid();
        var reject = (await SubmitMessageAsync(uploadToken, null, "Two")).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.NoContent, await PostAsync(helper, albumId, approve, "approve"));
        Assert.Equal(HttpStatusCode.NoContent, await PostAsync(helper, albumId, reject, "reject"));
        Assert.Equal(HttpStatusCode.NoContent, await PostAsync(helper, albumId, approve, "promote-hero"));
        Assert.Equal(HttpStatusCode.NoContent, await PostAsync(helper, albumId, approve, "demote-hero"));
        Assert.Equal(HttpStatusCode.NoContent, await PostAsync(helper, albumId, approve, "hide"));
        Assert.Equal(HttpStatusCode.NoContent, await PostAsync(helper, albumId, approve, "restore"));
    }

    [Fact]
    public async Task An_Editor_Without_The_Capability_Cannot_Manage_Messages()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, editor) = await _factory.CreateAuthenticatedClientAsync(EditorEmail);
        var albumId = await CreateAlbumAsync(owner, "Festa");
        var status = await EnablePartyAsync(owner, albumId);
        var messageId = (await SubmitMessageAsync(UploadTokenFromStatus(status), null, "Ciao"))
            .GetProperty("id").GetGuid();

        // A full editor — the most powerful album role there is — and accepted.
        await InviteAndAcceptAsync(owner, editor, albumId, EditorEmail, role: "editor");

        // Curating an album is not running its party. The role grants nothing here.
        Assert.Equal(HttpStatusCode.NotFound,
            (await editor.GetAsync($"/api/albums/{albumId}/party-messages")).StatusCode);
        foreach (var action in new[] { "approve", "reject", "hide", "restore", "promote-hero", "demote-hero" })
        {
            Assert.Equal(HttpStatusCode.NotFound, await PostAsync(editor, albumId, messageId, action));
        }
    }

    [Fact]
    public async Task A_Delegate_Gains_No_Party_Governance_Beyond_Messages()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, helper) = await _factory.CreateAuthenticatedClientAsync(DelegateEmail);
        var albumId = await CreateAlbumAsync(owner, "Festa");
        var status = await EnablePartyAsync(owner, albumId);
        var membershipId = await GrantMessageDelegationAsync(owner, helper, albumId, DelegateEmail);

        // They can moderate messages…
        (await helper.GetAsync($"/api/albums/{albumId}/party-messages")).EnsureSuccessStatusCode();

        // …and nothing else. Party settings, the tokens behind them, revocation,
        // slideshow timing, photo/video moderation and the delegation itself all
        // stay with the owner.
        Assert.Equal(HttpStatusCode.NotFound,
            (await helper.GetAsync($"/api/albums/{albumId}/party-settings")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await helper.PatchAsJsonAsync($"/api/albums/{albumId}/party-settings",
                new { enabled = true, requireMessageApproval = true })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await helper.PatchAsJsonAsync($"/api/albums/{albumId}/party-settings",
                new { enabled = false })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await helper.PatchAsJsonAsync($"/api/albums/{albumId}/party-slideshow-settings",
                new { photoSlideSeconds = 20 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await helper.GetAsync($"/api/albums/{albumId}/party-uploads")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await helper.GetAsync($"/api/albums/{albumId}/members")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await helper.PatchAsJsonAsync($"/api/albums/{albumId}/members/{membershipId}",
                new { allowOriginalDownload = true, canManagePartyMessages = true })).StatusCode);

        // The party is still exactly as the owner left it.
        var settings = await owner.GetFromJsonAsync<JsonElement>($"/api/albums/{albumId}/party-settings");
        Assert.True(settings.GetProperty("partyMode").GetBoolean());
        Assert.False(settings.GetProperty("requireMessageApproval").GetBoolean());
        Assert.Equal(
            UploadTokenFromStatus(status),
            UploadTokenFromStatus(settings));
    }

    [Fact]
    public async Task Revoking_The_Capability_Or_The_Membership_Takes_Effect_On_The_Next_Request()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, helper) = await _factory.CreateAuthenticatedClientAsync(DelegateEmail);
        var albumId = await CreateAlbumAsync(owner, "Festa");
        var status = await EnablePartyAsync(owner, albumId);
        var membershipId = await GrantMessageDelegationAsync(owner, helper, albumId, DelegateEmail);
        var messageId = (await SubmitMessageAsync(UploadTokenFromStatus(status), null, "Ciao"))
            .GetProperty("id").GetGuid();

        (await helper.GetAsync($"/api/albums/{albumId}/party-messages")).EnsureSuccessStatusCode();

        // Clearing just the capability, leaving the share intact.
        (await owner.PatchAsJsonAsync($"/api/albums/{albumId}/members/{membershipId}",
            new { allowOriginalDownload = false, canManagePartyMessages = false }))
            .EnsureSuccessStatusCode();

        // No cache to wait out: the very next call is refused.
        Assert.Equal(HttpStatusCode.NotFound,
            (await helper.GetAsync($"/api/albums/{albumId}/party-messages")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, await PostAsync(helper, albumId, messageId, "hide"));

        // Granting again, then revoking the whole membership instead.
        (await owner.PatchAsJsonAsync($"/api/albums/{albumId}/members/{membershipId}",
            new { allowOriginalDownload = false, canManagePartyMessages = true }))
            .EnsureSuccessStatusCode();
        (await helper.GetAsync($"/api/albums/{albumId}/party-messages")).EnsureSuccessStatusCode();

        (await owner.DeleteAsync($"/api/albums/{albumId}/members/{membershipId}"))
            .EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound,
            (await helper.GetAsync($"/api/albums/{albumId}/party-messages")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, await PostAsync(helper, albumId, messageId, "hide"));
    }

    [Fact]
    public async Task An_Unaccepted_Invitation_Does_Not_Grant_The_Delegation_Yet()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, helper) = await _factory.CreateAuthenticatedClientAsync(DelegateEmail);
        var albumId = await CreateAlbumAsync(owner, "Festa");
        await EnablePartyAsync(owner, albumId);

        var membershipId = await InviteAsync(owner, albumId, DelegateEmail);
        (await owner.PatchAsJsonAsync($"/api/albums/{albumId}/members/{membershipId}",
            new { allowOriginalDownload = false, canManagePartyMessages = true }))
            .EnsureSuccessStatusCode();

        // Granted, but the share is still pending: authority begins when the
        // person accepts, exactly as it does for viewing the album.
        Assert.Equal(HttpStatusCode.NotFound,
            (await helper.GetAsync($"/api/albums/{albumId}/party-messages")).StatusCode);
    }

    [Fact]
    public async Task Only_The_Owner_Can_Grant_The_Delegation()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, helper) = await _factory.CreateAuthenticatedClientAsync(DelegateEmail);
        var (_, stranger) = await _factory.CreateAuthenticatedClientAsync("stranger@example.com");
        var albumId = await CreateAlbumAsync(owner, "Festa");
        var membershipId = await GrantMessageDelegationAsync(owner, helper, albumId, DelegateEmail);

        // Neither a stranger nor the delegate themselves can hand it out.
        foreach (var client in new[] { stranger, helper })
        {
            Assert.Equal(HttpStatusCode.NotFound,
                (await client.PatchAsJsonAsync($"/api/albums/{albumId}/members/{membershipId}",
                    new { allowOriginalDownload = true, canManagePartyMessages = true })).StatusCode);
        }

        var members = await owner.GetFromJsonAsync<JsonElement>($"/api/albums/{albumId}/members");
        Assert.True(members[0].GetProperty("canManagePartyMessages").GetBoolean());
        Assert.False(members[0].GetProperty("allowOriginalDownload").GetBoolean());
    }

    [Fact]
    public async Task Updating_A_Member_Without_The_Field_Leaves_The_Delegation_Alone()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, helper) = await _factory.CreateAuthenticatedClientAsync(DelegateEmail);
        var albumId = await CreateAlbumAsync(owner, "Festa");
        await EnablePartyAsync(owner, albumId);
        var membershipId = await GrantMessageDelegationAsync(owner, helper, albumId, DelegateEmail);

        // An older client that only knows about downloads must not silently
        // clear a delegation it has never heard of.
        var updated = await (await owner.PatchAsJsonAsync(
            $"/api/albums/{albumId}/members/{membershipId}", new { allowOriginalDownload = true }))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(updated.GetProperty("allowOriginalDownload").GetBoolean());
        Assert.True(updated.GetProperty("canManagePartyMessages").GetBoolean());

        (await helper.GetAsync($"/api/albums/{albumId}/party-messages")).EnsureSuccessStatusCode();
    }

    // ── Validation ──────────────────────────────────────────────────────────

    [Fact]
    public async Task A_Body_Of_120_Is_Accepted_And_121_Is_Refused()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var albumId = await CreateAlbumAsync(owner, "Festa");
        var uploadToken = UploadTokenFromStatus(await EnablePartyAsync(owner, albumId));

        var ok = await SubmitRawAsync(uploadToken, new { text = new string('a', 120) });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        var tooLong = await SubmitRawAsync(uploadToken, new { text = new string('a', 121) });
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        var error = await tooLong.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_text", error.GetProperty("error").GetString());
        Assert.Equal(PartyMessageLimits.MaxBodyLength, error.GetProperty("maxTextLength").GetInt32());
    }

    [Theory]
    [InlineData("")]
    [InlineData("    ")]
    [InlineData("\n\t\r\n")]
    public async Task Empty_And_Whitespace_Only_Bodies_Are_Refused(string text)
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var albumId = await CreateAlbumAsync(owner, "Festa");
        var uploadToken = UploadTokenFromStatus(await EnablePartyAsync(owner, albumId));

        var response = await SubmitRawAsync(uploadToken, new { text });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Missing_Body_And_Missing_Text_Are_Both_Refused()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var albumId = await CreateAlbumAsync(owner, "Festa");
        var uploadToken = UploadTokenFromStatus(await EnablePartyAsync(owner, albumId));

        Assert.Equal(HttpStatusCode.BadRequest,
            (await SubmitRawAsync(uploadToken, new { displayName = "Ada" })).StatusCode);
    }

    [Fact]
    public async Task Emoji_Are_Counted_By_Code_Point_On_The_Wire_Too()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var albumId = await CreateAlbumAsync(owner, "Festa");
        var uploadToken = UploadTokenFromStatus(await EnablePartyAsync(owner, albumId));
        var tv = await PairTvAsync(owner);

        // 120 astral emoji: 120 code points, 240 UTF-16 units. Accepted, and
        // stored intact — which is also what proves the column is wide enough.
        var full = string.Concat(Enumerable.Repeat("🎉", 120));
        Assert.Equal(HttpStatusCode.OK, (await SubmitRawAsync(uploadToken, new { text = full })).StatusCode);
        Assert.Equal(full, (await TvMessagesAsync(tv, albumId))[0].GetProperty("text").GetString());

        Assert.Equal(HttpStatusCode.BadRequest,
            (await SubmitRawAsync(uploadToken, new { text = full + "🎉" })).StatusCode);
    }

    [Fact]
    public async Task A_Name_Over_40_Is_Refused_Rather_Than_Truncated()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var albumId = await CreateAlbumAsync(owner, "Festa");
        var uploadToken = UploadTokenFromStatus(await EnablePartyAsync(owner, albumId));

        Assert.Equal(HttpStatusCode.OK,
            (await SubmitRawAsync(uploadToken, new { displayName = new string('n', 40), text = "Ciao" })).StatusCode);

        var response = await SubmitRawAsync(uploadToken, new { displayName = new string('n', 41), text = "Ciao" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_display_name",
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Text_Is_Stored_And_Served_As_Plain_Text_Never_Interpreted()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var albumId = await CreateAlbumAsync(owner, "Festa");
        var uploadToken = UploadTokenFromStatus(await EnablePartyAsync(owner, albumId));
        var tv = await PairTvAsync(owner);

        // The server neither strips nor executes markup: it stores the characters
        // the guest typed. Not rendering them as HTML is the client's contract,
        // and escaping here would corrupt a message that legitimately contains
        // an angle bracket.
        const string markup = "<b>ciao</b> & <script>alert(1)</script>";
        Assert.Equal(HttpStatusCode.OK, (await SubmitRawAsync(uploadToken, new { text = markup })).StatusCode);
        Assert.Equal(markup, (await TvMessagesAsync(tv, albumId))[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task Newlines_Collapse_So_The_Ribbon_Never_Receives_A_Paragraph()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var albumId = await CreateAlbumAsync(owner, "Festa");
        var uploadToken = UploadTokenFromStatus(await EnablePartyAsync(owner, albumId));
        var tv = await PairTvAsync(owner);

        await SubmitRawAsync(uploadToken, new { text = "  riga uno\r\n\r\nriga due  " });
        Assert.Equal("riga uno riga due", (await TvMessagesAsync(tv, albumId))[0].GetProperty("text").GetString());
    }

    // ── Capability scope ────────────────────────────────────────────────────

    [Fact]
    public async Task A_Revoked_Party_Refuses_New_Messages_And_Empties_The_Tv_Feed()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var albumId = await CreateAlbumAsync(owner, "Festa");
        var status = await EnablePartyAsync(owner, albumId);
        var uploadToken = UploadTokenFromStatus(status);
        var tv = await PairTvAsync(owner);

        await SubmitMessageAsync(uploadToken, "Ada", "Ciao");
        Assert.Equal(1, (await TvMessagesAsync(tv, albumId)).GetArrayLength());

        (await owner.PatchAsJsonAsync($"/api/albums/{albumId}/party-settings", new { enabled = false }))
            .EnsureSuccessStatusCode();

        // The token is dead, and the feed is empty — without a single message
        // row having been rewritten.
        Assert.Equal(HttpStatusCode.NotFound, (await SubmitRawAsync(uploadToken, new { text = "Late" })).StatusCode);
        Assert.Equal(0, (await TvMessagesAsync(tv, albumId)).GetArrayLength());
    }

    [Fact]
    public async Task Disabling_Guest_Upload_Also_Closes_The_Message_Form()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var albumId = await CreateAlbumAsync(owner, "Festa");
        var uploadToken = UploadTokenFromStatus(await EnablePartyAsync(owner, albumId));

        (await owner.PatchAsJsonAsync($"/api/albums/{albumId}/party-settings",
            new { enabled = true, uploadEnabled = false })).EnsureSuccessStatusCode();

        // Writing is contributing. The upload token authorizes both, so the one
        // switch closes both — a host who has stopped taking contributions has
        // not accidentally left a text channel open.
        Assert.Equal(HttpStatusCode.NotFound,
            (await SubmitRawAsync(uploadToken, new { text = "Ciao" })).StatusCode);
    }

    [Fact]
    public async Task The_View_Token_Cannot_Post_A_Message()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var albumId = await CreateAlbumAsync(owner, "Festa");
        var status = await EnablePartyAsync(owner, albumId);

        // A view token hashes to a different column and can never satisfy the
        // upload capability, here or anywhere else.
        Assert.Equal(HttpStatusCode.NotFound,
            (await SubmitRawAsync(ViewTokenFromStatus(status), new { text = "Ciao" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await SubmitRawAsync("not-a-token", new { text = "Ciao" })).StatusCode);
    }

    [Fact]
    public async Task Turning_The_Album_Off_Tv_Removes_The_Message_Feed()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var albumId = await CreateAlbumAsync(owner, "Festa");
        var status = await EnablePartyAsync(owner, albumId);
        var tv = await PairTvAsync(owner);
        await SubmitMessageAsync(UploadTokenFromStatus(status), "Ada", "Ciao");

        (await owner.PatchAsJsonAsync($"/api/albums/{albumId}/tv-settings", new { showOnTv = false }))
            .EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.NotFound,
            (await TvGetAsync(tv, $"/api/tv/albums/{albumId}/party-messages")).StatusCode);
    }

    [Fact]
    public async Task A_New_Party_On_The_Same_Album_Does_Not_Resurrect_The_Previous_Events_Messages()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var albumId = await CreateAlbumAsync(owner, "Festa");
        var first = await EnablePartyAsync(owner, albumId);
        var tv = await PairTvAsync(owner);

        var lastYear = (await SubmitMessageAsync(UploadTokenFromStatus(first), "Ada", "Auguri 2025"))
            .GetProperty("id").GetGuid();
        await PostAsync(owner, albumId, lastYear, "promote-hero");
        Assert.Equal(1, (await TvMessagesAsync(tv, albumId)).GetArrayLength());

        // Disable, then enable again: a NEW link, new tokens, a new event.
        (await owner.PatchAsJsonAsync($"/api/albums/{albumId}/party-settings", new { enabled = false }))
            .EnsureSuccessStatusCode();
        var second = await EnablePartyAsync(owner, albumId);
        Assert.NotEqual(UploadTokenFromStatus(first), UploadTokenFromStatus(second));

        // This year's party starts silent — including the Hero card, and
        // including the host's own moderation queue.
        Assert.Equal(0, (await TvMessagesAsync(tv, albumId)).GetArrayLength());
        Assert.Equal(0, (await ListMessagesAsync(owner, albumId)).GetProperty("items").GetArrayLength());

        // And last year's message is unreachable through this year's routes,
        // rather than merely hidden from the listing.
        Assert.Equal(HttpStatusCode.NotFound, await PostAsync(owner, albumId, lastYear, "hide"));

        await SubmitMessageAsync(UploadTokenFromStatus(second), "Marco", "Auguri 2026");
        var now = await TvMessagesAsync(tv, albumId);
        Assert.Equal(1, now.GetArrayLength());
        Assert.Equal("Auguri 2026", now[0].GetProperty("text").GetString());
    }

    // ── Ownership / probing ─────────────────────────────────────────────────

    [Fact]
    public async Task A_Message_Id_From_Another_Album_Is_Not_Reachable_Through_A_Foreign_Route()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var mine = await CreateAlbumAsync(owner, "Mia");
        var theirs = await CreateAlbumAsync(owner, "Altra");
        var mineStatus = await EnablePartyAsync(owner, mine);
        await EnablePartyAsync(owner, theirs);

        var messageId = (await SubmitMessageAsync(UploadTokenFromStatus(mineStatus), null, "Ciao"))
            .GetProperty("id").GetGuid();

        // Same owner, both albums legitimately theirs — and still not found,
        // because the route's scope is the OTHER album's party.
        Assert.Equal(HttpStatusCode.NotFound, await PostAsync(owner, theirs, messageId, "hide"));
        Assert.Equal(HttpStatusCode.NotFound, await PostAsync(owner, theirs, messageId, "promote-hero"));
        Assert.Equal(HttpStatusCode.NotFound, await PostAsync(owner, mine, Guid.NewGuid(), "hide"));
    }

    [Fact]
    public async Task Another_Owners_Album_Is_A_Generic_404_Not_A_Forbidden()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, other) = await _factory.CreateAuthenticatedClientAsync("other@example.com");
        var albumId = await CreateAlbumAsync(owner, "Festa");
        var status = await EnablePartyAsync(owner, albumId);
        var messageId = (await SubmitMessageAsync(UploadTokenFromStatus(status), null, "Ciao"))
            .GetProperty("id").GetGuid();

        // 404 rather than 403: a stranger must not be able to tell an album they
        // may not manage from one that does not exist.
        Assert.Equal(HttpStatusCode.NotFound,
            (await other.GetAsync($"/api/albums/{albumId}/party-messages")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, await PostAsync(other, albumId, messageId, "hide"));
        Assert.Equal(HttpStatusCode.NotFound,
            (await other.GetAsync($"/api/albums/{Guid.NewGuid()}/party-messages")).StatusCode);
    }

    [Fact]
    public async Task Anonymous_Callers_Cannot_Reach_Any_Management_Surface()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var albumId = await CreateAlbumAsync(owner, "Festa");
        var status = await EnablePartyAsync(owner, albumId);
        var messageId = (await SubmitMessageAsync(UploadTokenFromStatus(status), null, "Ciao"))
            .GetProperty("id").GetGuid();
        var anon = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anon.GetAsync($"/api/albums/{albumId}/party-messages")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anon.PostAsync($"/api/albums/{albumId}/party-messages/{messageId}/hide", null)).StatusCode);

        // And an unpaired television gets nothing.
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anon.GetAsync($"/api/tv/albums/{albumId}/party-messages")).StatusCode);
    }

    // ── Leak boundary ───────────────────────────────────────────────────────

    [Fact]
    public async Task Neither_Projection_Exposes_Internals()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var albumId = await CreateAlbumAsync(owner, "Festa");
        var status = await EnablePartyAsync(owner, albumId);
        var tv = await PairTvAsync(owner);
        var messageId = (await SubmitMessageAsync(UploadTokenFromStatus(status), "Ada", "Ciao"))
            .GetProperty("id").GetGuid();
        await PostAsync(owner, albumId, messageId, "promote-hero");

        var ownerRaw = await (await owner.GetAsync($"/api/albums/{albumId}/party-messages"))
            .Content.ReadAsStringAsync();
        var tvRaw = await (await TvGetAsync(tv, $"/api/tv/albums/{albumId}/party-messages"))
            .Content.ReadAsStringAsync();

        foreach (var raw in new[] { ownerRaw, tvRaw })
        {
            foreach (var needle in new[]
            {
                "ownerUserId", "moderatedByUserId", "heroPromotedByUserId",
                "partyParticipantId", "partyAlbumLinkId", "tokenHash", "uploadTokenHash",
                "StorageKey", "BlobObjectId", "sha256", "PayloadJson", "stack", "Exception",
            })
            {
                Assert.DoesNotContain(needle, raw, StringComparison.OrdinalIgnoreCase);
            }
        }

        // The TV feed additionally carries no moderation vocabulary at all: it
        // receives only what it may show.
        Assert.DoesNotContain("pending", tvRaw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rejected", tvRaw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_Tv_Feed_Carries_Only_Visible_Messages_Whatever_Else_Exists()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var albumId = await CreateAlbumAsync(owner, "Festa");
        var status = await EnablePartyAsync(owner, albumId, requireMessageApproval: true);
        var uploadToken = UploadTokenFromStatus(status);
        var tv = await PairTvAsync(owner);

        var live = (await SubmitMessageAsync(uploadToken, null, "Live")).GetProperty("id").GetGuid();
        var hidden = (await SubmitMessageAsync(uploadToken, null, "Hidden")).GetProperty("id").GetGuid();
        var rejected = (await SubmitMessageAsync(uploadToken, null, "Rejected")).GetProperty("id").GetGuid();
        await SubmitMessageAsync(uploadToken, null, "Pending");

        await PostAsync(owner, albumId, live, "approve");
        await PostAsync(owner, albumId, hidden, "approve");
        await PostAsync(owner, albumId, hidden, "hide");
        await PostAsync(owner, albumId, rejected, "reject");

        var messages = await TvMessagesAsync(tv, albumId);
        Assert.Equal(1, messages.GetArrayLength());
        Assert.Equal("Live", messages[0].GetProperty("text").GetString());

        // The host still sees all four.
        Assert.Equal(4, (await ListMessagesAsync(owner, albumId)).GetProperty("items").GetArrayLength());
    }

    // ── Compatibility ───────────────────────────────────────────────────────

    [Fact]
    public async Task Messages_Never_Enter_The_Media_Carousel_Or_Change_TvAlbumItem()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var albumId = await CreateAlbumAsync(owner, "Festa");
        var status = await EnablePartyAsync(owner, albumId);
        var viewToken = ViewTokenFromStatus(status);
        var tv = await PairTvAsync(owner);

        await SubmitMessageAsync(UploadTokenFromStatus(status), "Ada", "Ciao");

        // An older TV APK polls exactly these two routes and sees an album that
        // is as empty of media as it was before anybody wrote anything.
        var tvItems = await TvJsonAsync(tv, $"/api/tv/albums/{albumId}/items");
        Assert.Equal(0, tvItems.GetProperty("items").GetArrayLength());
        var publicItems = await _factory.CreateClient()
            .GetFromJsonAsync<JsonElement>($"/api/party/{viewToken}/items");
        Assert.Equal(0, publicItems.GetProperty("items").GetArrayLength());

        // And no message has produced a moderation row on the media side.
        Assert.Equal(0, (await owner.GetFromJsonAsync<JsonElement>($"/api/albums/{albumId}/party-uploads"))
            .GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Ordering_Is_Oldest_First_For_The_Tv_And_Newest_First_For_The_Host()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var albumId = await CreateAlbumAsync(owner, "Festa");
        var uploadToken = UploadTokenFromStatus(await EnablePartyAsync(owner, albumId));
        var tv = await PairTvAsync(owner);

        foreach (var text in new[] { "uno", "due", "tre" })
        {
            await SubmitMessageAsync(uploadToken, null, text);
        }

        // The ribbon reads them in the order they were written…
        var feed = await TvMessagesAsync(tv, albumId);
        Assert.Equal(["uno", "due", "tre"], feed.EnumerateArray().Select(m => m.GetProperty("text").GetString()));

        // …while the host's queue puts what just arrived at the top.
        var queue = (await ListMessagesAsync(owner, albumId)).GetProperty("items");
        Assert.Equal(["tre", "due", "uno"], queue.EnumerateArray().Select(m => m.GetProperty("text").GetString()));
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private async Task<Guid> CreateAlbumAsync(HttpClient owner, string name)
    {
        var response = await owner.PostAsJsonAsync("/api/albums", new { name });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> EnablePartyAsync(
        HttpClient owner, Guid albumId,
        bool? requireUploadApproval = null, bool? requireMessageApproval = null)
    {
        var payload = new Dictionary<string, object> { ["enabled"] = true };
        if (requireUploadApproval is bool upload)
        {
            payload["requireUploadApproval"] = upload;
        }
        if (requireMessageApproval is bool message)
        {
            payload["requireMessageApproval"] = message;
        }

        var response = await owner.PatchAsJsonAsync($"/api/albums/{albumId}/party-settings", payload);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private Task<HttpResponseMessage> SubmitRawAsync(string uploadToken, object payload)
        => _factory.CreateClient().PostAsJsonAsync($"/api/party/{uploadToken}/messages", payload);

    private async Task<JsonElement> SubmitMessageAsync(string uploadToken, string? displayName, string text)
    {
        var response = await SubmitRawAsync(uploadToken, new { displayName, text });
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement> ListMessagesAsync(HttpClient client, Guid albumId)
        => await client.GetFromJsonAsync<JsonElement>($"/api/albums/{albumId}/party-messages");

    private static async Task<HttpStatusCode> PostAsync(
        HttpClient client, Guid albumId, Guid messageId, string action)
        => (await client.PostAsync($"/api/albums/{albumId}/party-messages/{messageId}/{action}", null)).StatusCode;

    private static Task<HttpStatusCode> ApproveAsync(HttpClient client, Guid albumId, Guid messageId)
        => PostAsync(client, albumId, messageId, "approve");

    private async Task<JsonElement> TvMessagesAsync(string tvCookie, Guid albumId)
        => (await TvJsonAsync(tvCookie, $"/api/tv/albums/{albumId}/party-messages")).GetProperty("messages");

    private static DateTime HeroPromotedAt(JsonElement messages, string text)
        => messages.EnumerateArray()
            .Single(m => m.GetProperty("text").GetString() == text)
            .GetProperty("heroPromotedAt").GetDateTime();

    // Invite, accept, then grant the narrow capability. Returns the membership id.
    private static async Task<Guid> GrantMessageDelegationAsync(
        HttpClient owner, HttpClient member, Guid albumId, string email)
    {
        var membershipId = await InviteAndAcceptAsync(owner, member, albumId, email);
        (await owner.PatchAsJsonAsync($"/api/albums/{albumId}/members/{membershipId}",
            new { allowOriginalDownload = false, canManagePartyMessages = true }))
            .EnsureSuccessStatusCode();
        return membershipId;
    }

    private static async Task<Guid> InviteAndAcceptAsync(
        HttpClient owner, HttpClient member, Guid albumId, string email, string? role = null)
    {
        var membershipId = await InviteAsync(owner, albumId, email, role);
        (await member.PostAsync($"/api/shared-albums/invitations/{membershipId}/accept", null))
            .EnsureSuccessStatusCode();
        return membershipId;
    }

    private static async Task<Guid> InviteAsync(
        HttpClient owner, Guid albumId, string email, string? role = null)
    {
        object payload = role is null ? new { email } : new { email, role };
        var response = await owner.PostAsJsonAsync($"/api/albums/{albumId}/members", payload);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("membershipId").GetGuid();
    }

    private static string ViewTokenFromStatus(JsonElement status)
        => status.GetProperty("partyUrl").GetString()!["/party/".Length..];

    private static string UploadTokenFromStatus(JsonElement status)
    {
        var url = status.GetProperty("uploadUrl").GetString()!;
        var rest = url["/party/".Length..];
        return rest[..rest.IndexOf("/upload", StringComparison.Ordinal)];
    }

    private async Task<string> PairTvAsync(HttpClient owner)
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
                personalCode = "URDLSUDLR",
                personalCodeConfirmation = "URDLSUDLR",
            })).EnsureSuccessStatusCode();
        var pollRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/tv/pairing/{started.PublicCode}/status");
        pollRequest.Headers.Add(TvPairingService.PairingSecretHeader, started.PairingSecret);
        var poll = await tvClient.SendAsync(pollRequest);
        poll.EnsureSuccessStatusCode();
        return poll.Headers.GetValues("Set-Cookie").Single();
    }

    private async Task<JsonElement> TvJsonAsync(string setCookie, string url)
    {
        var response = await TvGetAsync(setCookie, url);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private Task<HttpResponseMessage> TvGetAsync(string setCookie, string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Cookie", $"{TvPairingService.CookieName}={CookieValue(setCookie)}");
        return _factory.CreateClient().SendAsync(request);
    }

    private static string CookieValue(string setCookie)
    {
        var value = setCookie.Split(';', 2)[0];
        return value[(value.IndexOf('=') + 1)..];
    }
}
