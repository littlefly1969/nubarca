using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace NubArca.Api.Tests.Aesthetics;

// HTTP-level authorization + no-leak tests for the Aesthetics Lab endpoints,
// plus the add-from-gallery blob-reference lifecycle and source-file
// independence. Uses a per-test factory with the feature enabled (analysis
// stays behind the fake sidecar; these tests don't run the worker).
public class AestheticLabEndpointTests
{
    private static Endpoints.SqliteWebApplicationFactory NewFactory(bool enabled = true) =>
        new(new Dictionary<string, string?>
        {
            ["HumanAesExpert:Enabled"] = enabled ? "true" : "false",
            ["HumanAesExpert:SidecarBaseUrl"] = "http://fake:8091",
        }, poolHost: true);

    private static byte[] Png(int dim)
    {
        using var img = new Image<Rgba32>(dim, dim);
        using var ms = new MemoryStream();
        img.Save(ms, new PngEncoder());
        return ms.ToArray();
    }

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient client, int dim = 24)
    {
        var part = new ByteArrayContent(Png(dim));
        part.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        var multipart = new MultipartFormDataContent { { part, "file", "portrait.png" } };
        return await client.PostAsync("/api/aesthetics-lab/items/upload", multipart);
    }

    [Fact]
    public async Task All_endpoints_require_auth()
    {
        using var factory = NewFactory();
        factory.EnsureDatabaseCreated();
        var anon = factory.CreateClient();
        var id = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/aesthetics-lab/items")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync($"/api/aesthetics-lab/items/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.DeleteAsync($"/api/aesthetics-lab/items/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anon.PostAsJsonAsync("/api/aesthetics-lab/analyses", new { itemIds = new[] { id } })).StatusCode);
    }

    [Fact]
    public async Task Upload_creates_item_scoped_to_owner_and_foreign_owner_sees_404()
    {
        using var factory = NewFactory();
        factory.EnsureDatabaseCreated();
        var (_, alice) = await factory.CreateAuthenticatedClientAsync("alice@example.com");
        var (_, bob) = await factory.CreateAuthenticatedClientAsync("bob@example.com");

        var up = await UploadAsync(alice);
        Assert.Equal(HttpStatusCode.Created, up.StatusCode);
        var created = await up.Content.ReadFromJsonAsync<Item>();
        Assert.NotNull(created);

        // Alice sees it; Bob gets a generic 404 for detail + derivative.
        Assert.Equal(HttpStatusCode.OK, (await alice.GetAsync($"/api/aesthetics-lab/items/{created!.id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/api/aesthetics-lab/items/{created.id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/api/aesthetics-lab/items/{created.id}/thumbnail")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.DeleteAsync($"/api/aesthetics-lab/items/{created.id}")).StatusCode);
    }

    [Fact]
    public async Task Delete_physically_removes_the_item_and_detail_returns_404()
    {
        using var factory = NewFactory();
        factory.EnsureDatabaseCreated();
        var (_, client) = await factory.CreateAuthenticatedClientAsync();
        var created = await (await UploadAsync(client)).Content.ReadFromJsonAsync<Item>();

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/aesthetics-lab/items/{created!.id}")).StatusCode);
        // Safe not-found afterwards; a second delete is also 404.
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/aesthetics-lab/items/{created.id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/aesthetics-lab/items/{created.id}")).StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.AestheticLabItems.AnyAsync(i => i.Id == Guid.Parse(created.id)));
    }

    [Fact]
    public async Task Dtos_never_leak_storage_internals()
    {
        using var factory = NewFactory();
        factory.EnsureDatabaseCreated();
        var (_, client) = await factory.CreateAuthenticatedClientAsync();
        var created = await (await UploadAsync(client)).Content.ReadFromJsonAsync<Item>();

        var listJson = await (await client.GetAsync("/api/aesthetics-lab/items")).Content.ReadAsStringAsync();
        var detailJson = await (await client.GetAsync($"/api/aesthetics-lab/items/{created!.id}")).Content.ReadAsStringAsync();

        foreach (var body in new[] { listJson, detailJson })
        {
            Assert.DoesNotContain("blobObjectId", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("storageKey", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("sha256", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("rawOutput", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("logicalContainerKey", body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Analyses_when_feature_disabled_returns_controlled_result_and_no_job()
    {
        using var factory = NewFactory(enabled: false);
        factory.EnsureDatabaseCreated();
        var (_, client) = await factory.CreateAuthenticatedClientAsync();
        var created = await (await UploadAsync(client)).Content.ReadFromJsonAsync<Item>();

        var resp = await client.PostAsJsonAsync("/api/aesthetics-lab/analyses", new { itemIds = new[] { created!.id } });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<BatchResult>();
        Assert.NotNull(body);
        Assert.Empty(body!.enqueued);
        Assert.Contains(body.skipped, s => s.reason == "feature_disabled");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.BackgroundJobs.AnyAsync());
    }

    [Fact]
    public async Task Add_from_gallery_acquires_a_reference_and_source_deletion_keeps_the_lab_item()
    {
        using var factory = NewFactory();
        factory.EnsureDatabaseCreated();
        var (ownerId, client) = await factory.CreateAuthenticatedClientAsync();

        // Seed a gallery-eligible FileItem (blob refcount starts at 1).
        Guid fileId, blobId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            blobId = Guid.NewGuid();
            db.BlobObjects.Add(new BlobObject
            {
                Id = blobId, Sha256 = $"sha-{blobId:N}", SizeBytes = 1,
                StorageKey = $"sk/{blobId:N}", ReferenceCount = 1, CreatedAt = DateTime.UtcNow,
            });
            fileId = Guid.NewGuid();
            db.FileItems.Add(new FileItem
            {
                Id = fileId, OwnerUserId = ownerId, BlobObjectId = blobId,
                Name = "photo.png", MimeType = "image/png", SizeBytes = 1,
                CreatedAt = DateTime.UtcNow, EffectiveDateTaken = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var addResp = await client.PostAsJsonAsync("/api/aesthetics-lab/items/from-gallery", new { fileItemIds = new[] { fileId } });
        Assert.Equal(HttpStatusCode.OK, addResp.StatusCode);
        // Idempotent: re-adding the same gallery file must NOT acquire another
        // reference (still exactly one lab item, refcount stays 2).
        var addAgain = await client.PostAsJsonAsync("/api/aesthetics-lab/items/from-gallery", new { fileItemIds = new[] { fileId } });
        Assert.Equal(HttpStatusCode.OK, addAgain.StatusCode);

        // The blob has TWO references (gallery FileItem + lab item), no copy.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(2, await db.BlobObjects.Where(b => b.Id == blobId).Select(b => b.ReferenceCount).FirstAsync());
            Assert.Equal(1, await db.AestheticLabItems.CountAsync(i => i.SourceFileItemId == fileId));
        }

        // Deleting the SOURCE gallery file must NOT remove the lab item.
        using (var scope = factory.Services.CreateScope())
        {
            var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
            await files.SoftDeleteAsync(ownerId, fileId);
        }
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // Lab item survives; the blob keeps the lab's single reference.
            Assert.Equal(1, await db.AestheticLabItems.CountAsync(i => i.BlobObjectId == blobId));
            Assert.Equal(1, await db.BlobObjects.Where(b => b.Id == blobId).Select(b => b.ReferenceCount).FirstAsync());
        }
    }

    private sealed record Item(string id, string originalFileName);
    private sealed record Skip(string itemId, string reason);
    private sealed record Enq(string itemId, string runId, string status);
    private sealed record BatchResult(List<Enq> enqueued, List<Skip> skipped);
}
