using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Tests.Endpoints;
using Xunit;

namespace NubArca.Api.Tests.Security;

// Slice 54.2 — CSRF same-origin Origin/Referer validation for unsafe /api
// methods. The TestServer presents the request as http://localhost, so
// "http://localhost" is same-origin and anything else is cross-origin.
public sealed class CsrfProtectionTests : IDisposable
{
    private const string SameOrigin = "http://localhost";
    private const string CrossOrigin = "https://evil.example";

    private readonly SqliteWebApplicationFactory _factory;

    public CsrfProtectionTests()
    {
        _factory = new SqliteWebApplicationFactory();
        _factory.EnsureDatabaseCreated();
    }

    public void Dispose() => _factory.Dispose();

    private async Task<FileItem> SeedFileAsync(Guid ownerId, string name)
    {
        using var scope = _factory.Services.CreateScope();
        var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
        return await files.CreateAsync(ownerId, null, name, "text/plain",
            new MemoryStream(Encoding.UTF8.GetBytes("x")));
    }

    private static HttpRequestMessage Request(
        HttpMethod method, string url, string? origin = null, string? referer = null, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (origin is not null) request.Headers.Add("Origin", origin);
        if (referer is not null) request.Headers.Add("Referer", referer);
        if (content is not null) request.Content = content;
        return request;
    }

    private static HttpContent Json(object body) => JsonContent.Create(body);

    // ---- unsafe methods: same-origin allowed -------------------------------

    [Fact]
    public async Task SameOrigin_Post_Succeeds()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var response = await client.SendAsync(
            Request(HttpMethod.Post, "/api/folders", origin: SameOrigin, content: Json(new { name = "f" })));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task SameOrigin_Patch_Succeeds()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await SeedFileAsync(owner, "doc.txt");
        var response = await client.SendAsync(
            Request(HttpMethod.Patch, $"/api/files/{file.Id}/rename",
                origin: SameOrigin, content: Json(new { name = "renamed.txt" })));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task SameOrigin_Delete_Succeeds()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await SeedFileAsync(owner, "doc.txt");
        var response = await client.SendAsync(
            Request(HttpMethod.Delete, $"/api/files/{file.Id}", origin: SameOrigin));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    // ---- unsafe methods: cross-origin rejected -----------------------------

