using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Storage;
using NubArca.Api.Tests.Endpoints;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace NubArca.Api.Tests.Files;

// Service-level tests for ImageProcessingOptions resource caps. The
// FileThumbnailService is instantiated directly with low custom limits so we
// can prove the skip-without-failing-upload contract without building actual
// 8192×8192 images in memory.
public sealed class ImageProcessingLimitsServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly string _storageRoot;
    private readonly LocalFileSystemBlobStorage _storage;
    private readonly BlobService _blobService;

    public ImageProcessingLimitsServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;
        _db = new AppDbContext(dbOptions);
        _db.Database.EnsureCreated();

        _storageRoot = Path.Combine(Path.GetTempPath(), $"nubarca-imglimits-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_storageRoot);
        var blobOptions = Options.Create(new BlobStorageOptions { RootPath = _storageRoot });
        _storage = new LocalFileSystemBlobStorage(blobOptions);
        _blobService = new BlobService(_storage, _db, TimeProvider.System);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        try { if (Directory.Exists(_storageRoot)) Directory.Delete(_storageRoot, recursive: true); }
        catch { /* best effort */ }
    }

    private FileItemService NewService(ImageProcessingOptions options)
    {
        var thumbs = new FileThumbnailService(
            _db, _blobService, _storage, new SyntheticVideoPosterProvider(),
            TimeProvider.System, NullLogger<FileThumbnailService>.Instance,
            Options.Create(options));
        return new FileItemService(_db, _blobService, thumbs, TimeProvider.System);
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

    private static byte[] Png(int width, int height)
    {
        using var img = new Image<Rgba32>(width, height);
        using var ms = new MemoryStream();
        img.Save(ms, new PngEncoder());
        return ms.ToArray();
    }

    private static byte[] Jpeg(int width, int height)
    {
        using var img = new Image<Rgb24>(width, height);
        using var ms = new MemoryStream();
        img.Save(ms, new JpegEncoder());
        return ms.ToArray();
    }

    [Fact]
    public async Task Normal_PNG_Generates_Thumbnail_Under_Default_Limits()
    {
        var owner = await SeedUserAsync();
        var service = NewService(new ImageProcessingOptions()); // defaults

        var file = await service.CreateAsync(
            owner.Id, null, "pic.png", "image/png", new MemoryStream(Png(400, 300)));

        Assert.Equal(400, file.Width);
        Assert.Equal(300, file.Height);
        Assert.Equal(1, await _db.FileThumbnails.CountAsync(t => t.FileItemId == file.Id));
    }

    [Fact]
    public async Task Normal_JPEG_Generates_Thumbnail_Under_Default_Limits()
    {
        var owner = await SeedUserAsync();
        var service = NewService(new ImageProcessingOptions());

        var file = await service.CreateAsync(
            owner.Id, null, "pic.jpg", "image/jpeg", new MemoryStream(Jpeg(800, 600)));

        Assert.Equal(1, await _db.FileThumbnails.CountAsync(t => t.FileItemId == file.Id));
    }

    [Fact]
    public async Task Width_Above_MaxWidth_Skips_Thumbnail_But_Upload_Succeeds()
    {
        var owner = await SeedUserAsync();
        var service = NewService(new ImageProcessingOptions { MaxWidth = 100 });

        var file = await service.CreateAsync(
            owner.Id, null, "wide.png", "image/png", new MemoryStream(Png(200, 100)));

        // Upload completed and dimensions were detected (Identify is safe).
        Assert.Equal(200, file.Width);
        Assert.Equal(100, file.Height);
        Assert.Equal(0, await _db.FileThumbnails.CountAsync(t => t.FileItemId == file.Id));
    }

    [Fact]
    public async Task Height_Above_MaxHeight_Skips_Thumbnail_But_Upload_Succeeds()
    {
        var owner = await SeedUserAsync();
        var service = NewService(new ImageProcessingOptions { MaxHeight = 100 });

        var file = await service.CreateAsync(
            owner.Id, null, "tall.png", "image/png", new MemoryStream(Png(100, 300)));

        Assert.Equal(100, file.Width);
        Assert.Equal(300, file.Height);
        Assert.Equal(0, await _db.FileThumbnails.CountAsync(t => t.FileItemId == file.Id));
    }

    [Fact]
    public async Task Pixel_Count_Above_MaxPixels_Skips_Thumbnail_But_Upload_Succeeds()
    {
        var owner = await SeedUserAsync();
        // Image dims are well inside MaxWidth/MaxHeight, but the pixel cap is the
        // tighter gate — proves it gets checked independently.
        var service = NewService(new ImageProcessingOptions { MaxPixels = 10_000 });

        var file = await service.CreateAsync(
            owner.Id, null, "dense.png", "image/png", new MemoryStream(Png(200, 200))); // 40k px

        Assert.Equal(0, await _db.FileThumbnails.CountAsync(t => t.FileItemId == file.Id));
    }

    [Fact]
    public async Task Source_Bytes_Above_MaxThumbnailInputBytes_Skips_Thumbnail()
    {
        var owner = await SeedUserAsync();
        // PNG of 200×200 with default ImageSharp encoder is well over 100 B.
        var service = NewService(new ImageProcessingOptions { MaxThumbnailInputBytes = 100 });

        var file = await service.CreateAsync(
            owner.Id, null, "big.png", "image/png", new MemoryStream(Png(200, 200)));

        Assert.Equal(0, await _db.FileThumbnails.CountAsync(t => t.FileItemId == file.Id));
    }

    [Fact]
    public async Task EnableThumbnails_False_Skips_All_Thumbnails()
    {
        var owner = await SeedUserAsync();
        var service = NewService(new ImageProcessingOptions { EnableThumbnails = false });

        var file = await service.CreateAsync(
            owner.Id, null, "pic.png", "image/png", new MemoryStream(Png(200, 200)));

        Assert.Equal(0, await _db.FileThumbnails.CountAsync(t => t.FileItemId == file.Id));
    }

    [Fact]
    public async Task Corrupt_Image_Still_Uploads_Without_Thumbnail()
    {
        var owner = await SeedUserAsync();
        var service = NewService(new ImageProcessingOptions());

        // PNG magic header followed by garbage. Identify may or may not return
        // an info object — either way the catch path in IdentifySourceAsync
        // returns null and we skip cleanly.
        var corrupt = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0xff, 0xff, 0xff, 0xff };

        var file = await service.CreateAsync(
            owner.Id, null, "broken.png", "image/png", new MemoryStream(corrupt));

        Assert.Equal(corrupt.LongLength, file.SizeBytes);
        Assert.Equal(0, await _db.FileThumbnails.CountAsync(t => t.FileItemId == file.Id));
    }

    [Fact]
    public async Task Defaults_Allow_Reasonable_Phone_Size_Images()
    {
        // 4000×3000 ≈ 12 MP, comfortably under all default caps.
        var owner = await SeedUserAsync();
        var service = NewService(new ImageProcessingOptions());

        var file = await service.CreateAsync(
            owner.Id, null, "phone.jpg", "image/jpeg",
            new MemoryStream(Jpeg(4000, 3000)));

        Assert.Equal(1, await _db.FileThumbnails.CountAsync(t => t.FileItemId == file.Id));
    }
}

