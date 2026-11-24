using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Endpoints;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Print;
using NubArca.Api.Print;
using SixLabors.ImageSharp;

namespace NubArca.Api.Tests.Party;

public sealed partial class PartyGuestbookTests
{
    [Fact]
    public async Task Published_memory_prints_from_its_own_photo_after_album_removal_and_keeps_the_book_separate()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var party = await OpenPartyAsync(owner, guestbook: true);
        var published = await SubmitAsync(party.ViewToken, Memory(party.PhotoId, "Anna", "La dedica pubblicata",
            textLayout: new PhotoTextLayout("bubble", X: .8, Y: .2)));
        var id = published.GetProperty("id").GetGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var ownerId = await db.Albums.Where(a => a.Id == party.AlbumId).Select(a => a.OwnerUserId).SingleAsync();
            var station = Guid.NewGuid(); var printer = Guid.NewGuid();
            db.PrintStations.Add(new PrintStation { Id = station, OwnerUserId = ownerId, Name = "Ricordi",
                Enabled = true, CreatedAt = DateTime.UtcNow, LastSeenAt = DateTime.UtcNow });
            db.PrinterDevices.Add(new PrinterDevice { Id = printer, PrintStationId = station, DeviceKey = "memory-printer",
                DisplayName = "Foto", AdapterKind = "fake", CapabilitiesJson = "{\"formats\":[\"10x15\"]}",
                LoadedPaperSize = "10x15", LastObservedState = PrintDeviceStates.Ready, LastSeenAt = DateTime.UtcNow });
            db.PartyPrintProfiles.Add(new PartyPrintProfile { Id = Guid.NewGuid(), PartyAlbumId = party.AlbumId,
                OwnerUserId = ownerId, Enabled = true, PrintStationId = station, PrinterDeviceId = printer,
                PhotoEnabled = true, PhotoMaxPrints = 4, PublicSequenceNext = 1, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        var guest = _factory.CreateClient();
        var book = await guest.GetFromJsonAsync<JsonElement>($"/api/party/{party.ViewToken}/guestbook");
        var printUrl = book.GetProperty("printUrl").GetString()!;
        var token = printUrl.Split('/')[2];
        (await guest.GetAsync($"/api/party/{token}/print")).EnsureSuccessStatusCode();
        var preview = await guest.GetAsync($"/api/party/{token}/print/guestbook/{id}/preview");
        preview.EnsureSuccessStatusCode();
        using (var image = Image.Load(await preview.Content.ReadAsByteArrayAsync())) Assert.Null(image.Metadata.ExifProfile);
        (await owner.DeleteAsync($"/api/albums/{party.AlbumId}/items/{party.PhotoId}")).EnsureSuccessStatusCode();
        Assert.Equal(0, (await guest.GetFromJsonAsync<JsonElement>($"/api/party/{party.ViewToken}/items")).GetProperty("items").GetArrayLength());
        var request = new PartyPrintSubmitBody("photo", "pure", [new PartyPrintSlotBody(id,
            Placement: new PartyPrintPlacementRequest(.1, .1, 4))], GuestbookEntryId: id);
        async Task<HttpResponseMessage> Print(object body)
        {
            var message = new HttpRequestMessage(HttpMethod.Post, $"/api/party/{token}/print") { Content = JsonContent.Create(body) };
            message.Headers.Add("Idempotency-Key", "published-memory");
            return await guest.SendAsync(message);
        }
        var result = await Print(request);
        Assert.Equal(HttpStatusCode.Accepted, result.StatusCode);
        var accepted = await result.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(accepted.GetProperty("jobId").GetGuid(), (await (await Print(request)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("jobId").GetGuid());
        Assert.Equal(HttpStatusCode.BadRequest, (await Print(request with { Caption = "Una dedica diversa" })).StatusCode);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var entry = await db.PartyGuestbookEntries.SingleAsync(e => e.Id == id);
            Assert.Equal("La dedica pubblicata", entry.Body);
            Assert.Equal(.8, entry.TextLayout!.X);
            Assert.Equal(1, await db.PrintJobs.CountAsync());
            Assert.Empty(await db.PrintJobSources.ToListAsync());
            var job = await db.PrintJobs.SingleAsync();
            Assert.Null(job.FileItemId);
            Assert.Contains(id.ToString(), job.RenderSpecificationJson);
            entry.Status = PartyMessageStatuses.Hidden; await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync($"/api/party/{token}/print/guestbook/{id}/preview")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Print(request)).StatusCode);
    }
}
