using System.Net;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Metadata;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Tests.Metadata;
using Xunit;

namespace NubArca.Api.Tests.Storage;

// Slice 72 — split derived media storage backend. Verifies originals stay in
// the original root, derived artifacts (thumbnail/preview/poster) go to the
// configured derived root, and a missing derived artifact regenerates.
public sealed class DerivedStorageTests : IDisposable
{
    private readonly string _derivedRoot;
    private readonly SqliteWebApplicationFactory _factory;

    public DerivedStorageTests()
    {
        _derivedRoot = Path.Combine(Path.GetTempPath(), $"nubarca-derived-{Guid.NewGuid():N}");
        _factory = new SqliteWebApplicationFactory(new Dictionary<string, string?>
        {
            ["Storage:DerivedRootPath"] = _derivedRoot,
        });
        _factory.EnsureDatabaseCreated();
    }

    public void Dispose()
    {
        _factory.Dispose();
        try { if (Directory.Exists(_derivedRoot)) Directory.Delete(_derivedRoot, recursive: true); }
        catch { /* best effort */ }
    }

    private static int ObjectCount(string root)
    {
        var objects = Path.Combine(root, "objects");
        return Directory.Exists(objects)
            ? Directory.GetFiles(objects, "*", SearchOption.AllDirectories).Length
            : 0;
    }

    private async Task<FileItem> UploadAsync(Guid ownerId, byte[] bytes, string name, string mime)
    {
        using var scope = _factory.Services.CreateScope();
        var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
        return await files.CreateAsync(ownerId, null, name, mime, new MemoryStream(bytes));
    }

    [Fact]
    public async Task Original_Goes_To_Original_Root_Thumbnail_Goes_To_Derived_Root()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(owner, ImageFixtures.JpegWithExif(), "p.jpg", "image/jpeg");

        // Eager small thumbnail was generated at upload → derived root.
        Assert.True(ObjectCount(_derivedRoot) >= 1, "derived root should hold the thumbnail");

        // The original blob is in the original root, not the derived root.
        Assert.True(ObjectCount(_factory.StorageRoot) >= 1, "original root should hold the upload");

        // Thumbnail serves.
        var resp = await client.GetAsync($"/api/files/{file.Id}/thumbnail?size=small");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Original_Download_Reads_Original_Root_Even_If_Derived_Wiped()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var bytes = ImageFixtures.JpegWithExif();
        var file = await UploadAsync(owner, bytes, "p.jpg", "image/jpeg");

        // Nuke the derived cache entirely.
        if (Directory.Exists(_derivedRoot)) Directory.Delete(_derivedRoot, recursive: true);

