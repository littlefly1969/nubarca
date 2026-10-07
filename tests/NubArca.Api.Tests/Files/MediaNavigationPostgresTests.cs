using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Tests.Integration;

namespace NubArca.Api.Tests.Files;

[Collection(PostgresIntegrationCollection.Name)]
[Trait("Category", "External")]
public sealed class MediaNavigationPostgresTests(PostgresContainerFixture fixture)
{
    [SkippableTheory]
    [InlineData(ImageSortField.Created)]
    [InlineData(ImageSortField.DateTaken)]
    public async Task DateExtremaHaveMatchingIndexAndWindowOnPostgres(ImageSortField sort)
    {
        Skip.IfNot(fixture.Available, "Docker unavailable; PostgreSQL integration skipped.");
        await fixture.ResetDatabaseAsync();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(fixture.ConnectionString!).Options);
        var owner = Guid.NewGuid(); var blob = Guid.NewGuid();
        db.Users.Add(new User { Id = owner, Email = "boundary@example.com", DisplayName = "Boundary", CreatedAt = DateTime.UtcNow });
        db.BlobObjects.Add(new BlobObject { Id = blob, Sha256 = new string('b', 64), StorageKey = "fixture-only", SizeBytes = 100, ReferenceCount = 2, CreatedAt = DateTime.UtcNow });
        db.BlobMetadata.Add(new BlobMetadata { BlobObjectId = blob, DetectedContentType = "image/jpeg", MediaCategory = "image" });
        foreach (var date in new[] { DateTime.MinValue, DateTime.MaxValue })
            db.FileItems.Add(new FileItem { Id = Guid.NewGuid(), OwnerUserId = owner, BlobObjectId = blob,
                Name = date.Year + ".jpg", MimeType = "image/jpeg", SizeBytes = 100,
                CreatedAt = DateTime.SpecifyKind(date, DateTimeKind.Utc), EffectiveDateTaken = DateTime.SpecifyKind(date, DateTimeKind.Utc) });
        await db.SaveChangesAsync();
        var files = new FileItemService(db, null!, null!, TimeProvider.System);
        var index = await files.MediaNavigationAsync(owner, new ImageFilters(), MediaKindScope.All, sort, ImageSortDirection.Asc, default);
        Assert.Equal(new[] { "0001-01", "9999-12" }, index.Buckets.Select(b => b.Key));
        foreach (var bucket in index.Buckets)
        {
            var window = await files.MediaWindowAsync(owner, new ImageFilters(), MediaKindScope.All, sort, ImageSortDirection.Asc, 50, bucket.Key, null, false, default);
            Assert.NotNull(window);
            Assert.Equal(bucket.Key, window!.Items[0].CreatedAt.ToString("yyyy-MM"));
        }
    }

    [SkippableFact]
    public async Task CancelledNavigationRestoresTheSameConnectionsPlannerSetting()
    {
        Skip.IfNot(fixture.Available, "Docker unavailable; PostgreSQL integration skipped.");
        using var cancellation = new CancellationTokenSource();
        var interrupt = new InterruptRead(cancellation);
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(fixture.ConnectionString!).AddInterceptors(interrupt).Options);
        await db.Database.OpenConnectionAsync();
        var original = await db.Database.SqlQueryRaw<string>("SELECT current_setting('jit') AS \"Value\"").SingleAsync();
        var files = new FileItemService(db, null!, null!, TimeProvider.System);
        interrupt.Armed = true;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => files.MediaNavigationAsync(Guid.NewGuid(),
            new ImageFilters(), MediaKindScope.All, ImageSortField.Name, ImageSortDirection.Asc, cancellation.Token));
        Assert.Null(db.Database.CurrentTransaction);
        Assert.Equal(original, await db.Database.SqlQueryRaw<string>("SELECT current_setting('jit') AS \"Value\"").SingleAsync());
    }

    private sealed class InterruptRead(CancellationTokenSource cancellation) : DbCommandInterceptor
    {
        public bool Armed { get; set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Armed)
            {
                Armed = false;
                cancellation.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }
            return ValueTask.FromResult(result);
        }
    }

    [SkippableTheory]
    [InlineData(ImageSortField.DateTaken, ImageSortDirection.Asc, "2023-03")]
    [InlineData(ImageSortField.DateTaken, ImageSortDirection.Desc, "2023-03")]
    [InlineData(ImageSortField.Name, ImageSortDirection.Asc, "n:é")]
    [InlineData(ImageSortField.Name, ImageSortDirection.Desc, "n:é")]
    public async Task AggregateAndBidirectionalSeeksTranslateOnPostgresWithBoundedWindows(
        ImageSortField sort, ImageSortDirection direction, string target)
    {
        Skip.IfNot(fixture.Available, "Docker unavailable; PostgreSQL integration skipped.");
        await fixture.ResetDatabaseAsync();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(fixture.ConnectionString!).Options);
        var owner = Guid.NewGuid();
        db.Users.Add(new User { Id = owner, Email = "timeline@example.com", DisplayName = "Timeline", CreatedAt = DateTime.UtcNow });
        var blob = Guid.NewGuid();
        db.BlobObjects.Add(new BlobObject { Id = blob, Sha256 = new string('a', 64), StorageKey = "fixture-only", SizeBytes = 100, ReferenceCount = 251, CreatedAt = DateTime.UtcNow });
        db.BlobMetadata.Add(new BlobMetadata { BlobObjectId = blob, DetectedContentType = "image/jpeg", MediaCategory = "image" });
        var ids = new List<Guid>();
        for (var i = 0; i < 250; i++)
        {
            var id = Guid.NewGuid(); ids.Add(id);
            db.FileItems.Add(new FileItem { Id = id, OwnerUserId = owner, BlobObjectId = blob,
                Name = $"photo-{i:D3}.jpg", MimeType = "image/jpeg", SizeBytes = 100,
                CreatedAt = DateTime.UtcNow, EffectiveDateTaken = new DateTime(2021 + i % 5, 1 + i % 5, 15, 0, 0, 0, DateTimeKind.Utc) });
            db.FileItemUserMetadata.Add(new FileItemUserMetadata { Id = Guid.NewGuid(), FileItemId = id,
                Title = i % 3 == 0 ? $"Été-{i:D3}" : i % 3 == 1 ? $"Alba-{i:D3}" : $"Mare-{i:D3}", CreatedAt = DateTime.UtcNow });
        }
        var vault = Guid.NewGuid();
        db.PrivateVaults.Add(new PrivateVault { Id = vault, OwnerUserId = owner, PasswordHash = "fixture", CreatedAt = DateTime.UtcNow });
        db.FileItems.Add(new FileItem { Id = Guid.NewGuid(), OwnerUserId = owner, BlobObjectId = blob,
            PrivateVaultId = vault, Name = "hidden.jpg", MimeType = "image/jpeg", SizeBytes = 100,
            CreatedAt = DateTime.UtcNow, EffectiveDateTaken = new DateTime(2019, 1, 1, 0, 0, 0, DateTimeKind.Utc) });
        await db.SaveChangesAsync();
        // These read methods never use blob bytes or thumbnail generation.
        var files = new FileItemService(db, null!, null!, TimeProvider.System);
        var filters = new ImageFilters();
        // Keep the SAME physical connection open: pool reset must not hide a
        // leaked planner setting or an unfinished navigation transaction.
        await db.Database.OpenConnectionAsync();
        var originalJit = await db.Database.SqlQueryRaw<string>("SELECT current_setting('jit') AS \"Value\"").SingleAsync();
        var index = await files.MediaNavigationAsync(owner, filters, MediaKindScope.Image, sort, direction, default);
        Assert.Equal(250, index.Buckets.Sum(bucket => bucket.Count));
        Assert.Contains(index.Buckets, bucket => bucket.Key == target);
        if (sort == ImageSortField.Name) target = index.Buckets[index.Buckets.Count / 2].Key;
        var window = await files.MediaWindowAsync(owner, filters, MediaKindScope.Image, sort, direction, 7, target, null, false, default);
        Assert.NotNull(window);
        Assert.Equal(7, window!.Items.Count);
        Assert.All(window.Items, item => Assert.Contains(item.Id, ids));
        Assert.NotNull(window.PreviousCursor); Assert.NotNull(window.NextCursor);
        Assert.True(ImageCursor.TryParse(window.PreviousCursor, out var cursor));
        var previous = await files.MediaWindowAsync(owner, filters, MediaKindScope.Image, sort, direction, 7, null, cursor, true, default);
        Assert.Equal(7, previous!.Items.Count);
        Assert.Empty(previous.Items.Select(item => item.Id).Intersect(window.Items.Select(item => item.Id)));
        Assert.True(ImageCursor.TryParse(previous.NextCursor, out var next));
        var back = await files.ListMediaPageAsync(owner, 7, next, filters, MediaKindScope.Image, sort, direction);
        Assert.Equal(window.Items.Select(item => item.Id), back.Items.Select(item => item.Id));
        if (sort == ImageSortField.DateTaken && direction == ImageSortDirection.Desc)
        {
            // Walk across every tied-date boundary in the production provider.
            // Compare with PostgreSQL's own date/UUID order, not a client UUID
            // comparer whose tie-break ordering could differ from the database.
            var expected = await db.FileItems.Where(f => f.OwnerUserId == owner && f.PrivateVaultId == null)
                .OrderByDescending(f => f.EffectiveDateTaken).ThenByDescending(f => f.Id)
                .Select(f => f.Id).ToListAsync();
            var paged = new List<Guid>();
            ImageCursor? continuation = null;
            do
            {
                var page = await files.ListMediaPageAsync(owner, 7, continuation, filters, MediaKindScope.Image, sort, direction);
                paged.AddRange(page.Items.Select(item => item.Id));
                Assert.True(paged.Count <= expected.Count, "Cursor pagination must not repeat a page.");
                continuation = null;
                if (page.NextCursor is not null)
                {
                    Assert.True(ImageCursor.TryParse(page.NextCursor, out continuation));
                }
            } while (continuation is not null);
            Assert.Equal(expected, paged);
            Assert.Equal(expected.Count, paged.Distinct().Count());
        }
        Assert.Null(db.Database.CurrentTransaction);
        Assert.Equal(originalJit, await db.Database.SqlQueryRaw<string>("SELECT current_setting('jit') AS \"Value\"").SingleAsync());
    }
}
