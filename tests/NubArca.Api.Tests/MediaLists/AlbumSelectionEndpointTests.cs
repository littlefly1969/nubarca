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

namespace NubArca.Api.Tests.MediaLists;

// A member's personal selection inside ONE shared album: theirs only, scoped to
// that album, apart from their library selection, resumed from any of their
// sessions, and gone the moment the membership is.
public sealed class AlbumSelectionEndpointTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory = new();

    public AlbumSelectionEndpointTests() => _factory.EnsureDatabaseCreated();

    public void Dispose() => _factory.Dispose();

    private const string OwnerEmail = "alice@example.com";
    private const string MemberEmail = "bob@example.com";
    private const string OtherMemberEmail = "dan@example.com";
    private const string StrangerEmail = "carol@example.com";

    [Fact]
    public async Task A_Member_Picks_In_One_Session_And_Resumes_In_Another()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, member) = await _factory.CreateAuthenticatedClientAsync(MemberEmail);
        var albumId = await CreateAlbumAsync(owner, "Wedding");
        var p = new List<Guid>();
        for (var i = 0; i < 3; i++) p.Add(await AddPngAsync(owner, albumId, $"p{i}.png"));
        await InviteAndAcceptAsync(owner, member, albumId, MemberEmail);

        var added = await PostItemsAsync(member, albumId, [p[2], p[0], p[2]]);
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        var body = await added.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(3, body.GetProperty("requested").GetInt32());
        Assert.Equal(2, body.GetProperty("succeeded").GetInt32());
        Assert.Equal(2, body.GetProperty("count").GetInt32());

        // Idempotent: adding the same again changes nothing, not even the version.
        var version = body.GetProperty("version").GetInt32();
        var again = await (await PostItemsAsync(member, albumId, [p[0]])).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, again.GetProperty("succeeded").GetInt32());
        Assert.Equal(version, again.GetProperty("version").GetInt32());

        // A second session of the same account sees the same picks, in pick order.
        var second = await _factory.LoginAsync(MemberEmail);
        Assert.Equal([p[2], p[0]], await IdsAsync(second, albumId));
        var state = await second.GetFromJsonAsync<JsonElement>($"/api/shared-albums/{albumId}/selection");
        Assert.Equal(2, state.GetProperty("count").GetInt32());
        Assert.Equal(version, state.GetProperty("version").GetInt32());

        Assert.Equal(HttpStatusCode.OK, (await DeleteItemsAsync(second, albumId, [p[2]])).StatusCode);
        Assert.Equal([p[0]], await IdsAsync(member, albumId));
        Assert.Equal(HttpStatusCode.OK, (await member.PostAsync($"/api/shared-albums/{albumId}/selection/clear", null)).StatusCode);
        Assert.Empty(await IdsAsync(second, albumId));
    }

    [Fact]
    public async Task Only_What_The_Album_Serves_Can_Be_Picked()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, member) = await _factory.CreateAuthenticatedClientAsync(MemberEmail);
        var albumId = await CreateAlbumAsync(owner, "Wedding");
        var inAlbum = await AddPngAsync(owner, albumId, "in.png");
        var ownersOther = await UploadPngAsync(owner, "private.png");
        var membersOwn = await UploadPngAsync(member, "mine.png");
        var otherAlbum = await CreateAlbumAsync(owner, "Other");
        var elsewhere = await AddPngAsync(owner, otherAlbum, "elsewhere.png");
        await InviteAndAcceptAsync(owner, member, albumId, MemberEmail);

        var body = await (await PostItemsAsync(member, albumId, [inAlbum, ownersOther, membersOwn, elsewhere, Guid.NewGuid()]))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetProperty("succeeded").GetInt32());
        Assert.Equal(4, body.GetProperty("skipped").GetInt32());
        Assert.Equal([inAlbum], await IdsAsync(member, albumId));
    }

    [Fact]
    public async Task Members_Never_See_Each_Others_Selection_Or_Mix_It_With_Their_Library()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, member) = await _factory.CreateAuthenticatedClientAsync(MemberEmail);
        var (_, other) = await _factory.CreateAuthenticatedClientAsync(OtherMemberEmail);
        var albumId = await CreateAlbumAsync(owner, "Wedding");
        var a = await AddPngAsync(owner, albumId, "a.png");
        var b = await AddPngAsync(owner, albumId, "b.png");
        await InviteAndAcceptAsync(owner, member, albumId, MemberEmail);
        await InviteAndAcceptAsync(owner, other, albumId, OtherMemberEmail);

        await PostItemsAsync(member, albumId, [a]);
        await PostItemsAsync(other, albumId, [b]);
        Assert.Equal([a], await IdsAsync(member, albumId));
        Assert.Equal([b], await IdsAsync(other, albumId));

        // The album selection is not the account selection, and not a list.
        var account = await member.GetFromJsonAsync<JsonElement>("/api/selection");
        Assert.Equal(0, account.GetProperty("count").GetInt32());
        var lists = await member.GetFromJsonAsync<JsonElement>("/api/lists");
        Assert.Empty(lists.EnumerateArray());
        var scopedId = await ScopedListIdAsync(albumId, MemberEmail);
        Assert.Equal(HttpStatusCode.NotFound, (await member.GetAsync($"/api/lists/{scopedId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await member.GetAsync($"/api/lists/{scopedId}/ids")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await member.PostAsJsonAsync($"/api/lists/{scopedId}/items", new { fileItemIds = new[] { a } })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await member.DeleteAsync($"/api/lists/{scopedId}")).StatusCode);
    }

    [Fact]
    public async Task A_Stranger_Learns_Nothing_Even_With_A_Malformed_Request()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, stranger) = await _factory.CreateAuthenticatedClientAsync(StrangerEmail);
        var albumId = await CreateAlbumAsync(owner, "Wedding");
        var a = await AddPngAsync(owner, albumId, "a.png");

        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/shared-albums/{albumId}/selection")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await stranger.GetAsync($"/api/shared-albums/{albumId}/selection/ids?cursor=garbage")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostItemsAsync(stranger, albumId, [a])).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await stranger.PostAsJsonAsync($"/api/shared-albums/{albumId}/selection/items", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PostAsync($"/api/shared-albums/{albumId}/selection/clear", null)).StatusCode);

        var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/shared-albums/{albumId}/selection")).StatusCode);
    }

    [Fact]
    public async Task Revoking_The_Membership_Ends_Access_At_Once_And_Rejoining_Resumes()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, member) = await _factory.CreateAuthenticatedClientAsync(MemberEmail);
        var albumId = await CreateAlbumAsync(owner, "Wedding");
        var a = await AddPngAsync(owner, albumId, "a.png");
        var membershipId = await InviteAndAcceptAsync(owner, member, albumId, MemberEmail);
        await PostItemsAsync(member, albumId, [a]);

        (await owner.DeleteAsync($"/api/albums/{albumId}/members/{membershipId}")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await member.GetAsync($"/api/shared-albums/{albumId}/selection")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await member.GetAsync($"/api/shared-albums/{albumId}/selection/ids")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostItemsAsync(member, albumId, [a])).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await DeleteItemsAsync(member, albumId, [a])).StatusCode);

        // Invited again, the member takes up their own selection where they left it.
        await InviteAndAcceptAsync(owner, member, albumId, MemberEmail);
        Assert.Equal([a], await IdsAsync(member, albumId));
    }

    [Fact]
    public async Task What_The_Album_Stops_Serving_Drops_Out_And_Comes_Back()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, member) = await _factory.CreateAuthenticatedClientAsync(MemberEmail);
        var albumId = await CreateAlbumAsync(owner, "Wedding");
        var a = await AddPngAsync(owner, albumId, "a.png");
        var b = await AddPngAsync(owner, albumId, "b.png");
        var c = await AddPngAsync(owner, albumId, "c.png");
        await InviteAndAcceptAsync(owner, member, albumId, MemberEmail);
        await PostItemsAsync(member, albumId, [a, b, c]);

        // Removed from the album, trashed, moved to the Vault: none is shown.
        (await owner.DeleteAsync($"/api/albums/{albumId}/items/{a}")).EnsureSuccessStatusCode();
        await MutateFileAsync(b, f => f.DeletedAt = DateTime.UtcNow);
        await VaultFileAsync(c);
        Assert.Empty(await IdsAsync(member, albumId));
        var state = await member.GetFromJsonAsync<JsonElement>($"/api/shared-albums/{albumId}/selection");
        Assert.Equal(0, state.GetProperty("count").GetInt32());

        // Restored, the trashed one is back in the member's selection.
        await MutateFileAsync(b, f => f.DeletedAt = null);
        Assert.Equal([b], await IdsAsync(member, albumId));
    }

    [Fact]
    public async Task Deleting_The_Album_Deletes_The_Selections_And_No_Storage_Fact_Leaks()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, member) = await _factory.CreateAuthenticatedClientAsync(MemberEmail);
        var albumId = await CreateAlbumAsync(owner, "Wedding");
        var a = await AddPngAsync(owner, albumId, "a.png");
        await InviteAndAcceptAsync(owner, member, albumId, MemberEmail);
        var raw = await (await PostItemsAsync(member, albumId, [a])).Content.ReadAsStringAsync();
        raw += await member.GetStringAsync($"/api/shared-albums/{albumId}/selection/ids");
        raw += await member.GetStringAsync($"/api/shared-albums/{albumId}/selection");

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var file = await db.FileItems.FirstAsync(f => f.Id == a);
            var blob = await db.BlobObjects.FirstAsync(x => x.Id == file.BlobObjectId);
            Assert.DoesNotContain(blob.Id.ToString(), raw, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(blob.Sha256, raw, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(blob.StorageKey, raw, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("token", raw, StringComparison.OrdinalIgnoreCase);
        }

        (await owner.DeleteAsync($"/api/albums/{albumId}")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await member.GetAsync($"/api/shared-albums/{albumId}/selection")).StatusCode);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.False(await db.MediaLists.AnyAsync(l => l.Kind == MediaListKind.AlbumSelection));
        }
    }

    [Fact]
    public async Task Bounds_And_Malformed_Input_Are_Refused_For_A_Member()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, member) = await _factory.CreateAuthenticatedClientAsync(MemberEmail);
        var albumId = await CreateAlbumAsync(owner, "Wedding");
        await AddPngAsync(owner, albumId, "a.png");
        await InviteAndAcceptAsync(owner, member, albumId, MemberEmail);

        Assert.Equal(HttpStatusCode.BadRequest,
            (await member.PostAsJsonAsync($"/api/shared-albums/{albumId}/selection/items", new { })).StatusCode);
        var tooMany = Enumerable.Range(0, 3001).Select(_ => Guid.NewGuid()).ToArray();
        Assert.Equal(HttpStatusCode.BadRequest, (await PostItemsAsync(member, albumId, tooMany)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await member.GetAsync($"/api/shared-albums/{albumId}/selection/ids?cursor=garbage")).StatusCode);
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static Task<HttpResponseMessage> PostItemsAsync(HttpClient client, Guid albumId, Guid[] ids) =>
        client.PostAsJsonAsync($"/api/shared-albums/{albumId}/selection/items", new { fileItemIds = ids });

    private static Task<HttpResponseMessage> DeleteItemsAsync(HttpClient client, Guid albumId, Guid[] ids)
    {
        var req = new HttpRequestMessage(HttpMethod.Delete, $"/api/shared-albums/{albumId}/selection/items")
        {
            Content = JsonContent.Create(new { fileItemIds = ids }),
        };
        return client.SendAsync(req);
    }

    private static async Task<List<Guid>> IdsAsync(HttpClient client, Guid albumId)
    {
        var page = await client.GetFromJsonAsync<JsonElement>($"/api/shared-albums/{albumId}/selection/ids");
        return page.GetProperty("ids").EnumerateArray().Select(e => e.GetGuid()).ToList();
    }

    private async Task<Guid> ScopedListIdAsync(Guid albumId, string email)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var userId = await db.Users.Where(u => u.Email == email).Select(u => u.Id).FirstAsync();
        return await db.MediaLists.Where(l => l.OwnerUserId == userId && l.AlbumId == albumId)
            .Select(l => l.Id).FirstAsync();
    }

    private async Task VaultFileAsync(Guid fileId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var file = await db.FileItems.FirstAsync(f => f.Id == fileId);
        var vault = new PrivateVault { Id = Guid.NewGuid(), OwnerUserId = file.OwnerUserId, CreatedAt = DateTime.UtcNow };
        db.PrivateVaults.Add(vault);
        file.PrivateVaultId = vault.Id;
        await db.SaveChangesAsync();
    }

    private async Task MutateFileAsync(Guid fileId, Action<FileItem> change)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var file = await db.FileItems.IgnoreQueryFilters().FirstAsync(f => f.Id == fileId);
        change(file);
        await db.SaveChangesAsync();
    }

    private static async Task<Guid> CreateAlbumAsync(HttpClient owner, string name)
    {
        var response = await owner.PostAsJsonAsync("/api/albums", new { name });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> UploadPngAsync(HttpClient client, string name)
    {
        using var img = new Image<Rgba32>(8, 8);
        var tint = (byte)(name.Aggregate(17, (acc, c) => (acc * 31 + c) & 0xFF));
        img[0, 0] = new Rgba32(tint, tint, tint, 255);
        img[1, 1] = new Rgba32((byte)name.Length, tint, 3, 255);
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

    private static async Task<Guid> InviteAndAcceptAsync(HttpClient owner, HttpClient member, Guid albumId, string email)
    {
        var invited = await owner.PostAsJsonAsync($"/api/albums/{albumId}/members", new { email });
        invited.EnsureSuccessStatusCode();
        var membershipId = (await invited.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("membershipId").GetGuid();
        (await member.PostAsync($"/api/shared-albums/invitations/{membershipId}/accept", null)).EnsureSuccessStatusCode();
        return membershipId;
    }
}
