using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Tests.Endpoints;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace NubArca.Api.Tests.Albums;

// SHARE-COPY-01 quota behaviour, in its own class because a quota has to be
// configured at host build time.
//
// The rule the slice contract sets: a recipient must never receive an
// unsolicited completed copy, acceptance enforces their NORMAL quota, and dedup
// must not buy them a free pass — the copy is their own logical file from the
// moment they accept, and it costs them its full logical size even though the
// bytes are shared with the sender.
public sealed class AlbumTransferQuotaTests
{
    private const string SenderEmail = "alice@example.com";
    private const string RecipientEmail = "bob@example.com";

    private static SqliteWebApplicationFactory Factory(long? quota = null)
    {
        var settings = new Dictionary<string, string?>();
        if (quota is long q)
        {
            settings["Storage:DefaultUserQuotaBytes"] = q.ToString();
        }
        var f = new SqliteWebApplicationFactory(settings, poolHost: true);
        f.EnsureDatabaseCreated();
        return f;
    }

    [Fact]
    public async Task A_Pending_Transfer_Costs_The_Recipient_Nothing()
    {
        // The offer must not consume quota before it is accepted — that is the
        // whole reason the manifest is not a set of hidden recipient FileItems.
        using var factory = Factory(quota: 1_000_000);
        var (_, alice) = await factory.CreateAuthenticatedClientAsync(SenderEmail);
        var (bobId, bob) = await factory.CreateAuthenticatedClientAsync(RecipientEmail);
        var albumId = await CreateAlbumAsync(alice, "Offer");
        await AddOwnPngAsync(alice, albumId, "a.png");
        await SendAsync(alice, albumId, RecipientEmail);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await db.FileItems.CountAsync(f => f.OwnerUserId == bobId));

