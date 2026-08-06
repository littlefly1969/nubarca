using System.Net;
using System.Net.Http.Json;
using NubArca.Api.Auth;
using NubArca.Api.Tests.Endpoints;

namespace NubArca.Api.Tests.Auth;

// Persisted per-user UI language preference (it/en). Italian is the canonical
// default; updates are cookie-session scoped so a user only ever changes their
// own language, and unsupported codes are rejected before any write.
public sealed class UserLanguageEndpointTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public UserLanguageEndpointTests()
    {
        _factory = new SqliteWebApplicationFactory();
        _factory.EnsureDatabaseCreated();
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task New_User_Defaults_To_Italian()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync("alice@example.com");

        var me = (await (await client.GetAsync("/api/auth/me"))
            .Content.ReadFromJsonAsync<CurrentUserResponse>())!;

        Assert.Equal("it", me.Language);
    }

    [Fact]
    public async Task Login_Response_Carries_Language()
    {
        await _factory.SeedUserAsync("alice@example.com");
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/auth/login",
            new { email = "alice@example.com", password = SqliteWebApplicationFactory.TestPassword });

        var body = (await response.Content.ReadFromJsonAsync<CurrentUserResponse>())!;
        Assert.Equal("it", body.Language);
    }

    [Fact]
    public async Task User_Can_Set_Language_To_English_And_It_Persists()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync("alice@example.com");

        var update = await client.PutAsJsonAsync("/api/auth/me/language", new { language = "en" });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var updated = (await update.Content.ReadFromJsonAsync<CurrentUserResponse>())!;
        Assert.Equal("en", updated.Language);

        // Persisted: a fresh /me read reflects the change without re-login.
        var me = (await (await client.GetAsync("/api/auth/me"))
            .Content.ReadFromJsonAsync<CurrentUserResponse>())!;
        Assert.Equal("en", me.Language);
    }

    [Fact]
    public async Task User_Can_Set_Language_Back_To_Italian()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync("alice@example.com");

        await client.PutAsJsonAsync("/api/auth/me/language", new { language = "en" });
        var update = await client.PutAsJsonAsync("/api/auth/me/language", new { language = "it" });

        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var updated = (await update.Content.ReadFromJsonAsync<CurrentUserResponse>())!;
        Assert.Equal("it", updated.Language);
    }

    [Theory]
    [InlineData("fr")]
    [InlineData("en-US")]
    [InlineData("")]
    [InlineData("xx")]
    [InlineData("italiano")]
    public async Task Invalid_Language_Is_Rejected_And_Does_Not_Change_Stored_Value(string invalid)
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync("alice@example.com");

        var update = await client.PutAsJsonAsync("/api/auth/me/language", new { language = invalid });
        Assert.Equal(HttpStatusCode.BadRequest, update.StatusCode);

        // Stored value unchanged (still the Italian default).
        var me = (await (await client.GetAsync("/api/auth/me"))
            .Content.ReadFromJsonAsync<CurrentUserResponse>())!;
        Assert.Equal("it", me.Language);
    }

    [Fact]
    public async Task Updating_Language_Requires_Authentication()
    {
        var client = _factory.CreateClient();
        var update = await client.PutAsJsonAsync("/api/auth/me/language", new { language = "en" });
        Assert.Equal(HttpStatusCode.Unauthorized, update.StatusCode);
    }

    [Fact]
    public async Task One_Users_Update_Does_Not_Affect_Another_User()
    {
        var (_, alice) = await _factory.CreateAuthenticatedClientAsync("alice@example.com");
        var (_, bob) = await _factory.CreateAuthenticatedClientAsync("bob@example.com");

        var update = await alice.PutAsJsonAsync("/api/auth/me/language", new { language = "en" });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        // Alice is English; Bob is untouched (still the Italian default). The
        // endpoint takes the user id from the session, so there is no way to
        // target another user's row.
        var aliceMe = (await (await alice.GetAsync("/api/auth/me"))
            .Content.ReadFromJsonAsync<CurrentUserResponse>())!;
        var bobMe = (await (await bob.GetAsync("/api/auth/me"))
            .Content.ReadFromJsonAsync<CurrentUserResponse>())!;

        Assert.Equal("en", aliceMe.Language);
        Assert.Equal("it", bobMe.Language);
    }
}
