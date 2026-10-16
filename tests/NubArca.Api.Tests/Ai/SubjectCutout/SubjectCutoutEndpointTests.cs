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
    private readonly FakeSubjectPointModel _pointModel = new();

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
        {
            services.Replace(ServiceDescriptor.Singleton<ISubjectMaskModel>(_model));
            services.Replace(ServiceDescriptor.Singleton<ISubjectPointModel>(_pointModel));
        };
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
        _pointModel.Available = false;
        var noPoint = await client.GetFromJsonAsync<SubjectCutoutAvailability>("/api/subject-cutout");
        _pointModel.Available = true;
        _model.Available = false;
        var off = await client.GetFromJsonAsync<SubjectCutoutAvailability>("/api/subject-cutout");

        Assert.Equal(new SubjectCutoutAvailability(true, true), on);
        Assert.Equal(new SubjectCutoutAvailability(true, false), noPoint);
        // No tapping without the cutout model that draws the edges.
        Assert.Equal(new SubjectCutoutAvailability(false, false), off);
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
    public async Task A_tap_keeps_only_the_touched_subject()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(owner, ImageFixtures.PlainPng(400, 200), "p.png", "image/png");
        _model.TwoSubjects = true;

        var both = await client.PostAsync($"/api/files/{file.Id}/subject-cutout", null);
        var right = await client.PostAsJsonAsync($"/api/files/{file.Id}/subject-cutout", new { point = new { x = 0.8, y = 0.5 } });

        using var bothPng = Image.Load<Rgba32>(await both.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.OK, right.StatusCode);
        using var rightPng = Image.Load<Rgba32>(await right.Content.ReadAsByteArrayAsync());
        // Both people span the whole width; the touched one is the right 40%.
        Assert.InRange(bothPng.Width, 390, 400);
        Assert.InRange(rightPng.Width, 160, 176);
        Assert.Equal((0.8f, 0.5f), _pointModel.LastPoint);
        // The right person sits in the right 40% of the photo, full height.
        var region = right.Headers.GetValues(SubjectCutoutEndpoints.RegionHeader).Single()
            .Split(',').Select(v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        Assert.Equal(4, region.Length);
        Assert.InRange(region[0], 0.57, 0.6);
        Assert.Equal(0, region[1]);
        Assert.InRange(region[2], 0.4, 0.43);
        Assert.Equal(1, region[3]);
        Assert.Equal(1, _pointModel.Runs);
    }

    [Fact]
    public async Task A_tap_on_the_background_is_a_422()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(owner, ImageFixtures.PlainPng(400, 200), "p.png", "image/png");
        _model.TwoSubjects = true;

        var response = await client.PostAsJsonAsync(
            $"/api/files/{file.Id}/subject-cutout", new { point = new { x = 0.5, y = 0.5 } });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("no_subject", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_tap_on_the_background_is_a_422_even_when_the_point_model_proposes_a_person()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(owner, ImageFixtures.PlainPng(400, 200), "p.png", "image/png");
        _model.TwoSubjects = true;
        _pointModel.ProposeNearestPerson = true;

        var response = await client.PostAsJsonAsync(
            $"/api/files/{file.Id}/subject-cutout", new { point = new { x = 0.5, y = 0.5 } });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("no_subject", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData((ushort)6)]
    [InlineData((ushort)8)]
    public async Task A_photo_stored_turned_is_cut_out_upright(ushort orientation)
    {
        // Displayed: a standing 400x600 photo, a person standing in the right
        // part of it (x 0.55..0.85), full height. Stored: turned on its side,
        // with the EXIF tag that turns it back — as a phone saves a portrait.
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        using var upright = new Image<Rgb24>(400, 600, new Rgb24(30, 60, 200));
        for (var y = 0; y < 600; y++)
        for (var x = 220; x < 340; x++)
            upright[x, y] = new Rgb24(210, 30, 30);
        var file = await UploadAsync(owner, ImageFixtures.OrientedJpeg(upright, orientation), "p.jpg", "image/jpeg");
        _model.ByColour = true;

        var response = await client.PostAsync($"/api/files/{file.Id}/subject-cutout", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var png = Image.Load<Rgba32>(await response.Content.ReadAsByteArrayAsync());
        // Standing: tall and narrow, not lying on its side.
        Assert.InRange(png.Width, 120, 136);
        Assert.InRange(png.Height, 594, 600);
        var region = response.Headers.GetValues(SubjectCutoutEndpoints.RegionHeader).Single()
            .Split(',').Select(v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        // On the right, where the upright photo has the person.
        Assert.InRange(region[0], 0.52, 0.56);
        Assert.InRange(region[2], 0.3, 0.34);
        Assert.InRange(region[3], 0.99, 1);

        // And a tap on the person, in the upright photo's terms, finds them.
        var tapped = await client.PostAsJsonAsync(
            $"/api/files/{file.Id}/subject-cutout", new { point = new { x = 0.7, y = 0.5 } });
        Assert.Equal(HttpStatusCode.OK, tapped.StatusCode);
    }

    [Theory]
    [InlineData(-0.1, 0.5)]
    [InlineData(0.5, 1.5)]
    public async Task A_point_off_the_photo_is_a_400_and_runs_nothing(double x, double y)
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(owner, ImageFixtures.PlainPng(64, 64), "p.png", "image/png");

        var create = await client.PostAsJsonAsync($"/api/files/{file.Id}/subject-cutout", new { point = new { x, y } });
        var save = await client.PostAsJsonAsync($"/api/files/{file.Id}/subject-cutout/save", new { point = new { x, y } });

        Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);
        Assert.Contains("invalid_point", await create.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, save.StatusCode);
        Assert.Equal(0, _model.Runs);
    }

    [Fact]
    public async Task A_tap_without_the_point_model_is_a_503_while_the_whole_subject_still_works()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await UploadAsync(owner, ImageFixtures.PlainPng(64, 64), "p.png", "image/png");
        _pointModel.Available = false;

        var tapped = await client.PostAsJsonAsync(
            $"/api/files/{file.Id}/subject-cutout", new { point = new { x = 0.5, y = 0.5 } });
        var whole = await client.PostAsync($"/api/files/{file.Id}/subject-cutout", null);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, tapped.StatusCode);
        Assert.Contains("\"unavailable\"", await tapped.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, whole.StatusCode);
        Assert.Equal(0, _pointModel.Runs);
    }

    [Fact]
    public async Task Saving_a_tap_files_only_the_touched_subject()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var photo = await UploadAsync(owner, ImageFixtures.PlainPng(400, 200), "IMG_0002.png", "image/png");
        _model.TwoSubjects = true;

        var response = await client.PostAsJsonAsync(
            $"/api/files/{photo.Id}/subject-cutout/save", new { point = new { x = 0.2, y = 0.5 } });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var saved = await response.Content.ReadFromJsonAsync<FileSummary>();
        Assert.Equal("IMG_0002-cutout.png", saved!.Name);
        Assert.InRange(saved.Width ?? 0, 160, 176);
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
        // Two people side by side: the left and the right 40% of the photo.
        public volatile bool TwoSubjects;
        // Whatever is red in the photo the model is given (RedLogits).
        public volatile bool ByColour;
        private int _runs;

        public int Runs => _runs;

        public string? UnavailableReason() => Available ? null : "subject-cutout-disabled";

        public float[] PredictLogits(float[] input, int size)
        {
            Interlocked.Increment(ref _runs);
            var quarter = size / 4;
            if (ByColour) return SubjectCutoutImagingTests.RedLogits(input, size);
            if (TwoSubjects)
            {
                return SubjectCutoutImagingTests.Square(size, (x, _) =>
                    x < size * 2 / 5 || x >= size * 3 / 5 ? 12f : -12f);
            }
            return SubjectCutoutImagingTests.Square(size, (x, y) =>
                Subject && x >= quarter && x < size - quarter && y >= quarter && y < size - quarter ? 12f : -12f);
        }
    }

    // Around the touched point it proposes what SAM does: a small patch, the
    // whole person touched, and the whole photo.
    private sealed class FakeSubjectPointModel : ISubjectPointModel
    {
        public volatile bool Available = true;
        public (float X, float Y) LastPoint;
        // Touched between the two people, it proposes the nearer one anyway —
        // as SAM does for a touch on the wall beside someone.
        public volatile bool ProposeNearestPerson;
        private int _runs;

        public int Runs => _runs;

        public string? UnavailableReason() => Available ? null : "subject-point-disabled";

        public float[] PredictCandidates(float[] input, float x, float y, out int count)
        {
            Interlocked.Increment(ref _runs);
            LastPoint = (x, y);
            const int edge = SubjectPointModelShape.CandidateEdge;
            var (px, py) = ((int)(x * edge), (int)(y * edge));
            Func<int, int, bool> patch = (cx, cy) => Math.Abs(cx - px) < 8 && Math.Abs(cy - py) < 8;
            // Between the two people there is no person to propose.
            Func<int, int, bool> person = x < 0.4f ? (cx, _) => cx < edge * 2 / 5
                : x >= 0.6f || ProposeNearestPerson ? (cx, _) => cx >= edge * 3 / 5
                : patch;
            count = 3;
            return SubjectCutoutImagingTests.Candidates(edge, patch, person, (_, _) => true);
        }
    }
}