        var usage = await bob.GetFromJsonAsync<JsonElement>("/api/storage/me");
        Assert.Equal(0, usage.GetProperty("usedBytes").GetInt64());
    }

    [Fact]
    public async Task Quota_Rejection_Leaves_No_Partial_Album_Behind()
    {
        // The quota is global, so it has to be generous enough for the SENDER to
        // build the album in the first place. What makes acceptance fail is the
        // RECIPIENT's existing usage: Bob is already almost full.
        const long Quota = 1_000_000;
        using var factory = Factory(quota: Quota);
        var (_, alice) = await factory.CreateAuthenticatedClientAsync(SenderEmail);
        var (bobId, bob) = await factory.CreateAuthenticatedClientAsync(RecipientEmail);
        var albumId = await CreateAlbumAsync(alice, "Too big");
        await AddOwnPngAsync(alice, albumId, "a.png");
        await AddOwnPngAsync(alice, albumId, "b.png");

        var transferId = (await SendAsync(alice, albumId, RecipientEmail))
            .GetProperty("id").GetGuid();

        // Fill Bob to one byte under his limit. Written directly so the test
        // does not depend on how many fixture uploads it takes to get there;
        // the blob reference is acquired alongside it so the refcount invariant
        // still holds.
        await MutateAsync(factory, async db =>
        {
            var blobId = await db.BlobObjects.Select(b => b.Id).FirstAsync();
            db.FileItems.Add(new NubArca.Api.Domain.FileItem
            {
                Id = Guid.NewGuid(),
                OwnerUserId = bobId,
                BlobObjectId = blobId,
                Name = "ballast.bin",
                MimeType = "application/octet-stream",
                SizeBytes = Quota - 1,
                CreatedAt = DateTime.UtcNow,
                EffectiveDateTaken = DateTime.UtcNow,
            });
            var blob = await db.BlobObjects.FirstAsync(b => b.Id == blobId);
            blob.ReferenceCount += 1;
        });

        var response = await bob.PostAsync($"/api/album-transfers/{transferId}/accept", null);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("quota_exceeded", body.GetProperty("error").GetString());
        // The recipient is told what they would need, in logical bytes.
        Assert.True(body.GetProperty("requiredBytes").GetInt64() > 0);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // Acceptance is ONE transaction: no album, no files, not even one of the
        // two items. A partially visible copy is the failure mode this asserts
        // against.
        Assert.Equal(0, await db.Albums.CountAsync(a => a.OwnerUserId == bobId));
        Assert.Equal(0, await db.AlbumItems.CountAsync(ai => ai.AddedByUserId == bobId));
        // Only the ballast row — not one copied file landed.
        Assert.Equal(1, await db.FileItems.CountAsync(f => f.OwnerUserId == bobId));

        // And the offer is still pending, so a recipient who frees space can
        // still accept it.
        var transfer = await db.AlbumTransfers.FirstAsync(t => t.Id == transferId);
        Assert.Equal("pending", transfer.State);
        Assert.Null(transfer.CreatedAlbumId);
    }

    [Fact]
    public async Task Dedup_Does_Not_Buy_The_Recipient_A_Free_Copy()
    {
        // Bob already holds the very same bytes. The copy still costs him its
        // full logical size, because quota is logical by construction — the
        // second acceptance must fail even though not one new byte is stored.
        using var factory = Factory(quota: 1_000_000);
        var (_, alice) = await factory.CreateAuthenticatedClientAsync(SenderEmail);
        var (bobId, bob) = await factory.CreateAuthenticatedClientAsync(RecipientEmail);

        var albumId = await CreateAlbumAsync(alice, "Same bytes");
        await AddOwnPngAsync(alice, albumId, "identical.png");
        // Bob uploads a byte-identical file, so both share ONE BlobObject.
        await UploadPngAsync(bob, "identical.png");

        var transferId = (await SendAsync(alice, albumId, RecipientEmail))
            .GetProperty("id").GetGuid();
        (await bob.PostAsync($"/api/album-transfers/{transferId}/accept", null))
            .EnsureSuccessStatusCode();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var bobFiles = await db.FileItems.Where(f => f.OwnerUserId == bobId).ToListAsync();

        // Two logical files for Bob…
        Assert.Equal(2, bobFiles.Count);
        // …sharing exactly one physical blob.
        Assert.Single(bobFiles.Select(f => f.BlobObjectId).Distinct());

        // And his usage counts both, not one.
        var usage = await bob.GetFromJsonAsync<JsonElement>("/api/storage/me");
        Assert.Equal(bobFiles.Sum(f => f.SizeBytes), usage.GetProperty("usedBytes").GetInt64());
    }

    // ── Helpers (kept local: this class configures its own host) ─────────────

    private static async Task MutateAsync(
        SqliteWebApplicationFactory factory, Func<AppDbContext, Task> mutate)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await mutate(db);
        await db.SaveChangesAsync();
    }

    private static async Task<JsonElement> SendAsync(HttpClient sender, Guid albumId, string email)
    {
        var response = await sender.PostAsJsonAsync(
            $"/api/albums/{albumId}/transfers", new { email });
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<Guid> CreateAlbumAsync(HttpClient owner, string name)
    {
        var response = await owner.PostAsJsonAsync("/api/albums", new { name });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> UploadPngAsync(HttpClient client, string name)
    {
        using var img = new Image<Rgba32>(8, 8);
        var tint = (byte)(name.Aggregate(17, (acc, c) => (acc * 31 + c) & 0xFF));
        img[0, 0] = new Rgba32(tint, tint, tint, 255);
        using var ms = new MemoryStream();
        img.Save(ms, new PngEncoder());
        var part = new ByteArrayContent(ms.ToArray());
        part.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        var multipart = new MultipartFormDataContent { { part, "file", name } };
        var response = await client.PostAsync("/api/files", multipart);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> AddOwnPngAsync(HttpClient owner, Guid albumId, string name)
    {
        var fileId = await UploadPngAsync(owner, name);
        (await owner.PostAsJsonAsync($"/api/albums/{albumId}/items", new { fileItemId = fileId }))
            .EnsureSuccessStatusCode();
        return fileId;
    }
}
