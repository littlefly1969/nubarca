using Microsoft.EntityFrameworkCore;
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
    }
}
