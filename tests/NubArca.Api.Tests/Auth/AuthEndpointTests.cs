using System.Net;
using System.Net.Http.Json;
using NubArca.Api.Auth;
using NubArca.Api.Tests.Endpoints;

namespace NubArca.Api.Tests.Auth;

public sealed class AuthEndpointTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public AuthEndpointTests()
    {
        _factory = new SqliteWebApplicationFactory();
        _factory.EnsureDatabaseCreated();
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Login_With_Valid_Credentials_Returns_Safe_User_Dto_And_Sets_Cookie()
    {
        var userId = await _factory.SeedUserAsync("alice@example.com");

        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync(
            "/api/auth/login",
            new { email = "alice@example.com", password = SqliteWebApplicationFactory.TestPassword });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<CurrentUserResponse>();
        Assert.NotNull(body);
        Assert.Equal(userId, body!.Id);
        Assert.Equal("alice@example.com", body.Email);
        Assert.Equal("Owner", body.DisplayName);

        var setCookie = response.Headers.GetValues("Set-Cookie").Single();
        Assert.Contains("NubArca.Auth=", setCookie);
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", setCookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Login_With_Unknown_Email_Returns_401()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/auth/login",
            new { email = "ghost@example.com", password = "anything" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_With_Wrong_Password_Returns_401_With_Same_Shape_As_Unknown_Email()
    {
        await _factory.SeedUserAsync("alice@example.com");

        var client = _factory.CreateClient();
        var wrongPwd = await client.PostAsJsonAsync(
            "/api/auth/login",
            new { email = "alice@example.com", password = "totally-wrong" });
        var unknownEmail = await client.PostAsJsonAsync(
            "/api/auth/login",
            new { email = "ghost@example.com", password = "totally-wrong" });

        Assert.Equal(HttpStatusCode.Unauthorized, wrongPwd.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknownEmail.StatusCode);

        // Bodies must be indistinguishable to prevent user enumeration.
        var wrongPwdBody = await wrongPwd.Content.ReadAsStringAsync();
        var unknownEmailBody = await unknownEmail.Content.ReadAsStringAsync();
        Assert.Equal(unknownEmailBody, wrongPwdBody);
    }

    [Fact]
    public async Task Login_For_Disabled_User_Returns_401()
    {
        var userId = await _factory.SeedUserAsync("alice@example.com");
        await _factory.DisableUserAsync(userId);

        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync(
            "/api/auth/login",
            new { email = "alice@example.com", password = SqliteWebApplicationFactory.TestPassword });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_With_Missing_Email_Or_Password_Returns_401()
    {
        var client = _factory.CreateClient();

        var noEmail = await client.PostAsJsonAsync("/api/auth/login", new { password = "x" });
        var noPwd = await client.PostAsJsonAsync("/api/auth/login", new { email = "user@example.com" });
        var empty = await client.PostAsJsonAsync("/api/auth/login", new { });

        Assert.Equal(HttpStatusCode.Unauthorized, noEmail.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, noPwd.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, empty.StatusCode);
    }

    [Fact]
    public async Task Me_Returns_401_Before_Login()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Me_Returns_Safe_User_Dto_After_Login()
    {
        var (userId, client) = await _factory.CreateAuthenticatedClientAsync("bob@example.com");

        var response = await client.GetAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CurrentUserResponse>();
        Assert.NotNull(body);
        Assert.Equal(userId, body!.Id);
        Assert.Equal("bob@example.com", body.Email);
        // Default users are NOT admin (slice 46 + 47).
        Assert.False(body.IsAdmin);

        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("passwordHash", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PasswordHash", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("disabledAt", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Me_Returns_IsAdmin_True_For_Admin_User()
    {
        var userId = await _factory.SeedUserAsync("alice@example.com");
        await _factory.PromoteToAdminAsync(userId);
        var client = await _factory.LoginAsync("alice@example.com");

        var response = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = (await response.Content.ReadFromJsonAsync<CurrentUserResponse>())!;
        Assert.Equal(userId, body.Id);
        Assert.True(body.IsAdmin);
    }

    [Fact]
    public async Task Me_Reflects_Admin_Revoke_On_Next_Request_Without_Relogin()
    {
        var userId = await _factory.SeedUserAsync("carol@example.com");
        await _factory.PromoteToAdminAsync(userId);
        var client = await _factory.LoginAsync("carol@example.com");

        var first = (await (await client.GetAsync("/api/auth/me"))
            .Content.ReadFromJsonAsync<CurrentUserResponse>())!;
        Assert.True(first.IsAdmin);

        // Operator demotes the user out-of-band (CLI revoke-admin / DB edit).
        // The cookie revalidator (slice 46) drops the role claim on the next
        // request; the auth endpoint reads the live `user.IsAdmin` value.
        await _factory.DemoteFromAdminAsync(userId);

        var second = (await (await client.GetAsync("/api/auth/me"))
            .Content.ReadFromJsonAsync<CurrentUserResponse>())!;
        Assert.False(second.IsAdmin);
    }

    [Fact]
    public async Task Logout_Clears_Session_So_Subsequent_Me_Returns_401()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();

        var before = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);

        var logoutResponse = await client.PostAsync("/api/auth/logout", content: null);
        Assert.Equal(HttpStatusCode.NoContent, logoutResponse.StatusCode);

        var after = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    [Fact]
    public async Task Authenticated_Endpoints_Return_401_Without_Cookie()
    {
        await _factory.SeedUserAsync();
        var client = _factory.CreateClient();

        var endpoints = new[]
        {
            (HttpMethod.Get, "/api/folders/children"),
            (HttpMethod.Get, "/api/auth/me"),
        };

        foreach (var (method, path) in endpoints)
        {
            var response = await client.SendAsync(new HttpRequestMessage(method, path));
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }
}