// Endpoint-level coverage: when the host is configured with very low limits,
// uploading a normal image succeeds (no thumbnail row) and the thumbnail /
// gallery endpoints behave correctly.
public sealed class ImageProcessingLimitsEndpointTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public ImageProcessingLimitsEndpointTests()
    {
        // Force every image upload over the limit so the host skips thumbnails.
        _factory = new SqliteWebApplicationFactory(new Dictionary<string, string?>
        {
            ["ImageProcessing:MaxWidth"] = "32",
            ["ImageProcessing:MaxHeight"] = "32",
            ["ImageProcessing:MaxPixels"] = "1024",
            ["ImageProcessing:MaxThumbnailInputBytes"] = "10",
        }, poolHost: true);
        _factory.EnsureDatabaseCreated();
    }

    public void Dispose() => _factory.Dispose();

    private static MultipartFormDataContent Multipart(byte[] bytes, string filename, string contentType)
    {
        var multipart = new MultipartFormDataContent();
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        multipart.Add(part, "file", filename);
        return multipart;
    }

    private static byte[] Png(int width, int height)
    {
        using var img = new Image<Rgba32>(width, height);
        using var ms = new MemoryStream();
        img.Save(ms, new PngEncoder());
        return ms.ToArray();
    }

    [Fact]
    public async Task Image_Upload_Over_Limit_Still_Succeeds_And_Returns_201()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();

        var response = await client.PostAsync("/api/files",
            Multipart(Png(200, 200), "big.png", "image/png"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var summary = await response.Content.ReadFromJsonAsync<FileSummary>();
        Assert.NotNull(summary);
        // Identify is safe, so dimensions are populated even when the thumbnail
        // is skipped.
        Assert.Equal(200, summary!.Width);
        Assert.Equal(200, summary.Height);
    }

    [Fact]
    public async Task Thumbnail_Endpoint_Returns_404_When_Thumbnail_Skipped()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var upload = await client.PostAsync("/api/files",
            Multipart(Png(200, 200), "big.png", "image/png"));
        var summary = await upload.Content.ReadFromJsonAsync<FileSummary>();

        var thumb = await client.GetAsync($"/api/files/{summary!.Id}/thumbnail?size=small");

        Assert.Equal(HttpStatusCode.NotFound, thumb.StatusCode);
    }

    [Fact]
    public async Task Gallery_Lists_Image_Whose_Thumbnail_Was_Skipped()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var upload = await client.PostAsync("/api/files",
            Multipart(Png(200, 200), "big.png", "image/png"));
        var summary = await upload.Content.ReadFromJsonAsync<FileSummary>();

        var body = await client.GetFromJsonAsync<ImageListResponse>("/api/images");
        Assert.NotNull(body);

        var item = Assert.Single(body!.Items);
        Assert.Equal(summary!.Id, item.Id);
        Assert.Equal(200, item.Width);
        Assert.Equal(200, item.Height);
        // ThumbnailUrl is always populated — the URL itself just 404s when no
        // FileThumbnail row exists. Documented behaviour.
        Assert.Equal($"/api/files/{item.Id}/thumbnail?size=small", item.ThumbnailUrl);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await db.FileThumbnails.CountAsync(t => t.FileItemId == summary!.Id));
    }

    [Fact]
    public async Task Custom_Low_Limits_Apply_From_Configuration()
    {
        // Sanity-check the wire: with the factory's overrides the live
        // IOptions<ImageProcessingOptions> should mirror them.
        using var scope = _factory.Services.CreateScope();
        var options = scope.ServiceProvider
            .GetRequiredService<IOptions<ImageProcessingOptions>>().Value;

        Assert.Equal(32, options.MaxWidth);
        Assert.Equal(32, options.MaxHeight);
        Assert.Equal(1024, options.MaxPixels);
        Assert.Equal(10, options.MaxThumbnailInputBytes);
    }
}

// Verifies that absent any operator configuration, the defaults are the
// reviewed values. Catches accidental regressions in ImageProcessingOptions.
public sealed class ImageProcessingDefaultsTests
{
    [Fact]
    public void Defaults_Are_The_Reviewed_Personal_Cloud_Numbers()
    {
        var options = new ImageProcessingOptions();
        Assert.True(options.EnableThumbnails);
        Assert.Equal(8192, options.MaxWidth);
        Assert.Equal(8192, options.MaxHeight);
        Assert.Equal(64_000_000, options.MaxPixels);
        Assert.Equal(30L * 1024 * 1024, options.MaxThumbnailInputBytes);
    }
}
