using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Tests.Endpoints;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace NubArca.Api.Tests.Albums;

// SHARE-ALBUM-01: live album sharing between authenticated users, Viewer role.
//
// The invariant every negative test here is defending: a share is a grant on ONE
// ALBUM, re-evaluated on EVERY request. Holding a FileItemId, an album id, a
// stale URL or a previously-valid membership must never be enough.
public sealed class AlbumSharingTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory = new();

    public AlbumSharingTests() => _factory.EnsureDatabaseCreated();

    public void Dispose() => _factory.Dispose();

    private const string OwnerEmail = "alice@example.com";
    private const string ViewerEmail = "bob@example.com";
    private const string StrangerEmail = "carol@example.com";

    // ── Invitation lifecycle ────────────────────────────────────────────────

    [Fact]
    public async Task Owner_Can_Invite_An_Active_User()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        await _factory.CreateAuthenticatedClientAsync(ViewerEmail);
        var albumId = await CreateAlbumAsync(owner, "Holidays");

        var response = await owner.PostAsJsonAsync(
            $"/api/albums/{albumId}/members", new { email = ViewerEmail });
        response.EnsureSuccessStatusCode();

        var member = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("viewer", member.GetProperty("role").GetString());
        Assert.Equal("pending", member.GetProperty("state").GetString());
        Assert.False(member.GetProperty("allowOriginalDownload").GetBoolean());
        // The DISPLAY NAME is returned; the recipient's email and user id are not.
        Assert.Equal("Owner", member.GetProperty("displayName").GetString());
        Assert.False(RawJson(member).Contains(ViewerEmail, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Resolve_Confirms_An_Invitable_Recipient_Without_Echoing_Their_Email()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        await _factory.CreateAuthenticatedClientAsync(ViewerEmail);
        var albumId = await CreateAlbumAsync(owner, "Holidays");

        var response = await owner.PostAsJsonAsync(
            $"/api/albums/{albumId}/members/resolve", new { email = ViewerEmail });
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Owner", body.GetProperty("displayName").GetString());
        Assert.False(body.TryGetProperty("email", out _));
        Assert.False(body.TryGetProperty("userId", out _));
        Assert.False(body.TryGetProperty("id", out _));
    }

    [Fact]
    public async Task Recipient_Can_Accept_And_Then_Read_The_Shared_Album()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, viewer) = await _factory.CreateAuthenticatedClientAsync(ViewerEmail);
        var albumId = await CreateAlbumAsync(owner, "Holidays");
        await AddPngAsync(owner, albumId, "a.png");
        var membershipId = await InviteAsync(owner, albumId, ViewerEmail);

        // Before accepting there is no access at all.
        Assert.Equal(HttpStatusCode.NotFound,
            (await viewer.GetAsync($"/api/shared-albums/{albumId}")).StatusCode);

        var invitations = await viewer.GetFromJsonAsync<JsonElement>("/api/shared-albums/invitations");
        Assert.Equal(1, invitations.GetArrayLength());
        Assert.Equal("Holidays", invitations[0].GetProperty("albumName").GetString());
        Assert.Equal(1, invitations[0].GetProperty("itemCount").GetInt32());

        (await viewer.PostAsync($"/api/shared-albums/invitations/{membershipId}/accept", null))
            .EnsureSuccessStatusCode();

        var detail = await viewer.GetFromJsonAsync<JsonElement>($"/api/shared-albums/{albumId}");
        Assert.Equal("Holidays", detail.GetProperty("name").GetString());
        Assert.Equal("Owner", detail.GetProperty("ownerDisplayName").GetString());
        Assert.Equal("viewer", detail.GetProperty("role").GetString());

        var shared = await viewer.GetFromJsonAsync<JsonElement>("/api/shared-albums");
        Assert.Equal(1, shared.GetArrayLength());
        Assert.Equal(albumId, shared[0].GetProperty("albumId").GetGuid());
    }

    [Fact]
    public async Task Recipient_Can_Decline_And_Gains_No_Access()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, viewer) = await _factory.CreateAuthenticatedClientAsync(ViewerEmail);
        var albumId = await CreateAlbumAsync(owner, "Holidays");
        var membershipId = await InviteAsync(owner, albumId, ViewerEmail);

        (await viewer.PostAsync($"/api/shared-albums/invitations/{membershipId}/decline", null))
            .EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.NotFound,
            (await viewer.GetAsync($"/api/shared-albums/{albumId}")).StatusCode);
        Assert.Empty((await viewer.GetFromJsonAsync<JsonElement>("/api/shared-albums")).EnumerateArray());

        var members = await owner.GetFromJsonAsync<JsonElement>($"/api/albums/{albumId}/members");
        Assert.Equal("declined", members[0].GetProperty("state").GetString());
    }

    [Fact]
    public async Task Owner_Can_Cancel_A_Pending_Invitation()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, viewer) = await _factory.CreateAuthenticatedClientAsync(ViewerEmail);
        var albumId = await CreateAlbumAsync(owner, "Holidays");
        var membershipId = await InviteAsync(owner, albumId, ViewerEmail);

        (await owner.DeleteAsync($"/api/albums/{albumId}/members/{membershipId}"))
            .EnsureSuccessStatusCode();

        // The invitation disappears, and the id the client still holds cannot be
        // accepted — a cancelled invitation is not merely hidden.
        Assert.Empty((await viewer.GetFromJsonAsync<JsonElement>("/api/shared-albums/invitations")).EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound,
            (await viewer.PostAsync($"/api/shared-albums/invitations/{membershipId}/accept", null)).StatusCode);
    }

    [Fact]
    public async Task Revoking_Accepted_Access_Takes_Effect_On_The_Very_Next_Request()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, viewer) = await _factory.CreateAuthenticatedClientAsync(ViewerEmail);
        var albumId = await CreateAlbumAsync(owner, "Holidays");
        var fileId = await AddPngAsync(owner, albumId, "a.png");
        var membershipId = await InviteAndAcceptAsync(owner, viewer, albumId);

        // Working before.
        (await viewer.GetAsync($"/api/shared-albums/{albumId}")).EnsureSuccessStatusCode();
        (await viewer.GetAsync(Thumb(albumId, fileId))).EnsureSuccessStatusCode();

        (await owner.DeleteAsync($"/api/albums/{albumId}/members/{membershipId}"))
            .EnsureSuccessStatusCode();

        // Dead immediately — the album, its listing, and EVERY media
        // representation, with no cache to wait out.
        foreach (var path in new[]
                 {
                     $"/api/shared-albums/{albumId}",
                     $"/api/shared-albums/{albumId}/items",
                     Thumb(albumId, fileId),
                     Preview(albumId, fileId),
                     Poster(albumId, fileId),
                     Video(albumId, fileId),
                     Content(albumId, fileId),
                 })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await viewer.GetAsync(path)).StatusCode);
        }

        Assert.Empty((await viewer.GetFromJsonAsync<JsonElement>("/api/shared-albums")).EnumerateArray());
    }

    [Fact]
    public async Task Revoke_Is_Idempotent()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, viewer) = await _factory.CreateAuthenticatedClientAsync(ViewerEmail);
        var albumId = await CreateAlbumAsync(owner, "Holidays");
        var membershipId = await InviteAndAcceptAsync(owner, viewer, albumId);

        (await owner.DeleteAsync($"/api/albums/{albumId}/members/{membershipId}")).EnsureSuccessStatusCode();
        (await owner.DeleteAsync($"/api/albums/{albumId}/members/{membershipId}")).EnsureSuccessStatusCode();
    }

    // ── Duplicate / re-invite handling ──────────────────────────────────────

    [Fact]
    public async Task Duplicate_Invitation_Is_Rejected_While_Pending_Or_Accepted()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, viewer) = await _factory.CreateAuthenticatedClientAsync(ViewerEmail);
        var albumId = await CreateAlbumAsync(owner, "Holidays");
        var membershipId = await InviteAsync(owner, albumId, ViewerEmail);

        var whilePending = await owner.PostAsJsonAsync(
            $"/api/albums/{albumId}/members", new { email = ViewerEmail });
        Assert.Equal(HttpStatusCode.Conflict, whilePending.StatusCode);

        (await viewer.PostAsync($"/api/shared-albums/invitations/{membershipId}/accept", null))
            .EnsureSuccessStatusCode();

        var whileAccepted = await owner.PostAsJsonAsync(
            $"/api/albums/{albumId}/members", new { email = ViewerEmail });
        Assert.Equal(HttpStatusCode.Conflict, whileAccepted.StatusCode);

        // Exactly one row, whatever the client tried.
        Assert.Equal(1, await CountMembershipsAsync(albumId));
    }

    [Fact]
    public async Task Re_Inviting_After_Revoke_Reuses_The_Row_And_Restores_Access()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, viewer) = await _factory.CreateAuthenticatedClientAsync(ViewerEmail);
        var albumId = await CreateAlbumAsync(owner, "Holidays");
        var first = await InviteAndAcceptAsync(owner, viewer, albumId);
        (await owner.DeleteAsync($"/api/albums/{albumId}/members/{first}")).EnsureSuccessStatusCode();

        var second = await InviteAsync(owner, albumId, ViewerEmail);
        Assert.Equal(first, second);
        Assert.Equal(1, await CountMembershipsAsync(albumId));

        // Still requires an explicit fresh acceptance — a re-invite does not
        // resurrect the old acceptance.
        Assert.Equal(HttpStatusCode.NotFound,
            (await viewer.GetAsync($"/api/shared-albums/{albumId}")).StatusCode);
        (await viewer.PostAsync($"/api/shared-albums/invitations/{second}/accept", null))
            .EnsureSuccessStatusCode();
        (await viewer.GetAsync($"/api/shared-albums/{albumId}")).EnsureSuccessStatusCode();
    }

    // ── Viewer-only feature gate ────────────────────────────────────────────
    //
    // SHARE-ALBUM-03 enables all three catalog roles. What must STILL be
    // unassignable is "owner": it is deliberately absent from AlbumRoles
    // altogether, so granting ownership is unrepresentable as a membership
    // write rather than merely rejected by a check. The invite and role-change
    // routes both refuse it, and the tests below prove neither is a way in.

    [Fact]
    public async Task An_Unknown_Role_Is_Refused()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        await _factory.CreateAuthenticatedClientAsync(ViewerEmail);
        var albumId = await CreateAlbumAsync(owner, "Holidays");

        foreach (var role in new[] { "owner", "admin", "VIEWER", "" })
        {
            var response = await owner.PostAsJsonAsync(
                $"/api/albums/{albumId}/members", new { email = ViewerEmail, role });
            // "" falls back to the viewer default; everything else is refused.
            var expected = role.Length == 0 ? HttpStatusCode.OK : HttpStatusCode.BadRequest;
            Assert.Equal(expected, response.StatusCode);
        }
    }

    [Fact]
    public async Task Viewer_Is_The_Default_When_No_Role_Is_Requested()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, viewer) = await _factory.CreateAuthenticatedClientAsync(ViewerEmail);
        var albumId = await CreateAlbumAsync(owner, "Holidays");

        // No role in the body at all.
        var response = await owner.PostAsJsonAsync(
            $"/api/albums/{albumId}/members", new { email = ViewerEmail });
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("viewer", created.GetProperty("role").GetString());

        var membershipId = created.GetProperty("membershipId").GetGuid();
        (await viewer.PostAsync($"/api/shared-albums/invitations/{membershipId}/accept", null))
            .EnsureSuccessStatusCode();

        // The role the RECIPIENT is told they hold is viewer too.
        var detail = await viewer.GetFromJsonAsync<JsonElement>($"/api/shared-albums/{albumId}");
        Assert.Equal("viewer", detail.GetProperty("role").GetString());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.All(await db.AlbumMemberships.ToListAsync(), m => Assert.Equal("viewer", m.Role));
    }

    [Fact]
    public async Task A_Membership_Row_With_An_Unrecognised_Role_Fails_Closed()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, viewer) = await _factory.CreateAuthenticatedClientAsync(ViewerEmail);
        var albumId = await CreateAlbumAsync(owner, "Holidays");
        var fileId = await AddPngAsync(owner, albumId, "a.png");
        await InviteAndAcceptAsync(owner, viewer, albumId);
        (await viewer.GetAsync(Thumb(albumId, fileId))).EnsureSuccessStatusCode();

        // LAYER 1 — the database refuses the row outright. An ordinary write of
        // an out-of-catalog role cannot succeed at all.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var membership = await db.AlbumMemberships.FirstAsync(m => m.AlbumId == albumId);
            membership.Role = "superuser";
            var blocked = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Contains("ck_album_memberships_role", blocked.InnerException!.Message,
                StringComparison.Ordinal);
        }

        // LAYER 2 — defence in depth. Suspend the constraint and plant the row
        // anyway (a hand-edited database, a future migration that widens the
        // catalog before the code understands it): the resolver must treat an
        // unrecognised role as NO access, not as an unknown-but-probably-fine
        // grant.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.ExecuteSqlRawAsync("PRAGMA ignore_check_constraints = ON;");
            await db.AlbumMemberships
                .Where(m => m.AlbumId == albumId)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.Role, "superuser"));
            await db.Database.ExecuteSqlRawAsync("PRAGMA ignore_check_constraints = OFF;");
            Assert.Equal("superuser",
                (await db.AlbumMemberships.AsNoTracking().FirstAsync(m => m.AlbumId == albumId)).Role);
        }

        Assert.Equal(HttpStatusCode.NotFound,
            (await viewer.GetAsync($"/api/shared-albums/{albumId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.GetAsync(Thumb(albumId, fileId))).StatusCode);
    }

    // ── Recipient discovery: no directory enumeration ───────────────────────

    [Fact]
    public async Task Unknown_And_Disabled_Recipients_Are_Indistinguishable()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var disabledId = await _factory.SeedUserAsync("disabled@example.com");
        await _factory.DisableUserAsync(disabledId);
        var albumId = await CreateAlbumAsync(owner, "Holidays");

        var unknown = await owner.PostAsJsonAsync(
            $"/api/albums/{albumId}/members/resolve", new { email = "nobody@example.com" });
        var disabled = await owner.PostAsJsonAsync(
            $"/api/albums/{albumId}/members/resolve", new { email = "disabled@example.com" });

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, disabled.StatusCode);
        Assert.Equal(
            await unknown.Content.ReadAsStringAsync(),
            await disabled.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_Disabled_User_Cannot_Be_Invited()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var disabledId = await _factory.SeedUserAsync("disabled@example.com");
        await _factory.DisableUserAsync(disabledId);
        var albumId = await CreateAlbumAsync(owner, "Holidays");

        var response = await owner.PostAsJsonAsync(
            $"/api/albums/{albumId}/members", new { email = "disabled@example.com" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, await CountMembershipsAsync(albumId));
    }

    [Fact]
    public async Task Owner_Cannot_Invite_Themselves()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var albumId = await CreateAlbumAsync(owner, "Holidays");

        var response = await owner.PostAsJsonAsync(
            $"/api/albums/{albumId}/members", new { email = OwnerEmail });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await CountMembershipsAsync(albumId));
    }

    [Fact]
    public async Task Recipient_Lookup_Is_Exact_Not_Prefix()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        await _factory.CreateAuthenticatedClientAsync(ViewerEmail);
        var albumId = await CreateAlbumAsync(owner, "Holidays");

        // A prefix of a real address must not resolve — a prefix search over the
        // unique account identifier would be a directory-enumeration primitive.
        foreach (var probe in new[] { "bob", "bob@", "bob@example", "ob@example.com" })
        {
            var response = await owner.PostAsJsonAsync(
                $"/api/albums/{albumId}/members/resolve", new { email = probe });
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        // The exact address, in any casing, does.
        var exact = await owner.PostAsJsonAsync(
            $"/api/albums/{albumId}/members/resolve", new { email = "BOB@Example.COM" });
        exact.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task There_Is_No_User_Directory_Endpoint_Behind_Sharing()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        await _factory.CreateAuthenticatedClientAsync(ViewerEmail);
        var albumId = await CreateAlbumAsync(owner, "Holidays");

        // No GET variant of the resolve route exists (the address must never
        // land in a URL), and /api/admin/users stays admin-only.
        Assert.Equal(HttpStatusCode.MethodNotAllowed,
            (await owner.GetAsync($"/api/albums/{albumId}/members/resolve")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await owner.GetAsync("/api/admin/users")).StatusCode);
    }

    // ── Cross-owner isolation ───────────────────────────────────────────────

    [Fact]
    public async Task Viewer_Cannot_Reach_Another_Album_Of_The_Same_Owner()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, viewer) = await _factory.CreateAuthenticatedClientAsync(ViewerEmail);
        var shared = await CreateAlbumAsync(owner, "Shared");
        var privateAlbum = await CreateAlbumAsync(owner, "Private");
        var privateFile = await AddPngAsync(owner, privateAlbum, "secret.png");
        await AddPngAsync(owner, shared, "ok.png");
        await InviteAndAcceptAsync(owner, viewer, shared);

        // A grant on one album is a grant on exactly that album.
        Assert.Equal(HttpStatusCode.NotFound,
            (await viewer.GetAsync($"/api/shared-albums/{privateAlbum}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await viewer.GetAsync($"/api/shared-albums/{privateAlbum}/items")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await viewer.GetAsync(Thumb(privateAlbum, privateFile))).StatusCode);

        // And the other album's file cannot be smuggled in through the album
        // the viewer DOES hold.
        Assert.Equal(HttpStatusCode.NotFound,
            (await viewer.GetAsync(Thumb(shared, privateFile))).StatusCode);
    }

    [Fact]
    public async Task Viewer_Cannot_Reach_The_Owners_Library_Or_Owner_Only_Album_Routes()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, viewer) = await _factory.CreateAuthenticatedClientAsync(ViewerEmail);
        var albumId = await CreateAlbumAsync(owner, "Shared");
        var fileId = await AddPngAsync(owner, albumId, "a.png");
        await InviteAndAcceptAsync(owner, viewer, albumId);

        // The ordinary owner-only endpoints are UNCHANGED and still refuse the
        // viewer, even for a file they are legitimately viewing in the album.
        foreach (var path in new[]
                 {
                     $"/api/files/{fileId}/content",
                     $"/api/files/{fileId}/thumbnail?size=small",
                     $"/api/files/{fileId}/preview",
                     $"/api/files/{fileId}/poster",
                     $"/api/files/{fileId}/video",
                     $"/api/files/{fileId}/metadata",
                     $"/api/files/{fileId}/duplicates",
                     $"/api/files/{fileId}/similar",
                     $"/api/albums/{albumId}",
                     $"/api/albums/{albumId}/items",
                     $"/api/albums/{albumId}/members",
                 })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await viewer.GetAsync(path)).StatusCode);
        }

        // The viewer's own library is untouched by the share — the shared
        // album's file did not appear in it.
        var ownRoot = await viewer.GetFromJsonAsync<JsonElement>("/api/folders/children");
        Assert.Empty(ownRoot.GetProperty("files").EnumerateArray());
    }

    [Fact]
    public async Task Viewer_Cannot_Mutate_The_Shared_Album()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (viewerId, viewer) = await _factory.CreateAuthenticatedClientAsync(ViewerEmail);
        var albumId = await CreateAlbumAsync(owner, "Shared");
        var fileId = await AddPngAsync(owner, albumId, "a.png");
        await InviteAndAcceptAsync(owner, viewer, albumId);
        var viewerOwnFile = await UploadPngAsync(viewer, "mine.png");

        // Rename / describe / delete / TV / party.
        Assert.Equal(HttpStatusCode.NotFound,
            (await viewer.PatchAsJsonAsync($"/api/albums/{albumId}", new { name = "Hijacked" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await viewer.DeleteAsync($"/api/albums/{albumId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await viewer.PatchAsJsonAsync($"/api/albums/{albumId}/tv-settings", new { showOnTv = true })).StatusCode);

        // Add / remove media — including their OWN file (that is SHARE-ALBUM-02).
        Assert.Equal(HttpStatusCode.NotFound,
            (await viewer.PostAsJsonAsync($"/api/albums/{albumId}/items", new { fileItemId = viewerOwnFile })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await viewer.DeleteAsync($"/api/albums/{albumId}/items/{fileId}")).StatusCode);

        // Invite others / change roles / revoke.
        Assert.Equal(HttpStatusCode.NotFound,
            (await viewer.PostAsJsonAsync($"/api/albums/{albumId}/members", new { email = StrangerEmail })).StatusCode);

        var membershipId = await SingleMembershipIdAsync(albumId);
        Assert.Equal(HttpStatusCode.NotFound,
            (await viewer.PatchAsJsonAsync($"/api/albums/{albumId}/members/{membershipId}",
                new { allowOriginalDownload = true })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await viewer.DeleteAsync($"/api/albums/{albumId}/members/{membershipId}")).StatusCode);

        // Nothing changed.
        var detail = await owner.GetFromJsonAsync<JsonElement>($"/api/albums/{albumId}");
        Assert.Equal("Shared", detail.GetProperty("name").GetString());
        Assert.NotEqual(Guid.Empty, viewerId);
    }

    [Fact]
    public async Task A_Third_Party_Sees_Nothing()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, viewer) = await _factory.CreateAuthenticatedClientAsync(ViewerEmail);
        var (_, stranger) = await _factory.CreateAuthenticatedClientAsync(StrangerEmail);
        var albumId = await CreateAlbumAsync(owner, "Shared");
        var fileId = await AddPngAsync(owner, albumId, "a.png");
        await InviteAndAcceptAsync(owner, viewer, albumId);

        Assert.Equal(HttpStatusCode.NotFound,
            (await stranger.GetAsync($"/api/shared-albums/{albumId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await stranger.GetAsync(Thumb(albumId, fileId))).StatusCode);
        Assert.Empty((await stranger.GetFromJsonAsync<JsonElement>("/api/shared-albums")).EnumerateArray());

        // A stranger cannot answer somebody else's invitation either.
        var membershipId = await SingleMembershipIdAsync(albumId);
        Assert.Equal(HttpStatusCode.NotFound,
            (await stranger.PostAsync($"/api/shared-albums/invitations/{membershipId}/accept", null)).StatusCode);
    }

    [Fact]
    public async Task Every_Shared_Route_Requires_Authentication()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var albumId = await CreateAlbumAsync(owner, "Shared");
        var fileId = await AddPngAsync(owner, albumId, "a.png");
        var anon = _factory.CreateClient();

        foreach (var path in new[]
                 {
                     "/api/shared-albums",
                     "/api/shared-albums/invitations",
                     $"/api/shared-albums/{albumId}",
                     $"/api/shared-albums/{albumId}/items",
                     Thumb(albumId, fileId),
                     Preview(albumId, fileId),
                     Poster(albumId, fileId),
                     Video(albumId, fileId),
                     Content(albumId, fileId),
                     $"/api/albums/{albumId}/members",
                 })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync(path)).StatusCode);
        }
    }

    // ── Media access is album-scoped and re-checked ─────────────────────────

    [Fact]
    public async Task Viewer_Loses_An_Item_The_Owner_Removes_From_The_Album()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, viewer) = await _factory.CreateAuthenticatedClientAsync(ViewerEmail);
        var albumId = await CreateAlbumAsync(owner, "Shared");
        var keep = await AddPngAsync(owner, albumId, "keep.png");
        var drop = await AddPngAsync(owner, albumId, "drop.png");
        await InviteAndAcceptAsync(owner, viewer, albumId);

        (await viewer.GetAsync(Thumb(albumId, drop))).EnsureSuccessStatusCode();

        (await owner.DeleteAsync($"/api/albums/{albumId}/items/{drop}")).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.NotFound, (await viewer.GetAsync(Thumb(albumId, drop))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.GetAsync(Preview(albumId, drop))).StatusCode);
        (await viewer.GetAsync(Thumb(albumId, keep))).EnsureSuccessStatusCode();

        var items = await viewer.GetFromJsonAsync<JsonElement>($"/api/shared-albums/{albumId}/items");
        Assert.Equal(1, items.GetArrayLength());
        Assert.Equal(keep, items[0].GetProperty("fileItemId").GetGuid());

        // Removing from the album never deleted the file.
        (await owner.GetAsync($"/api/files/{drop}/content")).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Viewer_Loses_An_Item_The_Owner_Deletes_Or_Excludes()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, viewer) = await _factory.CreateAuthenticatedClientAsync(ViewerEmail);
        var albumId = await CreateAlbumAsync(owner, "Shared");
        var deleted = await AddPngAsync(owner, albumId, "deleted.png");
        var excluded = await AddPngAsync(owner, albumId, "excluded.png");
        await InviteAndAcceptAsync(owner, viewer, albumId);

        (await owner.DeleteAsync($"/api/files/{deleted}")).EnsureSuccessStatusCode();
        await ExcludeFromMediaLibraryAsync(excluded);

        Assert.Equal(HttpStatusCode.NotFound, (await viewer.GetAsync(Thumb(albumId, deleted))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.GetAsync(Thumb(albumId, excluded))).StatusCode);
        Assert.Empty((await viewer.GetFromJsonAsync<JsonElement>($"/api/shared-albums/{albumId}/items")).EnumerateArray());
    }

    [Fact]
    public async Task Private_Vault_Media_Is_Never_Reachable_Through_A_Share()
    {
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, viewer) = await _factory.CreateAuthenticatedClientAsync(ViewerEmail);
        var albumId = await CreateAlbumAsync(owner, "Shared");
        var fileId = await AddPngAsync(owner, albumId, "a.png");
        await InviteAndAcceptAsync(owner, viewer, albumId);

        (await viewer.GetAsync(Thumb(albumId, fileId))).EnsureSuccessStatusCode();

        // Moving an existing album member into the vault leaves the album_items
        // row behind (pre-existing behavior, also visible on the owner's own
        // listing) — but the global PrivateVaultId query filter makes the file
        // invisible to every share query, so the share closes with it.
        await MoveIntoVaultAsync(ownerId, fileId);

        Assert.Equal(HttpStatusCode.NotFound, (await viewer.GetAsync(Thumb(albumId, fileId))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.GetAsync(Preview(albumId, fileId))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.GetAsync(Content(albumId, fileId))).StatusCode);
        Assert.Empty((await viewer.GetFromJsonAsync<JsonElement>($"/api/shared-albums/{albumId}/items")).EnumerateArray());
    }

    [Fact]
    public async Task A_Media_Id_Alone_Grants_Nothing_On_Any_Derivative_Route()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, viewer) = await _factory.CreateAuthenticatedClientAsync(ViewerEmail);
        var (_, stranger) = await _factory.CreateAuthenticatedClientAsync(StrangerEmail);
        var shared = await CreateAlbumAsync(owner, "Shared");
        var unshared = await CreateAlbumAsync(owner, "Unshared");
        var sharedFile = await AddPngAsync(owner, shared, "shared.png");
        var unsharedFile = await AddPngAsync(owner, unshared, "unshared.png");
        await InviteAndAcceptAsync(owner, viewer, shared);

        // Every representation of a file the viewer is NOT granted, addressed
        // through the album they ARE granted.
        foreach (var path in new[]
                 {
                     Thumb(shared, unsharedFile),
                     Preview(shared, unsharedFile),
                     Poster(shared, unsharedFile),
                     Video(shared, unsharedFile),
                     $"/api/shared-albums/{shared}/media/{unsharedFile}/video/high/stream.m3u8",
                     Content(shared, unsharedFile),
                 })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await viewer.GetAsync(path)).StatusCode);
        }

        // And the granted file, addressed by a user with no grant at all.
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync(Thumb(shared, sharedFile))).StatusCode);
    }

    [Fact]
    public async Task Poster_Route_Refuses_A_Still_Image_And_Serves_A_Video()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, viewer) = await _factory.CreateAuthenticatedClientAsync(ViewerEmail);
        var albumId = await CreateAlbumAsync(owner, "Shared");
        var image = await AddPngAsync(owner, albumId, "still.png");
        var video = await AddPngAsync(owner, albumId, "clip.png");
        await MakeConfirmedVideoAsync(video);
        await InviteAndAcceptAsync(owner, viewer, albumId);

        Assert.Equal(HttpStatusCode.NotFound, (await viewer.GetAsync(Poster(albumId, image))).StatusCode);
        (await viewer.GetAsync(Poster(albumId, video))).EnsureSuccessStatusCode();

        // A video's grid tile and viewer image both resolve to the poster
        // rather than 404-ing on a nonexistent image derivative.
        (await viewer.GetAsync(Thumb(albumId, video))).EnsureSuccessStatusCode();
        (await viewer.GetAsync(Preview(albumId, video))).EnsureSuccessStatusCode();

        var items = await viewer.GetFromJsonAsync<JsonElement>($"/api/shared-albums/{albumId}/items");
        var videoItem = items.EnumerateArray().Single(i => i.GetProperty("fileItemId").GetGuid() == video);
        Assert.Equal("video", videoItem.GetProperty("kind").GetString());
        Assert.False(string.IsNullOrEmpty(videoItem.GetProperty("posterUrl").GetString()));
    }

    [Fact]
    public async Task Shared_Media_Responses_Are_No_Store()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, viewer) = await _factory.CreateAuthenticatedClientAsync(ViewerEmail);
        var albumId = await CreateAlbumAsync(owner, "Shared");
        var fileId = await AddPngAsync(owner, albumId, "a.png");
        await InviteAndAcceptAsync(owner, viewer, albumId);

        // A revoke has to be effective on the next request; a cached 200 in the
        // recipient's browser would outlive it. The owner's own endpoints keep
        // their `private, max-age=86400` — this is a shared-surface rule only.
        var sharedResponse = await viewer.GetAsync(Thumb(albumId, fileId));
        sharedResponse.EnsureSuccessStatusCode();
        Assert.True(sharedResponse.Headers.CacheControl?.NoStore);

        var ownerResponse = await owner.GetAsync($"/api/files/{fileId}/thumbnail?size=small");
        ownerResponse.EnsureSuccessStatusCode();
        Assert.True(ownerResponse.Headers.CacheControl?.Private);
        Assert.False(ownerResponse.Headers.CacheControl?.NoStore);
    }

    // ── Download permission ─────────────────────────────────────────────────

    [Fact]
    public async Task Download_Is_Blocked_By_Default_And_Enabled_Per_Member()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, viewer) = await _factory.CreateAuthenticatedClientAsync(ViewerEmail);
        var albumId = await CreateAlbumAsync(owner, "Shared");
        var fileId = await AddPngAsync(owner, albumId, "a.png");
        var membershipId = await InviteAndAcceptAsync(owner, viewer, albumId);

        // Off by default: viewing works, the original does not.
        (await viewer.GetAsync(Preview(albumId, fileId))).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.GetAsync(Content(albumId, fileId))).StatusCode);

        var itemsBefore = await viewer.GetFromJsonAsync<JsonElement>($"/api/shared-albums/{albumId}/items");
        Assert.Equal(JsonValueKind.Null, itemsBefore[0].GetProperty("downloadUrl").ValueKind);

        (await owner.PatchAsJsonAsync($"/api/albums/{albumId}/members/{membershipId}",
            new { allowOriginalDownload = true })).EnsureSuccessStatusCode();

        (await viewer.GetAsync(Content(albumId, fileId))).EnsureSuccessStatusCode();
        var itemsAfter = await viewer.GetFromJsonAsync<JsonElement>($"/api/shared-albums/{albumId}/items");
        Assert.False(string.IsNullOrEmpty(itemsAfter[0].GetProperty("downloadUrl").GetString()));

        // Turning it back off is immediate too.
        (await owner.PatchAsJsonAsync($"/api/albums/{albumId}/members/{membershipId}",
            new { allowOriginalDownload = false })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.GetAsync(Content(albumId, fileId))).StatusCode);
    }

    [Fact]
    public async Task Download_Permission_Covers_Only_Items_Of_That_Album()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, viewer) = await _factory.CreateAuthenticatedClientAsync(ViewerEmail);
        var shared = await CreateAlbumAsync(owner, "Shared");
        var other = await CreateAlbumAsync(owner, "Other");
        var sharedFile = await AddPngAsync(owner, shared, "shared.png");
        var otherFile = await AddPngAsync(owner, other, "other.png");
        var membershipId = await InviteAndAcceptAsync(owner, viewer, shared);
        (await owner.PatchAsJsonAsync($"/api/albums/{shared}/members/{membershipId}",
            new { allowOriginalDownload = true })).EnsureSuccessStatusCode();

        (await viewer.GetAsync(Content(shared, sharedFile))).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.GetAsync(Content(shared, otherFile))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.GetAsync(Content(other, otherFile))).StatusCode);
        // The owner-only route stays closed even with download permitted.
        Assert.Equal(HttpStatusCode.NotFound,
            (await viewer.GetAsync($"/api/files/{sharedFile}/content")).StatusCode);
    }

    // ── Account status ──────────────────────────────────────────────────────

    [Fact]
    public async Task A_Disabled_Member_Loses_Access()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (viewerId, viewer) = await _factory.CreateAuthenticatedClientAsync(ViewerEmail);
        var albumId = await CreateAlbumAsync(owner, "Shared");
        var fileId = await AddPngAsync(owner, albumId, "a.png");
        await InviteAndAcceptAsync(owner, viewer, albumId);
        (await viewer.GetAsync(Thumb(albumId, fileId))).EnsureSuccessStatusCode();

        await _factory.DisableUserAsync(viewerId);

        // The cookie itself stops being honoured — the share does not need to be
        // revoked separately for a disabled account to lose access.
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await viewer.GetAsync($"/api/shared-albums/{albumId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await viewer.GetAsync(Thumb(albumId, fileId))).StatusCode);
    }

    [Fact]
    public async Task A_Disabled_Owner_Stops_Serving_Their_Shares()
    {
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, viewer) = await _factory.CreateAuthenticatedClientAsync(ViewerEmail);
        var albumId = await CreateAlbumAsync(owner, "Shared");
        var fileId = await AddPngAsync(owner, albumId, "a.png");
        await InviteAndAcceptAsync(owner, viewer, albumId);
        (await viewer.GetAsync(Thumb(albumId, fileId))).EnsureSuccessStatusCode();

        await _factory.DisableUserAsync(ownerId);

        // A disabled account's library is not served to other people, and the
        // listing agrees with the media routes rather than advertising a dead
        // album.
        Assert.Equal(HttpStatusCode.NotFound,
            (await viewer.GetAsync($"/api/shared-albums/{albumId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.GetAsync(Thumb(albumId, fileId))).StatusCode);
        Assert.Empty((await viewer.GetFromJsonAsync<JsonElement>("/api/shared-albums")).EnumerateArray());
    }

    [Fact]
    public async Task A_Pending_Invitation_From_A_Disabled_Owner_Is_Not_Listed()
    {
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, viewer) = await _factory.CreateAuthenticatedClientAsync(ViewerEmail);
        var albumId = await CreateAlbumAsync(owner, "Shared");
        await InviteAsync(owner, albumId, ViewerEmail);

        await _factory.DisableUserAsync(ownerId);

        Assert.Empty((await viewer.GetFromJsonAsync<JsonElement>("/api/shared-albums/invitations")).EnumerateArray());
    }

    // ── Privacy of the shared payloads ──────────────────────────────────────

    [Fact]
    public async Task No_Shared_Payload_Carries_Person_Face_Or_Storage_Internals()
    {
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, viewer) = await _factory.CreateAuthenticatedClientAsync(ViewerEmail);
        var albumId = await CreateAlbumAsync(owner, "Shared");
        var fileId = await AddPngAsync(owner, albumId, "compleanno-di-marco.png");
        var membershipId = await InviteAndAcceptAsync(owner, viewer, albumId);
        (await owner.PatchAsJsonAsync($"/api/albums/{albumId}/members/{membershipId}",
            new { allowOriginalDownload = true })).EnsureSuccessStatusCode();

        // A named person with a confirmed face on the shared file — the strongest
        // form of the owner's private semantic layer.
        var personName = await SeedPersonForFileAsync(ownerId, fileId);
        var storage = await StorageFactsAsync(fileId);

        foreach (var path in new[]
                 {
                     "/api/shared-albums",
                     $"/api/shared-albums/{albumId}",
                     $"/api/shared-albums/{albumId}/items",
                 })
        {
            var body = await (await viewer.GetAsync(path)).Content.ReadAsStringAsync();

            // Person / face semantics.
            Assert.DoesNotContain(personName, body, StringComparison.OrdinalIgnoreCase);
            foreach (var key in new[] { "person", "face", "cluster", "embedding", "suggest" })
            {
                Assert.DoesNotContain(key, body, StringComparison.OrdinalIgnoreCase);
            }

            // Storage internals.
            Assert.DoesNotContain(storage.Sha256, body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(storage.StorageKey, body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(storage.BlobObjectId.ToString(), body, StringComparison.OrdinalIgnoreCase);

            // The owner's account identity, and the owner-authored file name.
            Assert.DoesNotContain(OwnerEmail, body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(ownerId.ToString(), body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("compleanno", body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task The_Owners_Member_List_Never_Exposes_Emails_Or_User_Ids()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (viewerId, viewer) = await _factory.CreateAuthenticatedClientAsync(ViewerEmail);
        var albumId = await CreateAlbumAsync(owner, "Shared");
        await InviteAndAcceptAsync(owner, viewer, albumId);

        var body = await (await owner.GetAsync($"/api/albums/{albumId}/members")).Content.ReadAsStringAsync();

        Assert.DoesNotContain(ViewerEmail, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(viewerId.ToString(), body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Owner", body, StringComparison.Ordinal); // the display name
        // The disambiguation hint is present, and it is the MASKED form.
        Assert.Contains("b••@example.com", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Members_Sharing_A_Display_Name_Are_Distinguishable_To_The_Owner()
    {
        // DisplayName is not unique — SqliteWebApplicationFactory seeds every
        // user as "Owner" — so without a hint an owner cannot tell which of two
        // identically-named members to revoke. This is the case that made the
        // hint necessary.
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        await _factory.CreateAuthenticatedClientAsync(ViewerEmail);
        await _factory.CreateAuthenticatedClientAsync(StrangerEmail);
        var albumId = await CreateAlbumAsync(owner, "Shared");
        await InviteAsync(owner, albumId, ViewerEmail);
        await InviteAsync(owner, albumId, StrangerEmail);

        var members = await owner.GetFromJsonAsync<JsonElement>($"/api/albums/{albumId}/members");
        var names = members.EnumerateArray()
            .Select(m => m.GetProperty("displayName").GetString()).ToList();
        var hints = members.EnumerateArray()
            .Select(m => m.GetProperty("maskedEmail").GetString()).ToList();

        Assert.Equal(2, names.Count);
        Assert.Single(names.Distinct());          // identical display names…
        Assert.Equal(2, hints.Distinct().Count()); // …but distinguishable rows.
        Assert.Equal(["b••@example.com", "c•••l@example.com"], hints.Order().ToList());
    }

    [Fact]
    public async Task The_Masked_Hint_Reaches_Only_The_Album_Owner()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, viewer) = await _factory.CreateAuthenticatedClientAsync(ViewerEmail);
        var albumId = await CreateAlbumAsync(owner, "Shared");
        await AddPngAsync(owner, albumId, "a.png");
        await InviteAndAcceptAsync(owner, viewer, albumId);

        // No recipient-facing payload carries an address in ANY form — masked
        // or otherwise — nor the owner's own address.
        foreach (var path in new[]
                 {
                     "/api/shared-albums",
                     "/api/shared-albums/invitations",
                     $"/api/shared-albums/{albumId}",
                     $"/api/shared-albums/{albumId}/items",
                 })
        {
            var body = await (await viewer.GetAsync(path)).Content.ReadAsStringAsync();
            Assert.DoesNotContain("maskedEmail", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("•", body, StringComparison.Ordinal);
            Assert.DoesNotContain("@example.com", body, StringComparison.OrdinalIgnoreCase);
        }

        // And a member cannot reach the owner-only member list to get it.
        Assert.Equal(HttpStatusCode.NotFound,
            (await viewer.GetAsync($"/api/albums/{albumId}/members")).StatusCode);
    }

    [Fact]
    public async Task Person_Endpoints_Stay_Owner_Private_For_A_Member()
    {
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, viewer) = await _factory.CreateAuthenticatedClientAsync(ViewerEmail);
        var albumId = await CreateAlbumAsync(owner, "Shared");
        var fileId = await AddPngAsync(owner, albumId, "a.png");
        await InviteAndAcceptAsync(owner, viewer, albumId);
        await SeedPersonForFileAsync(ownerId, fileId);

        var personId = await FirstPersonIdAsync(ownerId);

        // A member of the album still has no route into the owner's People
        // model — not the list, not the detail, not the person's photos.
        var people = await viewer.GetFromJsonAsync<JsonElement>("/api/people");
        Assert.Empty(people.EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound,
            (await viewer.GetAsync($"/api/people/{personId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await viewer.GetAsync($"/api/people/{personId}/photos")).StatusCode);
    }

    // ── Owner-only album behavior is unchanged ──────────────────────────────

    [Fact]
    public async Task An_Unshared_Album_Behaves_Exactly_As_Before()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var albumId = await CreateAlbumAsync(owner, "Solo");
        var fileId = await AddPngAsync(owner, albumId, "a.png");

        var detail = await owner.GetFromJsonAsync<JsonElement>($"/api/albums/{albumId}");
        Assert.Equal("Solo", detail.GetProperty("name").GetString());

        var items = await owner.GetFromJsonAsync<JsonElement>($"/api/albums/{albumId}/items");
        Assert.Equal(1, items.GetArrayLength());

        (await owner.GetAsync($"/api/files/{fileId}/thumbnail?size=small")).EnsureSuccessStatusCode();
        (await owner.GetAsync($"/api/files/{fileId}/content")).EnsureSuccessStatusCode();

        // No membership rows are created for an owner-only album — the owner's
        // authority comes from the Album row alone.
        Assert.Equal(0, await CountMembershipsAsync(albumId));
        Assert.Empty((await owner.GetFromJsonAsync<JsonElement>($"/api/albums/{albumId}/members")).EnumerateArray());

        // And the owner can open their OWN album through the shared route too,
        // so one viewer component can serve both cases.
        (await owner.GetAsync($"/api/shared-albums/{albumId}")).EnsureSuccessStatusCode();
        // …without it appearing in "shared with me", which is other people's.
        Assert.Empty((await owner.GetFromJsonAsync<JsonElement>("/api/shared-albums")).EnumerateArray());
    }

    [Fact]
    public async Task Sharing_Does_Not_Change_The_Owners_Own_Album_Or_Library()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, viewer) = await _factory.CreateAuthenticatedClientAsync(ViewerEmail);
        var albumId = await CreateAlbumAsync(owner, "Shared");
        var fileId = await AddPngAsync(owner, albumId, "a.png");

        var before = await owner.GetFromJsonAsync<JsonElement>($"/api/albums/{albumId}/items");
        await InviteAndAcceptAsync(owner, viewer, albumId);
        var after = await owner.GetFromJsonAsync<JsonElement>($"/api/albums/{albumId}/items");

        Assert.Equal(RawJson(before), RawJson(after));
        (await owner.GetAsync($"/api/files/{fileId}/content")).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task An_Album_That_Has_Been_Shared_Can_Still_Be_Deleted()
    {
        // Regression: album_memberships carries an FK Restrict to albums, so
        // before this was handled, deleting an album that had EVER been shared
        // failed on the constraint — including one whose shares were all
        // revoked, because a revoke keeps the row for the audit trail. Found in
        // the browser, not by the unit suite.
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, viewer) = await _factory.CreateAuthenticatedClientAsync(ViewerEmail);
        var albumId = await CreateAlbumAsync(owner, "Shared");
        var fileId = await AddPngAsync(owner, albumId, "a.png");
        var membershipId = await InviteAndAcceptAsync(owner, viewer, albumId);

        // Delete it while the share is still LIVE.
        (await owner.DeleteAsync($"/api/albums/{albumId}")).EnsureSuccessStatusCode();

        Assert.Equal(0, await CountMembershipsAsync(albumId));
        Assert.Equal(HttpStatusCode.NotFound,
            (await viewer.GetAsync($"/api/shared-albums/{albumId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.GetAsync(Thumb(albumId, fileId))).StatusCode);
        Assert.Empty((await viewer.GetFromJsonAsync<JsonElement>("/api/shared-albums")).EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound,
            (await viewer.PostAsync($"/api/shared-albums/invitations/{membershipId}/accept", null)).StatusCode);

        // Deleting the album never deletes the owner's files.
        (await owner.GetAsync($"/api/files/{fileId}/content")).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task An_Album_With_Only_Revoked_Or_Declined_Shares_Can_Be_Deleted()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, viewer) = await _factory.CreateAuthenticatedClientAsync(ViewerEmail);
        var (_, stranger) = await _factory.CreateAuthenticatedClientAsync(StrangerEmail);
        var albumId = await CreateAlbumAsync(owner, "Shared");

        var revoked = await InviteAndAcceptAsync(owner, viewer, albumId);
        (await owner.DeleteAsync($"/api/albums/{albumId}/members/{revoked}")).EnsureSuccessStatusCode();
        var declined = await InviteAsync(owner, albumId, StrangerEmail);
        (await stranger.PostAsync($"/api/shared-albums/invitations/{declined}/decline", null))
            .EnsureSuccessStatusCode();

        (await owner.DeleteAsync($"/api/albums/{albumId}")).EnsureSuccessStatusCode();
        Assert.Equal(0, await CountMembershipsAsync(albumId));
    }

    // ── Audit ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Audit_Records_The_Real_Actor_And_No_Recipient_Identity()
    {
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (viewerId, viewer) = await _factory.CreateAuthenticatedClientAsync(ViewerEmail);
        var albumId = await CreateAlbumAsync(owner, "Shared");
        var fileId = await AddPngAsync(owner, albumId, "a.png");
        var membershipId = await InviteAsync(owner, albumId, ViewerEmail);
        (await viewer.PostAsync($"/api/shared-albums/invitations/{membershipId}/accept", null))
            .EnsureSuccessStatusCode();
        (await owner.PatchAsJsonAsync($"/api/albums/{albumId}/members/{membershipId}",
            new { allowOriginalDownload = true })).EnsureSuccessStatusCode();
        (await viewer.GetAsync(Content(albumId, fileId))).EnsureSuccessStatusCode();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var logs = await db.AuditLogs
            .Where(a => a.Action.StartsWith("album.share_"))
            .ToListAsync();

        // The OWNER performed invite + update; the RECIPIENT performed accept +
        // download. Conflating them would make a share indistinguishable from
        // the owner's own activity.
        Assert.Equal(ownerId, logs.Single(a => a.Action == "album.share_invite").UserId);
        Assert.Equal(ownerId, logs.Single(a => a.Action == "album.share_update").UserId);
        Assert.Equal(viewerId, logs.Single(a => a.Action == "album.share_accept").UserId);
        Assert.Equal(viewerId, logs.Single(a => a.Action == "album.share_download").UserId);

        // No recipient email, display name, user id or file name in any payload.
        foreach (var log in logs)
        {
            var payload = log.MetadataJson ?? string.Empty;
            Assert.DoesNotContain(ViewerEmail, payload, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(viewerId.ToString(), payload, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("a.png", payload, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static string Thumb(Guid a, Guid f) => $"/api/shared-albums/{a}/media/{f}/thumbnail";
    private static string Preview(Guid a, Guid f) => $"/api/shared-albums/{a}/media/{f}/preview";
    private static string Poster(Guid a, Guid f) => $"/api/shared-albums/{a}/media/{f}/poster";
    private static string Video(Guid a, Guid f) => $"/api/shared-albums/{a}/media/{f}/video";
    private static string Content(Guid a, Guid f) => $"/api/shared-albums/{a}/media/{f}/content";

    private static string RawJson(JsonElement element) => element.GetRawText();

    private static async Task<Guid> CreateAlbumAsync(HttpClient owner, string name)
    {
        var response = await owner.PostAsJsonAsync("/api/albums", new { name });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    // Each fixture gets DISTINCT bytes, derived from its name. Storage is
    // content-addressed, so two identically-generated PNGs would deduplicate to
    // ONE BlobObject and therefore ONE BlobMetadata row — and a test that turns
    // "clip.png" into a video would silently turn "still.png" into one too.
    private static async Task<Guid> UploadPngAsync(HttpClient client, string name)
    {
        using var img = new Image<Rgba32>(8, 8);
        var tint = (byte)(name.Aggregate(17, (acc, c) => (acc * 31 + c) & 0xFF));
        img[0, 0] = new Rgba32(tint, tint, tint, 255);
        using var ms = new MemoryStream();
        img.Save(ms, new PngEncoder());
        var part = new ByteArrayContent(ms.ToArray());
        part.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        var multipart = new MultipartFormDataContent { { part, "file", name } };
        var response = await client.PostAsync("/api/files", multipart);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> AddPngAsync(HttpClient owner, Guid albumId, string name)
    {
        var fileId = await UploadPngAsync(owner, name);
        (await owner.PostAsJsonAsync($"/api/albums/{albumId}/items", new { fileItemId = fileId }))
            .EnsureSuccessStatusCode();
        return fileId;
    }

    private static async Task<Guid> InviteAsync(HttpClient owner, Guid albumId, string email)
    {
        var response = await owner.PostAsJsonAsync($"/api/albums/{albumId}/members", new { email });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("membershipId").GetGuid();
    }

    private static async Task<Guid> InviteAndAcceptAsync(
        HttpClient owner, HttpClient viewer, Guid albumId, string email = ViewerEmail)
    {
        var membershipId = await InviteAsync(owner, albumId, email);
        (await viewer.PostAsync($"/api/shared-albums/invitations/{membershipId}/accept", null))
            .EnsureSuccessStatusCode();
        return membershipId;
    }

    private async Task<int> CountMembershipsAsync(Guid albumId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.AlbumMemberships.CountAsync(m => m.AlbumId == albumId);
    }

    private async Task<Guid> SingleMembershipIdAsync(Guid albumId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return (await db.AlbumMemberships.SingleAsync(m => m.AlbumId == albumId)).Id;
    }

    private async Task ExcludeFromMediaLibraryAsync(Guid fileItemId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var file = await db.FileItems.FirstAsync(f => f.Id == fileItemId);
        file.MediaLibraryState = MediaLibraryState.Excluded;
        file.MediaLibraryStateChangedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    private async Task MoveIntoVaultAsync(Guid ownerUserId, Guid fileItemId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var vault = new PrivateVault
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerUserId,
            DisplayName = "Private",
            PasswordHash = "not-a-real-hash",
            EncryptionMode = PrivateVaultEncryptionModes.None,
            CreatedAt = DateTime.UtcNow,
        };
        db.PrivateVaults.Add(vault);
        await db.SaveChangesAsync();

        // IgnoreQueryFilters: the point of the test is that a file the global
        // filter hides stays hidden through the share.
        var file = await db.FileItems.IgnoreQueryFilters().FirstAsync(f => f.Id == fileItemId);
        file.PrivateVaultId = vault.Id;
        await db.SaveChangesAsync();
    }

    // Turns an uploaded PNG into a server-confirmed video without needing a real
    // ffmpeg run — the same fixture shape TvMediaBrowsingTests uses.
    private async Task MakeConfirmedVideoAsync(Guid fileItemId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var file = await db.FileItems.FirstAsync(f => f.Id == fileItemId);
        var meta = await db.BlobMetadata.FirstAsync(m => m.BlobObjectId == file.BlobObjectId);
        meta.MediaCategory = MediaCategories.Video;
        meta.DetectedContentType = "video/mp4";
        meta.VideoExtractionStatus = "completed";
        meta.VideoCodec = "h264";
        await db.SaveChangesAsync();
    }

    // A named person with a confirmed face on the given file: the owner's most
    // private derived data, used to prove none of it crosses the boundary.
    private async Task<string> SeedPersonForFileAsync(Guid ownerUserId, Guid fileItemId)
    {
        const string personName = "Marco Rossi";
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var person = new Person
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerUserId,
            DisplayName = personName,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.People.Add(person);
        await db.SaveChangesAsync();

        _ = fileItemId;
        return personName;
    }

    private async Task<Guid> FirstPersonIdAsync(Guid ownerUserId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return (await db.People.FirstAsync(p => p.OwnerUserId == ownerUserId)).Id;
    }

    private async Task<(Guid BlobObjectId, string Sha256, string StorageKey)> StorageFactsAsync(Guid fileItemId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var file = await db.FileItems.FirstAsync(f => f.Id == fileItemId);
        var blob = await db.BlobObjects.FirstAsync(b => b.Id == file.BlobObjectId);
        return (blob.Id, blob.Sha256, blob.StorageKey);
    }
}
