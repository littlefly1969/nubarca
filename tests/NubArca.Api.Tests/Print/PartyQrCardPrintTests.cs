using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Access;
using NubArca.Api.Data;
using NubArca.Api.Domain.Print;
using NubArca.Api.Print;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Tests.Party;
using QRCoder;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;

namespace NubArca.Api.Tests.Print;

/// <summary>
/// The host prints the party's QR for its tables: the twin strip's sheet and
/// cut, a photograph over the code in each strip. Proven through the real
/// endpoint, a real enrolled station, the real renderer and the agent's own
/// download — and the code read back module for module, so what is proven is
/// the address a guest's phone will open.
/// </summary>
public sealed class PartyQrCardPrintTests : IDisposable
{
    private readonly List<SqliteWebApplicationFactory> _factories = [];

    public void Dispose()
    {
        foreach (var factory in _factories) factory.Dispose();
    }

    private SqliteWebApplicationFactory Factory(bool withOrigin = true)
    {
        var factory = withOrigin
            ? PartyInvitationTestKit.NewFactory()
            : new SqliteWebApplicationFactory();
        if (!withOrigin) factory.EnsureDatabaseCreated();
        _factories.Add(factory);
        return factory;
    }

    private sealed record Printer(Guid StationId, Guid DeviceId, string Credential);

    private sealed record Host(Guid UserId, HttpClient Client, Guid AlbumId, string ViewToken);

    private static async Task<Host> HostAsync(SqliteWebApplicationFactory factory, string? email = null)
    {
        var (userId, client) = await factory.CreatePermissionClientAsync(
            email ?? $"host-{Guid.NewGuid():N}@example.com", PartyInvitationTestKit.EveryPartyPermission);
        var partyId = await PartyInvitationTestKit.CreatePartyAsync(client, "Festa al mare");
        var (albumId, viewToken, _) = await PartyInvitationTestKit.OpenPublicQrAsync(client, partyId, "Festa al mare");
        return new Host(userId, client, albumId, viewToken);
    }

