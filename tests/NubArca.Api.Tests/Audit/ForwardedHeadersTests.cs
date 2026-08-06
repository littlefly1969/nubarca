using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Audit;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Tests.Endpoints;

namespace NubArca.Api.Tests.Audit;

// Verifies that when the operator enables the ForwardedHeaders middleware,
// X-Forwarded-For from a trusted proxy is reflected in audit IpAddress and
// X-Forwarded-Proto does not break the auth cookie flow. Tests use the
// `TrustAny` opt-in so the in-process TestServer (which presents 127.0.0.1
// as the connection remote IP) is recognised as a trusted proxy.
public sealed class ForwardedHeadersTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public ForwardedHeadersTests()
    {
        _factory = new SqliteWebApplicationFactory(new Dictionary<string, string?>
        {
            ["ForwardedHeaders:Enabled"] = "true",
            ["ForwardedHeaders:TrustAny"] = "true",
            ["ForwardedHeaders:ForwardLimit"] = "1",
        }, poolHost: true);
        _factory.EnsureDatabaseCreated();
    }

    public void Dispose() => _factory.Dispose();

    private async Task<List<AuditLog>> ReadAuditAsync(string action)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.AuditLogs.AsNoTracking().Where(a => a.Action == action).ToListAsync();
    }

    [Fact]
    public async Task Forwarded_Client_IP_Is_Used_For_Audit_When_ForwardedHeaders_Enabled()
    {
        await _factory.SeedUserAsync("alice@example.com");

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.5");

        var response = await client.PostAsJsonAsync(
            "/api/auth/login",
            new { email = "alice@example.com", password = SqliteWebApplicationFactory.TestPassword });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var audit = await ReadAuditAsync(AuditActions.LoginSuccess);
        var row = Assert.Single(audit);
        Assert.Equal("203.0.113.5", row.IpAddress);
    }

    [Fact]
    public async Task Forwarded_Client_IP_Is_Used_For_Failed_Login_Audit()
    {
        await _factory.SeedUserAsync("alice@example.com");

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "198.51.100.42");

        var response = await client.PostAsJsonAsync(
            "/api/auth/login",
            new { email = "alice@example.com", password = "wrong-pw" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var audit = await ReadAuditAsync(AuditActions.LoginFailure);
        var row = Assert.Single(audit);
        Assert.Equal("198.51.100.42", row.IpAddress);
    }

    [Fact]
    public async Task Forwarded_Proto_Https_Issues_Secure_Cookie_And_Login_Succeeds()
    {
        // SecurePolicy = SameAsRequest means: when the request scheme is https,
        // the auth cookie is issued with the Secure flag. Verifying the Set-
        // Cookie header carries `secure` after a request with X-Forwarded-Proto:
        // https proves the proto rewrite reaches the cookie middleware. The
        // browser will then return the cookie on the next https request — a
        // real deployment runs https end-to-end so the cookie round-trip works.
        await _factory.SeedUserAsync("alice@example.com");

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.5");
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");

        var login = await client.PostAsJsonAsync(
            "/api/auth/login",
            new { email = "alice@example.com", password = SqliteWebApplicationFactory.TestPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var setCookie = login.Headers.GetValues("Set-Cookie").Single();
        Assert.Contains("NubArca.Auth=", setCookie);
        Assert.Contains("secure", setCookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Without_Forwarded_Proto_Cookie_Is_Not_Marked_Secure()
    {
        // Regression guard: a plain http request (no X-Forwarded-Proto) must
        // continue to issue a non-Secure cookie so http-only local dev keeps
        // working.
        await _factory.SeedUserAsync("alice@example.com");

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.5");

        var login = await client.PostAsJsonAsync(
            "/api/auth/login",
            new { email = "alice@example.com", password = SqliteWebApplicationFactory.TestPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var setCookie = login.Headers.GetValues("Set-Cookie").Single();
        Assert.DoesNotContain("secure", setCookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task First_Forwarded_For_Value_Is_Honoured_With_ForwardLimit_1()
    {
        await _factory.SeedUserAsync("alice@example.com");

        var client = _factory.CreateClient();
        // Two-hop chain: the rightmost value is the proxy nearest to us, the
        // leftmost the original client. With ForwardLimit=1 we accept exactly
        // the immediately-prior hop, which is the value to the right.
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.5, 198.51.100.99");

        var response = await client.PostAsJsonAsync(
            "/api/auth/login",
            new { email = "alice@example.com", password = SqliteWebApplicationFactory.TestPassword });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var audit = await ReadAuditAsync(AuditActions.LoginSuccess);
        var row = Assert.Single(audit);
        Assert.Equal("198.51.100.99", row.IpAddress);
    }
}

// Regression guard: with ForwardedHeaders disabled (the default), forwarded
// headers must NOT change the audit IP.
public sealed class ForwardedHeadersDisabledTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public ForwardedHeadersDisabledTests()
    {
        // Default factory: ForwardedHeaders:Enabled is unset (so false).
        _factory = new SqliteWebApplicationFactory();
        _factory.EnsureDatabaseCreated();
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Forwarded_Header_Is_Ignored_When_Middleware_Disabled()
    {
        await _factory.SeedUserAsync("alice@example.com");

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.5");

        var response = await client.PostAsJsonAsync(
            "/api/auth/login",
            new { email = "alice@example.com", password = SqliteWebApplicationFactory.TestPassword });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.AuditLogs.AsNoTracking()
            .Where(a => a.Action == AuditActions.LoginSuccess)
            .SingleAsync();

        // The TestServer's connection remote IP is 127.0.0.1; that's what gets
        // recorded when the middleware is off. Critically, NOT 203.0.113.5.
        Assert.NotEqual("203.0.113.5", row.IpAddress);
    }
}
