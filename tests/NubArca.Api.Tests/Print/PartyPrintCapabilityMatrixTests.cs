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
    private async Task<Party> SeedPartyAsync(bool enablePrinting = true)
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(
            $"host{Interlocked.Increment(ref _parties)}@example.com");
        var albumId = await CreateAlbumAsync(owner, "Festa");
        var photoId = await AddJpegWithExifAsync(owner, albumId, "p1.jpg");

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
                    CapabilitiesJson = "{\"formats\":[\"10x15\"]}",
                    LastObservedState = PrintDeviceStates.Ready, LastSeenAt = DateTime.UtcNow,
                });
                db.PartyPrintProfiles.Add(new PartyPrintProfile
                {
                    Id = Guid.NewGuid(), PartyAlbumId = albumId, OwnerUserId = ownerId,
                    Enabled = true, PrintStationId = stationId, PrinterDeviceId = deviceId,
                    PhotoEnabled = true, PhotoMaxPrints = 5,
                    StripEnabled = true, StripMaxPrints = 5,
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
        var printUrl = album.GetProperty("printUrl");
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
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
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
