using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Ai;
using NubArca.Api.Ai.Faces;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Files;
using NubArca.Api.Tests.Endpoints;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace NubArca.Api.Tests.Ai;

// High-quality face previews: generated from the ORIGINAL blob, owner/vault-safe,
// cached, and never an embedding source.
public sealed class FacePreviewTests
{
    private const string FaceProfileKey = "det-face-embedding-v1";

    private static readonly string[] Forbidden =
    {
        "BlobObjectId", "blobObjectId", "StorageKey", "storageKey", "Sha256", "sha256",
        "/storage/objects/", "EmbeddingBytes", "PrivateVaultId", "privateVaultId", "ProfileId", "at NubArca.",
    };

    private static SqliteWebApplicationFactory Factory()
    {
        var f = new SqliteWebApplicationFactory(
            new Dictionary<string, string?> { ["Ai:Enabled"] = "true" },
            poolHost: true);
        f.EnsureDatabaseCreated();
        return f;
    }

    private static async Task<Guid> SeedProfileAsync(SqliteWebApplicationFactory f)
    {
        using var scope = f.Services.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<IAiProfileRegistry>();
        await registry.SeedDeterministicProfilesAsync();
        return (await registry.GetProfileByKeyAsync(FaceProfileKey))!.Id;
    }

    private static byte[] Png(int w, int h)
    {
        using var img = new Image<Rgba32>(w, h);
        using var ms = new MemoryStream();
        img.Save(ms, new PngEncoder());
        return ms.ToArray();
    }

