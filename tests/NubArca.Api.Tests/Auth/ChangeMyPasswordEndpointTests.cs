using System.Net;
using System.Net.Http.Json;
using NubArca.Api.Tests.Endpoints;

namespace NubArca.Api.Tests.Auth;

public sealed class ChangeMyPasswordEndpointTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public ChangeMyPasswordEndpointTests()
    {
        _factory = new SqliteWebApplicationFactory();
        _factory.EnsureDatabaseCreated();
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task User_Can_Change_Own_Password_With_Current_Password()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync("alice@example.com");

        var change = await client.PostAsJsonAsync("/api/auth/me/password", new
        {
            currentPassword = SqliteWebApplicationFactory.TestPassword,
            newPassword = "brand-new-password-1",
        });
        Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);

        var anon = _factory.CreateClient();
        var oldLogin = await anon.PostAsJsonAsync(
            "/api/auth/login",
            new { email = "alice@example.com", password = SqliteWebApplicationFactory.TestPassword });
        Assert.Equal(HttpStatusCode.Unauthorized, oldLogin.StatusCode);

        var newLogin = await anon.PostAsJsonAsync(
            "/api/auth/login",
            new { email = "alice@example.com", password = "brand-new-password-1" });
        Assert.Equal(HttpStatusCode.OK, newLogin.StatusCode);
    }

    [Fact]
    public async Task Wrong_Current_Password_Fails_Generically()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync("bob@example.com");

        var change = await client.PostAsJsonAsync("/api/auth/me/password", new
        {
            currentPassword = "totally-wrong",
            newPassword = "brand-new-password-1",
        });

        Assert.Equal(HttpStatusCode.BadRequest, change.StatusCode);

        // Old password still works.
        var anon = _factory.CreateClient();
        var login = await anon.PostAsJsonAsync(
            "/api/auth/login",
            new { email = "bob@example.com", password = SqliteWebApplicationFactory.TestPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [Fact]
    public async Task Requires_Authentication()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/me/password", new
        {
            currentPassword = "x",
            newPassword = "brand-new-password-1",
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("   ")]
    public async Task Rejects_Invalid_New_Passwords(string newPassword)
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync("carol@example.com");

        var change = await client.PostAsJsonAsync("/api/auth/me/password", new
        {
            currentPassword = SqliteWebApplicationFactory.TestPassword,
            newPassword,
        });

        Assert.Equal(HttpStatusCode.BadRequest, change.StatusCode);
    }

    [Fact]
    public async Task Rejects_New_Password_Same_As_Current()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync("dave@example.com");

        var change = await client.PostAsJsonAsync("/api/auth/me/password", new
        {
            currentPassword = SqliteWebApplicationFactory.TestPassword,
            newPassword = SqliteWebApplicationFactory.TestPassword,
        });

        Assert.Equal(HttpStatusCode.BadRequest, change.StatusCode);
    }

    [Fact]
    public async Task One_Users_Change_Does_Not_Affect_Another_User()
    {
        var (_, alice) = await _factory.CreateAuthenticatedClientAsync("alice2@example.com");
        await _factory.CreateAuthenticatedClientAsync("eve2@example.com");

        var change = await alice.PostAsJsonAsync("/api/auth/me/password", new
        {
            currentPassword = SqliteWebApplicationFactory.TestPassword,
            newPassword = "brand-new-password-1",
        });
        Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);

        var anon = _factory.CreateClient();
        var eveStillWorks = await anon.PostAsJsonAsync(
            "/api/auth/login",
            new { email = "eve2@example.com", password = SqliteWebApplicationFactory.TestPassword });
        Assert.Equal(HttpStatusCode.OK, eveStillWorks.StatusCode);
    }

    [Fact]
    public async Task Response_Never_Contains_Plaintext_Password_Or_Hash()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync("frank@example.com");

        var change = await client.PostAsJsonAsync("/api/auth/me/password", new
        {
            currentPassword = SqliteWebApplicationFactory.TestPassword,
            newPassword = "brand-new-password-1",
        });

        var body = await change.Content.ReadAsStringAsync();
        Assert.DoesNotContain("brand-new-password-1", body);
        Assert.DoesNotContain(SqliteWebApplicationFactory.TestPassword, body);
        Assert.DoesNotContain("passwordHash", body, StringComparison.OrdinalIgnoreCase);
    }
}
