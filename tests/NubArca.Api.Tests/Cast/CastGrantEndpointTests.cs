using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Access;
using NubArca.Api.Cast;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Metadata;
using NubArca.Api.Storage;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Tests.Metadata;
using Xunit;

namespace NubArca.Api.Tests.Cast;

// NUBARCA-GOOGLE-CAST-01 — the HLS-mode Cast surface: grant lifecycle,
// grant-scoped playback, and the authorization gates that must hold on EVERY
// request rather than only at mint time.
//
// The progressive/Range contract has its own file (CastDirectVideoTests), and
// CORS has its own (CastCorsTests), because both need a differently-configured
// host.
public sealed class CastGrantEndpointTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public CastGrantEndpointTests()
    {
        _factory = new SqliteWebApplicationFactory(new Dictionary<string, string?>
        {
            ["Media:VideoHlsProvider"] = "ffmpeg",
        }, poolHost: true);
        _factory.EnsureDatabaseCreated();
    }

    public void Dispose() => _factory.Dispose();

    // ── fixtures ────────────────────────────────────────────────────────────

    // `marker` varies the content so two uploads get distinct sha256 values and
    // therefore distinct HLS ladders — which is what makes "grant A cannot reach
    // file B" an observable fact rather than a claim.
    internal static byte[] BulkyMp4(byte marker = 0)
    {
        var head = ImageFixtures.MinimalMp4();
        var bytes = new byte[1024];
        Array.Copy(head, bytes, head.Length);
        for (var i = head.Length; i < bytes.Length; i++) bytes[i] = (byte)((i + marker) & 0xFF);
        return bytes;
    }

    internal static async Task<FileItem> UploadAsync(
        SqliteWebApplicationFactory factory, Guid ownerId, byte[] bytes, string name, string mime)
    {
        using var scope = factory.Services.CreateScope();
        var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
        return await files.CreateAsync(ownerId, null, name, mime, new MemoryStream(bytes));
    }

    private Task<FileItem> UploadVideoAsync(Guid ownerId, string name = "clip.mp4", byte marker = 0)
        => UploadAsync(_factory, ownerId, BulkyMp4(marker), name, "video/mp4");

    // Publish a shape-correct ladder plus the ready row, as a completed
    // generation run would have. `segmentByte` labels the media segments so a
    // test can tell one file's ladder from another's.
    private async Task PublishReadyLadderAsync(FileItem file, byte segmentByte = 0x02)
    {
        Guid blobId;
        string sha;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var blob = await db.BlobObjects.AsNoTracking().SingleAsync(b => b.Id == file.BlobObjectId);
            (blobId, sha) = (blob.Id, blob.Sha256);
        }

        var hls = _factory.Services.GetRequiredService<HlsDerivativeStorage>();
        var staging = hls.CreateStagingDirectory();
        File.WriteAllText(
            Path.Combine(staging, "master.m3u8"),
            "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=5000000,RESOLUTION=1920x1080\nhigh/stream.m3u8\n"
            + "#EXT-X-STREAM-INF:BANDWIDTH=1000000,RESOLUTION=854x480\nlow/stream.m3u8\n");
        foreach (var name in new[] { "high", "low" })
        {
            var dir = Path.Combine(staging, name);
            Directory.CreateDirectory(dir);
            File.WriteAllText(
                Path.Combine(dir, "stream.m3u8"),
                "#EXTM3U\n#EXT-X-MAP:URI=\"init_0.mp4\"\n#EXTINF:4.0,\nseg-0.m4s\n#EXT-X-ENDLIST\n");
            File.WriteAllBytes(Path.Combine(dir, "init_0.mp4"), [0x00, 0x01]);
            File.WriteAllBytes(Path.Combine(dir, "seg-0.m4s"), [segmentByte, 0x03]);
        }
        hls.Publish(sha, staging);

        using var writeScope = _factory.Services.CreateScope();
        var writeDb = writeScope.ServiceProvider.GetRequiredService<AppDbContext>();
        writeDb.BlobHlsDerivatives.Add(new BlobHlsDerivative
        {
            Id = Guid.NewGuid(),
            BlobObjectId = blobId,
            Status = VideoHlsStatuses.Ready,
            Version = FfmpegVideoHlsTranscoder.Version,
            CreatedAt = DateTime.UtcNow,
            ReadyAt = DateTime.UtcNow,
        });
        await writeDb.SaveChangesAsync();
    }

    private sealed record Grant(
        Guid GrantId, string ContentPath, string PosterPath, string ContentType,
        string Mode, string StreamType, DateTime ExpiresAt);

    private static async Task<Grant> ReadGrantAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return new Grant(
            json.GetProperty("grantId").GetGuid(),
            json.GetProperty("contentPath").GetString()!,
            json.GetProperty("posterPath").GetString()!,
            json.GetProperty("contentType").GetString()!,
            json.GetProperty("mode").GetString()!,
            json.GetProperty("streamType").GetString()!,
            json.GetProperty("expiresAt").GetDateTime());
    }

    // Mint a ready-to-play grant for a freshly uploaded video.
    private async Task<(Guid OwnerId, HttpClient Client, FileItem File, Grant Grant)> CastableAsync(
        string email = "owner@example.com")
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync(email);
        var file = await UploadVideoAsync(owner);
        await PublishReadyLadderAsync(file);
        var response = await client.PostAsync($"/api/cast/videos/{file.Id}/grant", null);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (owner, client, file, await ReadGrantAsync(response));
    }

    private static string TokenOf(string path)
    {
        var marker = path.IndexOf("?token=", StringComparison.Ordinal);
        Assert.True(marker >= 0, $"no token in '{path}'");
        return path[(marker + "?token=".Length)..];
    }

    // ── permission gate ─────────────────────────────────────────────────────

    [Fact]
    public async Task Creating_A_Grant_Requires_Cast_Access()
    {
        var (ownerId, client) = await _factory.CreatePermissionClientAsync(
            "nocast@example.com", Permissions.PeopleAccess);
        var file = await UploadVideoAsync(ownerId);
        await PublishReadyLadderAsync(file);

        var response = await client.PostAsync($"/api/cast/videos/{file.Id}/grant", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Member_Holds_Cast_Access_By_Default()
    {
        var (_, _, _, grant) = await CastableAsync();
        Assert.Equal(CastPlaybackModes.Hls, grant.Mode);
    }

    [Fact]
    public async Task Creating_A_Grant_Without_Authentication_Is_401()
    {
        var anonymous = _factory.CreateClient();
        var response = await anonymous.PostAsync($"/api/cast/videos/{Guid.NewGuid()}/grant", null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── ownership ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Another_Users_Video_Cannot_Be_Granted()
    {
        var alice = await _factory.SeedUserAsync("alice@example.com");
        var aliceFile = await UploadVideoAsync(alice, "alice.mp4");
        await PublishReadyLadderAsync(aliceFile);

        var (_, bob) = await _factory.CreateAuthenticatedClientAsync("bob@example.com");
        var response = await bob.PostAsync($"/api/cast/videos/{aliceFile.Id}/grant", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_NonVideo_Cannot_Be_Granted()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var photo = await UploadAsync(
            _factory, owner, ImageFixtures.JpegWithExif(), "photo.jpg", "image/jpeg");

        var response = await client.PostAsync($"/api/cast/videos/{photo.Id}/grant", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── HLS preparation ─────────────────────────────────────────────────────

    [Fact]
    public async Task An_Unprepared_Ladder_Answers_202_With_RetryAfter()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadVideoAsync(owner);

        var response = await client.PostAsync($"/api/cast/videos/{file.Id}/grant", null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("Retry-After", out _));
        // Nothing is minted for a video that cannot be played yet.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Empty(await db.CastMediaGrants.AsNoTracking().ToListAsync());
    }

    // ── token storage ───────────────────────────────────────────────────────

    [Fact]
    public async Task Only_The_Token_Digest_Is_Persisted()
    {
        var (_, _, _, grant) = await CastableAsync();
        var token = TokenOf(grant.ContentPath);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.CastMediaGrants.AsNoTracking().SingleAsync();

        Assert.Equal(64, row.TokenHash.Length);
        Assert.NotEqual(token, row.TokenHash);
        Assert.DoesNotContain(token, row.TokenHash, StringComparison.Ordinal);
        // A 32-byte secret, base64url encoded, is 43 characters.
        Assert.Equal(43, token.Length);
        Assert.Matches("^[A-Za-z0-9_-]+$", token);
    }

    [Fact]
    public async Task The_Raw_Token_Never_Reaches_The_Audit_Trail()
    {
        var (_, _, _, grant) = await CastableAsync();
        var token = TokenOf(grant.ContentPath);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var entries = await db.AuditLogs.AsNoTracking()
            .Where(a => a.Action.StartsWith("cast."))
            .ToListAsync();

        var created = Assert.Single(entries);
        Assert.Equal(NubArca.Api.Audit.AuditActions.CastGrantCreate, created.Action);
        Assert.DoesNotContain(token, created.MetadataJson ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("token", created.MetadataJson ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(grant.GrantId.ToString(), created.MetadataJson ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    // ── grant-scoped playback ───────────────────────────────────────────────

    [Fact]
    public async Task The_Cast_Master_Uses_Only_GrantScoped_Urls()
    {
        var (_, _, file, grant) = await CastableAsync();
        var anonymous = _factory.CreateClient();

        var response = await anonymous.GetAsync(grant.ContentPath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var token = TokenOf(grant.ContentPath);
        var basePath = $"/api/cast/media/{grant.GrantId}";

        Assert.Contains($"{basePath}/hls/high/stream.m3u8?token={token}", body, StringComparison.Ordinal);
        Assert.Contains($"{basePath}/hls/low/stream.m3u8?token={token}", body, StringComparison.Ordinal);
        // No owner-authenticated URL may survive the rewrite.
        Assert.DoesNotContain("/api/files/", body, StringComparison.Ordinal);
        Assert.DoesNotContain(file.Id.ToString(), body, StringComparison.OrdinalIgnoreCase);
        // Tag lines are preserved.
        Assert.Contains("#EXT-X-STREAM-INF", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_Cast_Variant_Signs_Segments_And_The_Init_Map()
    {
        var (_, _, _, grant) = await CastableAsync();
        var anonymous = _factory.CreateClient();
        var token = TokenOf(grant.ContentPath);
        var basePath = $"/api/cast/media/{grant.GrantId}";

        var response = await anonymous.GetAsync($"{basePath}/hls/high/stream.m3u8?token={token}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        // HLS resolves a relative URI against the playlist URL and DROPS the
        // query, so an unsigned segment reference would stall the receiver.
        Assert.Contains($"{basePath}/hls/high/seg-0.m4s?token={token}", body, StringComparison.Ordinal);
        Assert.Contains(
            $"#EXT-X-MAP:URI=\"{basePath}/hls/high/init_0.mp4?token={token}\"",
            body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("high/seg-0.m4s", "video/iso.segment")]
    [InlineData("high/init_0.mp4", "video/mp4")]
    [InlineData("low/stream.m3u8", "application/vnd.apple.mpegurl")]
    public async Task Ladder_Files_Are_Served_With_The_Right_Type(string relative, string expected)
    {
        var (_, _, _, grant) = await CastableAsync();
        var anonymous = _factory.CreateClient();
        var token = TokenOf(grant.ContentPath);

        var response = await anonymous.GetAsync(
            $"/api/cast/media/{grant.GrantId}/hls/{relative}?token={token}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expected, response.Content.Headers.ContentType?.MediaType);
        Assert.NotEmpty(await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task The_Poster_Is_GrantScoped()
    {
        var (_, _, _, grant) = await CastableAsync();
        var anonymous = _factory.CreateClient();

        var ok = await anonymous.GetAsync(grant.PosterPath);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.StartsWith("image/", ok.Content.Headers.ContentType!.MediaType!, StringComparison.Ordinal);

        var noToken = await anonymous.GetAsync($"/api/cast/media/{grant.GrantId}/poster");
        Assert.Equal(HttpStatusCode.NotFound, noToken.StatusCode);
    }

    // ── path hardening ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("high/evil.sh")]
    [InlineData("high/seg-x.m4s")]
    [InlineData("medium/stream.m3u8")]
    [InlineData("high/master.m3u8")]
    [InlineData("..%2F..%2Fmaster.m3u8")]
    [InlineData("high/%2e%2e%2fmaster.m3u8")]
    public async Task Segment_Traversal_And_Unknown_Names_Are_Rejected(string relative)
    {
        var (_, _, _, grant) = await CastableAsync();
        var anonymous = _factory.CreateClient();
        var token = TokenOf(grant.ContentPath);

        var response = await anonymous.GetAsync(
            $"/api/cast/media/{grant.GrantId}/hls/{relative}?token={token}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // A grant addresses exactly one video. There is no route shape that takes a
    // second file id, and a grant's own secret does not unlock a sibling grant —
    // so the bytes a grant yields are always its own file's bytes.
    [Fact]
    public async Task A_Grant_Reaches_Only_Its_Own_File()
    {
        var (owner, client, _, first) = await CastableAsync();
        var other = await UploadVideoAsync(owner, "other.mp4", marker: 7);
        await PublishReadyLadderAsync(other, segmentByte: 0x55);
        var second = await ReadGrantAsync(
            await client.PostAsync($"/api/cast/videos/{other.Id}/grant", null));

        var anonymous = _factory.CreateClient();
        var firstToken = TokenOf(first.ContentPath);
        var secondToken = TokenOf(second.ContentPath);

        var firstSegment = await anonymous.GetAsync(
            $"/api/cast/media/{first.GrantId}/hls/high/seg-0.m4s?token={firstToken}");
        var secondSegment = await anonymous.GetAsync(
            $"/api/cast/media/{second.GrantId}/hls/high/seg-0.m4s?token={secondToken}");

        Assert.Equal([0x02, 0x03], await firstSegment.Content.ReadAsByteArrayAsync());
        Assert.Equal([0x55, 0x03], await secondSegment.Content.ReadAsByteArrayAsync());

        // One grant's secret is useless against the other's id.
        var crossed = await anonymous.GetAsync(
            $"/api/cast/media/{second.GrantId}/video?token={firstToken}");
        Assert.Equal(HttpStatusCode.NotFound, crossed.StatusCode);
    }

    // ── token / lifetime gates ──────────────────────────────────────────────

    [Fact]
    public async Task A_Wrong_Token_Is_404()
    {
        var (_, _, _, grant) = await CastableAsync();
        var anonymous = _factory.CreateClient();

        var response = await anonymous.GetAsync(
            $"/api/cast/media/{grant.GrantId}/video?token=not-the-secret");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task An_Unknown_Grant_Is_404()
    {
        var (_, _, _, grant) = await CastableAsync();
        var anonymous = _factory.CreateClient();
        var token = TokenOf(grant.ContentPath);

        var response = await anonymous.GetAsync(
            $"/api/cast/media/{Guid.NewGuid()}/video?token={token}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task An_Expired_Grant_Is_404()
    {
        var (_, _, _, grant) = await CastableAsync();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.CastMediaGrants.SingleAsync(g => g.Id == grant.GrantId);
            row.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }

        var anonymous = _factory.CreateClient();
        var response = await anonymous.GetAsync(grant.ContentPath);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Revoking_Stops_The_Next_Request_And_Is_Idempotent()
    {
        var (_, client, _, grant) = await CastableAsync();
        var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync(grant.ContentPath)).StatusCode);

        var first = await client.DeleteAsync($"/api/cast/grants/{grant.GrantId}");
        var second = await client.DeleteAsync($"/api/cast/grants/{grant.GrantId}");

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound, (await anonymous.GetAsync(grant.ContentPath)).StatusCode);
    }

    [Fact]
    public async Task Another_User_Cannot_Revoke_Someone_Elses_Grant()
    {
        var (_, _, _, grant) = await CastableAsync();
        var (_, mallory) = await _factory.CreateAuthenticatedClientAsync("mallory@example.com");

        // 204 (nothing leaks about whether the id exists) — and the grant is
        // still playable afterwards, which is what actually matters.
        var response = await mallory.DeleteAsync($"/api/cast/grants/{grant.GrantId}");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync(grant.ContentPath)).StatusCode);
    }

    // ── live authorization, re-read on every request ────────────────────────

    [Fact]
    public async Task Disabling_The_Account_Invalidates_Every_Grant()
    {
        var (ownerId, _, _, grant) = await CastableAsync();
        var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync(grant.ContentPath)).StatusCode);

        await _factory.DisableUserAsync(ownerId);

        Assert.Equal(
            HttpStatusCode.NotFound, (await anonymous.GetAsync(grant.ContentPath)).StatusCode);
    }

    [Fact]
    public async Task Losing_Cast_Access_Invalidates_An_Existing_Grant()
    {
        var roleKey = await _factory.CreateRoleAsync("Casters", Permissions.CastAccess);
        var (ownerId, client) = await _factory.CreateRoleClientAsync(roleKey, "caster@example.com");
        var file = await UploadVideoAsync(ownerId);
        await PublishReadyLadderAsync(file);
        var grant = await ReadGrantAsync(
            await client.PostAsync($"/api/cast/videos/{file.Id}/grant", null));

        var anonymous = _factory.CreateClient();
        var beforeMaster = await anonymous.GetAsync(grant.ContentPath);
        Assert.Equal(HttpStatusCode.OK, beforeMaster.StatusCode);
        var token = TokenOf(grant.ContentPath);
        var segment = $"/api/cast/media/{grant.GrantId}/hls/high/seg-0.m4s?token={token}";
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync(segment)).StatusCode);

        // The role loses the key. No re-login, no new grant — the very next
        // segment must stop.
        await _factory.SetRolePermissionsAsync(roleKey, Permissions.PeopleAccess);

        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(segment)).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound, (await anonymous.GetAsync(grant.ContentPath)).StatusCode);
    }

    [Fact]
    public async Task Soft_Deleting_The_File_Invalidates_The_Grant()
    {
        var (_, client, file, grant) = await CastableAsync();
        var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync(grant.ContentPath)).StatusCode);

        Assert.Equal(
            HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/files/{file.Id}")).StatusCode);

        Assert.Equal(
            HttpStatusCode.NotFound, (await anonymous.GetAsync(grant.ContentPath)).StatusCode);
    }

    // ── method surface ──────────────────────────────────────────────────────

    [Fact]
    public async Task Cast_Media_Answers_Get_And_Head_Only()
    {
        var (_, _, _, grant) = await CastableAsync();
        var anonymous = _factory.CreateClient();

        var head = await anonymous.SendAsync(
            new HttpRequestMessage(HttpMethod.Head, grant.ContentPath));
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);

        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Delete })
        {
            var response = await anonymous.SendAsync(
                new HttpRequestMessage(method, grant.ContentPath));
            Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        }
    }

    // ── the ordinary endpoints are untouched ────────────────────────────────

    [Fact]
    public async Task The_Owner_Video_Endpoint_Stays_CookieOnly()
    {
        var (_, _, file, _) = await CastableAsync();
        var anonymous = _factory.CreateClient();

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync($"/api/files/{file.Id}/video")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync($"/api/files/{file.Id}/video/high/seg-0.m4s")).StatusCode);
    }

    // ── no-leak ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Cast_Responses_Do_Not_Leak_Internals()
    {
        var (_, _, file, grant) = await CastableAsync();
        string sha;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            sha = (await db.BlobObjects.AsNoTracking()
                .SingleAsync(b => b.Id == file.BlobObjectId)).Sha256;
        }

        var anonymous = _factory.CreateClient();
        var response = await anonymous.GetAsync(grant.ContentPath);
        var headers = string.Join("\n",
            response.Headers.Concat(response.Content.Headers)
                .SelectMany(h => h.Value.Select(v => $"{h.Key}: {v}")));
        var body = await response.Content.ReadAsStringAsync();

        foreach (var needle in MetadataExposurePolicy.ForbiddenInResponses)
        {
            Assert.DoesNotContain(needle, headers, StringComparison.Ordinal);
            Assert.DoesNotContain(needle, body, StringComparison.Ordinal);
        }
        Assert.DoesNotContain(sha, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("objects/", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cast_Media_Is_Never_Cached_By_An_Intermediary()
    {
        var (_, _, _, grant) = await CastableAsync();
        var anonymous = _factory.CreateClient();

        var response = await anonymous.GetAsync(grant.ContentPath);

        Assert.Contains("no-store", response.Headers.CacheControl?.ToString() ?? string.Empty,
            StringComparison.Ordinal);
    }
}