    private static async Task<Guid> UploadAsync(HttpClient client, int w, int h)
    {
        var part = new ByteArrayContent(Png(w, h));
        part.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        var multipart = new MultipartFormDataContent { { part, "file", $"p{w}x{h}.png" } };
        var resp = await client.PostAsync("/api/files", multipart);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<FileSummary>())!.Id;
    }

    private static async Task<(Guid faceId, Guid fileId)> SeedFaceAsync(
        SqliteWebApplicationFactory f, HttpClient client, Guid profileId,
        int w, int h, double bx, double by, double bw, double bh)
    {
        var fileId = await UploadAsync(client, w, h);
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var blobId = await db.FileItems.Where(x => x.Id == fileId).Select(x => x.BlobObjectId).SingleAsync();
        var faceId = Guid.NewGuid();
        db.FaceDetections.Add(new FaceDetection
        {
            Id = faceId, BlobObjectId = blobId, ProfileId = profileId, FaceIndex = 0,
            BoundingBoxX = bx, BoundingBoxY = by, BoundingBoxWidth = bw, BoundingBoxHeight = bh,
            DetectionScore = 0.9, LandmarksJson = "[]", CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return (faceId, fileId);
    }

    private static async Task<ThumbnailContent?> EnsureAsync(
        SqliteWebApplicationFactory f, Guid faceId, Guid ownerId, string size)
    {
        using var scope = f.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<FacePreviewService>();
        return await svc.EnsureAsync(faceId, ownerId, size);
    }

    // ---- generation ------------------------------------------------------

    [Fact]
    public async Task Preview_Is_Generated_From_Original_Blob_At_Full_Resolution()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var (ownerId, client) = await f.CreateAuthenticatedClientAsync();
        // 1000x1000 original, 200px face → expanded square side 260 → small edge 192.
        // A small-thumbnail (256px) source could only yield ~51px for that region,
        // so a 192px output proves the crop came from the ORIGINAL blob.
        var (faceId, _) = await SeedFaceAsync(f, client, profileId, 1000, 1000, 0.4, 0.4, 0.2, 0.2);

        var content = await EnsureAsync(f, faceId, ownerId, "small");
        Assert.NotNull(content);
        Assert.Equal(192, content!.Width);
        Assert.Equal(192, content.Height);

        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await db.FacePreviews.AnyAsync(p => p.FaceDetectionId == faceId && p.Size == "small"));
        // Never an embedding source: no FaceEmbedding rows were created.
        Assert.Equal(0, await db.FaceEmbeddings.CountAsync());
    }

    [Fact]
    public async Task Small_Face_Is_Not_Upscaled()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var (ownerId, client) = await f.CreateAuthenticatedClientAsync();
        // 400x400, tiny 40px face → expanded square side 52 → output 52 (no upscale to 192).
        var (faceId, _) = await SeedFaceAsync(f, client, profileId, 400, 400, 0.45, 0.45, 0.1, 0.1);

        var content = await EnsureAsync(f, faceId, ownerId, "small");
        Assert.NotNull(content);
        Assert.Equal(content!.Width, content.Height);
        Assert.Equal(52, content.Width);
        Assert.True(content.Width < FacePreviewSizes.GetEdge("small"));
    }

    [Fact]
    public async Task Padded_Crop_Clamps_At_Image_Edges()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var (ownerId, client) = await f.CreateAuthenticatedClientAsync();
        // Face in the top-left corner — padding would run off the image; must clamp.
        var (faceId, _) = await SeedFaceAsync(f, client, profileId, 400, 400, 0.0, 0.0, 0.1, 0.1);

        var content = await EnsureAsync(f, faceId, ownerId, "small");
        Assert.NotNull(content); // no crop-out-of-bounds crash
        Assert.Equal(content!.Width, content.Height);
    }

    [Fact]
    public async Task Regeneration_Is_Idempotent()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var (ownerId, client) = await f.CreateAuthenticatedClientAsync();
        var (faceId, _) = await SeedFaceAsync(f, client, profileId, 600, 600, 0.3, 0.3, 0.3, 0.3);

        Assert.NotNull(await EnsureAsync(f, faceId, ownerId, "medium"));

        using (var scope = f.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<FacePreviewService>();
            Assert.True(await svc.RegenerateAsync(faceId, ownerId));
            Assert.True(await svc.RegenerateAsync(faceId, ownerId)); // idempotent
        }

        Assert.NotNull(await EnsureAsync(f, faceId, ownerId, "medium"));
        using var scope2 = f.Services.CreateScope();
        var db = scope2.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await db.FacePreviews.CountAsync(p => p.FaceDetectionId == faceId && p.Size == "medium"));
    }

    // ---- endpoint owner/vault safety -------------------------------------

    [Fact]
    public async Task Preview_Endpoint_Is_Owner_Scoped_And_CrossOwner_Is_404()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var (_, ownerClient) = await f.CreateAuthenticatedClientAsync("owner@example.com");
        var (_, otherClient) = await f.CreateAuthenticatedClientAsync("other@example.com");
        var (faceId, _) = await SeedFaceAsync(f, ownerClient, profileId, 400, 400, 0.25, 0.25, 0.5, 0.5);

        var ok = await ownerClient.GetAsync($"/api/people/faces/{faceId}/preview?size=small");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("image/jpeg", ok.Content.Headers.ContentType!.MediaType);

        var foreign = await otherClient.GetAsync($"/api/people/faces/{faceId}/preview?size=small");
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
    }

    [Fact]
    public async Task Preview_Requires_Authentication()
    {
        using var f = Factory();
        var anon = f.CreateClient();
        var resp = await anon.GetAsync($"/api/people/faces/{Guid.NewGuid()}/preview?size=small");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Vaulted_Face_Preview_Is_404_After_Move()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var (ownerId, client) = await f.CreateAuthenticatedClientAsync();
        var (faceId, fileId) = await SeedFaceAsync(f, client, profileId, 400, 400, 0.25, 0.25, 0.5, 0.5);

        // Visible first.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/people/faces/{faceId}/preview?size=small")).StatusCode);

        // Move the (only) referencing file into a Private Vault.
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var vault = new PrivateVault
            {
                Id = Guid.NewGuid(), OwnerUserId = ownerId, DisplayName = "Private",
                PasswordHash = "x", EncryptionMode = PrivateVaultEncryptionModes.None, CreatedAt = DateTime.UtcNow,
            };
            db.PrivateVaults.Add(vault);
            var file = await db.FileItems.IgnoreQueryFilters().SingleAsync(x => x.Id == fileId);
            file.PrivateVaultId = vault.Id;
            await db.SaveChangesAsync();
        }

        // Now vault-only → generic 404, and the context endpoint too.
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/people/faces/{faceId}/preview?size=small")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/people/faces/{faceId}/context")).StatusCode);
    }

    // ---- context ---------------------------------------------------------

    [Fact]
    public async Task Context_Returns_Faces_And_No_Internals()
    {
        using var f = Factory();
        var profileId = await SeedProfileAsync(f);
        var (_, client) = await f.CreateAuthenticatedClientAsync();
        var (faceId, fileId) = await SeedFaceAsync(f, client, profileId, 500, 500, 0.2, 0.2, 0.3, 0.3);
        // A second face on the same blob.
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var blobId = await db.FileItems.Where(x => x.Id == fileId).Select(x => x.BlobObjectId).SingleAsync();
            db.FaceDetections.Add(new FaceDetection
            {
                Id = Guid.NewGuid(), BlobObjectId = blobId, ProfileId = profileId, FaceIndex = 1,
                BoundingBoxX = 0.6, BoundingBoxY = 0.6, BoundingBoxWidth = 0.2, BoundingBoxHeight = 0.2,
                DetectionScore = 0.8, LandmarksJson = "[]", CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var resp = await client.GetAsync($"/api/people/faces/{faceId}/context");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var raw = await resp.Content.ReadAsStringAsync();
        Assert.Contains(fileId.ToString(), raw);          // fileItemId present
        Assert.Contains("\"faces\"", raw, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("selectedFaceId", raw, StringComparison.OrdinalIgnoreCase);
        foreach (var n in Forbidden)
        {
            Assert.DoesNotContain(n, raw, StringComparison.Ordinal);
        }
    }
}
