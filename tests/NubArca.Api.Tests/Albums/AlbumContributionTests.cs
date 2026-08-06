using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Tests.Endpoints;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace NubArca.Api.Tests.Albums;

// SHARE-ALBUM-02: the Contributor role and linked, revocable contributions.
//
// The invariant under test throughout: a contribution is a REFERENCE to media
// its contributor still owns. Nothing is copied, ownership never moves, the
// contributor can always take it back, and the album owner can remove it from
// the album but can never destroy it.
public sealed class AlbumContributionTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory = new();

    public AlbumContributionTests() => _factory.EnsureDatabaseCreated();

    public void Dispose() => _factory.Dispose();

    private const string OwnerEmail = "alice@example.com";
    private const string ContributorEmail = "bob@example.com";
    private const string OtherEmail = "carol@example.com";

    // ── Role assignment ─────────────────────────────────────────────────────

    [Fact]
    public async Task Owner_Can_Invite_Directly_As_Contributor()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, bob) = await _factory.CreateAuthenticatedClientAsync(ContributorEmail);
        var albumId = await CreateAlbumAsync(owner, "Trip");

        var response = await owner.PostAsJsonAsync($"/api/albums/{albumId}/members",
            new { email = ContributorEmail, role = "contributor" });
        response.EnsureSuccessStatusCode();
        var member = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("contributor", member.GetProperty("role").GetString());

        await AcceptAsync(bob, member.GetProperty("membershipId").GetGuid());
        var detail = await bob.GetFromJsonAsync<JsonElement>($"/api/shared-albums/{albumId}");
        Assert.Equal("contributor", detail.GetProperty("role").GetString());
    }

    [Fact]
    public async Task Editor_Is_Assignable_On_Invite_And_On_Role_Change()
    {
        // SHARE-ALBUM-03 enables the third role. Kept here, next to the
        // Contributor cases, so the whole assignable set is asserted in one
        // place and a future slice cannot quietly widen it unnoticed.
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, bob) = await _factory.CreateAuthenticatedClientAsync(ContributorEmail);
        var albumId = await CreateAlbumAsync(owner, "Trip");

        var invited = await owner.PostAsJsonAsync($"/api/albums/{albumId}/members",
            new { email = ContributorEmail, role = "editor" });
        invited.EnsureSuccessStatusCode();
        var member = await invited.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("editor", member.GetProperty("role").GetString());

        var membershipId = member.GetProperty("membershipId").GetGuid();
        await AcceptAsync(bob, membershipId);

        // …and the role-change route reaches it too.
        (await owner.PatchAsJsonAsync($"/api/albums/{albumId}/members/{membershipId}/role",
            new { role = "viewer" })).EnsureSuccessStatusCode();
        (await owner.PatchAsJsonAsync($"/api/albums/{albumId}/members/{membershipId}/role",
            new { role = "editor" })).EnsureSuccessStatusCode();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal("editor", (await db.AlbumMemberships.FirstAsync(m => m.Id == membershipId)).Role);
    }

    [Fact]
    public async Task Owner_Can_Promote_Viewer_To_Contributor_And_Demote_Back()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, bob) = await _factory.CreateAuthenticatedClientAsync(ContributorEmail);
        var albumId = await CreateAlbumAsync(owner, "Trip");
        var membershipId = await InviteAsync(owner, albumId, ContributorEmail, "viewer");
        await AcceptAsync(bob, membershipId);
        var bobFile = await UploadPngAsync(bob, "bob-1.png");

        // As a Viewer: cannot contribute.
        Assert.Equal(HttpStatusCode.Forbidden, (await Contribute(bob, albumId, bobFile)).StatusCode);

        // Promote → can contribute, without revoke-and-reinvite.
        (await owner.PatchAsJsonAsync($"/api/albums/{albumId}/members/{membershipId}/role",
            new { role = "contributor" })).EnsureSuccessStatusCode();
        (await Contribute(bob, albumId, bobFile)).EnsureSuccessStatusCode();

        // Demote → cannot add anything NEW…
        (await owner.PatchAsJsonAsync($"/api/albums/{albumId}/members/{membershipId}/role",
            new { role = "viewer" })).EnsureSuccessStatusCode();
        var second = await UploadPngAsync(bob, "bob-2.png");
        Assert.Equal(HttpStatusCode.Forbidden, (await Contribute(bob, albumId, second)).StatusCode);

        // …but the existing contribution STAYS, and stays viewable.
        Assert.Equal(1, await CountAlbumItemsAsync(albumId));
        (await bob.GetAsync(Thumb(albumId, bobFile))).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Only_The_Album_Owner_Can_Change_Roles()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, bob) = await _factory.CreateAuthenticatedClientAsync(ContributorEmail);
        var (_, carol) = await _factory.CreateAuthenticatedClientAsync(OtherEmail);
        var albumId = await CreateAlbumAsync(owner, "Trip");
        var membershipId = await InviteAsync(owner, albumId, ContributorEmail, "contributor");
        await AcceptAsync(bob, membershipId);

        // Not the member themselves — no self-promotion — and not a stranger.
        foreach (var client in new[] { bob, carol })
        {
            Assert.Equal(HttpStatusCode.NotFound,
                (await client.PatchAsJsonAsync($"/api/albums/{albumId}/members/{membershipId}/role",
                    new { role = "contributor" })).StatusCode);
        }
    }

    // ── Contributing ────────────────────────────────────────────────────────

    [Fact]
    public async Task Contributor_Adds_Own_Media_And_Everyone_In_The_Album_Sees_It()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, bob) = await _factory.CreateAuthenticatedClientAsync(ContributorEmail);
        var (_, carol) = await _factory.CreateAuthenticatedClientAsync(OtherEmail);
        var albumId = await CreateAlbumAsync(owner, "Trip");
        var ownerFile = await AddOwnPngAsync(owner, albumId, "owner.png");
        await InviteAcceptAsync(owner, bob, albumId, ContributorEmail, "contributor");
        await InviteAcceptAsync(owner, carol, albumId, OtherEmail, "viewer");

        var bobFile = await UploadPngAsync(bob, "bob.png");
        (await Contribute(bob, albumId, bobFile)).EnsureSuccessStatusCode();

        // The contributor, a plain viewer, and the owner all see both items.
        foreach (var client in new[] { bob, carol })
        {
            var items = await client.GetFromJsonAsync<JsonElement>($"/api/shared-albums/{albumId}/items");
            Assert.Equal(2, items.GetArrayLength());
            (await client.GetAsync(Thumb(albumId, bobFile))).EnsureSuccessStatusCode();
            (await client.GetAsync(Thumb(albumId, ownerFile))).EnsureSuccessStatusCode();
        }

        // No copy was made: still one FileItem, still Bob's.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var file = await db.FileItems.SingleAsync(f => f.Id == bobFile);
        Assert.Equal(await UserIdAsync(ContributorEmail), file.OwnerUserId);
        var item = await db.AlbumItems.SingleAsync(ai => ai.FileItemId == bobFile);
        // The invariant the resolver verifies.
        Assert.Equal(file.OwnerUserId, item.AddedByUserId);
    }

    [Fact]
    public async Task A_Contribution_Costs_Neither_Party_Extra_Quota()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, bob) = await _factory.CreateAuthenticatedClientAsync(ContributorEmail);
        var albumId = await CreateAlbumAsync(owner, "Trip");
        await InviteAcceptAsync(owner, bob, albumId, ContributorEmail, "contributor");
        var bobFile = await UploadPngAsync(bob, "bob.png");

        var ownerBefore = await UsedBytesAsync(owner);
        var bobBefore = await UsedBytesAsync(bob);

        (await Contribute(bob, albumId, bobFile)).EnsureSuccessStatusCode();

        // Linking creates no FileItem, so quota is untouched on both sides.
        Assert.Equal(ownerBefore, await UsedBytesAsync(owner));
        Assert.Equal(bobBefore, await UsedBytesAsync(bob));
    }

    [Fact]
    public async Task A_Contributor_Cannot_Add_Media_They_Do_Not_Own()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, bob) = await _factory.CreateAuthenticatedClientAsync(ContributorEmail);
        var (_, carol) = await _factory.CreateAuthenticatedClientAsync(OtherEmail);
        var albumId = await CreateAlbumAsync(owner, "Trip");
        await InviteAcceptAsync(owner, bob, albumId, ContributorEmail, "contributor");

        var carolFile = await UploadPngAsync(carol, "carol.png");
        var ownerFile = await UploadPngAsync(owner, "alice.png");

        // Neither a third party's file nor the album owner's own file.
        Assert.Equal(HttpStatusCode.NotFound, (await Contribute(bob, albumId, carolFile)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Contribute(bob, albumId, ownerFile)).StatusCode);
        Assert.Equal(0, await CountAlbumItemsAsync(albumId));
    }

    [Fact]
    public async Task The_Album_Owner_Cannot_Link_A_Collaborators_File()
    {
        // The owner-side add path validates file ownership, so an owner cannot
        // reach into a collaborator's library — it must be the file's owner who
        // contributes. This is what keeps AddedByUserId == FileItem.OwnerUserId
        // true for every row.
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, bob) = await _factory.CreateAuthenticatedClientAsync(ContributorEmail);
        var albumId = await CreateAlbumAsync(owner, "Trip");
        await InviteAcceptAsync(owner, bob, albumId, ContributorEmail, "contributor");
        var bobFile = await UploadPngAsync(bob, "bob.png");

        Assert.Equal(HttpStatusCode.NotFound,
            (await owner.PostAsJsonAsync($"/api/albums/{albumId}/items",
                new { fileItemId = bobFile })).StatusCode);
        var bulk = await owner.PostAsJsonAsync($"/api/albums/{albumId}/items/bulk",
            new { fileItemIds = new[] { bobFile } });
        bulk.EnsureSuccessStatusCode();
        Assert.Equal(0, (await bulk.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("succeeded").GetInt32());
        Assert.Equal(0, await CountAlbumItemsAsync(albumId));

        // And the owner cannot use the CONTRIBUTION route either.
        Assert.Equal(HttpStatusCode.Forbidden, (await Contribute(owner, albumId, bobFile)).StatusCode);
    }

    [Fact]
    public async Task A_Viewer_And_A_Stranger_Cannot_Contribute()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, bob) = await _factory.CreateAuthenticatedClientAsync(ContributorEmail);
        var (_, carol) = await _factory.CreateAuthenticatedClientAsync(OtherEmail);
        var albumId = await CreateAlbumAsync(owner, "Trip");
        await InviteAcceptAsync(owner, bob, albumId, ContributorEmail, "viewer");

        Assert.Equal(HttpStatusCode.Forbidden,
            (await Contribute(bob, albumId, await UploadPngAsync(bob, "bob.png"))).StatusCode);
        // A non-member gets 404 — not 403 — so the album's existence stays hidden.
        Assert.Equal(HttpStatusCode.NotFound,
            (await Contribute(carol, albumId, await UploadPngAsync(carol, "carol.png"))).StatusCode);
        Assert.Equal(0, await CountAlbumItemsAsync(albumId));
    }

    [Fact]
    public async Task Vaulted_Deleted_And_Excluded_Media_Cannot_Be_Contributed()
    {
        var (bobId, bob) = await _factory.CreateAuthenticatedClientAsync(ContributorEmail);
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var albumId = await CreateAlbumAsync(owner, "Trip");
        await InviteAcceptAsync(owner, bob, albumId, ContributorEmail, "contributor");

        var vaulted = await UploadPngAsync(bob, "vault.png");
        var deleted = await UploadPngAsync(bob, "deleted.png");
        var excluded = await UploadPngAsync(bob, "excluded.png");
        await MoveIntoVaultAsync(bobId, vaulted);
        (await bob.DeleteAsync($"/api/files/{deleted}")).EnsureSuccessStatusCode();
        await ExcludeFromMediaLibraryAsync(excluded);

        foreach (var fileId in new[] { vaulted, deleted, excluded })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await Contribute(bob, albumId, fileId)).StatusCode);
        }
        Assert.Equal(0, await CountAlbumItemsAsync(albumId));
    }

    [Fact]
    public async Task Contributing_The_Same_File_Twice_Is_A_Conflict()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, bob) = await _factory.CreateAuthenticatedClientAsync(ContributorEmail);
        var albumId = await CreateAlbumAsync(owner, "Trip");
        await InviteAcceptAsync(owner, bob, albumId, ContributorEmail, "contributor");
        var bobFile = await UploadPngAsync(bob, "bob.png");

        (await Contribute(bob, albumId, bobFile)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await Contribute(bob, albumId, bobFile)).StatusCode);
        Assert.Equal(1, await CountAlbumItemsAsync(albumId));
    }

    // ── Withdrawal and owner removal ────────────────────────────────────────

    [Fact]
    public async Task Contributor_Withdraws_Their_Own_Contribution_Without_Deleting_It()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, bob) = await _factory.CreateAuthenticatedClientAsync(ContributorEmail);
        var albumId = await CreateAlbumAsync(owner, "Trip");
        await InviteAcceptAsync(owner, bob, albumId, ContributorEmail, "contributor");
        var bobFile = await UploadPngAsync(bob, "bob.png");
        (await Contribute(bob, albumId, bobFile)).EnsureSuccessStatusCode();

        (await Withdraw(bob, albumId, bobFile)).EnsureSuccessStatusCode();

        Assert.Equal(0, await CountAlbumItemsAsync(albumId));
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync(Thumb(albumId, bobFile))).StatusCode);
        // The file is untouched in Bob's own library.
        (await bob.GetAsync($"/api/files/{bobFile}/content")).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Withdrawal_Still_Works_After_A_Downgrade_To_Viewer()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, bob) = await _factory.CreateAuthenticatedClientAsync(ContributorEmail);
        var albumId = await CreateAlbumAsync(owner, "Trip");
        var membershipId = await InviteAsync(owner, albumId, ContributorEmail, "contributor");
        await AcceptAsync(bob, membershipId);
        var bobFile = await UploadPngAsync(bob, "bob.png");
        (await Contribute(bob, albumId, bobFile)).EnsureSuccessStatusCode();

        (await owner.PatchAsJsonAsync($"/api/albums/{albumId}/members/{membershipId}/role",
            new { role = "viewer" })).EnsureSuccessStatusCode();

        // The right to take your media back follows from OWNING it and having
        // contributed it — not from the role you hold now.
        (await Withdraw(bob, albumId, bobFile)).EnsureSuccessStatusCode();
        Assert.Equal(0, await CountAlbumItemsAsync(albumId));
        (await bob.GetAsync($"/api/files/{bobFile}/content")).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task A_Contributor_Cannot_Withdraw_Somebody_Elses_Item()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, bob) = await _factory.CreateAuthenticatedClientAsync(ContributorEmail);
        var (_, carol) = await _factory.CreateAuthenticatedClientAsync(OtherEmail);
        var albumId = await CreateAlbumAsync(owner, "Trip");
        var ownerFile = await AddOwnPngAsync(owner, albumId, "owner.png");
        await InviteAcceptAsync(owner, bob, albumId, ContributorEmail, "contributor");
        await InviteAcceptAsync(owner, carol, albumId, OtherEmail, "contributor");
        var carolFile = await UploadPngAsync(carol, "carol.png");
        (await Contribute(carol, albumId, carolFile)).EnsureSuccessStatusCode();

        // Not another contributor's item, and not the album owner's item.
        Assert.Equal(HttpStatusCode.NotFound, (await Withdraw(bob, albumId, carolFile)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Withdraw(bob, albumId, ownerFile)).StatusCode);
        Assert.Equal(2, await CountAlbumItemsAsync(albumId));
    }

    [Fact]
    public async Task Owner_Removes_Any_Item_Without_Deleting_The_Source()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, bob) = await _factory.CreateAuthenticatedClientAsync(ContributorEmail);
        var albumId = await CreateAlbumAsync(owner, "Trip");
        var ownerFile = await AddOwnPngAsync(owner, albumId, "owner.png");
        await InviteAcceptAsync(owner, bob, albumId, ContributorEmail, "contributor");
        var bobFile = await UploadPngAsync(bob, "bob.png");
        (await Contribute(bob, albumId, bobFile)).EnsureSuccessStatusCode();

        foreach (var fileId in new[] { bobFile, ownerFile })
        {
            (await owner.DeleteAsync($"/api/albums/{albumId}/content/{fileId}"))
                .EnsureSuccessStatusCode();
        }

        Assert.Equal(0, await CountAlbumItemsAsync(albumId));
        // BOTH source files survive, each in its own owner's library.
        (await bob.GetAsync($"/api/files/{bobFile}/content")).EnsureSuccessStatusCode();
        (await owner.GetAsync($"/api/files/{ownerFile}/content")).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task A_Removed_Contribution_Cannot_Be_Re_Added_By_The_Album_Owner()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, bob) = await _factory.CreateAuthenticatedClientAsync(ContributorEmail);
        var albumId = await CreateAlbumAsync(owner, "Trip");
        await InviteAcceptAsync(owner, bob, albumId, ContributorEmail, "contributor");
        var bobFile = await UploadPngAsync(bob, "bob.png");
        (await Contribute(bob, albumId, bobFile)).EnsureSuccessStatusCode();
        (await owner.DeleteAsync($"/api/albums/{albumId}/content/{bobFile}")).EnsureSuccessStatusCode();

        // Only Bob can put it back.
        Assert.Equal(HttpStatusCode.NotFound,
            (await owner.PostAsJsonAsync($"/api/albums/{albumId}/items",
                new { fileItemId = bobFile })).StatusCode);
        Assert.Equal(0, await CountAlbumItemsAsync(albumId));

        (await Contribute(bob, albumId, bobFile)).EnsureSuccessStatusCode();
        Assert.Equal(1, await CountAlbumItemsAsync(albumId));
    }

    // ── Revocation ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Revoking_A_Membership_Withdraws_Every_Contribution_It_Made()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, bob) = await _factory.CreateAuthenticatedClientAsync(ContributorEmail);
        var (_, carol) = await _factory.CreateAuthenticatedClientAsync(OtherEmail);
        var albumId = await CreateAlbumAsync(owner, "Trip");
        var ownerFile = await AddOwnPngAsync(owner, albumId, "owner.png");
        var membershipId = await InviteAsync(owner, albumId, ContributorEmail, "contributor");
        await AcceptAsync(bob, membershipId);
        await InviteAcceptAsync(owner, carol, albumId, OtherEmail, "contributor");

        var bobA = await UploadPngAsync(bob, "bob-a.png");
        var bobB = await UploadPngAsync(bob, "bob-b.png");
        var carolFile = await UploadPngAsync(carol, "carol.png");
        foreach (var (client, file) in new[] { (bob, bobA), (bob, bobB), (carol, carolFile) })
        {
            (await Contribute(client, albumId, file)).EnsureSuccessStatusCode();
        }
        Assert.Equal(4, await CountAlbumItemsAsync(albumId));

        (await owner.DeleteAsync($"/api/albums/{albumId}/members/{membershipId}"))
            .EnsureSuccessStatusCode();

        // Bob's two contributions are gone; the owner's item and Carol's stay.
        Assert.Equal(2, await CountAlbumItemsAsync(albumId));
        foreach (var file in new[] { bobA, bobB })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync(Thumb(albumId, file))).StatusCode);
            // …and Bob still has both files.
            (await bob.GetAsync($"/api/files/{file}/content")).EnsureSuccessStatusCode();
        }
        (await carol.GetAsync(Thumb(albumId, carolFile))).EnsureSuccessStatusCode();
        (await carol.GetAsync(Thumb(albumId, ownerFile))).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task The_Resolver_Refuses_A_Contribution_Whose_Membership_Ended_Even_If_The_Row_Survives()
    {
        // Fail-closed guarantee. Revocation normally withdraws the items in the
        // same transaction; this forces the racy/inconsistent state — row still
        // present, membership already gone — and proves access ends anyway.
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, bob) = await _factory.CreateAuthenticatedClientAsync(ContributorEmail);
        var (_, carol) = await _factory.CreateAuthenticatedClientAsync(OtherEmail);
        var albumId = await CreateAlbumAsync(owner, "Trip");
        await InviteAcceptAsync(owner, bob, albumId, ContributorEmail, "contributor");
        await InviteAcceptAsync(owner, carol, albumId, OtherEmail, "viewer");
        var bobFile = await UploadPngAsync(bob, "bob.png");
        (await Contribute(bob, albumId, bobFile)).EnsureSuccessStatusCode();
        (await carol.GetAsync(Thumb(albumId, bobFile))).EnsureSuccessStatusCode();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var bobId = await UserIdAsync(ContributorEmail);
            await db.AlbumMemberships
                .Where(m => m.AlbumId == albumId && m.MemberUserId == bobId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(m => m.State, AlbumMembershipStates.Revoked)
                    .SetProperty(m => m.RevokedAt, DateTime.UtcNow));
        }

        // The album_items row is still there…
        Assert.Equal(1, await CountAlbumItemsAsync(albumId));
        // …but nobody can see or open it any more.
        Assert.Equal(HttpStatusCode.NotFound, (await carol.GetAsync(Thumb(albumId, bobFile))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync(Thumb(albumId, bobFile))).StatusCode);
        Assert.Empty((await carol.GetFromJsonAsync<JsonElement>($"/api/shared-albums/{albumId}/items"))
            .EnumerateArray());
    }

    [Fact]
    public async Task A_Corrupt_Provenance_Row_Fails_Closed()
    {
        // AddedByUserId disagreeing with the file's owner is unrepresentable
        // through the API. Forced here to prove the resolver refuses rather than
        // guessing which of the two to trust.
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, bob) = await _factory.CreateAuthenticatedClientAsync(ContributorEmail);
        var albumId = await CreateAlbumAsync(owner, "Trip");
        await InviteAcceptAsync(owner, bob, albumId, ContributorEmail, "contributor");
        var bobFile = await UploadPngAsync(bob, "bob.png");
        (await Contribute(bob, albumId, bobFile)).EnsureSuccessStatusCode();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var carolId = await _factory.SeedUserAsync(OtherEmail);
            await db.AlbumItems
                .Where(ai => ai.AlbumId == albumId && ai.FileItemId == bobFile)
                .ExecuteUpdateAsync(s => s.SetProperty(ai => ai.AddedByUserId, carolId));
        }

        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync(Thumb(albumId, bobFile))).StatusCode);
        Assert.Empty((await bob.GetFromJsonAsync<JsonElement>($"/api/shared-albums/{albumId}/items"))
            .EnumerateArray());
    }

    // ── Source lifecycle ────────────────────────────────────────────────────

    [Fact]
    public async Task Deleting_The_Source_Permanently_Withdraws_It_From_Every_Album()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, bob) = await _factory.CreateAuthenticatedClientAsync(ContributorEmail);
        var albumA = await CreateAlbumAsync(owner, "Trip A");
        var albumB = await CreateAlbumAsync(owner, "Trip B");
        await InviteAcceptAsync(owner, bob, albumA, ContributorEmail, "contributor");
        await InviteAcceptAsync(owner, bob, albumB, ContributorEmail, "contributor");
        var shared = await UploadPngAsync(bob, "shared.png");
        var kept = await UploadPngAsync(bob, "kept.png");
        (await Contribute(bob, albumA, shared)).EnsureSuccessStatusCode();
        (await Contribute(bob, albumB, shared)).EnsureSuccessStatusCode();
        (await Contribute(bob, albumA, kept)).EnsureSuccessStatusCode();

        (await bob.DeleteAsync($"/api/files/{shared}")).EnsureSuccessStatusCode();
        (await bob.DeleteAsync($"/api/trash/files/{shared}")).EnsureSuccessStatusCode();

        // Gone from BOTH albums, and nothing else went with it.
        Assert.Equal(1, await CountAlbumItemsAsync(albumA));
        Assert.Equal(0, await CountAlbumItemsAsync(albumB));
        (await bob.GetAsync($"/api/files/{kept}/content")).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Soft_Delete_Vault_And_Exclusion_Hide_A_Contribution_Fail_Closed()
    {
        var (bobId, bob) = await _factory.CreateAuthenticatedClientAsync(ContributorEmail);
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, carol) = await _factory.CreateAuthenticatedClientAsync(OtherEmail);
        var albumId = await CreateAlbumAsync(owner, "Trip");
        await InviteAcceptAsync(owner, bob, albumId, ContributorEmail, "contributor");
        await InviteAcceptAsync(owner, carol, albumId, OtherEmail, "viewer");

        var vaulted = await UploadPngAsync(bob, "v.png");
        var softDeleted = await UploadPngAsync(bob, "s.png");
        var excluded = await UploadPngAsync(bob, "e.png");
        foreach (var f in new[] { vaulted, softDeleted, excluded })
        {
            (await Contribute(bob, albumId, f)).EnsureSuccessStatusCode();
        }
        Assert.Equal(3, (await carol.GetFromJsonAsync<JsonElement>(
            $"/api/shared-albums/{albumId}/items")).GetArrayLength());

        await MoveIntoVaultAsync(bobId, vaulted);
        (await bob.DeleteAsync($"/api/files/{softDeleted}")).EnsureSuccessStatusCode();
        await ExcludeFromMediaLibraryAsync(excluded);

        Assert.Empty((await carol.GetFromJsonAsync<JsonElement>($"/api/shared-albums/{albumId}/items"))
            .EnumerateArray());
        foreach (var f in new[] { vaulted, softDeleted, excluded })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await carol.GetAsync(Thumb(albumId, f))).StatusCode);
        }
    }

    [Fact]
    public async Task A_Disabled_Contributor_Loses_Their_Contributions_Visibility()
    {
        var (bobId, bob) = await _factory.CreateAuthenticatedClientAsync(ContributorEmail);
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, carol) = await _factory.CreateAuthenticatedClientAsync(OtherEmail);
        var albumId = await CreateAlbumAsync(owner, "Trip");
        await InviteAcceptAsync(owner, bob, albumId, ContributorEmail, "contributor");
        await InviteAcceptAsync(owner, carol, albumId, OtherEmail, "viewer");
        var bobFile = await UploadPngAsync(bob, "bob.png");
        (await Contribute(bob, albumId, bobFile)).EnsureSuccessStatusCode();
        (await carol.GetAsync(Thumb(albumId, bobFile))).EnsureSuccessStatusCode();

        await _factory.DisableUserAsync(bobId);

        Assert.Equal(HttpStatusCode.NotFound, (await carol.GetAsync(Thumb(albumId, bobFile))).StatusCode);
        Assert.Empty((await carol.GetFromJsonAsync<JsonElement>($"/api/shared-albums/{albumId}/items"))
            .EnumerateArray());
    }

    [Fact]
    public async Task Moving_The_Source_Inside_Its_Own_Library_Keeps_The_Contribution()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, bob) = await _factory.CreateAuthenticatedClientAsync(ContributorEmail);
        var albumId = await CreateAlbumAsync(owner, "Trip");
        await InviteAcceptAsync(owner, bob, albumId, ContributorEmail, "contributor");
        var bobFile = await UploadPngAsync(bob, "bob.png");
        (await Contribute(bob, albumId, bobFile)).EnsureSuccessStatusCode();

        var folder = await bob.PostAsJsonAsync("/api/folders", new { name = "Trips", parentFolderId = (Guid?)null });
        folder.EnsureSuccessStatusCode();
        var folderId = (await folder.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        (await bob.PatchAsJsonAsync($"/api/files/{bobFile}/move", new { parentFolderId = folderId }))
            .EnsureSuccessStatusCode();

        // A move is DB-only and keeps the FileItemId, so album membership holds.
        Assert.Equal(1, await CountAlbumItemsAsync(albumId));
        (await bob.GetAsync(Thumb(albumId, bobFile))).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Deleting_The_Album_Removes_Contributions_But_No_Source_Files()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, bob) = await _factory.CreateAuthenticatedClientAsync(ContributorEmail);
        var albumId = await CreateAlbumAsync(owner, "Trip");
        await InviteAcceptAsync(owner, bob, albumId, ContributorEmail, "contributor");
        var bobFile = await UploadPngAsync(bob, "bob.png");
        (await Contribute(bob, albumId, bobFile)).EnsureSuccessStatusCode();

        (await owner.DeleteAsync($"/api/albums/{albumId}")).EnsureSuccessStatusCode();

        Assert.Equal(0, await CountAlbumItemsAsync(albumId));
        (await bob.GetAsync($"/api/files/{bobFile}/content")).EnsureSuccessStatusCode();
    }

    // ── Owner moderation surface ────────────────────────────────────────────

    [Fact]
    public async Task The_Owner_Content_View_Shows_Provenance_And_Source_State()
    {
        var (bobId, bob) = await _factory.CreateAuthenticatedClientAsync(ContributorEmail);
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var albumId = await CreateAlbumAsync(owner, "Trip");
        var ownerFile = await AddOwnPngAsync(owner, albumId, "owner.png");
        await InviteAcceptAsync(owner, bob, albumId, ContributorEmail, "contributor");
        var live = await UploadPngAsync(bob, "live.png");
        var vaulted = await UploadPngAsync(bob, "vault.png");
        (await Contribute(bob, albumId, live)).EnsureSuccessStatusCode();
        (await Contribute(bob, albumId, vaulted)).EnsureSuccessStatusCode();
        await MoveIntoVaultAsync(bobId, vaulted);

        var content = await owner.GetFromJsonAsync<JsonElement>($"/api/albums/{albumId}/content");
        var byId = content.GetProperty("items").EnumerateArray()
            .ToDictionary(x => x.GetProperty("fileItemId").GetGuid());
        Assert.Equal(3, byId.Count);

        Assert.Equal("owner", byId[ownerFile].GetProperty("origin").GetString());
        Assert.Equal(JsonValueKind.Null, byId[ownerFile].GetProperty("contributorDisplayName").ValueKind);

        Assert.Equal("contribution", byId[live].GetProperty("origin").GetString());
        Assert.Equal("available", byId[live].GetProperty("sourceState").GetString());
        Assert.Equal("Owner", byId[live].GetProperty("contributorDisplayName").GetString());
        // Same privacy-safe disambiguation as the member list.
        Assert.Equal("b••@example.com", byId[live].GetProperty("contributorMaskedEmail").GetString());

        // A vaulted source is still LISTED, so the owner can clear the row, but
        // it is reported unavailable and is not openable.
        Assert.Equal("unavailable", byId[vaulted].GetProperty("sourceState").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync(Thumb(albumId, vaulted))).StatusCode);

        // Full addresses never appear.
        var raw = content.GetRawText();
        Assert.DoesNotContain(ContributorEmail, raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(bobId.ToString(), raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_Owner_Content_View_Is_Owner_Only()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, bob) = await _factory.CreateAuthenticatedClientAsync(ContributorEmail);
        var albumId = await CreateAlbumAsync(owner, "Trip");
        await InviteAcceptAsync(owner, bob, albumId, ContributorEmail, "contributor");

        Assert.Equal(HttpStatusCode.NotFound,
            (await bob.GetAsync($"/api/albums/{albumId}/content")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await bob.DeleteAsync($"/api/albums/{albumId}/content/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Contributions_Never_Enter_The_Owners_Library_Or_Album_Workspace()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, bob) = await _factory.CreateAuthenticatedClientAsync(ContributorEmail);
        var albumId = await CreateAlbumAsync(owner, "Trip");
        await InviteAcceptAsync(owner, bob, albumId, ContributorEmail, "contributor");
        var bobFile = await UploadPngAsync(bob, "bob.png");
        (await Contribute(bob, albumId, bobFile)).EnsureSuccessStatusCode();

        // The owner's own library, gallery and album workspace stay theirs
        // alone — a contribution is visible only through the additive surfaces.
        var root = await owner.GetFromJsonAsync<JsonElement>("/api/folders/children");
        Assert.Empty(root.GetProperty("files").EnumerateArray());

        var media = await owner.GetFromJsonAsync<JsonElement>("/api/media?kind=image");
        Assert.Empty(media.GetProperty("items").EnumerateArray());

        var workspace = await owner.GetFromJsonAsync<JsonElement>($"/api/albums/{albumId}/media");
        Assert.Empty(workspace.GetProperty("items").EnumerateArray());

        var legacyItems = await owner.GetFromJsonAsync<JsonElement>($"/api/albums/{albumId}/items");
        Assert.Empty(legacyItems.EnumerateArray());

        // The owner also cannot reach the bytes through their own file routes.
        Assert.Equal(HttpStatusCode.NotFound,
            (await owner.GetAsync($"/api/files/{bobFile}/content")).StatusCode);
    }

    // ── Party / TV / face search must NOT gain contributed media ────────────

    [Fact]
    public async Task A_Contribution_Is_Never_Published_Through_Party_Or_TV()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, bob) = await _factory.CreateAuthenticatedClientAsync(ContributorEmail);
        var albumId = await CreateAlbumAsync(owner, "Trip");
        var ownerFile = await AddOwnPngAsync(owner, albumId, "owner.png");
        await InviteAcceptAsync(owner, bob, albumId, ContributorEmail, "contributor");
        var bobFile = await UploadPngAsync(bob, "bob.png");
        (await Contribute(bob, albumId, bobFile)).EnsureSuccessStatusCode();

        // It IS in the live shared album…
        (await bob.GetAsync(Thumb(albumId, bobFile))).EnsureSuccessStatusCode();

        // …but the owner turning on Party must not publish somebody else's
        // media to the public internet.
        (await owner.PatchAsJsonAsync($"/api/albums/{albumId}/tv-settings", new { showOnTv = true }))
            .EnsureSuccessStatusCode();
        var party = await owner.PatchAsJsonAsync($"/api/albums/{albumId}/party-settings",
            new { enabled = true });
        party.EnsureSuccessStatusCode();
        var token = (await party.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("partyUrl").GetString()!.Split('/')[^1];

        var anon = _factory.CreateClient();
        // /api/party/{token} is the header (name + count); the media list is /items.
        var publicHeader = await anon.GetFromJsonAsync<JsonElement>($"/api/party/{token}");
        Assert.Equal(1, publicHeader.GetProperty("itemCount").GetInt32());
        var publicItems = await anon.GetFromJsonAsync<JsonElement>($"/api/party/{token}/items");
        var publicIds = publicItems.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("id").GetGuid()).ToList();
        Assert.Contains(ownerFile, publicIds);
        Assert.DoesNotContain(bobFile, publicIds);
        Assert.Equal(HttpStatusCode.NotFound,
            (await anon.GetAsync($"/api/party/{token}/media/{bobFile}/thumbnail")).StatusCode);

        // Nor to the owner's paired TV.
        var tvCookie = await PairTvAsync(owner);
        var tvItems = await TvJsonAsync(tvCookie, $"/api/tv/albums/{albumId}/items");
        var tvIds = tvItems.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("id").GetGuid()).ToList();
        Assert.Contains(ownerFile, tvIds);
        Assert.DoesNotContain(bobFile, tvIds);
    }

    [Fact]
    public async Task A_Contribution_Brings_No_People_Data_Across()
    {
        var (bobId, bob) = await _factory.CreateAuthenticatedClientAsync(ContributorEmail);
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var albumId = await CreateAlbumAsync(owner, "Trip");
        await InviteAcceptAsync(owner, bob, albumId, ContributorEmail, "contributor");
        var bobFile = await UploadPngAsync(bob, "bob.png");
        (await Contribute(bob, albumId, bobFile)).EnsureSuccessStatusCode();

        // Bob names a person on his own file. None of that may follow the media
        // into Alice's album.
        const string personName = "Marco Rossi";
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.People.Add(new NubArca.Api.Domain.Ai.Person
            {
                Id = Guid.NewGuid(),
                OwnerUserId = bobId,
                DisplayName = personName,
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        // The album owner's People model is untouched, and the contributed
        // media carries no person payload on any surface they can reach.
        Assert.Empty((await owner.GetFromJsonAsync<JsonElement>("/api/people")).EnumerateArray());
        foreach (var path in new[]
                 {
                     $"/api/albums/{albumId}/content",
                     $"/api/shared-albums/{albumId}/items",
                 })
        {
            var body = await (await owner.GetAsync(path)).Content.ReadAsStringAsync();
            Assert.DoesNotContain(personName, body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("person", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("face", body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task A_Contribution_Is_Not_A_Party_Face_Search_Candidate()
    {
        // PartyFaceSearchService.VisibleImageMembersQuery is owner-scoped
        // (`f.OwnerUserId == ownerUserId`), so a contribution can never enter
        // the album owner's face-search candidate set. The public Party surface
        // test above proves the same exclusion end to end; this pins the
        // candidate set itself, because face search is the one Party path that
        // reads album_items through its OWN query rather than PartyMediaService.
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (bobId, bob) = await _factory.CreateAuthenticatedClientAsync(ContributorEmail);
        var albumId = await CreateAlbumAsync(owner, "Trip");
        var ownerFile = await AddOwnPngAsync(owner, albumId, "owner.png");
        await InviteAcceptAsync(owner, bob, albumId, ContributorEmail, "contributor");
        var bobFile = await UploadPngAsync(bob, "bob.png");
        (await Contribute(bob, albumId, bobFile)).EnsureSuccessStatusCode();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ownerId = await db.Users.Where(u => u.Email == OwnerEmail)
            .Select(u => u.Id).FirstAsync();

        var candidates = await db.AlbumItems.AsNoTracking()
            .Where(ai => ai.AlbumId == albumId)
            .Join(db.FileItems.AsNoTracking(), ai => ai.FileItemId, f => f.Id, (ai, f) => f)
            .Where(f => f.OwnerUserId == ownerId
                && f.DeletedAt == null
                && f.MediaLibraryState == MediaLibraryState.Active)
            .Select(f => f.Id)
            .ToListAsync();

        Assert.Equal([ownerFile], candidates);
        Assert.DoesNotContain(bobFile, candidates);
        Assert.NotEqual(ownerId, bobId);
    }

    // ── Audit ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Audit_Separates_Actor_Album_Owner_And_Source_Owner()
    {
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (bobId, bob) = await _factory.CreateAuthenticatedClientAsync(ContributorEmail);
        var albumId = await CreateAlbumAsync(owner, "Trip");
        var membershipId = await InviteAsync(owner, albumId, ContributorEmail, "contributor");
        await AcceptAsync(bob, membershipId);

        var added = await UploadPngAsync(bob, "added.png");
        var withdrawn = await UploadPngAsync(bob, "withdrawn.png");
        var removed = await UploadPngAsync(bob, "removed.png");
        var revoked = await UploadPngAsync(bob, "revoked.png");
        foreach (var f in new[] { added, withdrawn, removed, revoked })
        {
            (await Contribute(bob, albumId, f)).EnsureSuccessStatusCode();
        }
        (await Withdraw(bob, albumId, withdrawn)).EnsureSuccessStatusCode();
        (await owner.DeleteAsync($"/api/albums/{albumId}/content/{removed}")).EnsureSuccessStatusCode();
        (await owner.PatchAsJsonAsync($"/api/albums/{albumId}/members/{membershipId}/role",
            new { role = "viewer" })).EnsureSuccessStatusCode();
        (await owner.DeleteAsync($"/api/albums/{albumId}/members/{membershipId}"))
            .EnsureSuccessStatusCode();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var logs = await db.AuditLogs.Where(a => a.Action.StartsWith("album.")).ToListAsync();

        // The CONTRIBUTOR is the actor for adds and their own withdrawal.
        Assert.Equal(bobId, logs.Single(a =>
            a.Action == "album.contribution_add" && a.EntityId == added).UserId);
        Assert.Equal(bobId, logs.Single(a => a.Action == "album.contribution_withdraw").UserId);
        // The ALBUM OWNER is the actor for a removal, a role change and a revoke.
        Assert.Equal(ownerId, logs.Single(a => a.Action == "album.contribution_remove").UserId);
        Assert.Equal(ownerId, logs.Single(a => a.Action == "album.share_role_change").UserId);
        // Bob still had TWO items in the album at revoke time (`added` and
        // `revoked`), so the revocation emits one auto-withdrawal per item.
        var autos = logs.Where(a => a.Action == "album.contribution_auto_withdraw").ToList();
        Assert.Equal(2, autos.Count);
        Assert.All(autos, a => Assert.Equal(ownerId, a.UserId));
        Assert.Equal(
            new[] { added, revoked }.Order().ToList(),
            autos.Select(a => a.EntityId!.Value).Order().ToList());
        var auto = autos.First();

        // …and the SOURCE OWNER is recorded separately from the actor, with a
        // reason, so "who removed whose media from whose album" is answerable.
        Assert.Contains(bobId.ToString(), auto.MetadataJson!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("membership_revoked", auto.MetadataJson!, StringComparison.Ordinal);
        var removeLog = logs.Single(a => a.Action == "album.contribution_remove");
        Assert.Contains(bobId.ToString(), removeLog.MetadataJson!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(ownerId.ToString(), removeLog.MetadataJson!, StringComparison.OrdinalIgnoreCase);

        // Never a file name or storage internal.
        foreach (var log in logs)
        {
            var payload = log.MetadataJson ?? string.Empty;
            Assert.DoesNotContain(".png", payload, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(ContributorEmail, payload, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static string Thumb(Guid a, Guid f) => $"/api/shared-albums/{a}/media/{f}/thumbnail";

    private static Task<HttpResponseMessage> Contribute(HttpClient c, Guid albumId, Guid fileId) =>
        c.PostAsJsonAsync($"/api/shared-albums/{albumId}/contributions", new { fileItemId = fileId });

    private static Task<HttpResponseMessage> Withdraw(HttpClient c, Guid albumId, Guid fileId) =>
        c.DeleteAsync($"/api/shared-albums/{albumId}/contributions/{fileId}");

    private static async Task<Guid> CreateAlbumAsync(HttpClient owner, string name)
    {
        var response = await owner.PostAsJsonAsync("/api/albums", new { name });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> UploadPngAsync(HttpClient client, string name)
    {
        using var img = new Image<Rgba32>(8, 8);
        // Distinct bytes per name: storage is content-addressed, so identical
        // fixtures would deduplicate onto one blob and one BlobMetadata row.
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

    private static async Task<Guid> AddOwnPngAsync(HttpClient owner, Guid albumId, string name)
    {
        var fileId = await UploadPngAsync(owner, name);
        (await owner.PostAsJsonAsync($"/api/albums/{albumId}/items", new { fileItemId = fileId }))
            .EnsureSuccessStatusCode();
        return fileId;
    }

    private static async Task<Guid> InviteAsync(
        HttpClient owner, Guid albumId, string email, string role)
    {
        var response = await owner.PostAsJsonAsync(
            $"/api/albums/{albumId}/members", new { email, role });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("membershipId").GetGuid();
    }

    private static async Task AcceptAsync(HttpClient member, Guid membershipId) =>
        (await member.PostAsync($"/api/shared-albums/invitations/{membershipId}/accept", null))
            .EnsureSuccessStatusCode();

    private static async Task InviteAcceptAsync(
        HttpClient owner, HttpClient member, Guid albumId, string email, string role)
    {
        var id = await InviteAsync(owner, albumId, email, role);
        await AcceptAsync(member, id);
    }

    private async Task<int> CountAlbumItemsAsync(Guid albumId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.AlbumItems.CountAsync(ai => ai.AlbumId == albumId);
    }

    private async Task<int> CountMembershipsAsync(Guid albumId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.AlbumMemberships.CountAsync(m => m.AlbumId == albumId);
    }

    private async Task<Guid> UserIdAsync(string email)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return (await db.Users.FirstAsync(u => u.Email == email)).Id;
    }

    private static async Task<long> UsedBytesAsync(HttpClient client)
    {
        var body = await client.GetFromJsonAsync<JsonElement>("/api/storage/me");
        return body.GetProperty("usedBytes").GetInt64();
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
        var vault = await db.PrivateVaults.FirstOrDefaultAsync(v => v.OwnerUserId == ownerUserId);
        if (vault is null)
        {
            vault = new PrivateVault
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
        }

        var file = await db.FileItems.IgnoreQueryFilters().FirstAsync(f => f.Id == fileItemId);
        file.PrivateVaultId = vault.Id;
        await db.SaveChangesAsync();
    }

    // TV pairing, matching TvMediaBrowsingTests: pairing/start → owner approve
    // (atomic first PIN) → status poll returns the TV session cookie.
    private async Task<string> PairTvAsync(HttpClient owner)
    {
        var tvClient = _factory.CreateClient();
        var start = await tvClient.PostAsync("/api/tv/pairing/start", null);
        start.EnsureSuccessStatusCode();
        var started = (await start.Content.ReadFromJsonAsync<JsonElement>());
        var publicCode = started.GetProperty("publicCode").GetString()!;
        var pairingSecret = started.GetProperty("pairingSecret").GetString()!;

        (await owner.PostAsJsonAsync(
            $"/api/tv/pairing/{publicCode}/approve",
            new
            {
                pairingSecret,
                personalPin = "123456",
                personalPinConfirmation = "123456",
            })).EnsureSuccessStatusCode();

        var pollRequest = new HttpRequestMessage(
            HttpMethod.Get, $"/api/tv/pairing/{publicCode}/status");
        pollRequest.Headers.Add(
            NubArca.Api.Tv.TvPairingService.PairingSecretHeader, pairingSecret);
        var poll = await tvClient.SendAsync(pollRequest);
        poll.EnsureSuccessStatusCode();
        return poll.Headers.GetValues("Set-Cookie").Single();
    }

    private async Task<JsonElement> TvJsonAsync(string setCookie, string url)
    {
        var value = setCookie.Split(';', 2)[0];
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add(
            "Cookie",
            $"{NubArca.Api.Tv.TvPairingService.CookieName}={value[(value.IndexOf('=') + 1)..]}");
        var response = await _factory.CreateClient().SendAsync(request);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }
}
