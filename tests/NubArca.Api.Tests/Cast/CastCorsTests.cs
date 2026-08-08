using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Tests.Metadata;
using Xunit;

namespace NubArca.Api.Tests.Cast;

// NUBARCA-GOOGLE-CAST-01 — the CORS boundary.
//
// A Cast receiver is a foreign document fetching protected URLs, so the media
// routes need CORS. Nothing else in NubArca does, and these URLs carry a bearer
// secret — which is exactly why a wildcard would be a hole rather than a
// convenience. The allowlist is exact, operator-configured, and fails closed.
//
// The receiver origin used here is a deterministic TEST value, not a guess at
// what Google presents in production: the real value is captured once, from a
// physical device, and written into the installation's own configuration.
public sealed class CastCorsTests : IDisposable
{
    private const string AllowedReceiverOrigin = "https://receiver.test.invalid";
    private const string ForeignOrigin = "https://attacker.test.invalid";

    private readonly SqliteWebApplicationFactory _factory;

    public CastCorsTests()
    {
        _factory = new SqliteWebApplicationFactory(new Dictionary<string, string?>
        {
            ["Cast:AllowedReceiverOrigins:0"] = AllowedReceiverOrigin,
        }, poolHost: true);
        _factory.EnsureDatabaseCreated();
    }

    public void Dispose() => _factory.Dispose();

    private async Task<string> ContentPathAsync()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        FileItem file;
        using (var scope = _factory.Services.CreateScope())
        {
            var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
            var head = ImageFixtures.MinimalMp4();
            var bytes = new byte[1024];
            Array.Copy(head, bytes, head.Length);
            for (var i = head.Length; i < bytes.Length; i++) bytes[i] = (byte)(i & 0xFF);
            file = await files.CreateAsync(
                owner, null, "clip.mp4", "video/mp4", new MemoryStream(bytes));
        }

        var response = await client.PostAsync($"/api/cast/videos/{file.Id}/grant", null);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("contentPath").GetString()!;
    }

    private static HttpRequestMessage Preflight(string path, string origin, string method = "GET")
    {
        var request = new HttpRequestMessage(HttpMethod.Options, path);
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", method);
        request.Headers.Add("Access-Control-Request-Headers", "range");
        return request;
    }

    [Fact]
    public async Task An_Allowed_Receiver_Origin_Is_Echoed_Exactly()
    {
        var contentPath = await ContentPathAsync();
        var anonymous = _factory.CreateClient();

        var response = await anonymous.SendAsync(Preflight(contentPath, AllowedReceiverOrigin));

        var allowed = Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin"));
        Assert.Equal(AllowedReceiverOrigin, allowed);
        Assert.NotEqual("*", allowed);
    }

    [Fact]
    public async Task An_Allowed_Origin_May_Send_Range_And_Read_Back_Content_Range()
    {
        var contentPath = await ContentPathAsync();
        var anonymous = _factory.CreateClient();

        var preflight = await anonymous.SendAsync(Preflight(contentPath, AllowedReceiverOrigin));

        var methods = string.Join(",", preflight.Headers.GetValues("Access-Control-Allow-Methods"));
        Assert.Contains("GET", methods, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("HEAD", methods, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("OPTIONS", methods, StringComparison.OrdinalIgnoreCase);
        // Nothing that could change state.
        Assert.DoesNotContain("POST", methods, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PUT", methods, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DELETE", methods, StringComparison.OrdinalIgnoreCase);

        var requestHeaders = string.Join(
            ",", preflight.Headers.GetValues("Access-Control-Allow-Headers"));
        Assert.Contains("Range", requestHeaders, StringComparison.OrdinalIgnoreCase);

        // A player that cannot READ Content-Range/Accept-Ranges cannot seek.
        var actual = new HttpRequestMessage(HttpMethod.Get, contentPath);
        actual.Headers.Add("Origin", AllowedReceiverOrigin);
        var response = await anonymous.SendAsync(actual);
        var exposed = string.Join(",", response.Headers.GetValues("Access-Control-Expose-Headers"));
        foreach (var header in new[]
                 { "Content-Type", "Content-Length", "Content-Range", "Accept-Ranges" })
        {
            Assert.Contains(header, exposed, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task A_Foreign_Receiver_Origin_Gets_No_Cors_Permission()
    {
        var contentPath = await ContentPathAsync();
        var anonymous = _factory.CreateClient();

        var preflight = await anonymous.SendAsync(Preflight(contentPath, ForeignOrigin));
        Assert.False(preflight.Headers.Contains("Access-Control-Allow-Origin"));

        var actual = new HttpRequestMessage(HttpMethod.Get, contentPath);
        actual.Headers.Add("Origin", ForeignOrigin);
        var response = await anonymous.SendAsync(actual);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    // CORS is attached to the Cast media family and to nothing else. An
    // allowlisted origin must NOT be able to read the ordinary API.
    [Theory]
    [InlineData("/api/auth/me")]
    [InlineData("/api/media")]
    [InlineData("/api/storage/me")]
    public async Task Cors_Is_Not_Enabled_On_The_Rest_Of_The_Api(string path)
    {
        _ = await ContentPathAsync();
        var anonymous = _factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Origin", AllowedReceiverOrigin);
        var response = await anonymous.SendAsync(request);

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Cast_Media_Never_Advertises_A_Wildcard()
    {
        var contentPath = await ContentPathAsync();
        var anonymous = _factory.CreateClient();

        foreach (var origin in new[] { AllowedReceiverOrigin, ForeignOrigin })
        {
            var request = new HttpRequestMessage(HttpMethod.Get, contentPath);
            request.Headers.Add("Origin", origin);
            var response = await anonymous.SendAsync(request);
            if (response.Headers.TryGetValues("Access-Control-Allow-Origin", out var values))
            {
                Assert.DoesNotContain("*", values);
            }
        }
    }
}

// With no receiver origin configured at all, grant creation still works — the
// capability exists, it simply is not advertised to anybody. That is the safe
// direction to fail in: the operator sees a television that will not start,
// never a server that quietly allowed an unknown origin.
public sealed class CastCorsUnconfiguredTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public CastCorsUnconfiguredTests()
    {
        _factory = new SqliteWebApplicationFactory();
        _factory.EnsureDatabaseCreated();
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Without_A_Configured_Origin_No_Origin_Is_Allowed()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        FileItem file;
        using (var scope = _factory.Services.CreateScope())
        {
            var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
            var head = ImageFixtures.MinimalMp4();
            var bytes = new byte[1024];
            Array.Copy(head, bytes, head.Length);
            for (var i = head.Length; i < bytes.Length; i++) bytes[i] = (byte)(i & 0xFF);
            file = await files.CreateAsync(
                owner, null, "clip.mp4", "video/mp4", new MemoryStream(bytes));
        }

        // Minting still succeeds.
        var created = await client.PostAsync($"/api/cast/videos/{file.Id}/grant", null);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var json = await created.Content.ReadFromJsonAsync<JsonElement>();
        var contentPath = json.GetProperty("contentPath").GetString()!;

        var anonymous = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, contentPath);
        request.Headers.Add("Origin", "https://receiver.test.invalid");
        var response = await anonymous.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }
}
