using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Storage;
using NubArca.Api.Tests.Endpoints;
using Xunit;

namespace NubArca.Api.Tests.Storage;

// The writer half of the storage-mutation invariant, asserted as an END STATE
// rather than as a lock: whatever a writer records as durable ownership must
// point at bytes that are actually present.
//
// Advisory locks are a no-op on SQLite, so these tests cannot prove exclusion —
// that is StorageMutationLockConcurrencyTests' job on real PostgreSQL. What
// they prove is the other half: that each writer publishes and commits through
// the protected path, so its committed ownership never dangles. They fail if a
// writer is moved back to a bare WriteAsync or its commit is split away from
// its publish.
public sealed class WriterOwnershipIntegrityTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public WriterOwnershipIntegrityTests()
    {
        _factory = new SqliteWebApplicationFactory();
        _factory.EnsureDatabaseCreated();
    }

    public void Dispose() => _factory.Dispose();

    private string PathOf(string storageKey) =>
        Path.Combine(_factory.StorageRoot, storageKey.Replace('/', Path.DirectorySeparatorChar));

    private async Task<T> QueryAsync<T>(Func<AppDbContext, Task<T>> query)
    {
        using var scope = _factory.Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    // Every committed BlobObject must have its bytes. This is the invariant in
    // its most direct form.
    private async Task AssertEveryBlobHasBytesAsync()
    {
        var rows = await QueryAsync(db =>
            db.BlobObjects.AsNoTracking().Select(b => b.StorageKey).ToListAsync());
        var storage = _factory.Services.GetRequiredService<IBlobStorage>();
        var derived = _factory.Services.GetService<IDerivedBlobStorage>();
        foreach (var key in rows)
        {
            var present = await storage.ExistsAsync(key)
                || (derived is not null && await derived.ExistsAsync(key));
            Assert.True(present, "FORBIDDEN STATE: a committed BlobObject with no physical bytes.");
        }
    }

    [Fact]
    public async Task StoreAsync_Commits_Ownership_Only_Over_Real_Bytes()
    {
        using var scope = _factory.Services.CreateScope();
        var blobs = scope.ServiceProvider.GetRequiredService<IBlobService>();

        await using var ms = new MemoryStream(Encoding.UTF8.GetBytes("store-async-content"));
        var blob = await blobs.StoreAsync(ms);

        Assert.True(File.Exists(PathOf(blob.StorageKey)));
        await AssertEveryBlobHasBytesAsync();
    }

    [Fact]
    public async Task Staged_Bytes_Are_Not_Published_Until_Publish_Is_Called()
    {
        // The boundary the whole design rests on: staging must NOT occupy the
        // content-addressed key, so an abandoned write can never be mistaken
        // for an unowned object and swept.
        var storage = _factory.Services.GetRequiredService<IBlobStorage>();
        await using var ms = new MemoryStream(Encoding.UTF8.GetBytes("staged-not-published"));
        var staged = await storage.StageAsync(ms);

        Assert.False(
            File.Exists(PathOf(staged.StorageKey)),
            "staging must not publish; the key belongs to nobody yet");
        Assert.False(await storage.ExistsAsync(staged.StorageKey));

        await staged.DisposeAsync();

        // Abandoned: still not published, and the temp file is gone.
        Assert.False(await storage.ExistsAsync(staged.StorageKey));
    }

    [Fact]
    public async Task Publishing_Staged_Bytes_Makes_Them_Reachable_Exactly_Once()
    {
        var storage = _factory.Services.GetRequiredService<IBlobStorage>();
        var content = Encoding.UTF8.GetBytes("publish-idempotence");

        await using var first = new MemoryStream(content, writable: false);
        var stagedA = await storage.StageAsync(first);
        var publishedA = await storage.PublishAsync(stagedA);
        Assert.False(publishedA.AlreadyExisted);

        // A second writer of identical content observes reuse rather than
        // overwriting — the branch that only the shared lock makes safe.
        await using var second = new MemoryStream(content, writable: false);
        var stagedB = await storage.StageAsync(second);
        var publishedB = await storage.PublishAsync(stagedB);
        Assert.True(publishedB.AlreadyExisted);

        Assert.Equal(publishedA.StorageKey, publishedB.StorageKey);
        Assert.True(File.Exists(PathOf(publishedA.StorageKey)));
    }

    [Fact]
    public async Task Upload_Through_The_Api_Leaves_No_Dangling_Ownership()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var part = new ByteArrayContent(Encoding.UTF8.GetBytes("end-to-end-upload"));
        part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
        var response = await client.PostAsync(
            "/api/files", new MultipartFormDataContent { { part, "file", "upload.txt" } });
        response.EnsureSuccessStatusCode();

        await AssertEveryBlobHasBytesAsync();
    }

    [Fact]
    public async Task Derived_Artifact_Writer_Leaves_No_Dangling_Ownership()
    {
        // An image upload generates thumbnail/preview derived blobs through
        // StoreDerivedAsync — the same protected publish path.
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        using var img = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(240, 180);
        using var buffer = new MemoryStream();
        img.Save(buffer, new SixLabors.ImageSharp.Formats.Png.PngEncoder());

        var part = new ByteArrayContent(buffer.ToArray());
        part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        var response = await client.PostAsync(
            "/api/files", new MultipartFormDataContent { { part, "file", "pic.png" } });
        response.EnsureSuccessStatusCode();

        var thumbnails = await QueryAsync(db => db.FileThumbnails.AsNoTracking().CountAsync());
        Assert.True(thumbnails > 0, "the test needs at least one derived artifact to be meaningful");
        await AssertEveryBlobHasBytesAsync();
    }

    [Fact]
    public async Task Reused_Content_Still_Ends_With_Ownership_Over_Real_Bytes()
    {
        // Two uploads of identical content: the second takes the reuse branch,
        // which is the one that used to be able to commit onto bytes a purge
        // had just removed.
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var bytes = Encoding.UTF8.GetBytes("identical-content-twice");

        foreach (var name in new[] { "a.txt", "b.txt" })
        {
            var part = new ByteArrayContent(bytes);
            part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
            var response = await client.PostAsync(
                "/api/files", new MultipartFormDataContent { { part, "file", name } });
            response.EnsureSuccessStatusCode();
        }

        var blob = await QueryAsync(db => db.BlobObjects.AsNoTracking()
            .SingleAsync(b => b.ReferenceCount == 2));
        Assert.True(File.Exists(PathOf(blob.StorageKey)));
        await AssertEveryBlobHasBytesAsync();
    }
}
