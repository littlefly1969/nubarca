using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Tv;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace NubArca.Api.Tests.Tv;

// TV "Beauty Lab" (Laboratorio bellezza): the grant-gated projection of the
// owner-private Aesthetics Lab + the QR mobile-upload capability. Covers
// (1) authorization — every TV endpoint needs the TV session cookie AND a live
// personal unlock grant; owner web auth can't reach it; cross-owner ids are safe
// 404s; (2) NO-LEAK DTOs — no blob id / storage key / sha; (3) delegation to the
// SAME lab services (list/detail/remove/analyze); (4) the upload-session token:
// hash-only, owner/purpose scoped, expiring/revocable, upload-only, bounded, and
// isolated from Party. The analysis feature stays behind the fake sidecar.
public sealed class TvBeautyLabTests : IDisposable
{
    private const string Pin = "123456";

    private readonly SqliteWebApplicationFactory _factory = new(
        new Dictionary<string, string?>
        {
            ["RateLimits:TvPersonalUnlock:PermitLimit"] = "1000",
            ["RateLimits:BeautyLabUpload:PermitLimit"] = "1000",
            ["HumanAesExpert:Enabled"] = "false",
            ["HumanAesExpert:SidecarBaseUrl"] = "http://fake:8091",
        },
        poolHost: true);

    public TvBeautyLabTests() => _factory.EnsureDatabaseCreated();

    public void Dispose() => _factory.Dispose();

    // ── authorization ────────────────────────────────────────────────────────

