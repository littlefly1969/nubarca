using System.Net;
using System.Net.Http.Json;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Users;

namespace NubArca.Api.Tests.Users;

public sealed class AdminUsersEndpointTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public AdminUsersEndpointTests()
    {
        _factory = new SqliteWebApplicationFactory();
        _factory.EnsureDatabaseCreated();
    }

    public void Dispose() => _factory.Dispose();

    private async Task<(Guid AdminId, HttpClient AdminClient)> SeedAdminAsync(string email = "admin@example.com")
    {
        var adminId = await _factory.SeedUserAsync(email);
        await _factory.PromoteToAdminAsync(adminId);
        var client = await _factory.LoginAsync(email);
        return (adminId, client);
    }

    [Fact]
    public async Task NonAdmin_Cannot_List_Users()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync("user@example.com");
        var response = await client.GetAsync("/api/admin/users");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Anonymous_Cannot_List_Users()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/admin/users");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Admin_Can_List_Users_And_Response_Does_Not_Contain_PasswordHash()
    {
        var (_, adminClient) = await SeedAdminAsync();
        await _factory.SeedUserAsync("bob@example.com");

        var response = await adminClient.GetAsync("/api/admin/users");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("passwordHash", raw, StringComparison.OrdinalIgnoreCase);

        var body = System.Text.Json.JsonSerializer.Deserialize<ListAdminUsersResponse>(
            raw, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.NotNull(body);
        Assert.True(body!.Total >= 2);
        Assert.Contains(body.Items, u => u.Email == "bob@example.com" && u.HasPassword);
    }

    [Fact]
    public async Task Admin_Can_Create_User_With_Password_And_Created_User_Can_Login()
    {
        var (_, adminClient) = await SeedAdminAsync();

        var create = await adminClient.PostAsJsonAsync("/api/admin/users", new
        {
            email = "newuser@example.com",
            displayName = "New User",
            password = "correct-horse-battery",
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<AdminUserDto>();
        Assert.NotNull(created);
        Assert.Equal("newuser@example.com", created!.Email);
        Assert.True(created.HasPassword);
        Assert.False(created.IsAdmin);

        var anon = _factory.CreateClient();
        var login = await anon.PostAsJsonAsync(
            "/api/auth/login",
            new { email = "newuser@example.com", password = "correct-horse-battery" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [Fact]
    public async Task Creating_Duplicate_Email_Returns_Conflict()
    {
        var (_, adminClient) = await SeedAdminAsync();
        await _factory.SeedUserAsync("dup@example.com");

        var create = await adminClient.PostAsJsonAsync("/api/admin/users", new
        {
            email = "dup@example.com",
            displayName = "Dup",
            password = "correct-horse-battery",
        });
        Assert.Equal(HttpStatusCode.Conflict, create.StatusCode);
    }

    [Fact]
    public async Task Admin_Can_Create_Admin_User()
    {
        var (_, adminClient) = await SeedAdminAsync();

        var create = await adminClient.PostAsJsonAsync("/api/admin/users", new
        {
            email = "newadmin@example.com",
            displayName = "New Admin",
            password = "correct-horse-battery",
            isAdmin = true,
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<AdminUserDto>();
        Assert.True(created!.IsAdmin);
    }

    [Fact]
    public async Task Admin_Can_Reset_Another_Users_Password_Old_Fails_New_Succeeds()
    {
        var (_, adminClient) = await SeedAdminAsync();
        var targetId = await _factory.SeedUserAsync("target@example.com");

        var reset = await adminClient.PostAsJsonAsync(
            $"/api/admin/users/{targetId}/password",
            new { password = "brand-new-password-1" });
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);

        var anon = _factory.CreateClient();
        var oldLogin = await anon.PostAsJsonAsync(
            "/api/auth/login",
            new { email = "target@example.com", password = SqliteWebApplicationFactory.TestPassword });
        Assert.Equal(HttpStatusCode.Unauthorized, oldLogin.StatusCode);

        var newLogin = await anon.PostAsJsonAsync(
            "/api/auth/login",
            new { email = "target@example.com", password = "brand-new-password-1" });
        Assert.Equal(HttpStatusCode.OK, newLogin.StatusCode);
    }

    [Fact]
    public async Task Admin_Can_Grant_Admin_And_Granted_User_Gets_Admin_Access_Without_Relogin()
    {
        var (_, adminClient) = await SeedAdminAsync();
        var targetId = await _factory.SeedUserAsync("promote@example.com");
        var targetClient = await _factory.LoginAsync("promote@example.com");

        var forbidden = await targetClient.GetAsync("/api/admin/users");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        var grant = await adminClient.PutAsJsonAsync($"/api/admin/users/{targetId}/admin", new { isAdmin = true });
        Assert.Equal(HttpStatusCode.OK, grant.StatusCode);

        var allowed = await targetClient.GetAsync("/api/admin/users");
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    [Fact]
    public async Task Admin_Can_Revoke_Admin_And_Revoked_Admin_Loses_Access()
    {
        var (_, adminClient) = await SeedAdminAsync();
        var otherAdminId = await _factory.SeedUserAsync("other-admin@example.com");
        await _factory.PromoteToAdminAsync(otherAdminId);
        var otherAdminClient = await _factory.LoginAsync("other-admin@example.com");

        var revoke = await adminClient.PutAsJsonAsync(
            $"/api/admin/users/{otherAdminId}/admin", new { isAdmin = false });
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);

        var forbidden = await otherAdminClient.GetAsync("/api/admin/users");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    [Fact]
    public async Task Admin_Cannot_Remove_Own_Admin_Privilege()
    {
        var (adminId, adminClient) = await SeedAdminAsync();
        var otherAdminId = await _factory.SeedUserAsync("other-admin2@example.com");
        await _factory.PromoteToAdminAsync(otherAdminId);

        var response = await adminClient.PutAsJsonAsync($"/api/admin/users/{adminId}/admin", new { isAdmin = false });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var me = await adminClient.GetAsync("/api/admin/users");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
    }

    [Fact]
    public async Task Disabled_User_Cannot_Login()
    {
        var (_, adminClient) = await SeedAdminAsync();
        var targetId = await _factory.SeedUserAsync("todisable@example.com");

        var disable = await adminClient.PutAsJsonAsync($"/api/admin/users/{targetId}/disabled", new { disabled = true });
        Assert.Equal(HttpStatusCode.OK, disable.StatusCode);

        var anon = _factory.CreateClient();
        var login = await anon.PostAsJsonAsync(
            "/api/auth/login",
            new { email = "todisable@example.com", password = SqliteWebApplicationFactory.TestPassword });
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
    }

    [Fact]
    public async Task Disabled_Logged_In_User_Cookie_Is_Rejected_On_Next_Request()
    {
        var (_, adminClient) = await SeedAdminAsync();
        var targetId = await _factory.SeedUserAsync("liveDisable@example.com");
        var targetClient = await _factory.LoginAsync("liveDisable@example.com");

        var before = await targetClient.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);

        var disable = await adminClient.PutAsJsonAsync($"/api/admin/users/{targetId}/disabled", new { disabled = true });
        Assert.Equal(HttpStatusCode.OK, disable.StatusCode);

        var after = await targetClient.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    [Fact]
    public async Task Admin_Can_Re_Enable_A_Disabled_User()
    {
        var (_, adminClient) = await SeedAdminAsync();
        var targetId = await _factory.SeedUserAsync("reenable@example.com");

        await adminClient.PutAsJsonAsync($"/api/admin/users/{targetId}/disabled", new { disabled = true });
        var enable = await adminClient.PutAsJsonAsync($"/api/admin/users/{targetId}/disabled", new { disabled = false });
        Assert.Equal(HttpStatusCode.OK, enable.StatusCode);

        var anon = _factory.CreateClient();
        var login = await anon.PostAsJsonAsync(
            "/api/auth/login",
            new { email = "reenable@example.com", password = SqliteWebApplicationFactory.TestPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [Fact]
    public async Task Admin_Cannot_Disable_Own_Account()
    {
        var (adminId, adminClient) = await SeedAdminAsync();

        var response = await adminClient.PutAsJsonAsync($"/api/admin/users/{adminId}/disabled", new { disabled = true });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var me = await adminClient.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("   ")]
    public async Task Create_Rejects_Invalid_Passwords(string password)
    {
        var (_, adminClient) = await SeedAdminAsync();

        var create = await adminClient.PostAsJsonAsync("/api/admin/users", new
        {
            email = "badpwd@example.com",
            displayName = "Bad Pwd",
            password,
        });

        Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);
    }

    [Fact]
    public async Task Create_Rejects_Password_Longer_Than_Max()
    {
        var (_, adminClient) = await SeedAdminAsync();

        var create = await adminClient.PostAsJsonAsync("/api/admin/users", new
        {
            email = "toolong@example.com",
            displayName = "Too Long",
            password = new string('a', 257),
        });

        Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);
    }

    [Fact]
    public async Task Get_Missing_User_Returns_404()
    {
        var (_, adminClient) = await SeedAdminAsync();
        var response = await adminClient.GetAsync($"/api/admin/users/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Update_Missing_User_Returns_404()
    {
        var (_, adminClient) = await SeedAdminAsync();
        var response = await adminClient.PutAsJsonAsync(
            $"/api/admin/users/{Guid.NewGuid()}", new { displayName = "X" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Admin_Can_Update_DisplayName_And_Language()
    {
        var (_, adminClient) = await SeedAdminAsync();
        var targetId = await _factory.SeedUserAsync("editme@example.com");

        var update = await adminClient.PutAsJsonAsync(
            $"/api/admin/users/{targetId}", new { displayName = "Renamed", language = "en" });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        var updated = await update.Content.ReadFromJsonAsync<AdminUserDto>();
        Assert.Equal("Renamed", updated!.DisplayName);
        Assert.Equal("en", updated.Language);
    }

    [Fact]
    public async Task Responses_Never_Contain_Plaintext_Password_Or_Hash()
    {
        var (_, adminClient) = await SeedAdminAsync();

        var create = await adminClient.PostAsJsonAsync("/api/admin/users", new
        {
            email = "safe@example.com",
            displayName = "Safe",
            password = "correct-horse-battery",
        });
        var createBody = await create.Content.ReadAsStringAsync();
        Assert.DoesNotContain("correct-horse-battery", createBody);
        Assert.DoesNotContain("passwordHash", createBody, StringComparison.OrdinalIgnoreCase);

        var list = await adminClient.GetAsync("/api/admin/users");
        var listBody = await list.Content.ReadAsStringAsync();
        Assert.DoesNotContain("correct-horse-battery", listBody);
        Assert.DoesNotContain("passwordHash", listBody, StringComparison.OrdinalIgnoreCase);
    }
}
