using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.ShareLinks;
using NubArca.Api.Storage;

namespace NubArca.Api.Tests.ShareLinks;

public sealed class ShareLinkServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly string _storageRoot;
    private readonly LocalFileSystemBlobStorage _storage;
    private readonly BlobService _blobService;
    private readonly FileItemService _files;
    private readonly ShareLinkService _shareLinks;

    public ShareLinkServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;
        _db = new AppDbContext(dbOptions);
        _db.Database.EnsureCreated();

        _storageRoot = Path.Combine(Path.GetTempPath(), $"nubarca-share-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_storageRoot);

        var blobOptions = Options.Create(new BlobStorageOptions { RootPath = _storageRoot });
        _storage = new LocalFileSystemBlobStorage(blobOptions);
        _blobService = new BlobService(_storage, _db, TimeProvider.System);
        var thumbs = new FileThumbnailService(
            _db, _blobService, _storage, new SyntheticVideoPosterProvider(),
            TimeProvider.System, NullLogger<FileThumbnailService>.Instance,
            Options.Create(new ImageProcessingOptions()));
        _files = new FileItemService(_db, _blobService, thumbs, TimeProvider.System);
        _shareLinks = new ShareLinkService(_db, TimeProvider.System);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        try
        {
            if (Directory.Exists(_storageRoot))
            {
                Directory.Delete(_storageRoot, recursive: true);
            }
        }
        catch
        {
            // best effort
        }
    }

    private async Task<User> SeedUserAsync(string email = "owner@example.com")
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = email,
            DisplayName = "Owner",
            CreatedAt = DateTime.UtcNow,
        };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();
        return user;
    }

    private async Task<FileItem> SeedFileAsync(Guid ownerId, string name = "doc.txt", string mime = "text/plain", string content = "x")
    {
        return await _files.CreateAsync(ownerId, null, name, mime, new MemoryStream(Encoding.UTF8.GetBytes(content)));
    }

    private static string Sha256Hex(string input)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input)));

    [Fact]
    public async Task CreateAsync_For_Owned_File_Returns_Raw_Token_And_Persists_Only_Hash()
    {
        var owner = await SeedUserAsync();
        var file = await SeedFileAsync(owner.Id);

        var result = await _shareLinks.CreateAsync(owner.Id, file.Id, expiresAt: null, maxDownloads: null);

        Assert.NotNull(result);
        Assert.NotEqual(Guid.Empty, result!.Id);
        Assert.False(string.IsNullOrWhiteSpace(result.Token));

        var row = await _db.ShareLinks.AsNoTracking().SingleAsync();
        Assert.Equal(result.Id, row.Id);
        Assert.NotEqual(result.Token, row.TokenHash);
        Assert.Equal(Sha256Hex(result.Token), row.TokenHash);
        Assert.Equal(64, row.TokenHash.Length);
        Assert.Equal(0, row.DownloadCount);
        Assert.Null(row.RevokedAt);
        Assert.Null(row.LastAccessedAt);
    }

    [Fact]
    public async Task CreateAsync_For_Foreign_File_Returns_Null()
    {
        var alice = await SeedUserAsync("alice@example.com");
        var bob = await SeedUserAsync("bob@example.com");
        var aliceFile = await SeedFileAsync(alice.Id);

        var result = await _shareLinks.CreateAsync(bob.Id, aliceFile.Id, null, null);

        Assert.Null(result);
        Assert.Equal(0, await _db.ShareLinks.CountAsync());
    }

    [Fact]
    public async Task CreateAsync_For_Missing_File_Returns_Null()
    {
        var owner = await SeedUserAsync();

        var result = await _shareLinks.CreateAsync(owner.Id, Guid.NewGuid(), null, null);

        Assert.Null(result);
    }

    [Fact]
    public async Task CreateAsync_For_Soft_Deleted_File_Returns_Null()
    {
        var owner = await SeedUserAsync();
        var file = await SeedFileAsync(owner.Id);

        var tracked = await _db.FileItems.FirstAsync(f => f.Id == file.Id);
        tracked.DeletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var result = await _shareLinks.CreateAsync(owner.Id, file.Id, null, null);

        Assert.Null(result);
    }

    [Fact]
    public async Task RevokeAsync_For_Owned_Link_Sets_RevokedAt()
    {
        var owner = await SeedUserAsync();
        var file = await SeedFileAsync(owner.Id);
        var created = await _shareLinks.CreateAsync(owner.Id, file.Id, null, null);

        var revoked = await _shareLinks.RevokeAsync(owner.Id, created!.Id);

        Assert.True(revoked);
        var row = await _db.ShareLinks.AsNoTracking().SingleAsync();
        Assert.NotNull(row.RevokedAt);
    }

    [Fact]
    public async Task RevokeAsync_For_Foreign_Link_Returns_False()
    {
        var alice = await SeedUserAsync("alice@example.com");
        var bob = await SeedUserAsync("bob@example.com");
        var aliceFile = await SeedFileAsync(alice.Id);
        var aliceLink = await _shareLinks.CreateAsync(alice.Id, aliceFile.Id, null, null);

        var revoked = await _shareLinks.RevokeAsync(bob.Id, aliceLink!.Id);

        Assert.False(revoked);
        var row = await _db.ShareLinks.AsNoTracking().SingleAsync();
        Assert.Null(row.RevokedAt);
    }

    [Fact]
    public async Task RevokeAsync_For_Missing_Link_Returns_False()
    {
        var owner = await SeedUserAsync();

        var revoked = await _shareLinks.RevokeAsync(owner.Id, Guid.NewGuid());

        Assert.False(revoked);
    }

    [Fact]
    public async Task ConsumeAsync_With_Valid_Token_Returns_File_And_Increments_Counters()
    {
        var owner = await SeedUserAsync();
        var file = await SeedFileAsync(owner.Id);
        var created = await _shareLinks.CreateAsync(owner.Id, file.Id, null, null);

        var consumed = await _shareLinks.ConsumeAsync(created!.Token);

        Assert.NotNull(consumed);
        Assert.Equal(file.Id, consumed!.FileItemId);
        Assert.Equal(owner.Id, consumed.OwnerUserId);

        var row = await _db.ShareLinks.AsNoTracking().SingleAsync();
        Assert.Equal(1, row.DownloadCount);
        Assert.NotNull(row.LastAccessedAt);
    }

    [Fact]
    public async Task ConsumeAsync_With_Invalid_Token_Returns_Null()
    {
        var consumed = await _shareLinks.ConsumeAsync("not-a-real-token");
        Assert.Null(consumed);
    }

    [Fact]
    public async Task ConsumeAsync_With_Revoked_Token_Returns_Null()
    {
        var owner = await SeedUserAsync();
        var file = await SeedFileAsync(owner.Id);
        var created = await _shareLinks.CreateAsync(owner.Id, file.Id, null, null);
        await _shareLinks.RevokeAsync(owner.Id, created!.Id);

        var consumed = await _shareLinks.ConsumeAsync(created.Token);

        Assert.Null(consumed);
    }

    [Fact]
    public async Task ConsumeAsync_With_Expired_Token_Returns_Null()
    {
        var owner = await SeedUserAsync();
        var file = await SeedFileAsync(owner.Id);
        var created = await _shareLinks.CreateAsync(
            owner.Id, file.Id, expiresAt: DateTime.UtcNow.AddHours(1), maxDownloads: null);

        // Move expiration into the past directly.
        var tracked = await _db.ShareLinks.FirstAsync();
        tracked.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
        await _db.SaveChangesAsync();

        var consumed = await _shareLinks.ConsumeAsync(created!.Token);

        Assert.Null(consumed);
    }

    [Fact]
    public async Task ConsumeAsync_Enforces_MaxDownloads()
    {
        var owner = await SeedUserAsync();
        var file = await SeedFileAsync(owner.Id);
        var created = await _shareLinks.CreateAsync(owner.Id, file.Id, null, maxDownloads: 2);

        var first = await _shareLinks.ConsumeAsync(created!.Token);
        var second = await _shareLinks.ConsumeAsync(created.Token);
        var third = await _shareLinks.ConsumeAsync(created.Token);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Null(third);

        var row = await _db.ShareLinks.AsNoTracking().SingleAsync();
        Assert.Equal(2, row.DownloadCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task ConsumeAsync_With_Empty_Or_Null_Token_Returns_Null(string? token)
    {
        var consumed = await _shareLinks.ConsumeAsync(token!);
        Assert.Null(consumed);
    }
}