        // Original download must still work (reads the original root).
        var resp = await client.GetAsync($"/api/files/{file.Id}/content");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var got = await resp.Content.ReadAsByteArrayAsync();
        Assert.Equal(bytes.Length, got.Length);
    }

    [Fact]
    public async Task Preview_Endpoint_Persists_Medium_Derivative_In_Derived_Root()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        // Both current default boxes (small=768, medium=1920) avoid upscaling
        // this 600px source. Depending on encoder settings their bytes may
        // deduplicate, so physical-object COUNT is not a valid proof that the
        // medium derivative was persisted.
        var file = await UploadAsync(owner, ImageFixtures.PlainPng(600, 600), "p.png", "image/png");

        var resp = await client.GetAsync($"/api/files/{file.Id}/preview");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NubArca.Api.Data.AppDbContext>();
        var medium = await (
            from thumbnail in db.FileThumbnails.AsNoTracking()
            join blob in db.BlobObjects.AsNoTracking()
                on thumbnail.BlobObjectId equals blob.Id
            where thumbnail.FileItemId == file.Id
                && thumbnail.Size == ThumbnailSizes.Medium
            select new
            {
                thumbnail.Width,
                thumbnail.Height,
                blob.StorageKey,
            })
            .SingleAsync();

        Assert.Equal(600, medium.Width);
        Assert.Equal(600, medium.Height);
        Assert.True(
            File.Exists(Path.Combine(_derivedRoot, medium.StorageKey)),
            "the medium derivative row must point at bytes in the configured derived root");
    }

    [Fact]
    public async Task Missing_Derived_Artifact_Is_Regenerated()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(owner, ImageFixtures.JpegWithExif(), "p.jpg", "image/jpeg");

        // First request populates the derived root.
        Assert.Equal(HttpStatusCode.OK,
            (await client.GetAsync($"/api/files/{file.Id}/thumbnail?size=small")).StatusCode);

        // Wipe the derived cache (FileThumbnail row still exists in the DB).
        Directory.Delete(_derivedRoot, recursive: true);

        // Endpoint must self-heal: regenerate into the derived root and serve.
        var resp = await client.GetAsync($"/api/files/{file.Id}/thumbnail?size=small");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.True(ObjectCount(_derivedRoot) >= 1, "thumbnail should be regenerated into derived root");
    }

    [Fact]
    public async Task Backfill_Writes_Derivatives_To_Derived_Root()
    {
        var (owner, _) = await _factory.CreateAuthenticatedClientAsync();
        await UploadAsync(owner, ImageFixtures.PlainPng(600, 600), "p.png", "image/png");

        // Wipe derived cache so backfill must (re)create everything there.
        Directory.Delete(_derivedRoot, recursive: true);

        using var scope = _factory.Services.CreateScope();
        var backfill = scope.ServiceProvider.GetRequiredService<MediaDerivativesBackfillService>();
        await backfill.RunAsync(new MediaDerivativesBackfillOptions { MissingOnly = true });

        Assert.True(ObjectCount(_derivedRoot) >= 1, "backfill should write derivatives into derived root");
    }

    [Fact]
    public async Task Thumbnail_Response_Has_No_Storage_Key_Or_Path_Leak()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(owner, ImageFixtures.JpegWithExif(), "p.jpg", "image/jpeg");

        var resp = await client.GetAsync($"/api/files/{file.Id}/thumbnail?size=small");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var headerBlob = string.Join("\n", resp.Headers.Select(h => $"{h.Key}: {string.Join(",", h.Value)}"));
        foreach (var needle in MetadataExposurePolicy.ForbiddenInResponses)
        {
            Assert.DoesNotContain(needle, headerBlob, StringComparison.OrdinalIgnoreCase);
        }
        // The derived root path must never appear in any header.
        Assert.DoesNotContain(_derivedRoot, headerBlob, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("objects/", headerBlob, StringComparison.OrdinalIgnoreCase);
    }
}

// Slice 72 — the single-root default (Storage:DerivedRootPath unset) must
// behave exactly as before. The derived store is then a distinct instance
// rooted at the SAME path as the original (EffectiveDerivedRootPath ==
// RootPath), so this also covers the "equal roots" safety case.
public sealed class DerivedStorageDefaultRootTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public DerivedStorageDefaultRootTests()
    {
        _factory = new SqliteWebApplicationFactory();
        _factory.EnsureDatabaseCreated();
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Single_Root_Thumbnail_And_Download_Work()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        FileItem file;
        using (var scope = _factory.Services.CreateScope())
        {
            var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
            file = await files.CreateAsync(owner, null, "p.jpg", "image/jpeg",
                new MemoryStream(ImageFixtures.JpegWithExif()));
        }

        var thumb = await client.GetAsync($"/api/files/{file.Id}/thumbnail?size=small");
        Assert.Equal(HttpStatusCode.OK, thumb.StatusCode);
        var dl = await client.GetAsync($"/api/files/{file.Id}/content");
        Assert.Equal(HttpStatusCode.OK, dl.StatusCode);

        // Everything lives under the one root.
        var objects = Path.Combine(_factory.StorageRoot, "objects");
        Assert.True(Directory.Exists(objects)
            && Directory.GetFiles(objects, "*", SearchOption.AllDirectories).Length >= 2,
            "single root should hold both the original and the thumbnail");
    }
}
