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
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace NubArca.Api.Tests.Files;

// Slice 75 — gallery duplicate collapsing and the /api/files/{id}/duplicates
// endpoint. Uses the existing SQLite endpoint harness with real image bytes
// so the gallery-membership filter (image MIME) matches correctly.
public sealed class DuplicateCollapsingTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public DuplicateCollapsingTests()
    {
        _factory = new SqliteWebApplicationFactory();
        _factory.EnsureDatabaseCreated();
    }

    public void Dispose() => _factory.Dispose();

    private static byte[] MakeJpeg(byte r = 100, byte g = 100, byte b = 100)
    {
        using var img = new Image<Rgb24>(16, 16, new SixLabors.ImageSharp.PixelFormats.Rgb24(r, g, b));
        using var ms = new MemoryStream();
        img.Save(ms, new JpegEncoder());
        return ms.ToArray();
    }

    // Uploads a file via the HTTP API (triggers gallery membership + BlobMetadata).
    private async Task<Guid> UploadJpegAsync(HttpClient client, byte[] bytes, string name = "test.jpg")
    {
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        var form = new MultipartFormDataContent { { part, "file", name } };
        var resp = await client.PostAsync("/api/files", form);
        resp.EnsureSuccessStatusCode();
        var summary = await resp.Content.ReadFromJsonAsync<FileSummary>();
        return summary!.Id;
    }

    // Force two FileItems to share a blob in the DB (simulating a true
    // content-addressed dedup). The second FileItem's BlobObjectId is
    // updated to match the first.
    private async Task ForceSameBlobAsync(Guid keepId, Guid changeId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var keep = await db.FileItems.AsNoTracking().SingleAsync(f => f.Id == keepId);
        await db.FileItems
            .Where(f => f.Id == changeId)
            .ExecuteUpdateAsync(s => s.SetProperty(f => f.BlobObjectId, keep.BlobObjectId));
    }

    // ---- Core collapsing tests ----

    [Fact]
    public async Task Same_Owner_Same_Blob_Collapsed_To_One_With_Count_2()
    {
        var bytes = MakeJpeg();
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var id1 = await UploadJpegAsync(client, bytes, "a.jpg");
        var id2 = await UploadJpegAsync(client, bytes, "b.jpg");
        await ForceSameBlobAsync(id1, id2);

        var resp = await client.GetAsync("/api/images?collapseDuplicates=true");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var page = await resp.Content.ReadFromJsonAsync<ImageListResponse>();

        Assert.NotNull(page);
        Assert.Single(page!.Items);
        Assert.Equal(2, page.Items[0].OccurrenceCount);
        Assert.True(page.Items[0].HasDuplicates);
    }

    [Fact]
    public async Task Same_Owner_Different_Blobs_Appear_Separately()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        await UploadJpegAsync(client, MakeJpeg(100, 0, 0), "r.jpg");
        await UploadJpegAsync(client, MakeJpeg(0, 100, 0), "g.jpg");

        var resp = await client.GetAsync("/api/images?collapseDuplicates=true");
        var page = await resp.Content.ReadFromJsonAsync<ImageListResponse>();

        Assert.Equal(2, page!.Items.Count);
        Assert.All(page.Items, item => Assert.Equal(1, item.OccurrenceCount));
    }

    [Fact]
    public async Task Same_Blob_Different_Owners_Not_Counted_Cross_User()
    {
        var bytes = MakeJpeg();
        var (_, alice) = await _factory.CreateAuthenticatedClientAsync("alice-dup@example.com");
        var (_, bob) = await _factory.CreateAuthenticatedClientAsync("bob-dup@example.com");

        var aliceId = await UploadJpegAsync(alice, bytes, "a.jpg");
        var bobId = await UploadJpegAsync(bob, bytes, "b.jpg");
        // Force both FileItems to share the same blob (simulating true dedup).
        await ForceSameBlobAsync(aliceId, bobId);

        // Alice sees 1 item with occurrenceCount=1 — only her own FileItem.
        var alicePage = await (await alice.GetAsync("/api/images?collapseDuplicates=true"))
            .Content.ReadFromJsonAsync<ImageListResponse>();
        Assert.Single(alicePage!.Items);
        Assert.Equal(1, alicePage.Items[0].OccurrenceCount);

        // Bob sees 1 item with occurrenceCount=1 — only his own FileItem.
        var bobPage = await (await bob.GetAsync("/api/images?collapseDuplicates=true"))
            .Content.ReadFromJsonAsync<ImageListResponse>();
        Assert.Single(bobPage!.Items);
        Assert.Equal(1, bobPage.Items[0].OccurrenceCount);
    }

    [Fact]
    public async Task No_Collapse_Returns_Both_FileItems()
    {
        var bytes = MakeJpeg();
        var (_, client) = await _factory.CreateAuthenticatedClientAsync("nodedup@example.com");
        var id1 = await UploadJpegAsync(client, bytes, "x.jpg");
        var id2 = await UploadJpegAsync(client, bytes, "y.jpg");
        await ForceSameBlobAsync(id1, id2);

        // Default (no collapseDuplicates) must still show both.
        var resp = await client.GetAsync("/api/images");
        var page = await resp.Content.ReadFromJsonAsync<ImageListResponse>();
        Assert.Equal(2, page!.Items.Count);
    }

    [Fact]
    public async Task Group_Appears_When_Non_Canonical_Matches_Search_Filter()
    {
        // Canonical (oldest) = "sunset.jpg" — does NOT contain "beach".
        // Duplicate       = "beach_sunset.jpg" — DOES contain "beach".
        // With q=beach, the group must still appear and the representative
        // must be beach_sunset.jpg (the oldest filter-matching item).
        var bytes = MakeJpeg();
        var (_, client) = await _factory.CreateAuthenticatedClientAsync("filter-canon@example.com");

        // Upload the canonical (older) first — it will NOT match q=beach.
        var olderId = await UploadJpegAsync(client, bytes, "sunset.jpg");

        // Upload the newer one — it DOES match q=beach.
        var newerId = await UploadJpegAsync(client, bytes, "beach_sunset.jpg");

        await ForceSameBlobAsync(olderId, newerId);

        // With q=beach: only beach_sunset.jpg passes the filter. But as the
        // ONLY filter-matching item, it is also the canonical. The group
        // must appear with occurrenceCount=2 (library-wide count).
        var resp = await client.GetAsync("/api/images?q=beach&collapseDuplicates=true");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var page = await resp.Content.ReadFromJsonAsync<ImageListResponse>();

        Assert.NotNull(page);
        Assert.Single(page!.Items);
        // Representative must be the filter-matching item (beach_sunset.jpg).
        Assert.Equal(newerId, page.Items[0].Id);
        // Library-wide count still reflects both occurrences.
        Assert.Equal(2, page.Items[0].OccurrenceCount);
    }

    // ---- /api/files/{id}/duplicates tests ----

    [Fact]
    public async Task Duplicates_Endpoint_Returns_Both_Occurrences_For_Owner()
    {
        var bytes = MakeJpeg();
        var (_, client) = await _factory.CreateAuthenticatedClientAsync("dupep@example.com");
        var id1 = await UploadJpegAsync(client, bytes, "p.jpg");
        var id2 = await UploadJpegAsync(client, bytes, "q.jpg");
        await ForceSameBlobAsync(id1, id2);

        var resp = await client.GetAsync($"/api/files/{id1}/duplicates");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var items = await resp.Content.ReadFromJsonAsync<DuplicateOccurrence[]>();
        Assert.NotNull(items);
        Assert.Equal(2, items!.Length);
        Assert.Contains(items, d => d.FileItemId == id1);
        Assert.Contains(items, d => d.FileItemId == id2);
    }

    [Fact]
    public async Task Duplicates_Endpoint_Missing_File_Returns_404()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync("dup404@example.com");
        var resp = await client.GetAsync($"/api/files/{Guid.NewGuid()}/duplicates");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Duplicates_Endpoint_Foreign_File_Returns_404()
    {
        var bytes = MakeJpeg();
        var (_, alice) = await _factory.CreateAuthenticatedClientAsync("alice-ep@example.com");
        var (_, bob) = await _factory.CreateAuthenticatedClientAsync("bob-ep@example.com");
        var aliceId = await UploadJpegAsync(alice, bytes, "a.jpg");

        // Bob trying to inspect Alice's file → 404.
        var resp = await bob.GetAsync($"/api/files/{aliceId}/duplicates");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Duplicates_Endpoint_Response_Has_No_Internal_Fields()
    {
        var bytes = MakeJpeg();
        var (_, client) = await _factory.CreateAuthenticatedClientAsync("noleak-dup@example.com");
        var id = await UploadJpegAsync(client, bytes, "z.jpg");

        var resp = await client.GetAsync($"/api/files/{id}/duplicates");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();

        foreach (var needle in MetadataExposurePolicy.ForbiddenInResponses)
        {
            Assert.DoesNotContain(needle, body, StringComparison.OrdinalIgnoreCase);
        }
        // Blob-specific fields must never appear.
        Assert.DoesNotContain("blobObjectId", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sha256", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("storageKey", body, StringComparison.OrdinalIgnoreCase);
    }
}

// DTO used by the duplicates-endpoint tests (mirrors DuplicateOccurrence.cs).
file sealed class DuplicateOccurrence
{
    public Guid FileItemId { get; set; }
    public string Name { get; set; } = string.Empty;
    public Guid? ParentFolderId { get; set; }
    public string MimeType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}
