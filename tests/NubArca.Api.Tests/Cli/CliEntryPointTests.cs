using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Auth;
using NubArca.Api.Cli;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Users;

namespace NubArca.Api.Tests.Cli;

// SQLite in-memory unit tests for the operator CLI. We call the internal
// command methods directly with a hand-rolled service provider so the tests
// stay fast and Docker-free; the public RunAsync(...) entry point is the
// same dispatcher operators see in production.
public sealed class CliEntryPointTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _services;
    private readonly IServiceScope _assertScope;
    private readonly AppDbContext _db;

    public CliEntryPointTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var collection = new ServiceCollection();
        collection.AddDbContext<AppDbContext>(opt => opt.UseSqlite(_connection));
        collection.AddScoped<IUserService, UserService>();
        collection.AddScoped<IAuthService, AuthService>();
        collection.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();
        collection.AddSingleton(TimeProvider.System);
        _services = collection.BuildServiceProvider();

        // A long-lived "assertions" scope holds the DbContext used by tests
        // to read state. Each CLI invocation in RunCli() opens its OWN scope,
        // so the two contexts never collide on tracking — they just share
        // the same SQLite connection.
        _assertScope = _services.CreateScope();
        _db = _assertScope.ServiceProvider.GetRequiredService<AppDbContext>();
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _assertScope.Dispose();
        _services.Dispose();
        _connection.Dispose();
    }

    // Helper: builds a fresh DI scope so each call exercises a request-scoped
    // services pair (same shape as production where every CLI invocation gets
    // its own scope).
    private async Task<(int exit, string stdout, string stderr)> RunCli(params string[] args)
    {
        using var scope = _services.CreateScope();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = await CliEntryPoint.RunAsync(
            args,
            stdout,
            stderr,
            () => scope.ServiceProvider);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public async Task EnsureUser_Creates_User_When_Missing()
    {
        var (exit, stdout, stderr) = await RunCli(
            "users", "ensure",
            "--email", "alice@example.com",
            "--display-name", "Alice",
            "--password", "alice-password-1");

        Assert.Equal(0, exit);
        Assert.Contains("created user alice@example.com", stdout);
        Assert.Equal(string.Empty, stderr);

        var user = await _db.Users.AsNoTracking().SingleAsync();
        Assert.Equal("alice@example.com", user.Email);
        Assert.Equal("Alice", user.DisplayName);
        Assert.NotNull(user.PasswordHash);
        // Real PBKDF2 hash — never the plaintext.
        Assert.NotEqual("alice-password-1", user.PasswordHash);
    }

    [Fact]
    public async Task EnsureUser_Never_Echoes_Plaintext_Password()
    {
        var password = "very-secret-password";
        var (exit, stdout, stderr) = await RunCli(
            "users", "ensure",
            "--email", "alice@example.com",
            "--display-name", "Alice",
            "--password", password);

        Assert.Equal(0, exit);
        Assert.DoesNotContain(password, stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(password, stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnsureUser_Is_Idempotent_When_User_Already_Exists()
    {
        // First run creates the user.
        var first = await RunCli(
            "users", "ensure",
            "--email", "alice@example.com",
            "--display-name", "Alice",
            "--password", "alice-password-1");
        Assert.Equal(0, first.exit);

        var originalHash = (await _db.Users.AsNoTracking().SingleAsync()).PasswordHash;

        // Second run with a different password but no --update-password flag.
        var second = await RunCli(
            "users", "ensure",
            "--email", "alice@example.com",
            "--display-name", "Alice",
            "--password", "different-password");

        Assert.Equal(0, second.exit);
        Assert.Contains("already exists; password unchanged", second.stdout);
        Assert.Contains("--update-password", second.stdout);

        var afterHash = (await _db.Users.AsNoTracking().SingleAsync()).PasswordHash;
        Assert.Equal(originalHash, afterHash);
    }

    [Fact]
    public async Task EnsureUser_With_Update_Password_Flag_Overwrites_Hash()
    {
        var first = await RunCli(
            "users", "ensure",
            "--email", "alice@example.com",
            "--display-name", "Alice",
            "--password", "alice-password-1");
        Assert.Equal(0, first.exit);
        var originalHash = (await _db.Users.AsNoTracking().SingleAsync()).PasswordHash;

        var second = await RunCli(
            "users", "ensure",
            "--email", "alice@example.com",
            "--display-name", "Alice",
            "--password", "alice-password-2",
            "--update-password");

        Assert.Equal(0, second.exit);
        Assert.Contains("updated password", second.stdout);
        var afterHash = (await _db.Users.AsNoTracking().SingleAsync()).PasswordHash;
        Assert.NotEqual(originalHash, afterHash);
        Assert.NotEqual("alice-password-2", afterHash);
    }

    [Fact]
    public async Task EnsureUser_Without_Email_Returns_Usage_Error()
    {
        var (exit, _, stderr) = await RunCli(
            "users", "ensure",
            "--display-name", "Alice",
            "--password", "alice-password-1");

        Assert.Equal(64, exit);
        Assert.Contains("--email", stderr);
    }

    [Fact]
    public async Task EnsureUser_Without_Password_Returns_Usage_Error()
    {
        var (exit, _, stderr) = await RunCli(
            "users", "ensure",
            "--email", "alice@example.com",
            "--display-name", "Alice");

        Assert.Equal(64, exit);
        Assert.Contains("--password", stderr);
    }

    [Fact]
    public async Task EnsureUser_With_Short_Password_Returns_Usage_Error()
    {
        var (exit, _, stderr) = await RunCli(
            "users", "ensure",
            "--email", "alice@example.com",
            "--display-name", "Alice",
            "--password", "short");

        Assert.Equal(64, exit);
        Assert.Contains("at least 8 characters", stderr);
    }

    [Fact]
    public async Task EnsureUser_Recognises_Alias_ensure_user()
    {
        var (exit, stdout, _) = await RunCli(
            "ensure-user",
            "--email", "alice@example.com",
            "--display-name", "Alice",
            "--password", "alice-password-1");

        Assert.Equal(0, exit);
        Assert.Contains("created user", stdout);
    }

    [Fact]
    public async Task EnsureUser_Reads_From_Environment_Variables()
    {
        // Set per-test env so we don't leak into the rest of the run.
        var oldEmail = Environment.GetEnvironmentVariable("NUBARCA_ADMIN_EMAIL");
        var oldName = Environment.GetEnvironmentVariable("NUBARCA_ADMIN_DISPLAY_NAME");
        var oldPwd = Environment.GetEnvironmentVariable("NUBARCA_ADMIN_PASSWORD");
        try
        {
            Environment.SetEnvironmentVariable("NUBARCA_ADMIN_EMAIL", "bob@example.com");
            Environment.SetEnvironmentVariable("NUBARCA_ADMIN_DISPLAY_NAME", "Bob");
            Environment.SetEnvironmentVariable("NUBARCA_ADMIN_PASSWORD", "bob-password-1");

            var (exit, stdout, _) = await RunCli("users", "ensure");

            Assert.Equal(0, exit);
            Assert.Contains("created user bob@example.com", stdout);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NUBARCA_ADMIN_EMAIL", oldEmail);
            Environment.SetEnvironmentVariable("NUBARCA_ADMIN_DISPLAY_NAME", oldName);
            Environment.SetEnvironmentVariable("NUBARCA_ADMIN_PASSWORD", oldPwd);
        }
    }

    [Fact]
    public async Task DbMigrate_Without_Db_Configured_Returns_Config_Error()
    {
        // Build a service provider that lacks AppDbContext entirely.
        var empty = new ServiceCollection().BuildServiceProvider();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = await CliEntryPoint.RunAsync(
            new[] { "db", "migrate" },
            stdout,
            stderr,
            () => empty);

        Assert.Equal(78, exit);
        Assert.Contains("ConnectionStrings:Postgres", stderr.ToString());
    }

    [Fact]
    public async Task Help_Lists_Available_Commands()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = await CliEntryPoint.RunAsync(
            new[] { "--help" },
            stdout,
            stderr,
            () => _services);

        Assert.Equal(0, exit);
        var text = stdout.ToString();
        Assert.Contains("users ensure", text);
        Assert.Contains("db migrate", text);
        Assert.Contains("NUBARCA_ADMIN_EMAIL", text);
    }

    [Fact]
    public async Task Unknown_Command_Returns_Usage_Error()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = await CliEntryPoint.RunAsync(
            new[] { "wat" },
            stdout,
            stderr,
            () => _services);

        Assert.Equal(64, exit);
        Assert.Contains("Unknown command", stderr.ToString());
    }

    [Fact]
    public void IsCliInvocation_Recognises_Known_Verbs_And_Aliases()
    {
        Assert.True(CliEntryPoint.IsCliInvocation(new[] { "users" }));
        Assert.True(CliEntryPoint.IsCliInvocation(new[] { "db", "migrate" }));
        Assert.True(CliEntryPoint.IsCliInvocation(new[] { "ensure-user" }));
        Assert.True(CliEntryPoint.IsCliInvocation(new[] { "db-migrate" }));
        Assert.True(CliEntryPoint.IsCliInvocation(new[] { "grant-admin" }));
        Assert.True(CliEntryPoint.IsCliInvocation(new[] { "revoke-admin" }));
        Assert.True(CliEntryPoint.IsCliInvocation(new[] { "--help" }));
        Assert.False(CliEntryPoint.IsCliInvocation(Array.Empty<string>()));
        // Plain `--urls` or other host options must NOT be treated as a CLI
        // verb — the web host needs them.
        Assert.False(CliEntryPoint.IsCliInvocation(new[] { "--urls", "http://+:8080" }));
    }

    // ---- slice 46: admin flag + grant-admin / revoke-admin ----------------

    [Fact]
    public async Task EnsureUser_With_Admin_Flag_Creates_Admin_User()
    {
        var (exit, stdout, _) = await RunCli(
            "users", "ensure",
            "--email", "admin@example.com",
            "--display-name", "Admin",
            "--password", "strong-password-1",
            "--admin");

        Assert.Equal(0, exit);
        Assert.Contains("as admin", stdout);
        var row = await _db.Users.AsNoTracking().SingleAsync();
        Assert.True(row.IsAdmin);
    }

    [Fact]
    public async Task EnsureUser_Without_Admin_Flag_Does_Not_Grant_Admin()
    {
        var (exit, _, _) = await RunCli(
            "users", "ensure",
            "--email", "alice@example.com",
            "--display-name", "Alice",
            "--password", "strong-password-1");

        Assert.Equal(0, exit);
        var row = await _db.Users.AsNoTracking().SingleAsync();
        Assert.False(row.IsAdmin);
    }

    [Fact]
    public async Task EnsureUser_With_Admin_Flag_On_Existing_NonAdmin_Upgrades()
    {
        var first = await RunCli(
            "users", "ensure",
            "--email", "alice@example.com",
            "--display-name", "Alice",
            "--password", "strong-password-1");
        Assert.Equal(0, first.exit);
        Assert.False((await _db.Users.AsNoTracking().SingleAsync()).IsAdmin);

        var second = await RunCli(
            "users", "ensure",
            "--email", "alice@example.com",
            "--display-name", "Alice",
            "--password", "strong-password-1",
            "--admin");
        Assert.Equal(0, second.exit);
        Assert.Contains("granted admin", second.stdout);
        Assert.True((await _db.Users.AsNoTracking().SingleAsync()).IsAdmin);
    }

    [Fact]
    public async Task EnsureUser_Without_Admin_Flag_Does_Not_Downgrade_Existing_Admin()
    {
        // Seed an admin user.
        await RunCli(
            "users", "ensure",
            "--email", "alice@example.com",
            "--display-name", "Alice",
            "--password", "strong-password-1",
            "--admin");

        // Re-run without --admin: admin status must NOT change.
        var second = await RunCli(
            "users", "ensure",
            "--email", "alice@example.com",
            "--display-name", "Alice",
            "--password", "strong-password-1");
        Assert.Equal(0, second.exit);
        Assert.True((await _db.Users.AsNoTracking().SingleAsync()).IsAdmin);
    }

    [Fact]
    public async Task GrantAdmin_Creates_Admin_State_On_Existing_User()
    {
        await RunCli(
            "users", "ensure",
            "--email", "alice@example.com",
            "--display-name", "Alice",
            "--password", "strong-password-1");

        var (exit, stdout, _) = await RunCli(
            "users", "grant-admin",
            "--email", "alice@example.com");

        Assert.Equal(0, exit);
        Assert.Contains("is now admin", stdout);
        Assert.True((await _db.Users.AsNoTracking().SingleAsync()).IsAdmin);
    }

    [Fact]
    public async Task RevokeAdmin_Clears_Admin_State()
    {
        await RunCli(
            "users", "ensure",
            "--email", "alice@example.com",
            "--display-name", "Alice",
            "--password", "strong-password-1",
            "--admin");

        var (exit, stdout, _) = await RunCli(
            "users", "revoke-admin",
            "--email", "alice@example.com");

        Assert.Equal(0, exit);
        Assert.Contains("is now not admin", stdout);
        Assert.False((await _db.Users.AsNoTracking().SingleAsync()).IsAdmin);
    }

    [Fact]
    public async Task GrantAdmin_Idempotent_On_Already_Admin()
    {
        await RunCli(
            "users", "ensure",
            "--email", "alice@example.com",
            "--display-name", "Alice",
            "--password", "strong-password-1",
            "--admin");

        var (exit, stdout, _) = await RunCli(
            "users", "grant-admin",
            "--email", "alice@example.com");

        Assert.Equal(0, exit);
        Assert.Contains("already admin", stdout);
    }

    [Fact]
    public async Task GrantAdmin_With_Missing_User_Returns_UsageError()
    {
        var (exit, _, stderr) = await RunCli(
            "users", "grant-admin",
            "--email", "ghost@example.com");

        Assert.Equal(64, exit);
        Assert.Contains("no user with email", stderr.ToString());
    }

    [Fact]
    public async Task GrantAdmin_Without_Email_Returns_UsageError()
    {
        var (exit, _, stderr) = await RunCli("users", "grant-admin");

        Assert.Equal(64, exit);
        Assert.Contains("--email", stderr.ToString());
    }

    [Fact]
    public async Task BareAlias_GrantAdmin_Routes_To_The_Same_Command()
    {
        await RunCli(
            "users", "ensure",
            "--email", "alice@example.com",
            "--display-name", "Alice",
            "--password", "strong-password-1");

        var (exit, _, _) = await RunCli("grant-admin", "--email", "alice@example.com");
        Assert.Equal(0, exit);
        Assert.True((await _db.Users.AsNoTracking().SingleAsync()).IsAdmin);
    }
}
