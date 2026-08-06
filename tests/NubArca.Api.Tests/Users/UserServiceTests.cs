using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Users;

namespace NubArca.Api.Tests.Users;

// SQLite in-memory unit tests. Real transactions, real unique constraints, no Docker.
// The PostgreSQL-specific catch-on-unique-violation race-recovery branch is not
// reached here (sequential tests cannot race); UserServicePostgresTests covers it.
public sealed class UserServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly UserService _service;

    public UserServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;
        _db = new AppDbContext(dbOptions);
        _db.Database.EnsureCreated();

        _service = new UserService(_db, TimeProvider.System);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task CreateAsync_New_Email_Persists_User_With_Expected_Fields()
    {
        var before = DateTime.UtcNow;
        var user = await _service.CreateAsync("alice@example.com", "Alice");
        var after = DateTime.UtcNow;

        Assert.NotEqual(Guid.Empty, user.Id);
        Assert.Equal("alice@example.com", user.Email);
        Assert.Equal("Alice", user.DisplayName);
        Assert.Null(user.PasswordHash);
        Assert.Null(user.DisabledAt);
        Assert.InRange(user.CreatedAt, before.AddSeconds(-1), after.AddSeconds(1));

        var row = await _db.Users.AsNoTracking().SingleAsync();
        Assert.Equal(user.Id, row.Id);
        Assert.Equal("alice@example.com", row.Email);
    }

    [Fact]
    public async Task CreateAsync_Normalizes_Email_To_Lowercase_And_Trims()
    {
        var user = await _service.CreateAsync("  Bob@Example.COM  ", "Bob");

        Assert.Equal("bob@example.com", user.Email);
    }

    [Fact]
    public async Task CreateAsync_Trims_DisplayName()
    {
        var user = await _service.CreateAsync("carol@example.com", "  Carol  ");

        Assert.Equal("Carol", user.DisplayName);
    }

    [Fact]
    public async Task CreateAsync_Duplicate_Email_Throws_UserAlreadyExists()
    {
        await _service.CreateAsync("dup@example.com", "First");

        var ex = await Assert.ThrowsAsync<UserAlreadyExistsException>(
            () => _service.CreateAsync("dup@example.com", "Second"));

        Assert.Equal("dup@example.com", ex.Email);
        Assert.Equal(1, await _db.Users.CountAsync());
    }

    [Fact]
    public async Task CreateAsync_Duplicate_Email_Different_Case_Throws_UserAlreadyExists()
    {
        await _service.CreateAsync("Same@Example.com", "First");

        await Assert.ThrowsAsync<UserAlreadyExistsException>(
            () => _service.CreateAsync("same@example.com", "Second"));

        Assert.Equal(1, await _db.Users.CountAsync());
    }

    [Theory]
    [InlineData("", "Display")]
    [InlineData("   ", "Display")]
    [InlineData(null, "Display")]
    [InlineData("user@example.com", "")]
    [InlineData("user@example.com", "   ")]
    [InlineData("user@example.com", null)]
    public async Task CreateAsync_Rejects_Empty_Or_Whitespace_Inputs(string? email, string? displayName)
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => _service.CreateAsync(email!, displayName!));
    }

    [Fact]
    public async Task GetByIdAsync_Returns_Created_User()
    {
        var created = await _service.CreateAsync("findme@example.com", "Findme");

        var found = await _service.GetByIdAsync(created.Id);

        Assert.NotNull(found);
        Assert.Equal(created.Id, found!.Id);
        Assert.Equal("findme@example.com", found.Email);
    }

    [Fact]
    public async Task GetByIdAsync_Missing_Id_Returns_Null()
    {
        var found = await _service.GetByIdAsync(Guid.NewGuid());
        Assert.Null(found);
    }

    [Fact]
    public async Task GetByEmailAsync_Returns_Created_User()
    {
        var created = await _service.CreateAsync("look@example.com", "Look");

        var found = await _service.GetByEmailAsync("look@example.com");

        Assert.NotNull(found);
        Assert.Equal(created.Id, found!.Id);
    }

    [Fact]
    public async Task GetByEmailAsync_Is_Case_Insensitive_And_Trims()
    {
        await _service.CreateAsync("case@example.com", "Case");

        var found = await _service.GetByEmailAsync("  CASE@Example.com  ");

        Assert.NotNull(found);
    }

    [Fact]
    public async Task GetByEmailAsync_Missing_Email_Returns_Null()
    {
        var found = await _service.GetByEmailAsync("ghost@example.com");
        Assert.Null(found);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task GetByEmailAsync_Rejects_Empty_Or_Whitespace(string? email)
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() => _service.GetByEmailAsync(email!));
    }
}
