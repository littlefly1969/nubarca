using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NubArca.Api.Ai.SubjectCutout;
using NubArca.Api.Audit;
using NubArca.Api.Data;
using NubArca.Api.Endpoints;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Tests.Metadata;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace NubArca.Api.Tests.Ai.SubjectCutout;

// "Copy subject" over HTTP, with the segmentation model replaced by a fake that
// marks the middle half of the photo as the subject.
public sealed class SubjectCutoutEndpointTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;
    private readonly FakeSubjectMaskModel _model = new();

    public SubjectCutoutEndpointTests()
    {
        // Any setting gives the class its own, unpooled host, so the fake model
        // never leaks into another test. A zero queue timeout makes "busy"
        // immediate instead of waiting.
        _factory = new SqliteWebApplicationFactory(new Dictionary<string, string?>
        {
            ["Ai:SubjectCutout:QueueTimeoutSeconds"] = "0",
        });
        _factory.ConfigureExtraServices = services =>
            services.Replace(ServiceDescriptor.Singleton<ISubjectMaskModel>(_model));
        _factory.EnsureDatabaseCreated();
    }

    public void Dispose() => _factory.Dispose();

    private async Task<FileItem> UploadAsync(Guid ownerId, byte[] bytes, string name, string mime, Guid? folderId = null)
    {
        using var scope = _factory.Services.CreateScope();
        var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
        return await files.CreateAsync(ownerId, folderId, name, mime, new MemoryStream(bytes));
    }

    private async Task<T> InDbAsync<T>(Func<AppDbContext, Task<T>> work)
    {
        using var scope = _factory.Services.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    [Fact]
    public async Task Routes_require_a_signed_in_user()
    {
        var anonymous = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/subject-cutout")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.PostAsync($"/api/files/{Guid.NewGuid()}/subject-cutout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.PostAsync($"/api/files/{Guid.NewGuid()}/subject-cutout/save", null)).StatusCode);
    }

    [Fact]
    public async Task Availability_follows_the_model()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();

        var on = await client.GetFromJsonAsync<SubjectCutoutAvailability>("/api/subject-cutout");
        _model.Available = false;
        var off = await client.GetFromJsonAsync<SubjectCutoutAvailability>("/api/subject-cutout");

        Assert.True(on!.Available);
        Assert.False(off!.Available);
    }

    [Fact]
    public async Task Cutout_is_a_transparent_png_of_the_subject_and_is_audited()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(owner, ImageFixtures.PlainPng(400, 200), "p.png", "image/png");

        var response = await client.PostAsync($"/api/files/{file.Id}/subject-cutout", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
        using var png = Image.Load<Rgba32>(await response.Content.ReadAsByteArrayAsync());
        // The middle half of a 400x200 preview, plus a few pixels of padding.
        Assert.InRange(png.Width, 200, 216);
        Assert.InRange(png.Height, 100, 116);
        Assert.Equal(0, png[0, 0].A);
        Assert.Equal(255, png[png.Width / 2, png.Height / 2].A);

        var audited = await InDbAsync(db => db.AuditLogs.AsNoTracking()
            .Where(a => a.Action == AuditActions.FileSubjectCutout).ToListAsync());
        var entry = Assert.Single(audited);
        Assert.Equal(file.Id, entry.EntityId);
        Assert.Equal(owner, entry.UserId);
    }

    [Fact]
    public async Task A_foreign_missing_trashed_or_non_photo_file_is_a_plain_404()
    {
        var alice = await _factory.SeedUserAsync("alice@example.com");
        var alicePhoto = await UploadAsync(alice, ImageFixtures.PlainPng(64, 64), "a.png", "image/png");
        var (bob, client) = await _factory.CreateAuthenticatedClientAsync("bob@example.com");
        var text = await UploadAsync(bob, "hello"u8.ToArray(), "notes.txt", "text/plain");
        var trashed = await UploadAsync(bob, ImageFixtures.PlainPng(64, 64), "t.png", "image/png");
        await InDbAsync(async db =>
        {
            var row = await db.FileItems.SingleAsync(f => f.Id == trashed.Id);
            row.DeletedAt = DateTime.UtcNow;
            return await db.SaveChangesAsync();
        });

        foreach (var id in new[] { alicePhoto.Id, Guid.NewGuid(), text.Id, trashed.Id })
        {
            Assert.Equal(HttpStatusCode.NotFound,
                (await client.PostAsync($"/api/files/{id}/subject-cutout", null)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound,
                (await client.PostAsync($"/api/files/{id}/subject-cutout/save", null)).StatusCode);
        }
        Assert.Equal(0, _model.Runs);
    }

    [Fact]
    public async Task An_unavailable_model_is_a_503_and_runs_nothing()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(owner, ImageFixtures.PlainPng(64, 64), "p.png", "image/png");
        _model.Available = false;

        var response = await client.PostAsync($"/api/files/{file.Id}/subject-cutout", null);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("\"unavailable\"", await response.Content.ReadAsStringAsync());
        Assert.Equal(0, _model.Runs);
    }

    [Fact]
    public async Task A_photo_without_a_subject_is_a_422()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(owner, ImageFixtures.PlainPng(64, 64), "p.png", "image/png");
        _model.Subject = false;

        var response = await client.PostAsync($"/api/files/{file.Id}/subject-cutout", null);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("no_subject", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_cutout_in_progress_makes_the_next_one_busy()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(owner, ImageFixtures.PlainPng(64, 64), "p.png", "image/png");
        var gate = _factory.Services.GetRequiredService<SubjectCutoutGate>();

        using (await gate.TryEnterAsync(TimeSpan.Zero, CancellationToken.None))
        {
            var response = await client.PostAsync($"/api/files/{file.Id}/subject-cutout", null);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Contains("busy", await response.Content.ReadAsStringAsync());
            Assert.NotNull(response.Headers.RetryAfter);
        }

        Assert.Equal(HttpStatusCode.OK,
            (await client.PostAsync($"/api/files/{file.Id}/subject-cutout", null)).StatusCode);
    }

    [Fact]
    public async Task Saving_files_a_new_png_next_to_the_photo_and_leaves_the_photo_alone()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var folderId = await InDbAsync(async db =>
        {
            var folder = new Folder { Id = Guid.NewGuid(), OwnerUserId = owner, Name = "Trip", CreatedAt = DateTime.UtcNow };
            db.Folders.Add(folder);
            await db.SaveChangesAsync();
            return folder.Id;
        });
        var photo = await UploadAsync(owner, ImageFixtures.PlainPng(300, 300), "IMG_0001.png", "image/png", folderId);

        var first = await client.PostAsync($"/api/files/{photo.Id}/subject-cutout/save", null);
        var second = await client.PostAsync($"/api/files/{photo.Id}/subject-cutout/save", null);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var saved = await first.Content.ReadFromJsonAsync<FileSummary>();
        Assert.Equal("IMG_0001-cutout.png", saved!.Name);
        Assert.Equal("image/png", saved.MimeType);
        // The same name is taken the second time: the usual rename, never a 409.
        Assert.Equal("IMG_0001-cutout (2).png", (await second.Content.ReadFromJsonAsync<FileSummary>())!.Name);

        var rows = await InDbAsync(db => db.FileItems.AsNoTracking()
            .Where(f => f.OwnerUserId == owner).ToListAsync());
        Assert.All(rows, r => Assert.Equal(folderId, r.ParentFolderId));
        var original = rows.Single(r => r.Id == photo.Id);
        Assert.Equal(photo.BlobObjectId, original.BlobObjectId);
        Assert.Equal("IMG_0001.png", original.Name);
    }

    [Fact]
    public void Cutout_names_follow_the_photo()
    {
        Assert.Equal("IMG_0001-cutout.png", SubjectCutoutService.CutoutFileName("IMG_0001.HEIC"));
        Assert.Equal("photo-cutout.png", SubjectCutoutService.CutoutFileName(".jpg"));
    }

    private sealed class FakeSubjectMaskModel : ISubjectMaskModel
    {
        public volatile bool Available = true;
        public volatile bool Subject = true;
        private int _runs;

        public int Runs => _runs;

        public string? UnavailableReason() => Available ? null : "subject-cutout-disabled";

        public float[] PredictLogits(float[] input, int size)
        {
            Interlocked.Increment(ref _runs);
            var quarter = size / 4;
            return SubjectCutoutImagingTests.Square(size, (x, y) =>
                Subject && x >= quarter && x < size - quarter && y >= quarter && y < size - quarter ? 12f : -12f);
        }
    }
}
