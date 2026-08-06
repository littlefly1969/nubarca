using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using Xunit;

namespace NubArca.Api.Tests.Admin;

// Regression guard for the admin-import status/kind column widths. The
// deleted-content-import-skip feature added disjoint statuses longer than the
// original varchar(20) (e.g. "skipped_previously_deleted" = 26 chars), which on
// PostgreSQL raised "22001: value too long for type character varying(20)" and
// crashed the import. These assertions pin the widened bounds so a regression to
// 20 (or a new status that would overflow) fails in CI instead of in prod.
public sealed class AdminImportStatusLengthTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;

    public AdminImportStatusLengthTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        _db = new AppDbContext(options);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private int MaxLen<T>(string property) =>
        _db.Model.FindEntityType(typeof(T))!.FindProperty(property)!.GetMaxLength()
            ?? throw new Xunit.Sdk.XunitException($"{typeof(T).Name}.{property} has no MaxLength configured.");

    [Fact]
    public void Import_Status_And_Kind_Columns_Are_Widened_To_64()
    {
        Assert.Equal(64, MaxLen<AdminImportItem>(nameof(AdminImportItem.Status)));
        Assert.Equal(64, MaxLen<AdminImportItem>(nameof(AdminImportItem.Kind)));
        Assert.Equal(64, MaxLen<AdminImportRun>(nameof(AdminImportRun.Status)));
        Assert.Equal(64, MaxLen<AdminImportRun>(nameof(AdminImportRun.Phase)));
    }

    [Fact]
    public void Longest_Skip_Statuses_Fit_Within_The_Configured_Bound()
    {
        var itemStatusMax = MaxLen<AdminImportItem>(nameof(AdminImportItem.Status));

        // The two disjoint skip reasons that overflowed the old varchar(20).
        Assert.Equal("skipped_previously_deleted", AdminImportItemStatuses.SkippedPreviouslyDeleted);
        Assert.Equal("skipped_already_present", AdminImportItemStatuses.SkippedAlreadyPresent);
        Assert.True(AdminImportItemStatuses.SkippedPreviouslyDeleted.Length > 20,
            "sanity: the regressing status really is longer than the old bound");

        // Every known item status must fit the configured column width.
        string[] itemStatuses =
        {
            AdminImportItemStatuses.Pending, AdminImportItemStatuses.Importing,
            AdminImportItemStatuses.Imported, AdminImportItemStatuses.Skipped,
            AdminImportItemStatuses.SkippedPreviouslyDeleted, AdminImportItemStatuses.SkippedAlreadyPresent,
            AdminImportItemStatuses.Conflict, AdminImportItemStatuses.Failed, AdminImportItemStatuses.Cancelled,
        };
        foreach (var s in itemStatuses)
        {
            Assert.True(s.Length <= itemStatusMax, $"status '{s}' ({s.Length}) exceeds column width {itemStatusMax}");
        }
    }

    [Fact]
    public async Task Persists_Long_Skip_Statuses_Round_Trip()
    {
        // On SQLite the length is not enforced, but this proves the mapping
        // accepts and round-trips the long values end-to-end (the Postgres bound
        // is asserted structurally above).
        _db.Database.EnsureCreated();

        var run = new AdminImportRun
        {
            Id = Guid.NewGuid(),
            RootId = "root",
            SourceRelativePath = "src",
            Status = AdminImportStatuses.Running,
            Phase = AdminImportPhases.Importing,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.Set<AdminImportRun>().Add(run);
        _db.Set<AdminImportItem>().Add(new AdminImportItem
        {
            Id = Guid.NewGuid(),
            ImportRunId = run.Id,
            Ordinal = 1,
            Kind = AdminImportItemKinds.File,
            RelativePath = "a.jpg",
            Status = AdminImportItemStatuses.SkippedPreviouslyDeleted,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();

        var readBack = await _db.Set<AdminImportItem>()
            .AsNoTracking().SingleAsync(i => i.ImportRunId == run.Id);
        Assert.Equal(AdminImportItemStatuses.SkippedPreviouslyDeleted, readBack.Status);
    }
}
