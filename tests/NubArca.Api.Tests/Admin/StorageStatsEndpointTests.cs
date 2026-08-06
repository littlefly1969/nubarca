using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Admin;
using NubArca.Api.Data;
using NubArca.Api.Files;
using NubArca.Api.Folders;
using NubArca.Api.ShareLinks;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Tests.Metadata;
using NubArca.Api.Metadata;
using NubArca.Api.Domain;

namespace NubArca.Api.Tests.Admin;

public sealed class StorageStatsEndpointTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public StorageStatsEndpointTests()
    {
        _factory = new SqliteWebApplicationFactory();
        _factory.EnsureDatabaseCreated();
    }

    public void Dispose() => _factory.Dispose();

    // Slice 46: /api/admin/* is admin-only. Each test that needs to hit the
    // endpoint successfully promotes the seed user before logging in so the
    // cookie carries the admin role claim from the start.
    private async Task<(Guid UserId, HttpClient Client)> CreateAdminClientAsync(
        string email = "owner@example.com")
    {
        var userId = await _factory.SeedUserAsync(email);
        await _factory.PromoteToAdminAsync(userId);
        var client = await _factory.LoginAsync(email);
        return (userId, client);
    }

    [Fact]
    public async Task Get_Without_Auth_Returns_401()
    {
        var anonymous = _factory.CreateClient();
        var response = await anonymous.GetAsync("/api/admin/storage-stats");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Get_With_Auth_On_Empty_Database_Returns_All_Zero_Counters()
    {
        var (_, client) = await CreateAdminClientAsync();

        var response = await client.GetAsync("/api/admin/storage-stats");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<StorageStatsResponse>();
        Assert.NotNull(body);

        // The authentication step in CreateAuthenticatedClientAsync seeded one
        // user, so users.Total is 1. Everything else is empty.
        Assert.Equal(1, body!.Users.Total);
        Assert.Equal(1, body.Users.Active);
        Assert.Equal(0, body.Users.Disabled);

        Assert.Equal(0, body.Folders.Total);
        Assert.Equal(0, body.Folders.Active);
        Assert.Equal(0, body.Folders.SoftDeleted);

        Assert.Equal(0, body.Files.Total);
        Assert.Equal(0, body.Files.Active);
        Assert.Equal(0, body.Files.SoftDeleted);
        Assert.Equal(0L, body.Files.LogicalBytesTotal);
        Assert.Equal(0L, body.Files.LogicalBytesIncludingTrash);

        Assert.Equal(0, body.Blobs.Total);
        Assert.Equal(0, body.Blobs.ZeroReference);
        Assert.Equal(0, body.Blobs.ZeroReferenceBeyondGrace);
        Assert.Equal(0L, body.Blobs.PhysicalBytesTotal);

        Assert.Equal(0, body.Images.ImageFilesCount);
        Assert.Equal(0, body.Images.FilesWithDimensionsCount);
        Assert.Equal(0, body.Images.ThumbnailCount);
        Assert.Equal(0L, body.Images.ThumbnailBlobBytes);

        Assert.Equal(0, body.ShareLinks.Total);
        Assert.Equal(0, body.ShareLinks.Active);
        Assert.Equal(0, body.ShareLinks.Revoked);
        Assert.Equal(0, body.ShareLinks.Expired);
        Assert.Equal(0, body.ShareLinks.Exhausted);

        // CreateAuthenticatedClientAsync calls /api/auth/login, which writes
        // a single `auth.login.success` audit row. Anything > 1 would mean an
        // unintended audit-side effect crept in.
        Assert.Equal(1, body.Audit.Total);

        // Defaults from FileItemSweeperOptions / BlobJanitorOptions.
        Assert.False(body.Cleanup.FileItemSweeper.Enabled);
        Assert.False(body.Cleanup.BlobJanitor.Enabled);
        Assert.Equal(5, body.Cleanup.FileItemSweeper.IntervalMinutes);
        Assert.Equal(1440, body.Cleanup.FileItemSweeper.GraceMinutes);
        Assert.Equal(5, body.Cleanup.BlobJanitor.IntervalMinutes);
        Assert.Equal(1440, body.Cleanup.BlobJanitor.GraceMinutes);

        // Slice 64 additive blocks — every count zero on an empty database,
        // except CurrentVersion which always reflects the running extractor.
        Assert.Equal(0, body.Media.ImagesCount);
        Assert.Equal(0, body.Media.VideosCount);
        Assert.Equal(0, body.Media.OtherCount);

        Assert.Equal(0, body.Extraction.Pending);
        Assert.Equal(0, body.Extraction.Completed);
        Assert.True(body.Extraction.CurrentVersion >= 1);
        Assert.Equal(0, body.Extraction.AtCurrentVersion);
        Assert.Equal(0, body.Extraction.BelowCurrentVersion);

        Assert.Equal(0, body.Derivatives.SmallThumbnailCount);
        Assert.Equal(0, body.Derivatives.MediumPreviewCount);
        Assert.Equal(0, body.Derivatives.VideoPosterCount);
        Assert.Equal(0, body.Derivatives.ImagesMissingSmall);
        Assert.Equal(0, body.Derivatives.VideosMissingPoster);

        Assert.Equal(0, body.UserMetadata.TotalRows);
        Assert.Equal(0, body.UserMetadata.Favorites);

        Assert.Equal(0, body.SensitiveAggregates.BlobsWithGps);
        Assert.Equal(0, body.SensitiveAggregates.BlobsWithBodySerial);
        Assert.Equal(0, body.SensitiveAggregates.MetadataStripEvents);
    }

    [Fact]
    public async Task Get_With_Seeded_Data_Returns_Correct_Aggregates()
    {
        var (owner, client) = await CreateAdminClientAsync();

        // Create one root folder + two files (one will be soft-deleted).
        using (var scope = _factory.Services.CreateScope())
        {
            var folders = scope.ServiceProvider.GetRequiredService<IFolderService>();
            var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();

            await folders.CreateAsync(owner, parentFolderId: null, name: "Photos");
            await files.CreateAsync(owner, null, "alpha.txt", "text/plain",
                new MemoryStream(Encoding.UTF8.GetBytes("alpha"))); // 5 bytes
            var beta = await files.CreateAsync(owner, null, "beta.txt", "text/plain",
                new MemoryStream(Encoding.UTF8.GetBytes("beta-content"))); // 12 bytes
            await files.SoftDeleteAsync(owner, beta.Id);
        }

        var response = await client.GetAsync("/api/admin/storage-stats");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<StorageStatsResponse>())!;

        Assert.Equal(1, body.Folders.Total);
        Assert.Equal(1, body.Folders.Active);
        Assert.Equal(0, body.Folders.SoftDeleted);

        Assert.Equal(2, body.Files.Total);
        Assert.Equal(1, body.Files.Active);
        Assert.Equal(1, body.Files.SoftDeleted);

        // alpha.txt is active (5 bytes); beta is soft-deleted (12 bytes).
        Assert.Equal(5L, body.Files.LogicalBytesTotal);
        Assert.Equal(17L, body.Files.LogicalBytesIncludingTrash);

        // Two distinct contents → two blob rows. Beta's blob has reference
        // count 0 after soft-delete but remains restorable and therefore has
        // no purge-eligibility timestamp; BeyondGrace must stay 0.
        Assert.Equal(2, body.Blobs.Total);
        Assert.Equal(1, body.Blobs.ZeroReference);
        Assert.Equal(0, body.Blobs.ZeroReferenceBeyondGrace);
        Assert.Equal(17L, body.Blobs.PhysicalBytesTotal);

        // No images uploaded (mime is text/plain).
        Assert.Equal(0, body.Images.ImageFilesCount);
        Assert.Equal(0, body.Images.FilesWithDimensionsCount);
    }

    [Fact]
    public async Task Get_With_ShareLinks_Buckets_Revoked_Expired_Exhausted_Correctly()
    {
        var (owner, client) = await CreateAdminClientAsync();
        Guid activeId, revokedId, expiredId, exhaustedId;

        using (var scope = _factory.Services.CreateScope())
        {
            var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
            var shares = scope.ServiceProvider.GetRequiredService<IShareLinkService>();

            var f = await files.CreateAsync(owner, null, "doc.txt", "text/plain",
                new MemoryStream("x"u8.ToArray()));

            var active = await shares.CreateAsync(owner, f.Id, expiresAt: null, maxDownloads: null);
            var revoked = await shares.CreateAsync(owner, f.Id, expiresAt: null, maxDownloads: null);
            await shares.RevokeAsync(owner, revoked!.Id);
            var expired = await shares.CreateAsync(owner, f.Id,
                expiresAt: DateTime.UtcNow.AddHours(1), maxDownloads: null);
            var exhausted = await shares.CreateAsync(owner, f.Id,
                expiresAt: null, maxDownloads: 1);

            activeId = active!.Id;
            revokedId = revoked.Id;
            expiredId = expired!.Id;
            exhaustedId = exhausted!.Id;

            // Push the "expired" link's ExpiresAt into the past and bump the
            // "exhausted" link's DownloadCount to its MaxDownloads.
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var expiredRow = await db.ShareLinks.FirstAsync(s => s.Id == expiredId);
            expiredRow.ExpiresAt = DateTime.UtcNow.AddMinutes(-5);
            var exhaustedRow = await db.ShareLinks.FirstAsync(s => s.Id == exhaustedId);
            exhaustedRow.DownloadCount = exhaustedRow.MaxDownloads!.Value;
            await db.SaveChangesAsync();
        }

        var response = await client.GetAsync("/api/admin/storage-stats");
        var body = (await response.Content.ReadFromJsonAsync<StorageStatsResponse>())!;

        Assert.Equal(4, body.ShareLinks.Total);
        Assert.Equal(1, body.ShareLinks.Active);
        Assert.Equal(1, body.ShareLinks.Revoked);
        Assert.Equal(1, body.ShareLinks.Expired);
        Assert.Equal(1, body.ShareLinks.Exhausted);

        // Sanity: the four buckets sum exactly to Total (revoked precedence
        // means a revoked-and-expired row counts only as revoked).
        Assert.Equal(
            body.ShareLinks.Total,
            body.ShareLinks.Active + body.ShareLinks.Revoked
                + body.ShareLinks.Expired + body.ShareLinks.Exhausted);

        // Use the ids so the compiler doesn't warn — and double-check our
        // seed didn't accidentally insert a fifth row.
        Assert.NotEqual(Guid.Empty, activeId);
        Assert.NotEqual(Guid.Empty, revokedId);
        Assert.NotEqual(Guid.Empty, expiredId);
        Assert.NotEqual(Guid.Empty, exhaustedId);
    }

    [Fact]
    public async Task Get_Counts_ZeroReference_Blobs_Beyond_Grace()
    {
        var (owner, client) = await CreateAdminClientAsync();

        using (var scope = _factory.Services.CreateScope())
        {
            var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var f = await files.CreateAsync(owner, null, "old.txt", "text/plain",
                new MemoryStream("old"u8.ToArray()));
            await files.SoftDeleteAsync(owner, f.Id);

            // Backdate the explicit eligibility timestamp beyond the default
            // janitor grace (1440 minutes ≈ 24 h).
            var blob = await db.BlobObjects.FirstAsync();
            blob.PurgeEligibleAt = DateTime.UtcNow.AddDays(-2);
            await db.SaveChangesAsync();
        }

        var response = await client.GetAsync("/api/admin/storage-stats");
        var body = (await response.Content.ReadFromJsonAsync<StorageStatsResponse>())!;

        Assert.Equal(1, body.Blobs.Total);
        Assert.Equal(1, body.Blobs.ZeroReference);
        Assert.Equal(1, body.Blobs.ZeroReferenceBeyondGrace);
    }

    [Fact]
    public async Task Response_Does_Not_Leak_Storage_Internals()
    {
        var (owner, client) = await CreateAdminClientAsync();

        using (var scope = _factory.Services.CreateScope())
        {
            var folders = scope.ServiceProvider.GetRequiredService<IFolderService>();
            var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
            var shares = scope.ServiceProvider.GetRequiredService<IShareLinkService>();

            await folders.CreateAsync(owner, null, "Sensitive");
            var f = await files.CreateAsync(owner, null, "secret-name.txt", "text/plain",
                new MemoryStream("payload"u8.ToArray()));
            await shares.CreateAsync(owner, f.Id, null, null);
        }

        var response = await client.GetAsync("/api/admin/storage-stats");
        var bodyString = await response.Content.ReadAsStringAsync();

        var forbidden = new[]
        {
            // Identifiers / paths / tokens — none of these should ever appear
            // in an aggregate response.
            "secret-name", "Sensitive",
            "BlobObjectId", "blobObjectId", "blob_object_id",
            "StorageKey", "storageKey", "storage_key",
            "OwnerUserId", "ownerUserId", "owner_user_id",
            "ParentFolderId", "parentFolderId", "parent_folder_id",
            "DeletedAt", "deletedAt", "deleted_at",
            "PasswordHash", "passwordHash", "password_hash",
            "TokenHash", "tokenHash", "token_hash",
            "Token", "token",
            "FileItemId", "fileItemId", "file_item_id",
            "objects/",
        };
        foreach (var needle in forbidden)
        {
            Assert.DoesNotContain(needle, bodyString, StringComparison.Ordinal);
        }
    }

    // ---- slice 46: admin-only authorization --------------------------------

    [Fact]
    public async Task Get_With_NonAdmin_Authenticated_User_Returns_403()
    {
        // Non-admin uses the standard helper that does NOT promote the user.
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();

        var response = await client.GetAsync("/api/admin/storage-stats");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Cookie_Revalidator_Reflects_Admin_Revocation_Without_Relogin()
    {
        // Promote + login → cookie carries the admin claim; first request
        // succeeds.
        var (userId, client) = await CreateAdminClientAsync();
        var first = await client.GetAsync("/api/admin/storage-stats");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // Operator revokes admin out-of-band (e.g. via the CLI). The cookie
        // revalidator runs on the NEXT request and strips the role claim
        // from the principal, so the policy fails with 403 — no re-login
        // needed.
        await _factory.DemoteFromAdminAsync(userId);

        var second = await client.GetAsync("/api/admin/storage-stats");
        Assert.Equal(HttpStatusCode.Forbidden, second.StatusCode);
    }

    [Fact]
    public async Task Cookie_Revalidator_Reflects_Admin_Grant_Without_Relogin()
    {
        // Sign in as non-admin first.
        var userId = await _factory.SeedUserAsync();
        var client = await _factory.LoginAsync();

        var firstStats = await client.GetAsync("/api/admin/storage-stats");
        Assert.Equal(HttpStatusCode.Forbidden, firstStats.StatusCode);

        // Operator grants admin out-of-band. Next request is admin.
        await _factory.PromoteToAdminAsync(userId);

        var secondStats = await client.GetAsync("/api/admin/storage-stats");
        Assert.Equal(HttpStatusCode.OK, secondStats.StatusCode);
    }

    // ---- slice 64: metadata diagnostics + admin visibility ----------------

    [Fact]
    public async Task Media_Counts_By_Server_Detected_Category()
    {
        var (owner, client) = await CreateAdminClientAsync();

        using (var scope = _factory.Services.CreateScope())
        {
            var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
            await files.CreateAsync(owner, null, "photo.jpg", "image/jpeg",
                new MemoryStream(ImageFixtures.JpegWithExif()));
            await files.CreateAsync(owner, null, "clip.mp4", "video/mp4",
                new MemoryStream(ImageFixtures.MinimalMp4()));
            await files.CreateAsync(owner, null, "notes.txt", "text/plain",
                new MemoryStream("hi"u8.ToArray()));
        }

        var body = (await (await client.GetAsync("/api/admin/storage-stats"))
            .Content.ReadFromJsonAsync<StorageStatsResponse>())!;

        Assert.Equal(1, body.Media.ImagesCount);
        Assert.Equal(1, body.Media.VideosCount);
        // text/plain falls into "document" via MediaCategories.FromMimeType.
        Assert.Equal(1, body.Media.DocumentsCount);
    }

    [Fact]
    public async Task Extraction_Status_Counts_Reflect_Upload_Outcomes()
    {
        var (owner, client) = await CreateAdminClientAsync();

        using (var scope = _factory.Services.CreateScope())
        {
            var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
            // JPEG with EXIF → extraction Completed.
            await files.CreateAsync(owner, null, "p.jpg", "image/jpeg",
                new MemoryStream(ImageFixtures.JpegWithExif()));
            // Text file → extraction Skipped (non-image branch in EnsureBlobMetadata).
            await files.CreateAsync(owner, null, "n.txt", "text/plain",
                new MemoryStream("hi"u8.ToArray()));
        }

        var body = (await (await client.GetAsync("/api/admin/storage-stats"))
            .Content.ReadFromJsonAsync<StorageStatsResponse>())!;

        Assert.Equal(1, body.Extraction.Completed);
        Assert.Equal(1, body.Extraction.Skipped);
        Assert.True(body.Extraction.CurrentVersion >= 1);
        Assert.True(body.Extraction.AtCurrentVersion >= 1);
    }

    [Fact]
    public async Task HasGps_Count_Reflects_Image_With_Gps_Without_Exposing_Coordinates()
    {
        var (owner, client) = await CreateAdminClientAsync();

        using (var scope = _factory.Services.CreateScope())
        {
            var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
            await files.CreateAsync(owner, null, "geo.jpg", "image/jpeg",
                new MemoryStream(ImageFixtures.JpegWithExif(includeGps: true)));
            await files.CreateAsync(owner, null, "plain.jpg", "image/jpeg",
                new MemoryStream(ImageFixtures.JpegWithExif(includeGps: false)));
        }

        var response = await client.GetAsync("/api/admin/storage-stats");
        var body = (await response.Content.ReadFromJsonAsync<StorageStatsResponse>())!;
        Assert.Equal(1, body.SensitiveAggregates.BlobsWithGps);
        // Both fixtures include the serial-numbers EXIF tags by default.
        Assert.Equal(2, body.SensitiveAggregates.BlobsWithBodySerial);

        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Latitude", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("Longitude", raw, StringComparison.Ordinal);
        Assert.DoesNotContain(ImageFixtures.BodySerial, raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Derivative_Counts_Reflect_Eager_Small_Thumbnail()
    {
        var (owner, client) = await CreateAdminClientAsync();

        using (var scope = _factory.Services.CreateScope())
        {
            var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
            await files.CreateAsync(owner, null, "p.jpg", "image/jpeg",
                new MemoryStream(ImageFixtures.JpegWithExif()));
            await files.CreateAsync(owner, null, "v.mp4", "video/mp4",
                new MemoryStream(ImageFixtures.MinimalMp4()));
        }

        var body = (await (await client.GetAsync("/api/admin/storage-stats"))
            .Content.ReadFromJsonAsync<StorageStatsResponse>())!;

        // Eager small thumbnail on image upload (slice 19); medium + poster
        // are lazy so they are missing until first access / prewarm.
        Assert.Equal(1, body.Derivatives.SmallThumbnailCount);
        Assert.Equal(0, body.Derivatives.MediumPreviewCount);
        Assert.Equal(0, body.Derivatives.VideoPosterCount);
        Assert.Equal(1, body.Derivatives.ImagesMissingMedium);
        Assert.Equal(1, body.Derivatives.VideosMissingPoster);
    }

    [Fact]
    public async Task UserMetadata_Aggregate_Counts_Reflect_Patched_Fields()
    {
        var (owner, client) = await CreateAdminClientAsync();

        using (var scope = _factory.Services.CreateScope())
        {
            var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
            var meta = scope.ServiceProvider.GetRequiredService<IMetadataService>();
            var f = await files.CreateAsync(owner, null, "p.jpg", "image/jpeg",
                new MemoryStream(ImageFixtures.JpegWithExif()));
            await meta.UpdateUserMetadataAsync(owner, f.Id, new UpdateFileMetadataRequest(
                Title: "Trip",
                Description: null,
                Tags: new[] { "park" },
                Rating: 4,
                Favorite: true,
                DateTakenOverride: null,
                LocationOverride: null));
        }

        var body = (await (await client.GetAsync("/api/admin/storage-stats"))
            .Content.ReadFromJsonAsync<StorageStatsResponse>())!;

        Assert.Equal(1, body.UserMetadata.TotalRows);
        Assert.Equal(1, body.UserMetadata.WithTitle);
        Assert.Equal(0, body.UserMetadata.WithDescription);
        Assert.Equal(1, body.UserMetadata.WithTags);
        Assert.Equal(1, body.UserMetadata.WithRating);
        Assert.Equal(1, body.UserMetadata.Favorites);
        Assert.Equal(0, body.UserMetadata.WithDateTakenOverride);
        Assert.Equal(0, body.UserMetadata.WithLocationOverride);
    }

    [Fact]
    public async Task New_Diagnostics_Blocks_Have_No_Sensitive_Leakage()
    {
        var (owner, client) = await CreateAdminClientAsync();

        using (var scope = _factory.Services.CreateScope())
        {
            var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
            var meta = scope.ServiceProvider.GetRequiredService<IMetadataService>();
            var f = await files.CreateAsync(owner, null, "geo-leak.jpg", "image/jpeg",
                new MemoryStream(ImageFixtures.JpegWithExif(includeGps: true)));
            await meta.UpdateUserMetadataAsync(owner, f.Id, new UpdateFileMetadataRequest(
                Title: "VERY-SECRET-TITLE",
                Description: "VERY-SECRET-DESCRIPTION",
                Tags: new[] { "secret-tag" },
                Rating: null, Favorite: null,
                DateTakenOverride: null,
                LocationOverride: "VERY-SECRET-LOCATION"));
        }

        var response = await client.GetAsync("/api/admin/storage-stats");
        var raw = await response.Content.ReadAsStringAsync();

        // Aggregate-only — no per-file titles, descriptions, locations,
        // tag content, GPS coordinates, serials, or file names.
        var forbidden = new[]
        {
            "VERY-SECRET-TITLE", "VERY-SECRET-DESCRIPTION", "VERY-SECRET-LOCATION",
            "secret-tag",
            ImageFixtures.BodySerial, ImageFixtures.LensSerial, ImageFixtures.Software,
            ImageFixtures.CameraModel, ImageFixtures.LensModel,
            "geo-leak",
            "Latitude", "Longitude",
        };
        foreach (var needle in forbidden)
        {
            Assert.DoesNotContain(needle, raw, StringComparison.Ordinal);
        }

        // Centralised slice-57 needle list passes too.
        foreach (var needle in MetadataExposurePolicy.ForbiddenInResponses)
        {
            Assert.DoesNotContain(needle, raw, StringComparison.Ordinal);
        }
    }
}