    private static async Task<Printer> PrinterAsync(
        SqliteWebApplicationFactory factory, HttpClient owner, bool cuts = true, string loaded = "10x15")
    {
        var created = await owner.PostAsJsonAsync("/api/print/stations", new { name = "Sala" });
        created.EnsureSuccessStatusCode();
        var station = await created.Content.ReadFromJsonAsync<JsonElement>();
        var stationId = station.GetProperty("id").GetGuid();
        var enrolled = await factory.CreateClient().PostAsJsonAsync("/api/print-agent/enroll", new
        {
            stationId, enrollmentToken = station.GetProperty("enrollmentToken").GetString(), agentVersion = "0.5.0",
        });
        enrolled.EnsureSuccessStatusCode();
        var credential = (await enrolled.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("stationCredential").GetString()!;
        var formats = cuts ? new[] { "10x15", "13x18", "2x6x2" } : ["10x15", "13x18"];
        (await Agent(factory, HttpMethod.Post, "/api/print-agent/heartbeat", credential, new
        {
            agentVersion = "0.5.0",
            devices = new[]
            {
                new
                {
                    deviceKey = "dnp", displayName = "DNP DS-RX1HS", manufacturer = "DNP", model = "DS-RX1HS",
                    adapterKind = "cups", capabilities = new { formats, color = true }, observedState = "ready",
                    mediaRemaining = new { available = true, remainingPrints = 187, ageSeconds = 0 },
                },
            },
        })).EnsureSuccessStatusCode();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var device = await db.PrinterDevices.SingleAsync(d => d.PrintStationId == stationId);
        device.LoadedPaperSize = loaded;
        await db.SaveChangesAsync();
        return new Printer(stationId, device.Id, credential);
    }

    private static Task<HttpResponseMessage> Agent(
        SqliteWebApplicationFactory factory, HttpMethod method, string url, string credential, object? json = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (json is not null) request.Content = JsonContent.Create(json);
        request.Headers.Add("X-NubArca-Print-Credential", credential);
        return factory.CreateClient().SendAsync(request);
    }

    private static async Task ChoosePrinterAsync(Host host, Printer printer) =>
        (await host.Client.PatchAsJsonAsync($"/api/albums/{host.AlbumId}/party-print-settings",
            new { printStationId = printer.StationId, printerDeviceId = printer.DeviceId })).EnsureSuccessStatusCode();

    private static async Task<Guid> PhotoAsync(SqliteWebApplicationFactory factory, HttpClient client)
    {
        using var image = new Image<Rgb24>(300, 400, new Rgb24(200, 30, 30));
        image[0, 0] = new Rgb24((byte)Random.Shared.Next(256), 0, 0);
        using var stream = new MemoryStream();
        image.Save(stream, new JpegEncoder());
        var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(stream.ToArray());
        part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
        form.Add(part, "file", $"p{Guid.NewGuid():N}.jpg");
        var uploaded = await client.PostAsync("/api/files", form);
        uploaded.EnsureSuccessStatusCode();
        var id = (await uploaded.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var file = await db.FileItems.SingleAsync(f => f.Id == id);
        file.Width = 300;
        file.Height = 400;
        await db.SaveChangesAsync();
        return id;
    }

    private static object Body(Guid photo, double zoom = 1, string locale = "it") =>
        new { fileItemId = photo, placement = new { centerX = 0.5, centerY = 0.5, zoom }, locale };

    private static async Task<HttpResponseMessage> Submit(Host host, object body, string? key = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/albums/{host.AlbumId}/party-print/qr-card")
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString("N"));
        return await host.Client.SendAsync(request);
    }

    private static async Task<string?> Error(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString();

    private static async Task<T> Db<T>(SqliteWebApplicationFactory factory, Func<AppDbContext, Task<T>> read)
    {
        using var scope = factory.Services.CreateScope();
        return await read(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    /// <summary>The sheet the agent would print: claimed and downloaded as the agent does.</summary>
    private static async Task<(JsonElement Job, Image<Rgb24> Sheet)> PrintedSheetAsync(
        SqliteWebApplicationFactory factory, Printer printer)
    {
        var claim = await Agent(factory, HttpMethod.Post, "/api/print-agent/jobs/claim", printer.Credential,
            new { adapterKind = "cups" });
        claim.EnsureSuccessStatusCode();
        var job = await claim.Content.ReadFromJsonAsync<JsonElement>();
        var download = new HttpRequestMessage(HttpMethod.Get, job.GetProperty("artifactUrl").GetString());
        download.Headers.Add("X-NubArca-Print-Credential", printer.Credential);
        download.Headers.Add("X-NubArca-Print-Claim", job.GetProperty("claimToken").GetString());
        var bytes = await (await factory.CreateClient().SendAsync(download)).Content.ReadAsByteArrayAsync();
        return (job, Image.Load<Rgb24>(bytes));
    }

    // --- reading the sheet back -------------------------------------------------

    private static Rectangle Cell(int strip, int cell)
    {
        var (x, y, w, h) = PartyPrintGeometry.QrCardCell(strip, cell);
        const int W = PartyPrintGeometry.PortraitWidth, H = PartyPrintGeometry.PortraitHeight;
        return new Rectangle((int)Math.Round(x * W), (int)Math.Round(y * H),
            (int)Math.Round(w * W), (int)Math.Round(h * H));
    }

    /// <summary>
    /// Whether the code printed in <paramref name="strip"/> is, module for
    /// module, the code for <paramref name="url"/> — its quiet zone included.
    /// </summary>
    private static void AssertCodeIs(Image<Rgb24> sheet, int strip, string url)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
        var matrix = data.ModuleMatrix;
        var area = PartyPrintComposer.QrCodeArea(Cell(strip, 1), matrix.Count, PartyPrintGeometry.PortraitWidth);
        var wrong = 0;
        for (var my = 0; my < matrix.Count; my++)
            for (var mx = 0; mx < matrix.Count; mx++)
            {
                var p = sheet[area.Code.X + (mx * area.Module) + (area.Module / 2),
                    area.Code.Y + (my * area.Module) + (area.Module / 2)];
                var dark = p.R + p.G + p.B < 3 * 128;
                if (dark != matrix[my][mx]) wrong++;
            }
        Assert.True(wrong == 0, $"{wrong} of {matrix.Count * matrix.Count} modules differ from the party's address");
        // A module near a millimetre at 300dpi: read at arm's length.
        Assert.True(area.Module >= 9, $"modules of {area.Module}px");
    }

    // --- the sheet ----------------------------------------------------------------

    [Fact]
    public async Task The_Card_Is_Two_Same_Strips_With_Nothing_Across_The_Cut()
    {
        using var photo = new Image<Rgb24>(400, 600, new Rgb24(20, 120, 220));
        using var stream = new MemoryStream();
        photo.Save(stream, new JpegEncoder());
        const string url = "https://cloud.example.com/party/abcDEF123";
        var bytes = await new PartyPrintComposer().RenderQrCardAsync(new PartyQrCardComposition(
            stream.ToArray(), new PhotoPlacement(0.5, 0.5, 1), url, "Festa al mare",
            PartyQrCardText.Line("it")), default);
        using var sheet = Image.Load<Rgb24>(bytes);

        // The twin strip's sheet: a portrait 10x15.
        Assert.Equal((PartyPrintGeometry.PortraitWidth, PartyPrintGeometry.PortraitHeight), (sheet.Width, sheet.Height));
        // The printer cuts down the middle: nothing is printed across it.
        for (var y = 0; y < sheet.Height; y += 3)
            for (var x = sheet.Width / 2 - 6; x <= sheet.Width / 2 + 6; x++)
            {
                var p = sheet[x, y];
                Assert.True(p.R > 225 && p.G > 225 && p.B > 225, $"ink at ({x},{y}) on the cut");
            }
        // Each strip is a whole card: the photograph over the code, the code the address.
        for (var strip = 0; strip < 2; strip++)
        {
            var cell = Cell(strip, 0);
            var middle = sheet[cell.X + (cell.Width / 2), cell.Y + (cell.Height / 2)];
            Assert.True(middle.B > 180 && middle.R < 60, $"strip {strip} has no photograph on top");
            AssertCodeIs(sheet, strip, url);
        }
    }

    [Fact]
    public void Every_Language_Has_Its_Line_And_No_Other_Is_Guessed()
    {
        foreach (var locale in new[] { "it", "en", "es", "de" })
            Assert.False(string.IsNullOrWhiteSpace(PartyQrCardText.Line(locale)));
        Assert.False(PartyQrCardText.IsKnownLocale("fr"));
        Assert.False(PartyQrCardText.IsKnownLocale(null));
    }

    // --- printing -----------------------------------------------------------------

    [Fact]
    public async Task The_Host_Prints_The_Partys_Address_On_The_Cut_Sheet_And_Keeps_It_Nowhere_Else()
    {
        var factory = Factory();
        var host = await HostAsync(factory);
        var printer = await PrinterAsync(factory, host.Client);
        await ChoosePrinterAsync(host, printer);
        var photo = await PhotoAsync(factory, host.Client);

        var response = await Submit(host, Body(photo));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var accepted = await response.Content.ReadFromJsonAsync<JsonElement>();
        var jobId = accepted.GetProperty("jobId").GetGuid();
        Assert.DoesNotContain(host.ViewToken, accepted.GetRawText());

        var job = await Db(factory, db => db.PrintJobs.SingleAsync(j => j.Id == jobId));
        Assert.Equal(PrintJobKinds.PartyQrCard, job.Kind);
        Assert.Equal(PrintFormats.Strip2x6Pair, job.Format);
        Assert.Equal(PrintJobStates.Ready, job.State);
        // The address is on the paper and nowhere in the record of it.
        Assert.DoesNotContain(host.ViewToken, job.RenderSpecificationJson);
        Assert.DoesNotContain("/party/", job.RenderSpecificationJson);

        // What the agent receives is the cut format, and both cards open the party.
        var (claimed, sheet) = await PrintedSheetAsync(factory, printer);
        Assert.Equal("2x6x2", claimed.GetProperty("format").GetString());
        using (sheet)
        {
            for (var strip = 0; strip < 2; strip++)
                AssertCodeIs(sheet, strip, $"{PartyInvitationTestKit.Origin}/party/{host.ViewToken}");
        }
    }

    [Fact]
    public async Task A_Printer_That_Does_Not_Cut_Or_Has_Other_Paper_Prints_Nothing()
    {
        var factory = Factory();
        var host = await HostAsync(factory);
        var photo = await PhotoAsync(factory, host.Client);
        foreach (var printer in new[]
        {
            await PrinterAsync(factory, host.Client, cuts: false),
            await PrinterAsync(factory, host.Client, loaded: "13x18"),
        })
        {
            await ChoosePrinterAsync(host, printer);
            var refused = await Submit(host, Body(photo));
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Equal("format_unsupported", await Error(refused));
        }
        Assert.Equal(0, await Db(factory, db => db.PrintJobs.CountAsync()));
    }

    [Fact]
    public async Task No_Printer_A_Closed_Party_Or_No_Public_Address_Prints_Nothing()
    {
        var factory = Factory();
        var host = await HostAsync(factory);
        var photo = await PhotoAsync(factory, host.Client);

        var noPrinter = await Submit(host, Body(photo));
        Assert.Equal(HttpStatusCode.Conflict, noPrinter.StatusCode);
        Assert.Equal("no_printer", await Error(noPrinter));

        await ChoosePrinterAsync(host, await PrinterAsync(factory, host.Client));
        (await host.Client.PatchAsJsonAsync($"/api/albums/{host.AlbumId}/party-settings", new { enabled = false }))
            .EnsureSuccessStatusCode();
        var closed = await Submit(host, Body(photo));
        Assert.Equal(HttpStatusCode.Conflict, closed.StatusCode);
        Assert.Equal("party_closed", await Error(closed));

        // Without the installation's public address, a code would point at
        // whatever the host's browser happened to use.
        var bare = Factory(withOrigin: false);
        var other = await HostAsync(bare);
        await ChoosePrinterAsync(other, await PrinterAsync(bare, other.Client));
        var noOrigin = await Submit(other, Body(await PhotoAsync(bare, other.Client)));
        Assert.Equal(HttpStatusCode.Conflict, noOrigin.StatusCode);
        Assert.Equal("origin_unavailable", await Error(noOrigin));

        Assert.Equal(0, await Db(factory, db => db.PrintJobs.CountAsync()));
        Assert.Equal(0, await Db(bare, db => db.PrintJobs.CountAsync()));
    }

    [Fact]
    public async Task Another_Hosts_Party_Or_Photograph_Is_One_Answer()
    {
        var factory = Factory();
        var host = await HostAsync(factory);
        await ChoosePrinterAsync(host, await PrinterAsync(factory, host.Client));
        var stranger = await HostAsync(factory);
        var theirs = await PhotoAsync(factory, stranger.Client);

        // Someone else's photograph on my party.
        var foreignPhoto = await Submit(host, Body(theirs));
        Assert.Equal(HttpStatusCode.NotFound, foreignPhoto.StatusCode);
        // My photograph on someone else's party.
        var mine = await PhotoAsync(factory, host.Client);
        var foreignParty = await Submit(host with { AlbumId = stranger.AlbumId }, Body(mine));
        Assert.Equal(HttpStatusCode.NotFound, foreignParty.StatusCode);
        Assert.Equal(0, await Db(factory, db => db.PrintJobs.CountAsync()));
    }

    [Fact]
    public async Task A_Host_Without_Party_Printing_Is_Turned_Away()
    {
        var factory = Factory();
        var host = await HostAsync(factory);
        var (_, plain) = await factory.CreatePermissionClientAsync(
            $"plain-{Guid.NewGuid():N}@example.com", [Permissions.PartyAccess]);
        var refused = await Submit(host with { Client = plain }, Body(Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    [Fact]
    public async Task The_Same_Key_Is_The_Same_Sheet_And_Another_Card_Under_It_Is_Refused()
    {
        var factory = Factory();
        var host = await HostAsync(factory);
        await ChoosePrinterAsync(host, await PrinterAsync(factory, host.Client));
        var photo = await PhotoAsync(factory, host.Client);

        var first = await Submit(host, Body(photo), "sheet-1");
        var retry = await Submit(host, Body(photo), "sheet-1");
        Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
        Assert.Equal((await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("jobId").GetGuid(),
            (await retry.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("jobId").GetGuid());

        foreach (var changed in new[] { Body(photo, zoom: 1.5), Body(photo, locale: "en") })
        {
            var refused = await Submit(host, changed, "sheet-1");
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Equal("idempotency_conflict", await Error(refused));
        }
        // Several sheets are several keys.
        Assert.Equal(HttpStatusCode.Accepted, (await Submit(host, Body(photo), "sheet-2")).StatusCode);
        Assert.Equal(2, await Db(factory, db => db.PrintJobs.CountAsync()));
    }

    [Fact]
    public async Task On_A_Lent_Printer_Each_Sheet_Is_One_Of_The_Loan()
    {
        var factory = Factory();
        var (_, lender) = await factory.CreateAuthenticatedClientAsync("stefano@example.com");
        var printer = await PrinterAsync(factory, lender);
        var host = await HostAsync(factory, "federica@example.com");
        (await lender.PostAsJsonAsync($"/api/print/stations/{printer.StationId}/devices/{printer.DeviceId}/shares",
            new { email = "federica@example.com", maxSheets = 1 })).EnsureSuccessStatusCode();
        await ChoosePrinterAsync(host, printer);
        var photo = await PhotoAsync(factory, host.Client);

        Assert.Equal(HttpStatusCode.Accepted, (await Submit(host, Body(photo))).StatusCode);
        var spent = await Submit(host, Body(photo));
        Assert.Equal(HttpStatusCode.Conflict, spent.StatusCode);
        Assert.Equal("share_exhausted", await Error(spent));
        Assert.Equal(1, await Db(factory, db => db.PrinterShares.Select(s => s.UsedSheets).SingleAsync()));
    }

    /// <summary>A photograph in the party's album, as the server would know it after ingestion.</summary>
    private static async Task<Guid> PartyPhotoAsync(SqliteWebApplicationFactory factory, Host host)
    {
        var id = await PhotoAsync(factory, host.Client);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var file = await db.FileItems.SingleAsync(f => f.Id == id);
            var meta = await db.BlobMetadata.SingleOrDefaultAsync(m => m.BlobObjectId == file.BlobObjectId);
            if (meta is null)
            {
                meta = new NubArca.Api.Domain.BlobMetadata { Id = Guid.NewGuid(), BlobObjectId = file.BlobObjectId };
                db.BlobMetadata.Add(meta);
            }
            meta.MediaCategory = NubArca.Api.Domain.MediaCategories.Image;
            meta.DetectedContentType = "image/jpeg";
            await db.SaveChangesAsync();
        }
        (await host.Client.PostAsJsonAsync($"/api/albums/{host.AlbumId}/items", new { fileItemId = id }))
            .EnsureSuccessStatusCode();
        return id;
    }

    [Fact]
    public async Task The_Card_Offers_Every_Photograph_The_Party_Shows_Not_A_First_Page()
    {
        // The dialog asked the album's paged media list for 60 and stopped
        // there: parties of 81, 170 and 206 photographs offered 60.
        var factory = Factory();
        var host = await HostAsync(factory);
        var photos = new List<Guid>();
        for (var i = 0; i < 64; i++) photos.Add(await PartyPhotoAsync(factory, host));
        // A guest's upload still waiting for the host is not the party's yet.
        var pending = await PartyPhotoAsync(factory, host);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.PartyUploadItems.Add(new NubArca.Api.Domain.PartyUploadItem
            {
                Id = Guid.NewGuid(), OwnerUserId = host.UserId, AlbumId = host.AlbumId, FileItemId = pending,
                Status = NubArca.Api.Domain.PartyUploadStatuses.Pending, UploadedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var listed = await host.Client.GetFromJsonAsync<JsonElement>(
            $"/api/albums/{host.AlbumId}/party-print/qr-card/photos");
        var ids = listed.EnumerateArray().Select(p => p.GetProperty("fileItemId").GetGuid()).ToList();
        Assert.Equal(64, ids.Count);
        Assert.Equal(photos.ToHashSet(), ids.ToHashSet());
        Assert.DoesNotContain(pending, ids);
        // The shape the framing starts from, as displayed.
        Assert.Equal(300, listed[0].GetProperty("width").GetInt32());
        Assert.Equal(400, listed[0].GetProperty("height").GetInt32());

        // Someone else's party lists nothing of it.
        var stranger = await HostAsync(factory);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.Client.GetAsync(
            $"/api/albums/{host.AlbumId}/party-print/qr-card/photos")).StatusCode);
    }

    [Fact]
    public async Task A_Framing_Further_Out_Than_The_Whole_Photograph_Is_Refused()
    {
        var factory = Factory();
        var host = await HostAsync(factory);
        await ChoosePrinterAsync(host, await PrinterAsync(factory, host.Client));
        var photo = await PhotoAsync(factory, host.Client);
        var refused = await Submit(host, Body(photo, zoom: 0.2));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("invalid_placement", await Error(refused));
    }
}
