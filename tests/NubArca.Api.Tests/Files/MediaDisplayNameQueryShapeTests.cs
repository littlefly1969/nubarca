using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using Xunit;

namespace NubArca.Api.Tests.Files;

// Query-SHAPE guard for the display-name projection and the album-membership
// filter, asserted on the EF-generated SQL (no rows needed, no Docker).
//
// The point of both features is that they cost ONE statement: the owner title
// resolves through a correlated scalar subquery inside the page SELECT, and
// album membership is a plain EXISTS / NOT EXISTS. If either ever degrades into
// a per-card lookup (an N+1), the page would issue one SELECT per row and these
// assertions on the single captured statement would fail.
public sealed class MediaDisplayNameQueryShapeTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly List<string> _sql = new();
    private readonly AppDbContext _db;
    private readonly FileItemService _service;
    private readonly Guid _owner = Guid.NewGuid();

    public MediaDisplayNameQueryShapeTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .LogTo(s => { if (s.Contains("SELECT", StringComparison.Ordinal)) _sql.Add(s); }, LogLevel.Information)
            .Options;
        _db = new AppDbContext(options);
        _db.Database.EnsureCreated();
        _db.Users.Add(new User
        {
            Id = _owner,
            Email = $"o-{_owner:N}@x.t",
            DisplayName = "O",
            CreatedAt = DateTime.UtcNow,
        });
        _db.SaveChanges();
        _service = new FileItemService(_db, null!, null!, TimeProvider.System);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private string PageSql()
    {
        var sql = _sql.LastOrDefault(s => s.Contains("file_items", StringComparison.Ordinal));
        Assert.NotNull(sql);
        return sql!;
    }

    // Gallery statements issued while listing one page. Two is the budget: the
    // server-authoritative COUNT and the page SELECT itself. EF's own schema
    // probes (sqlite_master) are not gallery queries and are excluded.
    private int StatementCount => _sql.Count(s => s.Contains("file_items", StringComparison.Ordinal));

    // The text immediately preceding the album_items subquery, which is what
    // says whether that particular EXISTS is negated. The surrounding gallery
    // query has its own unrelated NOT EXISTS (the blob-metadata fallback), so a
    // whole-statement search for "NOT EXISTS" would prove nothing.
    private static string BeforeAlbumSubquery(string sql)
    {
        var index = sql.IndexOf("\"album_items\"", StringComparison.Ordinal);
        Assert.True(index > 0, "expected an album_items subquery");
        return sql[Math.Max(0, index - 80)..index];
    }

    [Fact]
    public async Task Page_Projection_Reads_The_Title_In_The_Same_Statement()
    {
        await _service.ListImagesPageAsync(_owner, 50, null, new ImageFilters());

        var sql = PageSql();
        Assert.Contains("file_item_user_metadata", sql, StringComparison.Ordinal);
        Assert.True(StatementCount <= 2,
            $"expected at most 2 statements for one page, saw {StatementCount}:\n{string.Join("\n---\n", _sql)}");
    }

    [Fact]
    public async Task Sort_By_Name_Orders_On_The_Coalesced_Lowercased_Display_Name()
    {
        await _service.ListImagesPageAsync(
            _owner, 50, null, new ImageFilters(), ImageSortField.Name, ImageSortDirection.Asc);

        var sql = PageSql();
        // lower(COALESCE(title, name)) — the SQL form of MediaDisplayName.SortKey.
        Assert.Contains("ORDER BY", sql, StringComparison.Ordinal);
        Assert.Contains("COALESCE", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("lower(", sql, StringComparison.OrdinalIgnoreCase);
        Assert.True(StatementCount <= 2,
            $"expected at most 2 statements for one page, saw {StatementCount}");
    }

    [Fact]
    public async Task Album_Membership_Assigned_Translates_To_A_Single_Exists()
    {
        await _service.ListImagesPageAsync(
            _owner, 50, null, new ImageFilters { AlbumMembership = AlbumMembershipFilter.Assigned });

        var sql = PageSql();
        var lead = BeforeAlbumSubquery(sql);
        Assert.Contains("EXISTS", lead, StringComparison.Ordinal);
        Assert.DoesNotContain("NOT EXISTS", lead, StringComparison.Ordinal);
        Assert.True(StatementCount <= 2,
            $"expected at most 2 statements for one page, saw {StatementCount}");
    }

    [Fact]
    public async Task Album_Membership_Unassigned_Translates_To_A_Single_Not_Exists()
    {
        await _service.ListImagesPageAsync(
            _owner, 50, null, new ImageFilters { AlbumMembership = AlbumMembershipFilter.Unassigned });

        var sql = PageSql();
        Assert.Contains("NOT EXISTS", BeforeAlbumSubquery(sql), StringComparison.Ordinal);
        Assert.True(StatementCount <= 2,
            $"expected at most 2 statements for one page, saw {StatementCount}");
    }

    [Fact]
    public async Task Album_Membership_Any_Adds_No_Album_Predicate()
    {
        await _service.ListImagesPageAsync(
            _owner, 50, null, new ImageFilters { AlbumMembership = AlbumMembershipFilter.Any });

        Assert.DoesNotContain("album_items", PageSql(), StringComparison.Ordinal);
    }
}
