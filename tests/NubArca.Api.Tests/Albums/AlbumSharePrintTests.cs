using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Print;
using NubArca.Api.Tests.Endpoints;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;

namespace NubArca.Api.Tests.Albums;

/// <summary>
/// PRINTING FROM A SHARE: one rule, asked by the server on every request —
///
///   canPrint = the share is email + second factor
///              AND this visitor verified on it
///              AND the owner turned printing on.
///
/// Anything less is a 403 on the print routes whatever the page shows. And a
/// print that is allowed is the owner's own album print: the job is the
/// owner's, on the owner's printer, from the shared album — never an owner or
/// a printer the browser named.
/// </summary>
public sealed class AlbumSharePrintTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory = new();
    public AlbumSharePrintTests() => _factory.EnsureDatabaseCreated();
    public void Dispose() => _factory.Dispose();

    private const string Listed = "zia@example.com";

    private sealed record Printer(Guid StationId, Guid DeviceId, string Credential);

    private sealed record Shared(HttpClient Owner, Guid OwnerId, Guid Album, string Token, Guid Photo, Printer Printer);

    // --- the five cases that close the slice -------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_public_share_never_prints_whatever_its_switch_says(bool allowPrint)
    {
        var shared = await SharedAlbumAsync(secondFactor: false, allowPrint: allowPrint);
        var visitor = _factory.CreateClient();

        // The link opens — it is a live public share — and offers no print.
        var page = await visitor.GetFromJsonAsync<JsonElement>($"/api/album-share/{shared.Token}");
        Assert.False(page.GetProperty("canPrint").GetBoolean());

        await AssertForbiddenAsync(visitor, shared);
        Assert.Equal(0, await CountJobsAsync());
    }

    [Fact]
    public async Task A_protected_share_does_not_print_before_the_visitor_verifies()
    {
        var shared = await SharedAlbumAsync(secondFactor: true, allowPrint: true);
        var visitor = _factory.CreateClient();

        await AssertForbiddenAsync(visitor, shared);
        Assert.Equal(0, await CountJobsAsync());
    }

    [Fact]
    public async Task A_verified_visitor_does_not_print_while_the_owner_has_not_allowed_it()
    {
        var shared = await SharedAlbumAsync(secondFactor: true, allowPrint: false);
        var visitor = await VerifiedVisitorAsync(shared.Token);

        var page = await visitor.GetFromJsonAsync<JsonElement>($"/api/album-share/{shared.Token}");
        Assert.False(page.GetProperty("canPrint").GetBoolean());

        await AssertForbiddenAsync(visitor, shared);
        Assert.Equal(0, await CountJobsAsync());
    }

    [Fact]
    public async Task A_verified_visitor_prints_the_shared_album_on_the_owners_printer()
    {
        var shared = await SharedAlbumAsync(secondFactor: true, allowPrint: true);
        var visitor = await VerifiedVisitorAsync(shared.Token);

        var page = await visitor.GetFromJsonAsync<JsonElement>($"/api/album-share/{shared.Token}");
        Assert.True(page.GetProperty("canPrint").GetBoolean());

        // The studio is told what the owner's printer can do, and not which it is.
        var manifest = await visitor.GetAsync($"/api/album-share/{shared.Token}/print");
        Assert.Equal(HttpStatusCode.OK, manifest.StatusCode);
        var printer = await manifest.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(printer.GetProperty("available").GetBoolean());
        Assert.Equal("10x15", printer.GetProperty("paper").GetString());
        var raw = await manifest.Content.ReadAsStringAsync();
        Assert.DoesNotContain(shared.Printer.StationId.ToString(), raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(shared.Printer.DeviceId.ToString(), raw, StringComparison.OrdinalIgnoreCase);

        // A browser that names somebody else's printer and somebody else as the
        // owner is read without them: neither is the visitor's to choose.
        var (strangerId, stranger) = await _factory.CreateAuthenticatedClientAsync("anna@example.com");
        var strangerPrinter = await PrinterAsync(stranger);
        var response = await SubmitAsync(visitor, shared.Token, PrintBody(shared.Photo, extra: new
        {
            ownerUserId = strangerId,
            printStationId = strangerPrinter.StationId,
            printerDeviceId = strangerPrinter.DeviceId,
        }));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var accepted = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, accepted.GetProperty("queueAhead").GetInt32());
        Assert.False(accepted.TryGetProperty("jobId", out _));

        var job = await Db(db => db.PrintJobs.SingleAsync());
        Assert.Equal(shared.OwnerId, job.OwnerUserId);
        Assert.Equal(shared.Printer.StationId, job.PrintStationId);
        Assert.Equal(shared.Printer.DeviceId, job.PrinterDeviceId);
        Assert.Equal(PrintJobKinds.OwnerPhoto, job.Kind);
        Assert.Equal(shared.Photo, job.FileItemId);
        var spec = JsonDocument.Parse(job.RenderSpecificationJson).RootElement;
        Assert.Equal(shared.Album, spec.GetProperty("albumId").GetGuid());
        Assert.Equal("album-share", spec.GetProperty("source").GetString());

        // The owner's print, through the owner's own idempotency record.
        Assert.Equal(1, await Db(db => db.OwnerPhotoPrintRequests.CountAsync(r => r.OwnerUserId == shared.OwnerId)));
    }

    // --- what else the rule implies ----------------------------------------

    [Fact]
    public async Task A_photograph_outside_the_shared_album_is_not_printed()
    {
        var shared = await SharedAlbumAsync(secondFactor: true, allowPrint: true);
        var visitor = await VerifiedVisitorAsync(shared.Token);
        // The owner's own photograph, in their library — but not in this album.
        var elsewhere = await PhotoAsync(shared.Owner);

        var response = await SubmitAsync(visitor, shared.Token, PrintBody(elsewhere));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, await CountJobsAsync());
    }

    [Fact]
    public async Task Turning_the_switch_off_stops_a_visitor_who_already_verified()
    {
        var shared = await SharedAlbumAsync(secondFactor: true, allowPrint: true);
        var visitor = await VerifiedVisitorAsync(shared.Token);
        Assert.Equal(HttpStatusCode.OK, (await visitor.GetAsync($"/api/album-share/{shared.Token}/print")).StatusCode);

        (await shared.Owner.PatchAsJsonAsync($"/api/albums/{shared.Album}/share-link",
            new { allowPrint = false })).EnsureSuccessStatusCode();

        await AssertForbiddenAsync(visitor, shared);
    }

    [Fact]
    public async Task Turning_the_second_factor_off_stops_printing_even_with_the_switch_on()
    {
        var shared = await SharedAlbumAsync(secondFactor: true, allowPrint: true);
        var visitor = await VerifiedVisitorAsync(shared.Token);

        (await shared.Owner.PatchAsJsonAsync($"/api/albums/{shared.Album}/share-link",
            new { requireSecondFactor = false })).EnsureSuccessStatusCode();

        // The switch is still stored as the owner left it…
        var link = await shared.Owner.GetFromJsonAsync<JsonElement>($"/api/albums/{shared.Album}/share-link");
        Assert.True(link.GetProperty("allowPrint").GetBoolean());
        // …and the link, now public, prints nothing.
        await AssertForbiddenAsync(visitor, shared);
    }

    [Fact]
    public async Task The_switch_is_off_on_a_new_share_and_survives_a_rotation()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await AlbumAsync(owner);
        var created = await (await owner.PostAsync($"/api/albums/{album}/share-link", null))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(created.GetProperty("allowPrint").GetBoolean());

        (await owner.PatchAsJsonAsync($"/api/albums/{album}/share-link",
            new { allowPrint = true })).EnsureSuccessStatusCode();
        var rotated = await (await owner.PostAsync($"/api/albums/{album}/share-link/rotate", null))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(rotated.GetProperty("allowPrint").GetBoolean());
    }

    // --- helpers -----------------------------------------------------------

    private async Task AssertForbiddenAsync(HttpClient visitor, Shared shared)
    {
        Assert.Equal(HttpStatusCode.Forbidden,
            (await visitor.GetAsync($"/api/album-share/{shared.Token}/print")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await visitor.GetAsync(
            $"/api/album-share/{shared.Token}/print/date?fileItemId={shared.Photo}&timeZone=Europe/Rome")).StatusCode);
        // Asking the API directly, past the page that would have hidden the button.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await SubmitAsync(visitor, shared.Token, PrintBody(shared.Photo))).StatusCode);
    }

    private static object PrintBody(Guid photo, object? extra = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["expectedPaperSize"] = "10x15",
            ["orientation"] = "landscape",
            ["layout"] = "photo",
            ["style"] = "fullBleed",
            ["photos"] = new[] { new { fileItemId = photo, placement = new { centerX = 0.5, centerY = 0.5, zoom = 1 } } },
            ["includeDate"] = false,
        };
        if (extra is not null)
        {
            foreach (var property in JsonSerializer.SerializeToElement(extra).EnumerateObject())
                body[property.Name] = property.Value;
        }
        return body;
    }

    private static Task<HttpResponseMessage> SubmitAsync(HttpClient client, string token, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/album-share/{token}/print/jobs")
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        return client.SendAsync(request);
    }

    private async Task<Shared> SharedAlbumAsync(bool secondFactor, bool allowPrint)
    {
        var (ownerId, owner) = await _factory.CreateAuthenticatedClientAsync("stefano@example.com");
        var printer = await PrinterAsync(owner);
        var album = await AlbumAsync(owner);
        var photo = await PhotoAsync(owner);
        (await owner.PostAsJsonAsync($"/api/albums/{album}/items", new { fileItemId = photo }))
            .EnsureSuccessStatusCode();

        var created = await owner.PostAsync($"/api/albums/{album}/share-link", null);
        created.EnsureSuccessStatusCode();
        var token = (await created.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("url").GetString()!["/album/".Length..];
        if (secondFactor)
        {
            (await owner.PostAsJsonAsync($"/api/albums/{album}/share-link/guests",
                new { email = Listed, displayName = "La zia" })).EnsureSuccessStatusCode();
        }
        (await owner.PatchAsJsonAsync($"/api/albums/{album}/share-link",
            new { requireSecondFactor = secondFactor, allowPrint })).EnsureSuccessStatusCode();
        return new Shared(owner, ownerId, album, token, photo, printer);
    }

    private async Task<HttpClient> VerifiedVisitorAsync(string token)
    {
        var visitor = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Accepted, (await visitor.PostAsJsonAsync(
            $"/api/album-share/{token}/challenge", new { email = Listed })).StatusCode);
        await _factory.WaitForShareCodesAsync(1);
        var match = Regex.Match(_factory.EmailSender.Messages[^1].TextBody, @"\b(\d{6})\b");
        Assert.True(match.Success, "the message carried no six-digit code");
        Assert.Equal(HttpStatusCode.NoContent, (await visitor.PostAsJsonAsync(
            $"/api/album-share/{token}/verify", new { email = Listed, code = match.Groups[1].Value })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await visitor.GetAsync($"/api/album-share/{token}")).StatusCode);
        return visitor;
    }

    private static async Task<Guid> AlbumAsync(HttpClient owner) =>
        (await (await owner.PostAsJsonAsync("/api/albums", new { name = $"Album {Guid.NewGuid():N}" }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

    private async Task<Printer> PrinterAsync(HttpClient owner)
    {
        var created = await owner.PostAsJsonAsync("/api/print/stations", new { name = "Sala" });
        created.EnsureSuccessStatusCode();
        var station = await created.Content.ReadFromJsonAsync<JsonElement>();
        var stationId = station.GetProperty("id").GetGuid();
        var enrolled = await _factory.CreateClient().PostAsJsonAsync("/api/print-agent/enroll", new
        {
            stationId, enrollmentToken = station.GetProperty("enrollmentToken").GetString(), agentVersion = "0.5.0",
        });
        enrolled.EnsureSuccessStatusCode();
        var credential = (await enrolled.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("stationCredential").GetString()!;
        var heartbeat = new HttpRequestMessage(HttpMethod.Post, "/api/print-agent/heartbeat")
        {
            Content = JsonContent.Create(new
            {
                agentVersion = "0.5.0",
                devices = new[]
                {
                    new
                    {
                        deviceKey = "dnp", displayName = "DNP DS-RX1HS", manufacturer = "DNP", model = "DS-RX1HS",
                        adapterKind = "cups",
                        capabilities = new { formats = new[] { "10x15", "13x18", "20x15" }, color = true },
                        observedState = "ready",
                    },
                },
            }),
        };
        heartbeat.Headers.Add("X-NubArca-Print-Credential", credential);
        (await _factory.CreateClient().SendAsync(heartbeat)).EnsureSuccessStatusCode();
        var deviceId = await Db(db => db.PrinterDevices.Where(d => d.PrintStationId == stationId)
            .Select(d => d.Id).SingleAsync());
        return new Printer(stationId, deviceId, credential);
    }

    /// <summary>A photograph in the owner's library with the metadata a real one carries.</summary>
    private async Task<Guid> PhotoAsync(HttpClient client, int width = 300, int height = 200)
    {
        using var image = new Image<Rgb24>(width, height, new Rgb24(200, 30, 30));
        // Each photograph its own bytes: identical bytes are ONE blob.
        image[0, 0] = new Rgb24((byte)Random.Shared.Next(256), (byte)Random.Shared.Next(256), 0);
        using var jpeg = new MemoryStream();
        image.Save(jpeg, new JpegEncoder());
        var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(jpeg.ToArray());
        part.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(part, "file", $"p{Guid.NewGuid():N}.jpg");
        var uploaded = await client.PostAsync("/api/files", form);
        uploaded.EnsureSuccessStatusCode();
        var id = (await uploaded.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var file = await db.FileItems.SingleAsync(f => f.Id == id);
        file.Width = width;
        file.Height = height;
        var meta = await db.BlobMetadata.SingleOrDefaultAsync(m => m.BlobObjectId == file.BlobObjectId);
        if (meta is null)
        {
            meta = new BlobMetadata { Id = Guid.NewGuid(), BlobObjectId = file.BlobObjectId };
            db.BlobMetadata.Add(meta);
        }
        meta.MediaCategory = MediaCategories.Image;
        meta.DetectedContentType = "image/jpeg";
        meta.Width = width;
        meta.Height = height;
        await db.SaveChangesAsync();
        return id;
    }

    private Task<int> CountJobsAsync() => Db(db => db.PrintJobs.CountAsync());

    private async Task<T> Db<T>(Func<AppDbContext, Task<T>> read)
    {
        using var scope = _factory.Services.CreateScope();
        return await read(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }
}
