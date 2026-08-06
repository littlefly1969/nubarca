using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Tests.Metadata;
using NubArca.Api.Tv;

namespace NubArca.Api.Tests.Party;

// Owner-side safety controls for anonymous party uploads: hide/remove guest
// content quickly, and an optional (default OFF) approval-before-visible mode.
// Uploads stay immediately visible by default; hidden/pending/rejected items are
// excluded from every public party + TV surface (list + media). Owner-scoped,
// no storage/blob/token/hash internals in the DTOs.
public sealed class PartyUploadModerationTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory = new();

    public PartyUploadModerationTests() => _factory.EnsureDatabaseCreated();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Default_Is_Immediate_Visibility_And_Owner_Sees_Approved_Upload()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var albumId = await CreateAlbumAsync(owner, "Party");
        var status = await EnablePartyAsync(owner, albumId);

        // Default approval mode is OFF.
        Assert.False(status.GetProperty("requireUploadApproval").GetBoolean());

        var anon = _factory.CreateClient();
        var viewToken = ViewTokenFromStatus(status);
        await UploadAsync(anon, UploadTokenFromStatus(status), ("a.jpg", ImageFixtures.JpegWithExif(), "image/jpeg"));

        // Immediately visible on the public party page.
        Assert.Equal(1, await PartyItemCountAsync(anon, viewToken));

        // Owner sees it as approved with a safe thumbnail path.
        var uploads = await ListUploadsAsync(owner, albumId);
        Assert.False(uploads.GetProperty("requireUploadApproval").GetBoolean());
        var items = uploads.GetProperty("items");
        Assert.Equal(1, items.GetArrayLength());
        Assert.Equal("approved", items[0].GetProperty("status").GetString());
        Assert.StartsWith("/api/files/", items[0].GetProperty("thumbnailUrl").GetString());
    }

    [Fact]
    public async Task Owner_Can_Hide_Upload_Removing_It_From_Public_And_Tv()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var albumId = await CreateAlbumAsync(owner, "Party");
        var status = await EnablePartyAsync(owner, albumId);
        var viewToken = ViewTokenFromStatus(status);
        var cookie = await PairTvAsync(owner);

        var anon = _factory.CreateClient();
        await UploadAsync(anon, UploadTokenFromStatus(status), ("a.jpg", ImageFixtures.JpegWithExif(), "image/jpeg"));
        var fileItemId = await FirstUploadFileIdAsync(owner, albumId);

        // Visible everywhere before hiding.
        Assert.Equal(1, await PartyItemCountAsync(anon, viewToken));
        Assert.Equal(1, await TvItemCountAsync(cookie, albumId));
        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync($"/api/party/{viewToken}/media/{fileItemId}/preview")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await TvGetAsync(cookie, $"/api/tv/media/{fileItemId}/preview")).StatusCode);

        // Hide it.
        Assert.Equal(HttpStatusCode.NoContent,
            (await owner.PostAsync($"/api/albums/{albumId}/party-uploads/{fileItemId}/hide", null)).StatusCode);

        // Gone from every public + TV surface; media 404s.
        Assert.Equal(0, await PartyItemCountAsync(anon, viewToken));
        Assert.Equal(0, await TvItemCountAsync(cookie, albumId));
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/api/party/{viewToken}/media/{fileItemId}/preview")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await TvGetAsync(cookie, $"/api/tv/media/{fileItemId}/preview")).StatusCode);

        // Owner still sees it, now marked hidden.
        var uploads = await ListUploadsAsync(owner, albumId);
        Assert.Equal("hidden", uploads.GetProperty("items")[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task Removing_Guest_Upload_From_Album_Marks_Removed_And_Owner_Can_Restore()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var albumId = await CreateAlbumAsync(owner, "Party");
        var status = await EnablePartyAsync(owner, albumId);
        var viewToken = ViewTokenFromStatus(status);
        var cookie = await PairTvAsync(owner);

        var anon = _factory.CreateClient();
        await UploadAsync(anon, UploadTokenFromStatus(status), ("a.jpg", ImageFixtures.JpegWithExif(), "image/jpeg"));
        var fileItemId = await FirstUploadFileIdAsync(owner, albumId);

        Assert.Equal(1, await PartyItemCountAsync(anon, viewToken));
        Assert.Equal(1, await TvItemCountAsync(cookie, albumId));

        (await owner.DeleteAsync($"/api/albums/{albumId}/items/{fileItemId}")).EnsureSuccessStatusCode();

        var uploads = await ListUploadsAsync(owner, albumId);
        Assert.Equal("removed_from_album", uploads.GetProperty("items")[0].GetProperty("status").GetString());
        Assert.Equal(0, await PartyItemCountAsync(anon, viewToken));
        Assert.Equal(0, await TvItemCountAsync(cookie, albumId));
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/api/party/{viewToken}/media/{fileItemId}/preview")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await TvGetAsync(cookie, $"/api/tv/media/{fileItemId}/preview")).StatusCode);

        (await owner.PostAsync($"/api/albums/{albumId}/party-uploads/{fileItemId}/restore", null))
            .EnsureSuccessStatusCode();

        uploads = await ListUploadsAsync(owner, albumId);
        Assert.Equal("approved", uploads.GetProperty("items")[0].GetProperty("status").GetString());
        Assert.Equal(1, await PartyItemCountAsync(anon, viewToken));
        Assert.Equal(1, await TvItemCountAsync(cookie, albumId));
        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync($"/api/party/{viewToken}/media/{fileItemId}/preview")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await TvGetAsync(cookie, $"/api/tv/media/{fileItemId}/preview")).StatusCode);
    }

    [Fact]
    public async Task Approval_Mode_Makes_New_Uploads_Pending_And_Invisible_Until_Approved()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var albumId = await CreateAlbumAsync(owner, "Party");
        // Enable party WITH approval required.
        var status = await EnablePartyAsync(owner, albumId, requireApproval: true);
        Assert.True(status.GetProperty("requireUploadApproval").GetBoolean());
        var viewToken = ViewTokenFromStatus(status);
        var cookie = await PairTvAsync(owner);

        var anon = _factory.CreateClient();
        // Upload still SUCCEEDS (accepted) — approval affects visibility, not acceptance.
        var result = await UploadAsync(anon, UploadTokenFromStatus(status), ("a.jpg", ImageFixtures.JpegWithExif(), "image/jpeg"));
        var body = await result.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetProperty("accepted").GetInt32());

        var fileItemId = await FirstUploadFileIdAsync(owner, albumId);
        var uploads = await ListUploadsAsync(owner, albumId);
        Assert.Equal("pending", uploads.GetProperty("items")[0].GetProperty("status").GetString());

        // Not visible on public or TV while pending.
        Assert.Equal(0, await PartyItemCountAsync(anon, viewToken));
        Assert.Equal(0, await TvItemCountAsync(cookie, albumId));
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/api/party/{viewToken}/media/{fileItemId}/preview")).StatusCode);

        // Owner approves → becomes visible everywhere.
        Assert.Equal(HttpStatusCode.NoContent,
            (await owner.PostAsync($"/api/albums/{albumId}/party-uploads/{fileItemId}/approve", null)).StatusCode);
        Assert.Equal(1, await PartyItemCountAsync(anon, viewToken));
        Assert.Equal(1, await TvItemCountAsync(cookie, albumId));
        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync($"/api/party/{viewToken}/media/{fileItemId}/preview")).StatusCode);
    }

    [Fact]
    public async Task Owner_Can_Reject_Pending_Item_And_It_Stays_Hidden()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var albumId = await CreateAlbumAsync(owner, "Party");
        var status = await EnablePartyAsync(owner, albumId, requireApproval: true);
        var viewToken = ViewTokenFromStatus(status);

        var anon = _factory.CreateClient();
        await UploadAsync(anon, UploadTokenFromStatus(status), ("a.jpg", ImageFixtures.JpegWithExif(), "image/jpeg"));
        var fileItemId = await FirstUploadFileIdAsync(owner, albumId);

        Assert.Equal(HttpStatusCode.NoContent,
            (await owner.PostAsync($"/api/albums/{albumId}/party-uploads/{fileItemId}/reject", null)).StatusCode);

        var uploads = await ListUploadsAsync(owner, albumId);
        Assert.Equal("rejected", uploads.GetProperty("items")[0].GetProperty("status").GetString());
        Assert.Equal(0, await PartyItemCountAsync(anon, viewToken));
    }

    [Fact]
    public async Task Disabling_Approval_Mode_Does_Not_Expose_Existing_Pending_But_New_Uploads_Are_Visible()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var albumId = await CreateAlbumAsync(owner, "Party");
        var status = await EnablePartyAsync(owner, albumId, requireApproval: true);
        var viewToken = ViewTokenFromStatus(status);
        var uploadToken = UploadTokenFromStatus(status);

        var anon = _factory.CreateClient();
        await UploadAsync(anon, uploadToken, ("pending.jpg", ImageFixtures.JpegWithExif(), "image/jpeg"));

        // Turn approval mode OFF (party stays enabled).
        var after = await (await owner.PatchAsJsonAsync(
            $"/api/albums/{albumId}/party-settings", new { enabled = true, requireUploadApproval = false }))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(after.GetProperty("requireUploadApproval").GetBoolean());

        // The already-pending upload is NOT auto-exposed.
        Assert.Equal(0, await PartyItemCountAsync(anon, viewToken));
        var uploads = await ListUploadsAsync(owner, albumId);
        Assert.Equal("pending", uploads.GetProperty("items")[0].GetProperty("status").GetString());

        // But a NEW upload after disabling is immediately visible.
        await UploadAsync(anon, uploadToken, ("live.jpg", ImageFixtures.JpegWithExif(), "image/jpeg"));
        Assert.Equal(1, await PartyItemCountAsync(anon, viewToken));
    }

    [Fact]
    public async Task Moderation_Is_Owner_Scoped_Cross_Owner_Is_404()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync("owner@example.com");
        var albumId = await CreateAlbumAsync(owner, "Party");
        var status = await EnablePartyAsync(owner, albumId);

        var anon = _factory.CreateClient();
        await UploadAsync(anon, UploadTokenFromStatus(status), ("a.jpg", ImageFixtures.JpegWithExif(), "image/jpeg"));
        var fileItemId = await FirstUploadFileIdAsync(owner, albumId);

        var (_, other) = await _factory.CreateAuthenticatedClientAsync("other@example.com");
        // Other owner cannot list this album's uploads.
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/albums/{albumId}/party-uploads")).StatusCode);
        // Other owner cannot hide/approve/reject this album's uploads.
        Assert.Equal(HttpStatusCode.NotFound,
            (await other.PostAsync($"/api/albums/{albumId}/party-uploads/{fileItemId}/hide", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await other.PostAsync($"/api/albums/{albumId}/party-uploads/{fileItemId}/approve", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await other.PostAsync($"/api/albums/{albumId}/party-uploads/{fileItemId}/restore", null)).StatusCode);
        // A random file id → 404.
        Assert.Equal(HttpStatusCode.NotFound,
            (await owner.PostAsync($"/api/albums/{albumId}/party-uploads/{Guid.NewGuid()}/hide", null)).StatusCode);
    }

    [Fact]
    public async Task Owner_Added_Content_Is_Never_Moderated_Away()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var albumId = await CreateAlbumAsync(owner, "Party");
        // Owner adds their own photo directly (NOT a guest upload).
        await AddPngAsync(owner, albumId, "owner.png");
        var status = await EnablePartyAsync(owner, albumId);
        var viewToken = ViewTokenFromStatus(status);

        var anon = _factory.CreateClient();
        // Owner content is visible and has NO moderation row.
        Assert.Equal(1, await PartyItemCountAsync(anon, viewToken));
        var uploads = await ListUploadsAsync(owner, albumId);
        Assert.Equal(0, uploads.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Party_Uploads_Dto_Exposes_No_Internals()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var albumId = await CreateAlbumAsync(owner, "Party");
        var status = await EnablePartyAsync(owner, albumId);
        var anon = _factory.CreateClient();
        await UploadAsync(anon, UploadTokenFromStatus(status), ("a.jpg", ImageFixtures.JpegWithExif(includeGps: true), "image/jpeg"));

        var raw = await (await owner.GetAsync($"/api/albums/{albumId}/party-uploads")).Content.ReadAsStringAsync();
        foreach (var needle in new[]
        {
            "StorageKey", "BlobObjectId", "sha256", "TokenHash", "UploadTokenHash",
            "physicalPath", "PayloadJson", "stack", "Exception", "gps", "latitude",
            "embedding", "vector", "faceId", "person",
        })
        {
            Assert.DoesNotContain(needle, raw, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Enabling_Approval_Requires_Auth_Anonymous_Cannot_Moderate()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var albumId = await CreateAlbumAsync(owner, "Party");
        var status = await EnablePartyAsync(owner, albumId);
        var anon = _factory.CreateClient();
        await UploadAsync(anon, UploadTokenFromStatus(status), ("a.jpg", ImageFixtures.JpegWithExif(), "image/jpeg"));
        var fileItemId = await FirstUploadFileIdAsync(owner, albumId);

        // Anonymous (no cookie) → 401 on every moderation surface.
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync($"/api/albums/{albumId}/party-uploads")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anon.PostAsync($"/api/albums/{albumId}/party-uploads/{fileItemId}/hide", null)).StatusCode);
    }

    // --- helpers ---

    private async Task<Guid> CreateAlbumAsync(HttpClient owner, string name)
    {
        var response = await owner.PostAsJsonAsync("/api/albums", new { name });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private async Task<JsonElement> EnablePartyAsync(HttpClient owner, Guid albumId, bool? requireApproval = null)
    {
        object payload = requireApproval is null
            ? new { enabled = true }
            : new { enabled = true, requireUploadApproval = requireApproval.Value };
        var resp = await owner.PatchAsJsonAsync($"/api/albums/{albumId}/party-settings", payload);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<JsonElement> ListUploadsAsync(HttpClient owner, Guid albumId)
        => await owner.GetFromJsonAsync<JsonElement>($"/api/albums/{albumId}/party-uploads");

    private async Task<Guid> FirstUploadFileIdAsync(HttpClient owner, Guid albumId)
    {
        var uploads = await ListUploadsAsync(owner, albumId);
        return uploads.GetProperty("items")[0].GetProperty("fileItemId").GetGuid();
    }

    private async Task<int> PartyItemCountAsync(HttpClient anon, string viewToken)
    {
        var items = await anon.GetFromJsonAsync<JsonElement>($"/api/party/{viewToken}/items");
        return items.GetProperty("items").GetArrayLength();
    }

    private async Task<int> TvItemCountAsync(string cookie, Guid albumId)
    {
        var detail = await TvJsonAsync(cookie, $"/api/tv/albums/{albumId}/items");
        return detail.GetProperty("items").GetArrayLength();
    }

    private static string ViewTokenFromStatus(JsonElement status)
        => status.GetProperty("partyUrl").GetString()!["/party/".Length..];

    private static string UploadTokenFromStatus(JsonElement status)
    {
        var url = status.GetProperty("uploadUrl").GetString()!;
        var rest = url["/party/".Length..];
        return rest[..rest.IndexOf("/upload", StringComparison.Ordinal)];
    }

    private static Task<HttpResponseMessage> UploadAsync(
        HttpClient anon, string uploadToken, params (string Name, byte[] Bytes, string ContentType)[] files)
    {
        var multipart = new MultipartFormDataContent();
        foreach (var (name, bytes, contentType) in files)
        {
            var part = new ByteArrayContent(bytes);
            part.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            multipart.Add(part, "file", name);
        }
        return anon.PostAsync($"/api/party/{uploadToken}/upload", multipart);
    }

    private async Task AddPngAsync(HttpClient owner, Guid albumId, string name)
    {
        var part = new ByteArrayContent(ImageFixtures.PlainPng());
        part.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        var multipart = new MultipartFormDataContent { { part, "file", name } };
        var resp = await owner.PostAsync("/api/files", multipart);
        resp.EnsureSuccessStatusCode();
        var fileId = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        (await owner.PostAsJsonAsync($"/api/albums/{albumId}/items", new { fileItemId = fileId }))
            .EnsureSuccessStatusCode();
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
                // Atomic first pairing: approval creates the owner's PIN when
                // missing; ignored for owners who already have one.
                personalPin = "123456",
                personalPinConfirmation = "123456",
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
