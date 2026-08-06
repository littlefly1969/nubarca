using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NubArca.Api.Auth;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Users;

namespace NubArca.Api.Tests.Auth;

// SQLite in-memory unit tests for the password-hashing service. Exercises
// PasswordHasher<User> for real (PBKDF2-HMACSHA256); no mocking.
public sealed class AuthServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly UserService _users;
    private readonly AuthService _auth;

    public AuthServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;
        _db = new AppDbContext(options);
        _db.Database.EnsureCreated();

        _users = new UserService(_db, TimeProvider.System);
        _auth = new AuthService(_db, _users, new PasswordHasher<User>());
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task SetPasswordAsync_Stores_A_Hashed_Password()
    {
        var user = await _users.CreateAsync("alice@example.com", "Alice");

        await _auth.SetPasswordAsync(user.Id, "correct-horse-battery-staple");

        var row = await _db.Users.AsNoTracking().SingleAsync();
        Assert.NotNull(row.PasswordHash);
        Assert.NotEqual("correct-horse-battery-staple", row.PasswordHash);
        Assert.True(row.PasswordHash!.Length > 32);
    }

    [Fact]
    public async Task AuthenticateAsync_With_Correct_Password_Returns_User()
    {
        var user = await _users.CreateAsync("alice@example.com", "Alice");
        await _auth.SetPasswordAsync(user.Id, "right-password");

        var authenticated = await _auth.AuthenticateAsync("alice@example.com", "right-password");

        Assert.NotNull(authenticated);
        Assert.Equal(user.Id, authenticated!.Id);
    }

    [Fact]
    public async Task AuthenticateAsync_Is_Case_Insensitive_On_Email()
    {
        var user = await _users.CreateAsync("alice@example.com", "Alice");
        await _auth.SetPasswordAsync(user.Id, "right-password");

        var authenticated = await _auth.AuthenticateAsync("Alice@Example.COM", "right-password");

        Assert.NotNull(authenticated);
        Assert.Equal(user.Id, authenticated!.Id);
    }

    [Fact]
    public async Task AuthenticateAsync_With_Wrong_Password_Returns_Null()
    {
        var user = await _users.CreateAsync("alice@example.com", "Alice");
        await _auth.SetPasswordAsync(user.Id, "right-password");

        var authenticated = await _auth.AuthenticateAsync("alice@example.com", "wrong-password");

        Assert.Null(authenticated);
    }

    [Fact]
    public async Task AuthenticateAsync_With_Unknown_Email_Returns_Null()
    {
        var authenticated = await _auth.AuthenticateAsync("ghost@example.com", "anything");

        Assert.Null(authenticated);
    }

    [Fact]
    public async Task AuthenticateAsync_For_Disabled_User_Returns_Null()
    {
        var user = await _users.CreateAsync("alice@example.com", "Alice");
        await _auth.SetPasswordAsync(user.Id, "right-password");

        var tracked = await _db.Users.FirstAsync(u => u.Id == user.Id);
        tracked.DisabledAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var authenticated = await _auth.AuthenticateAsync("alice@example.com", "right-password");

        Assert.Null(authenticated);
    }

    [Fact]
    public async Task AuthenticateAsync_For_User_Without_PasswordHash_Returns_Null()
    {
        await _users.CreateAsync("alice@example.com", "Alice");

        var authenticated = await _auth.AuthenticateAsync("alice@example.com", "anything");

        Assert.Null(authenticated);
    }

    [Theory]
    [InlineData("", "password")]
    [InlineData("   ", "password")]
    [InlineData(null, "password")]
    [InlineData("user@example.com", "")]
    [InlineData("user@example.com", "   ")]
    [InlineData("user@example.com", null)]
    public async Task AuthenticateAsync_With_Empty_Inputs_Returns_Null(string? email, string? password)
    {
        var authenticated = await _auth.AuthenticateAsync(email, password);
        Assert.Null(authenticated);
    }

    [Fact]
    public async Task SetPasswordAsync_Rejects_Empty_Password()
    {
        var user = await _users.CreateAsync("alice@example.com", "Alice");

        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => _auth.SetPasswordAsync(user.Id, ""));
    }

    [Fact]
    public async Task SetPasswordAsync_For_Missing_User_Throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _auth.SetPasswordAsync(Guid.NewGuid(), "x"));
    }
}
