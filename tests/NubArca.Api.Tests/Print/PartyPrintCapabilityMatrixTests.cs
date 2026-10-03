using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Domain.Print;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Tests.Metadata;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using NubArca.Api.Tests.Party;

namespace NubArca.Api.Tests.Print;

/// <summary>
/// The authorization matrix for party printing, frozen against the real
/// endpoints.
///
/// A print token and a view token authorize DIFFERENT capabilities, but where
/// both may see a photograph they travel the SAME serving path — one boundary,
/// so metadata stripping, derived-size selection and the refusal to serve an
/// original cannot drift apart between them. These tests are what keeps that
/// true: a future endpoint that served its own bytes would fail here.
/// </summary>
public sealed class PartyPrintCapabilityMatrixTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory = new();
    public PartyPrintCapabilityMatrixTests() => _factory.EnsureDatabaseCreated();
    public void Dispose() => _factory.Dispose();

    private int _parties;

    private sealed record Party(
        string ViewToken, string PrintToken, Guid AlbumId, Guid OwnerId, Guid PhotoId);

    /// <summary>A party with printing configured and one photograph carrying EXIF.</summary>
    private async Task<Party> SeedPartyAsync(
        bool enablePrinting = true, string capabilities = "{\"formats\":[\"10x15\",\"2x6x2\"]}",
        string paper = "10x15", string? hostEmail = null)
    {
        // A named host may throw a second party: the account is made once.
        var email = hostEmail ?? $"host{Interlocked.Increment(ref _parties)}@example.com";
        bool exists;
        using (var scope = _factory.Services.CreateScope())
        {
            exists = await scope.ServiceProvider.GetRequiredService<AppDbContext>()
                .Users.AnyAsync(u => u.Email == email);
        }
        var owner = exists
            ? await _factory.LoginAsync(email)
            : (await _factory.CreateAuthenticatedClientAsync(email)).Client;
        // A second party of the same host needs its own album and file names.
        var again = exists ? $" {Guid.NewGuid():N}" : string.Empty;
        var albumId = await CreateAlbumAsync(owner, $"Festa{again}");
        var photoId = await AddJpegWithExifAsync(owner, albumId, $"p1{again}.jpg");

        var status = await EnablePartyAsync(owner, albumId);
        var viewToken = status.GetProperty("partyUrl").GetString()!["/party/".Length..];

        Guid ownerId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            ownerId = await db.Albums.Where(a => a.Id == albumId)
                .Select(a => a.OwnerUserId).SingleAsync();

            if (enablePrinting)
            {
                var stationId = Guid.NewGuid();
                var deviceId = Guid.NewGuid();
                db.PrintStations.Add(new PrintStation
                {
                    Id = stationId, OwnerUserId = ownerId, Name = "Postazione",
                    Enabled = true, CreatedAt = DateTime.UtcNow,
                });
                db.PrinterDevices.Add(new PrinterDevice
                {
                    Id = deviceId, PrintStationId = stationId, DeviceKey = "d1",
                    DisplayName = "DS620", AdapterKind = "fake",
                    CapabilitiesJson = capabilities,
                    LoadedPaperSize = paper,
                    LastObservedState = PrintDeviceStates.Ready, LastSeenAt = DateTime.UtcNow,
                });
                db.PartyPrintProfiles.Add(new PartyPrintProfile
                {
                    Id = Guid.NewGuid(), PartyAlbumId = albumId, OwnerUserId = ownerId,
                    Enabled = true, PrintStationId = stationId, PrinterDeviceId = deviceId,
                    PhotoEnabled = true, PhotoMaxPrints = 5,
                    StripEnabled = true, StripMaxPrints = 5,
                    GridEnabled = true, GridMaxPrints = 5,
                    PublicSequenceNext = 1,
                    CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
                });
                await db.SaveChangesAsync();
            }
        }

        // The print token reaches a guest exactly one way: the landing publishes
        // it, and only when printing would actually work.
        var anon = _factory.CreateClient();
        var album = await anon.GetFromJsonAsync<JsonElement>($"/api/party/{viewToken}");
        var printUrl = album.GetProperty("capabilities").GetProperty("printUrl");
        var printToken = printUrl.ValueKind == JsonValueKind.Null
            ? string.Empty
            : printUrl.GetString()!["/party/".Length..].Replace("/print", string.Empty);

        return new Party(viewToken, printToken, albumId, ownerId, photoId);
    }

    [Fact]
    public async Task A_Print_Token_Is_Published_Only_When_Printing_Would_Work()
    {
        var configured = await SeedPartyAsync(enablePrinting: true);
        Assert.NotEqual(string.Empty, configured.PrintToken);

        // No profile, no capability, no card on the guest hub.
        var bare = await SeedPartyAsync(enablePrinting: false);
        Assert.Equal(string.Empty, bare.PrintToken);
    }

    [Fact]
    public async Task The_Manifest_Says_When_The_Printer_Cuts_The_Strips()
    {
        var anon = _factory.CreateClient();
        async Task<Dictionary<string, bool>> CutByPrinter(Party party)
        {
            var manifest = await anon.GetFromJsonAsync<JsonElement>(
                $"/api/party/{party.PrintToken}/print");
            return manifest.GetProperty("formats").EnumerateArray().ToDictionary(
                f => f.GetProperty("type").GetString()!,
                f => f.GetProperty("cutByPrinter").GetBoolean());
        }

        // A printer that cuts the 10x15 in two: the twin strip, cut by it.
        var cutting = await CutByPrinter(await SeedPartyAsync());
        Assert.False(cutting["photo"]);
        Assert.True(cutting["twinStrip4"]);

        // One that only prints 10x15 has no twin strip at all: the two strips
        // are the printer's cut, never a sheet for scissors.
        var plain = await CutByPrinter(await SeedPartyAsync(
            capabilities: "{\"formats\":[\"10x15\"]}"));
        Assert.False(plain.ContainsKey("twinStrip4"));
        Assert.False(plain.ContainsKey("strip4"));

        // Cutting is an extra ON TOP of 10x15, never a way around it.
        var cutOnly = await SeedPartyAsync(capabilities: "{\"formats\":[\"2x6x2\"]}");
        Assert.Equal(string.Empty, cutOnly.PrintToken);
    }

    [Theory]
    [InlineData("10x15", "{\"formats\":[\"10x15\",\"2x6x2\"]}", "photo,grid4,twinStrip4")]
    [InlineData("13x18", "{\"formats\":[\"10x15\",\"13x18\",\"2x6x2\"]}", "photo,grid4")]
    [InlineData("20x15", "{\"formats\":[\"10x15\",\"20x15\",\"2x6x2\"]}", "photo,grid4")]
    public async Task A_Guest_Is_Offered_What_The_Loaded_Paper_Can_Make(
        string paper, string capabilities, string expected)
    {
        // The paper is a fact about the printer, not a guest's choice: the
        // studio lists the products that paper makes, and nothing else.
        var party = await SeedPartyAsync(capabilities: capabilities, paper: paper);
        var manifest = await _factory.CreateClient().GetFromJsonAsync<JsonElement>(
            $"/api/party/{party.PrintToken}/print");
        Assert.Equal(paper, manifest.GetProperty("paperSize").GetString());
        var formats = manifest.GetProperty("formats").EnumerateArray().ToList();
        Assert.Equal(expected, string.Join(",", formats.Select(f => f.GetProperty("type").GetString())));
        foreach (var format in formats)
            Assert.Equal(paper, format.GetProperty("paperSize").GetString());
        var required = formats.ToDictionary(
            f => f.GetProperty("type").GetString()!, f => f.GetProperty("requiredPhotos").GetInt32());
        Assert.Equal(1, required["photo"]);
        Assert.Equal(4, required["grid4"]);
        if (required.TryGetValue("twinStrip4", out var eight)) Assert.Equal(8, eight);
    }

    [Fact]
    public async Task A_Paper_The_Printer_Cannot_Print_Opens_Nothing()
    {
        // The operator says 13x18 is in, but the Print Agent reports only 10x15
        // (an older agent, or a driver without the size): nothing is printed
        // rather than something on the wrong paper.
        var party = await SeedPartyAsync(paper: "13x18");
        Assert.Equal(string.Empty, party.PrintToken);
    }

    [Fact]
    public async Task A_Host_Prints_On_A_Printer_Lent_To_Them_Until_The_Loan_Ends()
    {
        // The party's own printer is swapped for one another account lends the
        // host: a station and printer that are NOT the host's.
        var party = await SeedPartyAsync();
        var lender = await _factory.SeedUserAsync("lender@example.com");
        Guid shareId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var station = new PrintStation
            {
                Id = Guid.NewGuid(), OwnerUserId = lender, Name = "Studio di Stefano", Enabled = true,
                CreatedAt = DateTime.UtcNow,
            };
            var device = new PrinterDevice
            {
                Id = Guid.NewGuid(), PrintStationId = station.Id, DeviceKey = "dnp", DisplayName = "DNP",
                AdapterKind = "fake", CapabilitiesJson = "{\"formats\":[\"10x15\",\"2x6x2\"]}",
                LastObservedState = PrintDeviceStates.Ready, LastSeenAt = DateTime.UtcNow,
            };
            db.PrintStations.Add(station);
            db.PrinterDevices.Add(device);
            var profile = await db.PartyPrintProfiles.SingleAsync(p => p.PartyAlbumId == party.AlbumId);
            profile.PrintStationId = station.Id;
            profile.PrinterDeviceId = device.Id;
            await db.SaveChangesAsync();
            var share = new PrinterShare
            {
                Id = Guid.NewGuid(), PrinterDeviceId = device.Id, OwnerUserId = lender,
                GranteeUserId = party.OwnerId, MaxSheets = 3, CreatedAt = DateTime.UtcNow,
            };
            db.PrinterShares.Add(share);
            await db.SaveChangesAsync();
            shareId = share.Id;
        }

        var anon = _factory.CreateClient();
        async Task<bool> Printing() =>
            (await anon.GetFromJsonAsync<JsonElement>($"/api/party/{party.ViewToken}"))
                .GetProperty("capabilities").GetProperty("printUrl").ValueKind != JsonValueKind.Null;
        async Task Change(Action<PrinterShare> change)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            change(await db.PrinterShares.SingleAsync(s => s.Id == shareId));
            await db.SaveChangesAsync();
        }

        Assert.True(await Printing());
        // The sheet that takes the loan's last sheet is accepted…
        var last = await QueueSheetAsync(party);
        // Its sheets spent: the party's card goes away, like an empty budget —
        // and the guest who took the last one still follows it.
        await Change(s => s.UsedSheets = 3);
        Assert.False(await Printing());
        Assert.Equal(HttpStatusCode.OK, (await StatusAsync(party, last)).StatusCode);
        await Change(s => s.MaxSheets = 10);
        Assert.True(await Printing());
        // Ended: the next request prints nothing; what is queued drains, and
        // is still followed while it does.
        await Change(s => s.RevokedAt = DateTime.UtcNow);
        Assert.False(await Printing());
        Assert.Equal(HttpStatusCode.OK, (await StatusAsync(party, last)).StatusCode);
    }

    /// <summary>
    /// A sheet accepted for <paramref name="party"/>, as the submission leaves it:
    /// the job on the party's printer, and the request that ties it to the party.
    /// </summary>
    private async Task<Guid> QueueSheetAsync(Party party)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var profile = await db.PartyPrintProfiles.SingleAsync(p => p.PartyAlbumId == party.AlbumId);
        var job = new PrintJob
        {
            Id = Guid.NewGuid(), OwnerUserId = party.OwnerId, PrintStationId = profile.PrintStationId!.Value,
            PrinterDeviceId = profile.PrinterDeviceId!.Value, FileItemId = party.PhotoId,
            Kind = PrintJobKinds.PartyPhoto, Format = PrintFormats.Photo10x15, State = PrintJobStates.Ready,
            PublicSequence = 7, RenderSpecificationJson = "{}", ArtifactStorageKey = "artifact",
            ArtifactContentType = "image/jpeg", ArtifactByteLength = 1,
            CreatedAt = DateTime.UtcNow, RenderedAt = DateTime.UtcNow,
        };
        db.PrintJobs.Add(job);
        db.PartyPrintRequests.Add(new PartyPrintRequest
        {
            Id = Guid.NewGuid(), PartyAlbumId = party.AlbumId, IdempotencyKeyHash = new string('a', 64),
            Product = PartyPrintProducts.Photo, PrintJobId = job.Id, CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return job.Id;
    }

    private Task<HttpResponseMessage> StatusAsync(Party party, Guid jobId) =>
        _factory.CreateClient().GetAsync($"/api/party/{party.PrintToken}/print/{jobId}");

    private async Task ChangeProfileAsync(Party party, Action<PartyPrintProfile> change)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        change(await db.PartyPrintProfiles.SingleAsync(p => p.PartyAlbumId == party.AlbumId));
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task A_Sheet_Is_Followed_Only_Through_The_Party_That_Accepted_It()
    {
        // One host, two parties: a job id of one is not found through the other's token.
        var a = await SeedPartyAsync(hostEmail: "twoparties@example.com");
        var b = await SeedPartyAsync(hostEmail: "twoparties@example.com");
        Assert.Equal(a.OwnerId, b.OwnerId);
        var jobA = await QueueSheetAsync(a);
        var jobB = await QueueSheetAsync(b);

        Assert.Equal(HttpStatusCode.OK, (await StatusAsync(a, jobA)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await StatusAsync(b, jobB)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await StatusAsync(a, jobB)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await StatusAsync(b, jobA)).StatusCode);
    }

    [Fact]
    public async Task An_Accepted_Sheet_Stays_Followable_When_Printing_Closes_But_Not_When_Its_Token_Does()
    {
        var party = await SeedPartyAsync();
        var jobId = await QueueSheetAsync(party);

        // Every product spent, then printing switched off: nothing new may be
        // sent, and the sheet already in the queue is still followed.
        await ChangeProfileAsync(party, p =>
        {
            p.PhotoAcceptedCount = p.PhotoMaxPrints;
            p.StripAcceptedCount = p.StripMaxPrints;
            p.GridAcceptedCount = p.GridMaxPrints;
        });
        var anon = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/api/party/{party.PrintToken}/print")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await StatusAsync(party, jobId)).StatusCode);
        await ChangeProfileAsync(party, p => p.Enabled = false);
        var followed = await (await StatusAsync(party, jobId)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("queued", followed.GetProperty("state").GetString());

        // The token itself ended — the party's link revoked — follows nothing.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await db.PartyAlbumLinks.SingleAsync(l => l.AlbumId == party.AlbumId)).RevokedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.NotFound, (await StatusAsync(party, jobId)).StatusCode);
    }

    [Fact]
    public async Task A_Guest_Whose_Sheet_Waits_For_Its_Paper_Is_Told_So()
    {
        // The printer prints both papers; a 10x15 sheet is queued and 20x15 is
        // then put in. The guest at the desk is told why nothing is coming out.
        var party = await SeedPartyAsync(capabilities: "{\"formats\":[\"10x15\",\"20x15\",\"2x6x2\"]}");
        var jobId = await QueueSheetAsync(party);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var profile = await db.PartyPrintProfiles.SingleAsync(p => p.PartyAlbumId == party.AlbumId);
            (await db.PrinterDevices.SingleAsync(d => d.Id == profile.PrinterDeviceId)).LoadedPaperSize = "20x15";
            await db.SaveChangesAsync();
        }
        var anon = _factory.CreateClient();
        async Task<string> State() => (await anon.GetFromJsonAsync<JsonElement>(
            $"/api/party/{party.PrintToken}/print/{jobId}")).GetProperty("state").GetString()!;
        Assert.Equal("waiting_paper", await State());

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await db.PrinterDevices.SingleAsync(d => d.PrintStationId != Guid.Empty
                && db.PartyPrintProfiles.Any(p => p.PartyAlbumId == party.AlbumId && p.PrinterDeviceId == d.Id)))
                .LoadedPaperSize = "10x15";
            await db.SaveChangesAsync();
        }
        Assert.Equal("queued", await State());
    }

    [Fact]
    public async Task The_Manifest_Tells_A_Guest_THEIR_Remaining_Prints()
    {
        // A guest bounded to two on a party of forty was being told forty, and
        // discovered their own limit only by being refused: the studio was
        // hiding the rule from the one person it applies to.
        var party = await SeedPartyAsync();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var profile = await db.PartyPrintProfiles
                .SingleAsync(p => p.PartyAlbumId == party.AlbumId);
            profile.PhotoMaxPrints = 40;
            profile.PhotoPrintsPerGuest = 2;
            await db.SaveChangesAsync();
        }

        var anon = _factory.CreateClient();
        var manifest = await anon.GetFromJsonAsync<JsonElement>(
            $"/api/party/{party.PrintToken}/print");
        var photo = manifest.GetProperty("formats").EnumerateArray()
            .Single(f => f.GetProperty("type").GetString() == "photo");

        // The party's number is still reported; the guest's is reported beside
        // it, and the client shows whichever is smaller.
        Assert.Equal(40, photo.GetProperty("remaining").GetInt32());
        Assert.Equal(2, photo.GetProperty("remainingForYou").GetInt32());

        // With no per-guest ceiling it is null, which is NOT zero: it means the
        // limit does not exist.
        var strip = manifest.GetProperty("formats").EnumerateArray()
            .Single(f => f.GetProperty("type").GetString() == "twinStrip4");
        Assert.Equal(JsonValueKind.Null, strip.GetProperty("remainingForYou").ValueKind);
    }

    [Fact]
    public async Task A_Print_Token_May_Read_Derived_Media_And_Nothing_Else()
    {
        var party = await SeedPartyAsync();
        var anon = _factory.CreateClient();

        // Allowed: the two derived sizes the studio composes against.
        foreach (var variant in new[] { "thumbnail", "preview" })
        {
            var ok = await anon.GetAsync(
                $"/api/party/{party.PrintToken}/print/media/{party.PhotoId}/{variant}");
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        // Denied: the original, under every spelling. A print token composes; it
        // never hands a guest the file.
        foreach (var variant in new[] { "download", "original", "full", "source" })
        {
            var denied = await anon.GetAsync(
                $"/api/party/{party.PrintToken}/print/media/{party.PhotoId}/{variant}");
            Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        }
    }

    [Fact]
    public async Task Derived_Media_Through_The_Print_Token_Is_Stripped_And_Downscaled()
    {
        // The point of one shared serving boundary: what a print token sees has
        // been through the same stripping and resizing as what a viewer sees.
        var party = await SeedPartyAsync();
        var anon = _factory.CreateClient();

        var response = await anon.GetAsync(
            $"/api/party/{party.PrintToken}/print/media/{party.PhotoId}/preview");
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync();

        using var image = Image.Load<Rgba32>(bytes);
        // No EXIF profile at all: no camera, no GPS, no capture time travels out
        // of the party through the print capability either.
        Assert.Null(image.Metadata.ExifProfile);
        Assert.Null(image.Metadata.XmpProfile);
        // A derived copy, not the original bytes.
        Assert.NotEqual(ImageFixtures.JpegWithExif().Length, bytes.Length);
    }

    [Fact]
    public async Task The_View_Token_Behaves_Exactly_As_It_Did()
    {
        // Extracting the shared core must not have changed the surface it came
        // from: the landing still serves its three variants and still refuses
        // an original.
        var party = await SeedPartyAsync();
        var anon = _factory.CreateClient();

        foreach (var variant in new[] { "thumbnail", "preview", "download" })
        {
            var ok = await anon.GetAsync(
                $"/api/party/{party.ViewToken}/media/{party.PhotoId}/{variant}");
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        var stripped = await anon.GetAsync(
            $"/api/party/{party.ViewToken}/media/{party.PhotoId}/preview");
        using var image = Image.Load<Rgba32>(await stripped.Content.ReadAsByteArrayAsync());
        Assert.Null(image.Metadata.ExifProfile);
    }

    [Fact]
    public async Task The_Two_Tokens_Do_Not_Open_Each_Other_Is_Doors()
    {
        var party = await SeedPartyAsync();
        var anon = _factory.CreateClient();

        // A view token is not a print capability: it cannot read the manifest
        // and it cannot submit. This is the whole reason printing has its own.
        Assert.Equal(HttpStatusCode.NotFound,
            (await anon.GetAsync($"/api/party/{party.ViewToken}/print")).StatusCode);

        // And a print token is not a view token: it cannot reach the album, the
        // items or the ordinary media route.
        Assert.Equal(HttpStatusCode.NotFound,
            (await anon.GetAsync($"/api/party/{party.PrintToken}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await anon.GetAsync($"/api/party/{party.PrintToken}/items")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await anon.GetAsync(
                $"/api/party/{party.PrintToken}/media/{party.PhotoId}/download")).StatusCode);
    }

    [Fact]
    public async Task An_Unknown_Or_Foreign_Token_Opens_Nothing()
    {
        var mine = await SeedPartyAsync();
        var theirs = await SeedPartyAsync();
        var anon = _factory.CreateClient();

        // Made up.
        Assert.Equal(HttpStatusCode.NotFound,
            (await anon.GetAsync("/api/party/not-a-token/print")).StatusCode);

        // Another party's print token cannot reach THIS party's photograph: the
        // capability is scoped to the album it was issued for.
        var crossed = await anon.GetAsync(
            $"/api/party/{theirs.PrintToken}/print/media/{mine.PhotoId}/preview");
        Assert.Equal(HttpStatusCode.NotFound, crossed.StatusCode);
    }

    [Fact]
    public async Task Turning_Printing_Off_Closes_The_Capability_Immediately()
    {
        var party = await SeedPartyAsync();
        var anon = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK,
            (await anon.GetAsync($"/api/party/{party.PrintToken}/print")).StatusCode);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var profile = await db.PartyPrintProfiles
                .SingleAsync(p => p.PartyAlbumId == party.AlbumId);
            profile.Enabled = false;
            await db.SaveChangesAsync();
        }

        // The token did not change; what it authorizes did. Re-resolving on every
        // request is what makes a host's switch take effect now rather than when
        // some cache expires.
        Assert.Equal(HttpStatusCode.NotFound,
            (await anon.GetAsync($"/api/party/{party.PrintToken}/print")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await anon.GetAsync(
                $"/api/party/{party.PrintToken}/print/media/{party.PhotoId}/preview")).StatusCode);
    }

    [Fact]
    public async Task The_Manifest_Tells_A_Guest_Nothing_About_The_Machinery()
    {
        var party = await SeedPartyAsync();
        var anon = _factory.CreateClient();
        var body = await (await anon.GetAsync($"/api/party/{party.PrintToken}/print"))
            .Content.ReadAsStringAsync();

        // No owner, no station, no device, no key, no storage path — a guest
        // learns what they can print, not what prints it.
        foreach (var secret in new[]
        {
            party.OwnerId.ToString(), "printStationId", "printerDeviceId",
            "deviceKey", "storageKey", "OwnerUserId", "adapterKind",
        })
        {
            Assert.DoesNotContain(secret, body, StringComparison.OrdinalIgnoreCase);
        }
        // And no original URL anywhere in it.
        Assert.DoesNotContain("/download", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Submitting_Without_An_Idempotency_Key_Is_Refused()
    {
        // Printing has a physical effect, so the protection against a double tap
        // is required rather than optional.
        var party = await SeedPartyAsync();
        var anon = _factory.CreateClient();

        var response = await anon.PostAsJsonAsync(
            $"/api/party/{party.PrintToken}/print",
            new { product = "photo", theme = "pure", slots = new[]
            {
                new { itemId = party.PhotoId, cropX = 0.0, cropY = 0.0, cropWidth = 1.0, cropHeight = 1.0 },
            } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("idempotency_key_required", await response.Content.ReadAsStringAsync());
    }

    private async Task<HttpResponseMessage> SubmitAsync(Party party, object slot)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/party/{party.PrintToken}/print")
        {
            Content = JsonContent.Create(new { product = "photo", theme = "pure", slots = new[] { slot } }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        return await _factory.CreateClient().SendAsync(request);
    }

    [Fact]
    public async Task The_Studio_Prints_With_A_Placement_Through_The_Endpoint()
    {
        // The current studio sends a placement and no crop. It reached the
        // service without its placement — and with an empty crop in its place —
        // so every print from it was refused as invalid.
        var party = await SeedPartyAsync();
        var placed = await SubmitAsync(party,
            new { itemId = party.PhotoId, placement = new { centerX = 0.5, centerY = 0.5, zoom = 1.0 } });
        Assert.Equal(HttpStatusCode.Accepted, placed.StatusCode);
        var jobId = (await placed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("jobId").GetGuid();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var source = await db.PrintJobSources.SingleAsync(s => s.PrintJobId == jobId);
        Assert.Equal(1.0, source.PlacementZoom);
        Assert.Equal(0.5, source.PlacementCenterX);
    }

    [Fact]
    public async Task A_Page_From_Before_Placements_Still_Prints_With_Its_Crop()
    {
        var party = await SeedPartyAsync();
        var cropped = await SubmitAsync(party,
            new { itemId = party.PhotoId, cropX = 0.0, cropY = 0.0, cropWidth = 1.0, cropHeight = 1.0 });
        Assert.Equal(HttpStatusCode.Accepted, cropped.StatusCode);
    }

    [Fact]
    public async Task A_Slot_With_Both_Or_Neither_Framing_Is_Refused_At_The_Endpoint()
    {
        var party = await SeedPartyAsync();
        var both = await SubmitAsync(party, new
        {
            itemId = party.PhotoId, cropX = 0.0, cropY = 0.0, cropWidth = 1.0, cropHeight = 1.0,
            placement = new { centerX = 0.5, centerY = 0.5, zoom = 1.0 },
        });
        Assert.Equal(HttpStatusCode.BadRequest, both.StatusCode);
        var neither = await SubmitAsync(party, new { itemId = party.PhotoId });
        Assert.Equal(HttpStatusCode.BadRequest, neither.StatusCode);
    }

    // --- Owner-side helpers, matching the party tests' shapes ---

    private async Task<Guid> CreateAlbumAsync(HttpClient owner, string name)
    {
        var response = await owner.PostAsJsonAsync("/api/albums", new { name });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private async Task<JsonElement> EnablePartyAsync(HttpClient owner, Guid albumId)
    {
        var resp = await owner.PatchAsJsonAsync(
            $"/api/albums/{albumId}/party-settings", new { enabled = true });
        resp.EnsureSuccessStatusCode();
        var settings = await resp.Content.ReadFromJsonAsync<JsonElement>();
        // Enabling guest access PUBLISHES the party — an invitation, which is
        // deliberately not the party itself. These tests exercise the party, so
        // they start it, exactly as a host does.
        await PartyTestHost.StartAsync(owner, settings);
        return settings;
    }

    private async Task<Guid> AddJpegWithExifAsync(HttpClient owner, Guid albumId, string name)
    {
        var fileId = await UploadAsync(owner, name, ImageFixtures.JpegWithExif(), "image/jpeg");
        (await owner.PostAsJsonAsync($"/api/albums/{albumId}/items", new { fileItemId = fileId }))
            .EnsureSuccessStatusCode();
        return fileId;
    }

    private static async Task<Guid> UploadAsync(
        HttpClient owner, string name, byte[] bytes, string contentType)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        content.Add(file, "file", name);
        var response = await owner.PostAsync("/api/files", content);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }
}