    [Fact]
    public async Task CrossOrigin_Post_Is_Rejected_403()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var response = await client.SendAsync(
            Request(HttpMethod.Post, "/api/folders", origin: CrossOrigin, content: Json(new { name = "f" })));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task CrossOrigin_Patch_Is_Rejected_403()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await SeedFileAsync(owner, "doc.txt");
        var response = await client.SendAsync(
            Request(HttpMethod.Patch, $"/api/files/{file.Id}/rename",
                origin: CrossOrigin, content: Json(new { name = "x.txt" })));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        // The mutation must not have happened.
        using var scope = _factory.Services.CreateScope();
        var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
        var still = await files.GetByIdAsync(file.Id, owner);
        Assert.Equal("doc.txt", still!.Name);
    }

    [Fact]
    public async Task CrossOrigin_Delete_Is_Rejected_403()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await SeedFileAsync(owner, "doc.txt");
        var response = await client.SendAsync(
            Request(HttpMethod.Delete, $"/api/files/{file.Id}", origin: CrossOrigin));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task CrossOrigin_Upload_Is_Rejected_403()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var multipart = new MultipartFormDataContent
        {
            { new ByteArrayContent("hello"u8.ToArray()), "file", "note.txt" },
        };
        var response = await client.SendAsync(
            Request(HttpMethod.Post, "/api/files", origin: CrossOrigin, content: multipart));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---- scheme matters (cross-scheme is cross-origin) ---------------------

    [Fact]
    public async Task CrossScheme_Origin_Is_Rejected_403()
    {
        // Same host, different scheme (https vs the http request) is cross-origin.
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var response = await client.SendAsync(
            Request(HttpMethod.Post, "/api/folders", origin: "https://localhost", content: Json(new { name = "f" })));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---- the port is part of the origin ------------------------------------
    //
    // These pin the contract a reverse proxy has to honour. An installation
    // served on a non-default port shipped broken because the documented nginx
    // config forwarded `Host $host`, which drops the port: the API then inferred
    // port 80, disagreed with the browser's Origin, and rejected every write —
    // login included — with 403. On :443 the bug is invisible, because the
    // stripped port and the inferred one agree. Only the port was untested.

    [Fact]
    public async Task NonDefaultPort_SameOrigin_Succeeds()
    {
        // What a proxy forwarding the full Host produces: the API sees the
        // address the browser actually used, so the Origin matches.
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var request = Request(HttpMethod.Post, "/api/folders",
            origin: "http://localhost:8443", content: Json(new { name = "f" }));
        request.Headers.Host = "localhost:8443";

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task NonDefaultPort_Origin_Against_PortStripped_Host_Is_Rejected_403()
    {
        // The shipped failure, reproduced: proxy stripped the port from Host,
        // browser's Origin still carries it. Rejecting is correct given the
        // inputs — which is why the fix belongs in the proxy, not here.
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var request = Request(HttpMethod.Post, "/api/folders",
            origin: "http://localhost:8443", content: Json(new { name = "f" }));
        request.Headers.Host = "localhost";

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task DifferentPort_SameHost_Is_Rejected_403()
    {
        // A neighbouring service on the same hostname is still cross-origin.
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var request = Request(HttpMethod.Post, "/api/folders",
            origin: "http://localhost:9999", content: Json(new { name = "f" }));
        request.Headers.Host = "localhost:8443";

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---- safe methods are never blocked ------------------------------------

    [Fact]
    public async Task CrossOrigin_Get_Is_Not_Blocked()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var response = await client.SendAsync(
            Request(HttpMethod.Get, "/api/folders/children", origin: CrossOrigin));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---- Referer fallback when Origin is absent ----------------------------

    [Fact]
    public async Task SameOrigin_Referer_Without_Origin_Succeeds()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var response = await client.SendAsync(
            Request(HttpMethod.Post, "/api/folders",
                referer: "http://localhost/app/files", content: Json(new { name = "f" })));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task CrossOrigin_Referer_Without_Origin_Is_Rejected_403()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var response = await client.SendAsync(
            Request(HttpMethod.Post, "/api/folders",
                referer: "https://evil.example/page", content: Json(new { name = "f" })));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---- absent Origin AND Referer is allowed (documented) -----------------

    [Fact]
    public async Task Absent_Origin_And_Referer_Is_Allowed_For_NonBrowser_Clients()
    {
        // Documented choice: non-browser callers (curl / API tooling / the test
        // harness) send neither header and are not a CSRF vector. The whole
        // existing test suite relies on this (it sends no Origin), and this
        // test pins the behavior explicitly.
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var response = await client.PostAsJsonAsync("/api/folders", new { name = "plain" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }
}

// Verifies the CSRF origin check honours the reverse-proxy scheme: behind a
// trusted proxy sending X-Forwarded-Proto: https, an https Origin is same-origin.
// Uses the unauthenticated login endpoint to avoid Secure-cookie follow-up
// complications under a forwarded https scheme.
public sealed class CsrfForwardedProtoTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public CsrfForwardedProtoTests()
    {
        _factory = new SqliteWebApplicationFactory(new Dictionary<string, string?>
        {
            ["ForwardedHeaders:Enabled"] = "true",
            ["ForwardedHeaders:TrustAny"] = "true",
            ["ForwardedHeaders:ForwardLimit"] = "1",
        });
        _factory.EnsureDatabaseCreated();
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Https_Origin_Behind_Forwarded_Proto_Https_Is_Allowed()
    {
        await _factory.SeedUserAsync("alice@example.com");
        var client = _factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new
            {
                email = "alice@example.com",
                password = SqliteWebApplicationFactory.TestPassword,
            }),
        };
        request.Headers.Add("X-Forwarded-Proto", "https");
        request.Headers.Add("Origin", "https://localhost");

        var response = await client.SendAsync(request);

        // CSRF passes (scheme matches via forwarded proto) → real login runs.
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Https_Origin_Without_Forwarded_Proto_Is_Rejected_403()
    {
        await _factory.SeedUserAsync("alice@example.com");
        var client = _factory.CreateClient();

        // No X-Forwarded-Proto → request scheme stays http → https Origin is a
        // scheme mismatch → cross-origin.
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new
            {
                email = "alice@example.com",
                password = SqliteWebApplicationFactory.TestPassword,
            }),
        };
        request.Headers.Add("Origin", "https://localhost");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