    [Fact]
    public async Task Every_Beauty_Lab_Endpoint_Requires_A_Tv_Session()
    {
        var anon = _factory.CreateClient();
        var id = Guid.NewGuid();

        foreach (var url in new[]
        {
            "/api/tv/personal/aesthetics/items",
            $"/api/tv/personal/aesthetics/items/{id}",
            $"/api/tv/personal/aesthetics/items/{id}/thumbnail",
            $"/api/tv/personal/aesthetics/items/{id}/preview",
            $"/api/tv/personal/aesthetics/upload-sessions/{id}",
        })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync(url)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anon.PostAsJsonAsync("/api/tv/personal/aesthetics/analyses", new { itemIds = new[] { id } })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anon.PostAsync("/api/tv/personal/aesthetics/upload-sessions", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anon.PostAsync($"/api/tv/personal/aesthetics/runs/{id}/cancel", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anon.PostAsync($"/api/tv/personal/aesthetics/runs/{id}/retry", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anon.DeleteAsync($"/api/tv/personal/aesthetics/items/{id}")).StatusCode);
    }

    [Fact]
    public async Task Owner_Web_Auth_Alone_Cannot_Reach_The_Beauty_Lab_Projection()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        // The owner's normal auth cookie is not a TV session → 401, never 200.
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await owner.GetAsync("/api/tv/personal/aesthetics/items")).StatusCode);
    }

    [Fact]
    public async Task Paired_Session_Without_A_Grant_Is_Locked()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var cookie = await PairTvAsync(owner);

        var locked = await TvSendAsync(cookie, HttpMethod.Get, "/api/tv/personal/aesthetics/items");
        Assert.Equal(HttpStatusCode.Forbidden, locked.StatusCode);
        Assert.Contains("locked", await locked.Content.ReadAsStringAsync());
        AssertNoStore(locked);
    }

    [Fact]
    public async Task A_Beauty_Lab_Upload_Token_Cannot_Be_Used_As_A_Personal_Grant()
    {
        // The upload capability is UPLOAD-ONLY: presenting its token as the
        // personal unlock header must never authorize a read/list TV endpoint.
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var cookie = await PairTvAsync(owner);
        var grant = await UnlockTokenAsync(cookie);
        var created = await CreateUploadSessionAsync(cookie, grant);
        var rawToken = TokenFromUploadUrl(created);

        var abused = await TvSendAsync(cookie, HttpMethod.Get, "/api/tv/personal/aesthetics/items", grant: rawToken);
        Assert.Equal(HttpStatusCode.Forbidden, abused.StatusCode);
    }

    // ── delegation to the shared lab services + no-leak DTOs ──────────────────

    [Fact]
    public async Task Uploaded_Image_Appears_In_The_Tv_List_With_No_Internal_Fields()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var cookie = await PairTvAsync(owner);
        var grant = await UnlockTokenAsync(cookie);
        var created = await CreateUploadSessionAsync(cookie, grant);
        var rawToken = TokenFromUploadUrl(created);

        var upload = await UploadFileAsync(rawToken, Png(24), "portrait.png");
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        var uploadJson = JsonDocument.Parse(await upload.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(1, uploadJson.GetProperty("accepted").GetInt32());
        Assert.Equal(0, uploadJson.GetProperty("rejected").GetInt32());

        var list = await TvSendAsync(cookie, HttpMethod.Get, "/api/tv/personal/aesthetics/items", grant);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        AssertNoStore(list);
        var body = await list.Content.ReadAsStringAsync();
        var items = JsonDocument.Parse(body).RootElement.GetProperty("items");
        Assert.Equal(1, items.GetArrayLength());

        // No storage/AI internals may ever appear in the TV projection.
        foreach (var forbidden in new[] { "blobObjectId", "blobId", "storageKey", "sha", "logicalContainerKey", "payloadJson", "rawOutput" })
        {
            Assert.DoesNotContain(forbidden, body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Upload_Uses_The_Lab_Store_And_Creates_No_Gallery_File_Item()
    {
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync();
        var cookie = await PairTvAsync(owner);
        var grant = await UnlockTokenAsync(cookie);
        var created = await CreateUploadSessionAsync(cookie, grant);
        var rawToken = TokenFromUploadUrl(created);

        await UploadFileAsync(rawToken, Png(24), "portrait.png");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // Landed in the isolated lab, NOT in Files/Gallery.
        Assert.Equal(1, await db.AestheticLabItems.CountAsync(i => i.OwnerUserId == ownerId));
        Assert.Equal(0, await db.FileItems.CountAsync(f => f.OwnerUserId == ownerId));
    }

    [Fact]
    public async Task Cross_Owner_Item_Id_Returns_Safe_Not_Found()
    {
        // Owner A adds a lab item; Owner B (grant-authorized) must get 404, never
        // its detail/derivatives.
        var (ownerAId, ownerA) = await _factory.CreateAuthenticatedClientAsync("a@example.com");
        var cookieA = await PairTvAsync(ownerA);
        var grantA = await UnlockTokenAsync(cookieA);
        var createdA = await CreateUploadSessionAsync(cookieA, grantA);
        await UploadFileAsync(TokenFromUploadUrl(createdA), Png(24), "a.png");

        Guid itemAId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            itemAId = (await db.AestheticLabItems.SingleAsync(i => i.OwnerUserId == ownerAId)).Id;
        }

        var (_, ownerB) = await _factory.CreateAuthenticatedClientAsync("b@example.com");
        var cookieB = await PairTvAsync(ownerB);
        var grantB = await UnlockTokenAsync(cookieB);

        Assert.Equal(HttpStatusCode.NotFound,
            (await TvSendAsync(cookieB, HttpMethod.Get, $"/api/tv/personal/aesthetics/items/{itemAId}", grantB)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await TvSendAsync(cookieB, HttpMethod.Get, $"/api/tv/personal/aesthetics/items/{itemAId}/preview", grantB)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await TvSendAsync(cookieB, HttpMethod.Delete, $"/api/tv/personal/aesthetics/items/{itemAId}", grantB)).StatusCode);
    }

    [Fact]
    public async Task Remove_Deletes_Via_The_Shared_Lab_Service()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var cookie = await PairTvAsync(owner);
        var grant = await UnlockTokenAsync(cookie);
        var created = await CreateUploadSessionAsync(cookie, grant);
        await UploadFileAsync(TokenFromUploadUrl(created), Png(24), "portrait.png");

        var detailId = (await ListItemIdsAsync(cookie, grant)).Single();
        var removed = await TvSendAsync(cookie, HttpMethod.Delete, $"/api/tv/personal/aesthetics/items/{detailId}", grant);
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Empty(await ListItemIdsAsync(cookie, grant));
    }

    [Fact]
    public async Task Analyses_With_Feature_Disabled_Skips_Every_Item_And_Enqueues_Nothing()
    {
        // Same controlled-unavailable semantics as the web lab (feature off here).
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var cookie = await PairTvAsync(owner);
        var grant = await UnlockTokenAsync(cookie);
        var created = await CreateUploadSessionAsync(cookie, grant);
        await UploadFileAsync(TokenFromUploadUrl(created), Png(24), "portrait.png");
        var itemId = (await ListItemIdsAsync(cookie, grant)).Single();

        var resp = await TvSendAsync(cookie, HttpMethod.Post, "/api/tv/personal/aesthetics/analyses",
            grant, new { itemIds = new[] { itemId } });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(0, json.GetProperty("enqueued").GetArrayLength());
        Assert.Equal(1, json.GetProperty("skipped").GetArrayLength());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.AestheticAnalysisRuns.AnyAsync());
    }

    [Fact]
    public async Task Cancel_And_Retry_Unknown_Run_Are_Safe_Not_Found()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var cookie = await PairTvAsync(owner);
        var grant = await UnlockTokenAsync(cookie);
        var missing = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.NotFound,
            (await TvSendAsync(cookie, HttpMethod.Post, $"/api/tv/personal/aesthetics/runs/{missing}/cancel", grant)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await TvSendAsync(cookie, HttpMethod.Post, $"/api/tv/personal/aesthetics/runs/{missing}/retry", grant)).StatusCode);
    }

    // ── upload-session token security ─────────────────────────────────────────

    [Fact]
    public async Task Session_Token_Is_Stored_Hash_Only_And_Owner_Scoped()
    {
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync();
        var cookie = await PairTvAsync(owner);
        var grant = await UnlockTokenAsync(cookie);
        var created = await CreateUploadSessionAsync(cookie, grant);
        var rawToken = TokenFromUploadUrl(created);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.AestheticUploadSessions.SingleAsync();
        Assert.Equal(ownerId, row.OwnerUserId);
        // Only the HASH is persisted; the raw token never appears in any column.
        Assert.Equal(Sha256Hex(rawToken), row.TokenHash);
        Assert.NotEqual(rawToken, row.TokenHash);
        Assert.True(row.ExpiresAt > row.CreatedAt);
        Assert.Null(row.RevokedAt);
    }

    [Fact]
    public async Task Revoked_And_Expired_Tokens_Cannot_Upload()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var cookie = await PairTvAsync(owner);
        var grant = await UnlockTokenAsync(cookie);

        // Revoked.
        var revokedSession = await CreateUploadSessionAsync(cookie, grant);
        var revokedToken = TokenFromUploadUrl(revokedSession);
        var sessionId = revokedSession.GetProperty("id").GetGuid();
        var revoke = await TvSendAsync(cookie, HttpMethod.Post,
            $"/api/tv/personal/aesthetics/upload-sessions/{sessionId}/revoke", grant);
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        // The UPLOAD is refused (generic 404) once revoked...
        Assert.Equal(HttpStatusCode.NotFound,
            (await UploadFileAsync(revokedToken, Png(24), "x.png")).StatusCode);
        // ...but the mobile page can still READ the lifecycle state so it can show
        // a clear "revoked" message (200, no upload authority granted).
        var revokedState = await _factory.CreateClient().GetAsync($"/api/beauty-lab-upload/{revokedToken}");
        Assert.Equal(HttpStatusCode.OK, revokedState.StatusCode);
        Assert.Equal(AestheticUploadSessionStates.Revoked,
            JsonDocument.Parse(await revokedState.Content.ReadAsStringAsync())
                .RootElement.GetProperty("status").GetString());

        // Expired (force ExpiresAt into the past).
        var expiredSession = await CreateUploadSessionAsync(cookie, grant);
        var expiredToken = TokenFromUploadUrl(expiredSession);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.AestheticUploadSessions
                .Where(s => s.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.ExpiresAt, DateTime.UtcNow.AddMinutes(-1)));
        }
        Assert.Equal(HttpStatusCode.NotFound,
            (await UploadFileAsync(expiredToken, Png(24), "x.png")).StatusCode);
        var expiredState = await _factory.CreateClient().GetAsync($"/api/beauty-lab-upload/{expiredToken}");
        Assert.Equal(AestheticUploadSessionStates.Expired,
            JsonDocument.Parse(await expiredState.Content.ReadAsStringAsync())
                .RootElement.GetProperty("status").GetString());

        // A genuinely UNKNOWN token is an indistinguishable 404 on both surfaces.
        Assert.Equal(HttpStatusCode.NotFound,
            (await _factory.CreateClient().GetAsync("/api/beauty-lab-upload/nope-not-real")).StatusCode);
    }

    [Fact]
    public async Task File_Count_Limit_Is_Enforced_Per_Session()
    {
        using var factory = new SqliteWebApplicationFactory(new Dictionary<string, string?>
        {
            ["RateLimits:TvPersonalUnlock:PermitLimit"] = "1000",
            ["RateLimits:BeautyLabUpload:PermitLimit"] = "1000",
            ["HumanAesExpert:UploadSessionMaxFiles"] = "1",
        });
        factory.EnsureDatabaseCreated();
        var (_, owner) = await factory.CreateAuthenticatedClientAsync();
        var cookie = await PairTvAsync(owner, factory);
        var grant = await UnlockTokenAsync(cookie, factory);
        var created = await CreateUploadSessionAsync(cookie, grant, factory);
        var rawToken = TokenFromUploadUrl(created);

        // Two DIFFERENT images in one request; only the first fits the cap.
        var resp = await UploadFilesAsync(rawToken, factory,
            (Png(24), "a.png"), (Png(32), "b.png"));
        var json = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(1, json.GetProperty("accepted").GetInt32());
        Assert.Equal(1, json.GetProperty("rejected").GetInt32());
    }

    [Fact]
    public async Task Duplicate_Bytes_Are_Deduplicated_In_The_Lab()
    {
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync();
        var cookie = await PairTvAsync(owner);
        var grant = await UnlockTokenAsync(cookie);
        var created = await CreateUploadSessionAsync(cookie, grant);
        var rawToken = TokenFromUploadUrl(created);

        var bytes = Png(24);
        await UploadFileAsync(rawToken, bytes, "one.png");
        await UploadFileAsync(rawToken, bytes, "two.png");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // Same content-addressed blob → a single lab item (idempotent add).
        Assert.Equal(1, await db.AestheticLabItems.CountAsync(i => i.OwnerUserId == ownerId));
    }

    [Fact]
    public async Task Session_Status_Reports_Safe_Aggregate_Counts()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var cookie = await PairTvAsync(owner);
        var grant = await UnlockTokenAsync(cookie);
        var created = await CreateUploadSessionAsync(cookie, grant);
        var sessionId = created.GetProperty("id").GetGuid();
        var rawToken = TokenFromUploadUrl(created);

        await UploadFileAsync(rawToken, Png(24), "ok.png");
        await UploadFileAsync(rawToken, Encoding.UTF8.GetBytes("<html>not an image</html>"), "evil.png");

        var status = await TvSendAsync(cookie, HttpMethod.Get,
            $"/api/tv/personal/aesthetics/upload-sessions/{sessionId}", grant);
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        AssertNoStore(status);
        var json = JsonDocument.Parse(await status.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(1, json.GetProperty("accepted").GetInt32());
        Assert.Equal(1, json.GetProperty("rejected").GetInt32());
        // No token surfaces in the status DTO.
        Assert.DoesNotContain(rawToken, json.GetRawText());
    }

    [Fact]
    public async Task Party_And_Beauty_Lab_Tokens_Do_Not_Cross()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var cookie = await PairTvAsync(owner);
        var grant = await UnlockTokenAsync(cookie);
        var created = await CreateUploadSessionAsync(cookie, grant);
        var beautyToken = TokenFromUploadUrl(created);

        // A Beauty Lab token can never satisfy the Party upload endpoint.
        var onParty = await UploadFileToAsync($"/api/party/{beautyToken}/upload", beautyToken, Png(24), "x.png");
        Assert.Equal(HttpStatusCode.NotFound, onParty.StatusCode);

        // A random/unknown token can never satisfy the Beauty Lab endpoint.
        Assert.Equal(HttpStatusCode.NotFound,
            (await UploadFileAsync("not-a-real-token", Png(24), "x.png")).StatusCode);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static byte[] Png(int dim)
    {
        using var img = new Image<Rgba32>(dim, dim);
        using var ms = new MemoryStream();
        img.Save(ms, new PngEncoder());
        return ms.ToArray();
    }

    private static string Sha256Hex(string token)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private async Task<JsonElement> CreateUploadSessionAsync(
        string cookie, string grant, SqliteWebApplicationFactory? factory = null)
    {
        var resp = await TvSendAsync(cookie, HttpMethod.Post,
            "/api/tv/personal/aesthetics/upload-sessions", grant, factory: factory);
        resp.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static string TokenFromUploadUrl(JsonElement created)
    {
        var url = created.GetProperty("uploadUrl").GetString()!;
        return url[(url.LastIndexOf('/') + 1)..];
    }

    private async Task<List<Guid>> ListItemIdsAsync(string cookie, string grant)
    {
        var resp = await TvSendAsync(cookie, HttpMethod.Get, "/api/tv/personal/aesthetics/items", grant);
        resp.EnsureSuccessStatusCode();
        var items = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.GetProperty("items");
        return items.EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToList();
    }

    private Task<HttpResponseMessage> UploadFileAsync(string token, byte[] bytes, string name, SqliteWebApplicationFactory? factory = null)
        => UploadFilesAsync(token, factory, (bytes, name));

    private Task<HttpResponseMessage> UploadFilesAsync(
        string token, SqliteWebApplicationFactory? factory, params (byte[] Bytes, string Name)[] files)
        => UploadFileToAsync($"/api/beauty-lab-upload/{token}/files", token, factory, files);

    private Task<HttpResponseMessage> UploadFileToAsync(string url, string token, byte[] bytes, string name)
        => UploadFileToAsync(url, token, null, (bytes, name));

    private Task<HttpResponseMessage> UploadFileToAsync(
        string url, string token, SqliteWebApplicationFactory? factory, params (byte[] Bytes, string Name)[] files)
    {
        var multipart = new MultipartFormDataContent();
        foreach (var (bytes, name) in files)
        {
            var part = new ByteArrayContent(bytes);
            part.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            multipart.Add(part, "file", name);
        }
        return (factory ?? _factory).CreateClient().PostAsync(url, multipart);
    }

    private async Task<string> PairTvAsync(HttpClient owner, SqliteWebApplicationFactory? factory = null)
    {
        var f = factory ?? _factory;
        var tvClient = f.CreateClient();
        var start = await tvClient.PostAsync("/api/tv/pairing/start", null);
        start.EnsureSuccessStatusCode();
        var started = (await start.Content.ReadFromJsonAsync<TvPairingStartedDto>())!;

        (await owner.PostAsJsonAsync(
            $"/api/tv/pairing/{started.PublicCode}/approve",
            new { pairingSecret = started.PairingSecret, personalPin = Pin, personalPinConfirmation = Pin }))
            .EnsureSuccessStatusCode();

        var pollRequest = new HttpRequestMessage(
            HttpMethod.Get, $"/api/tv/pairing/{started.PublicCode}/status");
        pollRequest.Headers.Add(TvPairingService.PairingSecretHeader, started.PairingSecret);
        var poll = await tvClient.SendAsync(pollRequest);
        poll.EnsureSuccessStatusCode();
        return poll.Headers.GetValues("Set-Cookie").Single();
    }

    private async Task<string> UnlockTokenAsync(string setCookie, SqliteWebApplicationFactory? factory = null)
    {
        var response = await TvSendAsync(
            setCookie, HttpMethod.Post, "/api/tv/personal/unlock", json: new { pin = Pin }, factory: factory);
        response.EnsureSuccessStatusCode();
        var dto = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        return dto.GetProperty("unlockToken").GetString()!;
    }

    private Task<HttpResponseMessage> TvSendAsync(
        string setCookie, HttpMethod method, string url, string? grant = null, object? json = null,
        SqliteWebApplicationFactory? factory = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("Cookie", $"{TvPairingService.CookieName}={CookieValue(setCookie)}");
        if (grant is not null)
        {
            request.Headers.Add(TvPersonalAreaService.UnlockHeader, grant);
        }
        if (json is not null)
        {
            request.Content = JsonContent.Create(json);
        }
        return (factory ?? _factory).CreateClient().SendAsync(request);
    }

    private static void AssertNoStore(HttpResponseMessage response)
        => Assert.Contains("no-store", response.Headers.CacheControl?.ToString() ?? "");

    private static string CookieValue(string setCookie)
    {
        var value = setCookie.Split(';', 2)[0];
        return value[(value.IndexOf('=') + 1)..];
    }
}
