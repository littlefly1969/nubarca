using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Files;
using NubArca.Api.Metadata;
using NubArca.Api.Tests.Endpoints;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace NubArca.Api.Tests.Files;

// Slice 55 — GET /api/images?sort=datetaken. Effective date = embedded
// DateTaken when present, else upload (CreatedAt) time. We set DateTaken /
// CreatedAt directly so ordering is deterministic and independent.
public sealed class ImagesDateTakenSortTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public ImagesDateTakenSortTests()
    {
        _factory = new SqliteWebApplicationFactory();
        _factory.EnsureDatabaseCreated();
    }

    public void Dispose() => _factory.Dispose();

    // Distinct dimensions → distinct bytes → distinct blobs (no dedup), so each
    // upload gets its own BlobMetadata row we can set DateTaken on.
    private static byte[] PngBytes(int dim)
    {
        using var img = new Image<Rgba32>(dim, dim);
        using var ms = new MemoryStream();
        img.Save(ms, new PngEncoder());
        return ms.ToArray();
    }

    private async Task<Guid> UploadAsync(HttpClient client, string name, int dim)
    {
        var part = new ByteArrayContent(PngBytes(dim));
        part.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        var multipart = new MultipartFormDataContent { { part, "file", name } };
        var resp = await client.PostAsync("/api/files", multipart);
        resp.EnsureSuccessStatusCode();
        var summary = await resp.Content.ReadFromJsonAsync<FileSummary>();
        return summary!.Id;
    }

    private async Task SetDatesAsync(Guid fileId, DateTime? dateTaken, DateTime createdAt)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var file = await db.FileItems.SingleAsync(f => f.Id == fileId);
        file.CreatedAt = createdAt;
        var blobMeta = await db.BlobMetadata.SingleAsync(m => m.BlobObjectId == file.BlobObjectId);
        blobMeta.DateTaken = dateTaken;
        // Slice 88: this helper mutates the sources of truth directly (bypassing
        // the service write paths), so it must keep the denormalized
        // EffectiveDateTaken column in sync just as production writers do. No
        // user override is set in these tests, so it layers embedded over created.
        var (eff, src) = EffectiveDateTakenSources.Compute(null, dateTaken, createdAt);
        file.EffectiveDateTaken = eff;
        file.EffectiveDateTakenSource = src;
        await db.SaveChangesAsync();
    }

    private static async Task<List<Guid>> OrderAsync(HttpClient client, string query)
    {
        var resp = await client.GetAsync($"/api/images?{query}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<ImageListResponse>();
        return body!.Items.Select(i => i.Id).ToList();
    }

    private static DateTime Utc(int year, int month, int day)
        => new(year, month, day, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Sort_By_DateTaken_Ascending_Uses_Embedded_Date()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var a = await UploadAsync(client, "a.png", 10);
        var b = await UploadAsync(client, "b.png", 11);
        var c = await UploadAsync(client, "c.png", 12);

        // DateTaken order C < B < A; CreatedAt order is the reverse, proving the
        // sort uses the embedded date, not upload time.
        await SetDatesAsync(a, Utc(2020, 1, 3), createdAt: Utc(2026, 1, 1));
        await SetDatesAsync(b, Utc(2020, 1, 2), createdAt: Utc(2026, 1, 2));
        await SetDatesAsync(c, Utc(2020, 1, 1), createdAt: Utc(2026, 1, 3));

        var order = await OrderAsync(client, "sort=datetaken&direction=asc");
        Assert.Equal(new[] { c, b, a }, order);
    }

    [Fact]
    public async Task Sort_By_DateTaken_Descending_Uses_Embedded_Date()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var a = await UploadAsync(client, "a.png", 10);
        var b = await UploadAsync(client, "b.png", 11);
        var c = await UploadAsync(client, "c.png", 12);

        await SetDatesAsync(a, Utc(2020, 1, 3), createdAt: Utc(2026, 1, 1));
        await SetDatesAsync(b, Utc(2020, 1, 2), createdAt: Utc(2026, 1, 2));
        await SetDatesAsync(c, Utc(2020, 1, 1), createdAt: Utc(2026, 1, 3));

        var order = await OrderAsync(client, "sort=datetaken&direction=desc");
        Assert.Equal(new[] { a, b, c }, order);
    }

    [Fact]
    public async Task Sort_By_DateTaken_Falls_Back_To_Created_When_Missing()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var withDate = await UploadAsync(client, "with.png", 10);
        var noDateOld = await UploadAsync(client, "old.png", 11);
        var noDateNew = await UploadAsync(client, "new.png", 12);

        // Effective dates: noDateOld (2019, fallback) < withDate (2020 embedded)
        // < noDateNew (2027, fallback).
        await SetDatesAsync(withDate, Utc(2020, 6, 1), createdAt: Utc(2026, 1, 1));
        await SetDatesAsync(noDateOld, dateTaken: null, createdAt: Utc(2019, 1, 1));
        await SetDatesAsync(noDateNew, dateTaken: null, createdAt: Utc(2027, 1, 1));

        var order = await OrderAsync(client, "sort=datetaken&direction=asc");
        Assert.Equal(new[] { noDateOld, withDate, noDateNew }, order);
    }

    [Fact]
    public async Task Sort_By_DateTaken_Is_Owner_Scoped()
    {
        var (alice, aliceClient) = await _factory.CreateAuthenticatedClientAsync("alice@example.com");
        var a1 = await UploadAsync(aliceClient, "a1.png", 10);
        var a2 = await UploadAsync(aliceClient, "a2.png", 11);
        await SetDatesAsync(a1, Utc(2020, 1, 1), createdAt: Utc(2026, 1, 1));
        await SetDatesAsync(a2, Utc(2020, 1, 2), createdAt: Utc(2026, 1, 2));

        var (_, bobClient) = await _factory.CreateAuthenticatedClientAsync("bob@example.com");
        var b1 = await UploadAsync(bobClient, "b1.png", 12);
        await SetDatesAsync(b1, Utc(2020, 1, 3), createdAt: Utc(2026, 1, 3));

        var aliceOrder = await OrderAsync(aliceClient, "sort=datetaken&direction=asc");
        Assert.Equal(new[] { a1, a2 }, aliceOrder);
        Assert.DoesNotContain(b1, aliceOrder);
    }

    [Fact]
    public async Task Sort_By_DateTaken_Composes_With_Q_And_Pagination()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var trip1 = await UploadAsync(client, "trip-early.png", 10);
        var trip2 = await UploadAsync(client, "trip-late.png", 11);
        var other = await UploadAsync(client, "other.png", 12);

        await SetDatesAsync(trip1, Utc(2020, 1, 1), createdAt: Utc(2026, 1, 1));
        await SetDatesAsync(trip2, Utc(2020, 1, 2), createdAt: Utc(2026, 1, 2));
        await SetDatesAsync(other, Utc(2020, 1, 3), createdAt: Utc(2026, 1, 3));

        // q=trip filters to the two trip images; datetaken asc orders them
        // trip1 then trip2; paginate one at a time.
        var page1 = await OrderAsync(client, "q=trip&sort=datetaken&direction=asc&limit=1&offset=0");
        Assert.Equal(new[] { trip1 }, page1);

        var page2 = await OrderAsync(client, "q=trip&sort=datetaken&direction=asc&limit=1&offset=1");
        Assert.Equal(new[] { trip2 }, page2);

        Assert.DoesNotContain(other, page1);
        Assert.DoesNotContain(other, page2);
    }

    [Fact]
    public async Task DateTaken_Sort_Is_Accepted_And_Invalid_Sort_Still_400()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();

        var ok = await client.GetAsync("/api/images?sort=datetaken");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        var bad = await client.GetAsync("/api/images?sort=bogus");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }
}
