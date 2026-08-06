using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Files;
using NubArca.Api.Metadata;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Tests.Metadata;
using Xunit;

namespace NubArca.Api.Tests.Files;

// Slice 88 — denormalized FileItem.EffectiveDateTaken. These tests assert the
// column is populated/kept-in-sync by every write path, matching the layered
// precedence: user DateTakenOverride → embedded blob DateTaken → CreatedAt.
public sealed class EffectiveDateTakenTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public EffectiveDateTakenTests()
    {
        _factory = new SqliteWebApplicationFactory();
        _factory.EnsureDatabaseCreated();
    }

    public void Dispose() => _factory.Dispose();

    // Embedded DateTimeOriginal baked into JpegWithExif (no offset → UTC).
    private static readonly DateTime EmbeddedDate = new(2023, 6, 15, 14, 30, 0, DateTimeKind.Utc);

    private async Task<Guid> UploadAsync(HttpClient client, string name, byte[]? bytes = null, string mime = "image/png")
    {
        var part = new ByteArrayContent(bytes ?? ImageFixtures.PlainPng());
        part.Headers.ContentType = new MediaTypeHeaderValue(mime);
        var multipart = new MultipartFormDataContent { { part, "file", name } };
        var resp = await client.PostAsync("/api/files", multipart);
        resp.EnsureSuccessStatusCode();
        var summary = await resp.Content.ReadFromJsonAsync<FileSummary>();
        return summary!.Id;
    }

    private async Task PatchMetaAsync(HttpClient client, Guid fileId, object body)
    {
        var resp = await client.PatchAsync($"/api/files/{fileId}/metadata", JsonContent.Create(body));
        resp.EnsureSuccessStatusCode();
    }

    private async Task<(DateTime Effective, string? Source)> ReadColumnAsync(Guid fileId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.FileItems.AsNoTracking()
            .Where(f => f.Id == fileId)
            .Select(f => new { f.EffectiveDateTaken, f.EffectiveDateTakenSource })
            .SingleAsync();
        return (row.EffectiveDateTaken, row.EffectiveDateTakenSource);
    }

    [Fact]
    public async Task Effective_Date_Is_Set_On_Creation_From_CreatedAt_When_No_Embedded()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var id = await UploadAsync(client, "plain.png");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var file = await db.FileItems.AsNoTracking().SingleAsync(f => f.Id == id);

        // A plain PNG has no embedded date and no override → effective = upload.
        Assert.Equal(file.CreatedAt, file.EffectiveDateTaken);
        Assert.Equal(EffectiveDateTakenSources.Uploaded, file.EffectiveDateTakenSource);
    }

    [Fact]
    public async Task Embedded_DateTaken_Beats_CreatedAt_On_Creation()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var id = await UploadAsync(client, "exif.jpg", ImageFixtures.JpegWithExif(), "image/jpeg");

        var (eff, src) = await ReadColumnAsync(id);
        Assert.Equal(EmbeddedDate, eff);
        Assert.Equal(EffectiveDateTakenSources.Embedded, src);
    }

    [Fact]
    public async Task User_Override_Beats_Embedded_DateTaken()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var id = await UploadAsync(client, "exif.jpg", ImageFixtures.JpegWithExif(), "image/jpeg");

        var overrideDate = new DateTime(2024, 7, 1, 10, 0, 0, DateTimeKind.Utc);
        await PatchMetaAsync(client, id, new { dateTakenOverride = "2024-07-01T10:00:00Z" });

        var (eff, src) = await ReadColumnAsync(id);
        Assert.Equal(overrideDate, eff);
        Assert.Equal(EffectiveDateTakenSources.User, src);
    }

    [Fact]
    public async Task Removing_Override_Falls_Back_To_Embedded()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var id = await UploadAsync(client, "exif.jpg", ImageFixtures.JpegWithExif(), "image/jpeg");

        await PatchMetaAsync(client, id, new { dateTakenOverride = "2024-07-01T10:00:00Z" });
        Assert.Equal(EffectiveDateTakenSources.User, (await ReadColumnAsync(id)).Source);

        // Clear the override (omitted field = cleared) → fall back to embedded.
        await PatchMetaAsync(client, id, new { });

        var (eff, src) = await ReadColumnAsync(id);
        Assert.Equal(EmbeddedDate, eff);
        Assert.Equal(EffectiveDateTakenSources.Embedded, src);
    }

    [Fact]
    public async Task Removing_Override_Falls_Back_To_CreatedAt_When_No_Embedded()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var id = await UploadAsync(client, "plain.png");

        await PatchMetaAsync(client, id, new { dateTakenOverride = "2024-07-01T10:00:00Z" });
        Assert.Equal(EffectiveDateTakenSources.User, (await ReadColumnAsync(id)).Source);

        await PatchMetaAsync(client, id, new { });

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var file = await db.FileItems.AsNoTracking().SingleAsync(f => f.Id == id);
        Assert.Equal(file.CreatedAt, file.EffectiveDateTaken);
        Assert.Equal(EffectiveDateTakenSources.Uploaded, file.EffectiveDateTakenSource);
    }

    [Fact]
    public async Task ReExtraction_Refreshes_EffectiveDateTaken_For_Files_On_The_Blob()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var id = await UploadAsync(client, "exif.jpg", ImageFixtures.JpegWithExif(), "image/jpeg");

        Guid blobId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var file = await db.FileItems.SingleAsync(f => f.Id == id);
            blobId = file.BlobObjectId;
            // Simulate column drift, then prove re-extraction recomputes it.
            file.EffectiveDateTaken = new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            file.EffectiveDateTakenSource = "stale";
            await db.SaveChangesAsync();
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
            var ok = await files.ReExtractEmbeddedMetadataAsync(blobId);
            Assert.True(ok);
        }

        var (eff, src) = await ReadColumnAsync(id);
        Assert.Equal(EmbeddedDate, eff);
        Assert.Equal(EffectiveDateTakenSources.Embedded, src);
    }

    [Fact]
    public async Task ReExtraction_Does_Not_Override_A_User_Override()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var id = await UploadAsync(client, "exif.jpg", ImageFixtures.JpegWithExif(), "image/jpeg");
        await PatchMetaAsync(client, id, new { dateTakenOverride = "2024-07-01T10:00:00Z" });

        Guid blobId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            blobId = (await db.FileItems.AsNoTracking().SingleAsync(f => f.Id == id)).BlobObjectId;
        }
        using (var scope = _factory.Services.CreateScope())
        {
            var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
            await files.ReExtractEmbeddedMetadataAsync(blobId);
        }

        // The override must remain authoritative after a blob re-extraction.
        var (eff, src) = await ReadColumnAsync(id);
        Assert.Equal(new DateTime(2024, 7, 1, 10, 0, 0, DateTimeKind.Utc), eff);
        Assert.Equal(EffectiveDateTakenSources.User, src);
    }

    [Fact]
    public async Task Recompute_Command_Repairs_Drifted_Columns()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var plain = await UploadAsync(client, "plain.png");
        var exif = await UploadAsync(client, "exif.jpg", ImageFixtures.JpegWithExif(), "image/jpeg");

        DateTime plainCreated;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // Corrupt both rows' denormalized column.
            await db.FileItems.ExecuteUpdateAsync(s => s
                .SetProperty(f => f.EffectiveDateTaken, _ => new DateTime(1990, 1, 1, 0, 0, 0, DateTimeKind.Utc))
                .SetProperty(f => f.EffectiveDateTakenSource, _ => "stale"));
            plainCreated = (await db.FileItems.AsNoTracking().SingleAsync(f => f.Id == plain)).CreatedAt;
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var backfill = scope.ServiceProvider.GetRequiredService<MetadataBackfillService>();
            var updated = await backfill.RecomputeEffectiveDatesAsync();
            Assert.True(updated >= 2);
        }

        var plainCol = await ReadColumnAsync(plain);
        Assert.Equal(plainCreated, plainCol.Effective);
        Assert.Equal(EffectiveDateTakenSources.Uploaded, plainCol.Source);

        var exifCol = await ReadColumnAsync(exif);
        Assert.Equal(EmbeddedDate, exifCol.Effective);
        Assert.Equal(EffectiveDateTakenSources.Embedded, exifCol.Source);
    }
}
