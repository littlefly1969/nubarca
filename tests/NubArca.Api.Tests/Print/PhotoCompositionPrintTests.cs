using Microsoft.EntityFrameworkCore;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Print;
using NubArca.Api.Party;
using NubArca.Api.Print;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace NubArca.Api.Tests.Print;

public sealed partial class OwnerPhotoPrintTests
{
    [Fact]
    public async Task Owner_print_uses_the_selected_album_version_then_adds_a_second_bubble_on_the_real_sheet()
    {
        var people = await PeopleAsync();
        var printer = await PrinterAsync(people.Owner);
        var photo = await PhotoAsync(people.Owner, 600, 400);
        var albumResponse = await people.Owner.PostAsJsonAsync("/api/albums", new { name = "Fumetti" });
        albumResponse.EnsureSuccessStatusCode();
        var albumId = (await albumResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        (await people.Owner.PostAsJsonAsync($"/api/albums/{albumId}/items", new { fileItemId = photo })).EnsureSuccessStatusCode();
        (await people.Owner.PutAsJsonAsync($"/api/albums/{albumId}/photos/{photo}/text", new {
            text = "Primo", layout = new PhotoTextLayout("bubble", X: 0, Y: 0, Width: .4, Height: .4), expectedVersion = 0,
        })).EnsureSuccessStatusCode();
        object Print(bool original, long version) => new {
            printStationId = printer.StationId, printerDeviceId = printer.DeviceId, expectedPaperSize = "10x15",
            orientation = "landscape", style = "fullBleed", photoAlbumId = albumId,
            photos = new[] { new { fileItemId = photo, placement = new { centerX = .5, centerY = .5, zoom = 1 },
                useOriginal = original, photoTextVersion = version } },
            caption = "Secondo", textLayout = new PhotoTextLayout("bubble", X: 1, Y: 1, Width: .4, Height: .4),
        };
        Assert.Equal(HttpStatusCode.Conflict, (await Submit(people.Owner, Print(false, 0))).StatusCode);
        (await Submit(people.Owner, Print(false, 1))).EnsureSuccessStatusCode();
        using var sheet = await PrintedSheetAsync(printer);
        Rgb24 Pixel(double x, double y) => sheet[(int)(sheet.Width * x), (int)(sheet.Height * y)];
        Assert.True(Pixel(.06, .06).G > 200); // published bubble retained
        Assert.True(Pixel(.7, .68).G > 200); // personal bubble added
        Assert.True(Pixel(.5, .5).R > 150 && Pixel(.5, .5).G < 70);
        // Printing changes neither the published version nor its membership.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var saved = await db.AlbumItems.SingleAsync(i => i.AlbumId == albumId);
        Assert.Equal(1, saved.PhotoTextVersion);
        Assert.Equal("Primo", saved.PhotoText);
    }
}

public sealed partial class PartyPrintSubmissionTests
{
    [Fact]
    public async Task Changed_shared_photo_is_refused_before_budget_and_original_can_still_be_printed()
    {
        var (access, album) = await SeedAsync();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.AlbumItems.Add(new AlbumItem { Id = Guid.NewGuid(), AlbumId = album, FileItemId = _photos[0],
                AddedByUserId = access.OwnerUserId, AddedAt = DateTime.UtcNow,
                PhotoText = "Condiviso", PhotoTextLayout = new PhotoTextLayout("bubble"), PhotoTextVersion = 2 });
            await db.SaveChangesAsync();
        }
        var stale = Request(PartyPrintProducts.Photo, _photos[0]) with {
            Slots = [new PartyPrintSlotRequest(_photos[0], 0, 0, 1, 1, PhotoTextVersion: 1)] };
        Assert.Equal(PartyPrintRefusal.PhotoChanged, (await SubmitAsync(access, stale, "stale")).Refusal);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Empty(await db.PrintJobs.ToListAsync());
            Assert.Equal(0, (await db.PartyPrintProfiles.SingleAsync(p => p.PartyAlbumId == album)).PhotoAcceptedCount);
        }
        var original = stale with { Slots = [stale.Slots[0] with { UseOriginal = true }] };
        Assert.True((await SubmitAsync(access, original, "original")).Ok);
        var decorated = stale with { Slots = [stale.Slots[0] with { PhotoTextVersion = 2 }],
            Caption = "Il mio", TextLayout = new PhotoTextLayout("bubble", Y: 1) };
        Assert.True((await SubmitAsync(access, decorated, "decorated")).Ok);
        // Reusing a key for a changed personal bubble cannot return or queue a different print.
        Assert.Equal(PartyPrintRefusal.Invalid, (await SubmitAsync(access, decorated with { Caption = "Altro" }, "decorated")).Refusal);
        Assert.True((await SubmitAsync(access, decorated, "decorated")).Ok);
    }
}

public sealed class PhotoCompositionRenderTests
{
    [Fact]
    public async Task Published_bubble_is_flattened_before_a_second_print_bubble_and_original_is_unchanged()
    {
        using var original = new Image<Rgba32>(600, 400, new Rgba32(200, 30, 30));
        using var stream = new MemoryStream(); await original.SaveAsPngAsync(stream);
        var bytes = stream.ToArray();
        var shared = new AlbumPhotoTextDto("Primo", new PhotoTextLayout("bubble", X: 0, Y: 0, Width: .4, Height: .4), 1);
        var first = await AlbumPhotoText.RenderAsync(bytes, shared, default);
        using var photo = Image.Load<Rgba32>(first);
        PhotoTextRenderer.Draw(photo, new Rectangle(0, 0, 600, 400), "Secondo", new PhotoTextLayout("bubble", X: 1, Y: 1, Width: .4, Height: .4));
        Assert.True(photo[40, 30].G > 200); // first bubble remains
        Assert.True(photo[400, 270].G > 200); // second is added
        Assert.True(photo[300, 200].R > 150 && photo[300, 200].G < 70); // photograph between them
        Assert.Equal(bytes, await AlbumPhotoText.RenderAsync(bytes, null, default));
    }

    [Theory]
    [InlineData("nubarca")]
    [InlineData("polaroid")]
    [InlineData("editorial")]
    [InlineData("celebration")]
    public async Task Guestbook_print_keeps_full_dedication_signature_crop_and_published_bubble(string template)
    {
        using var source = new Image<Rgba32>(600, 400, new Rgba32(200, 30, 30));
        using var stream = new MemoryStream(); await source.SaveAsPngAsync(stream);
        var entry = new PartyGuestbookEntry { TemplateKey = template, Body = string.Join("\n", Enumerable.Repeat("Un ricordo da conservare", 25)),
            AuthorDisplayName = "Anna", PhotoWidth = 600, PhotoHeight = 400, CropZoom = 1 };
        var plain = await GuestbookPrintSource.DrawAsync(entry, stream.ToArray(), 600, default);
        using var card = Image.Load<Rgba32>(plain);
        Assert.True(card.Height > 600); // dedication is not truncated to the photo or 40-character caption
        entry.TextLayout = new PhotoTextLayout("bubble");
        var bubble = await GuestbookPrintSource.DrawAsync(entry, stream.ToArray(), 600, default);
        using var bubbleCard = Image.Load<Rgba32>(bubble);
        Assert.True(bubbleCard.Height < card.Height);
        Assert.Null(bubbleCard.Metadata.ExifProfile);
        Assert.Equal("Anna", entry.AuthorDisplayName); // published data is never rewritten by printing
    }
}
