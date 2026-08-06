using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Metadata;
using NubArca.Api.Tests.Endpoints;
using Xunit;

namespace NubArca.Api.Tests.Files;

// Slice 54.1 — hard-delete paths must remove FileItemUserMetadata (FK Restrict
// to FileItem) before deleting the FileItem, or files with edited user
// metadata fail their permanent delete / empty-trash / sweeper purge.
public sealed class MetadataDeletionLifecycleTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public MetadataDeletionLifecycleTests()
    {
        _factory = new SqliteWebApplicationFactory();
        _factory.EnsureDatabaseCreated();
    }

    public void Dispose() => _factory.Dispose();

    private async Task<FileItem> SeedFileAsync(Guid ownerId, string name, string content = "x")
    {
        using var scope = _factory.Services.CreateScope();
        var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
        return await files.CreateAsync(
            ownerId, null, name, "text/plain", new MemoryStream(Encoding.UTF8.GetBytes(content)));
    }

    // Creates a FileItemUserMetadata row for the file via the real service path.
    private async Task AddUserMetadataAsync(Guid ownerId, Guid fileId, string title)
    {
        using var scope = _factory.Services.CreateScope();
        var metadata = scope.ServiceProvider.GetRequiredService<IMetadataService>();
        var result = await metadata.UpdateUserMetadataAsync(
            ownerId, fileId,
            new UpdateFileMetadataRequest(title, null, new[] { "tag" }, 4, true, null, null));
        Assert.NotNull(result);
    }

    private async Task SoftDeleteAsync(Guid ownerId, Guid fileId)
    {
        using var scope = _factory.Services.CreateScope();
        var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
        await files.SoftDeleteAsync(ownerId, fileId);
    }

    private async Task<T> InDbAsync<T>(Func<AppDbContext, Task<T>> work)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await work(db);
    }

    private FileItemSweeper CreateSweeper(bool enabled = true, int graceMinutes = 0)
        => new(
            _factory.Services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new FileItemSweeperOptions
            {
                Enabled = enabled,
                IntervalMinutes = 5,
                GraceMinutes = graceMinutes,
            }),
            TimeProvider.System,
            NullLogger<FileItemSweeper>.Instance);

    [Fact]
    public async Task PermanentDelete_Succeeds_For_File_With_UserMetadata()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await SeedFileAsync(owner, "doc.txt");
        await AddUserMetadataAsync(owner, file.Id, "My doc");
        await SoftDeleteAsync(owner, file.Id);

        // Pre-state: the user-metadata row exists.
        Assert.Equal(1, await InDbAsync(db =>
            db.FileItemUserMetadata.CountAsync(m => m.FileItemId == file.Id)));

        var response = await client.DeleteAsync($"/api/trash/files/{file.Id}");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // File AND its user-metadata row are gone (no FK-violation failure).
        Assert.False(await InDbAsync(db => db.FileItems.AnyAsync(f => f.Id == file.Id)));
        Assert.False(await InDbAsync(db =>
            db.FileItemUserMetadata.AnyAsync(m => m.FileItemId == file.Id)));
    }

    [Fact]
    public async Task EmptyTrash_Succeeds_For_Files_With_UserMetadata()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var fileA = await SeedFileAsync(owner, "a.txt");
        var fileB = await SeedFileAsync(owner, "b.txt");
        await AddUserMetadataAsync(owner, fileA.Id, "A");
        await AddUserMetadataAsync(owner, fileB.Id, "B");
        await SoftDeleteAsync(owner, fileA.Id);
        await SoftDeleteAsync(owner, fileB.Id);

        var response = await client.DeleteAsync("/api/trash");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.False(await InDbAsync(db => db.FileItems.AnyAsync(
            f => f.Id == fileA.Id || f.Id == fileB.Id)));
        Assert.Equal(0, await InDbAsync(db => db.FileItemUserMetadata.CountAsync()));
    }

    [Fact]
    public async Task Sweeper_Purges_Old_SoftDeleted_File_With_UserMetadata()
    {
        var (owner, _) = await _factory.CreateAuthenticatedClientAsync();
        var file = await SeedFileAsync(owner, "old.txt");
        await AddUserMetadataAsync(owner, file.Id, "Old");
        await SoftDeleteAsync(owner, file.Id);
        await InDbAsync(async db =>
        {
            await db.FileItems.Where(f => f.Id == file.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(
                    f => f.DeletedAt, _ => DateTime.UtcNow.AddMinutes(-9999)));
            return 0;
        });

        var purged = await CreateSweeper().RunOnceAsync(default);

        Assert.Equal(1, purged);
        Assert.False(await InDbAsync(db => db.FileItems.AnyAsync(f => f.Id == file.Id)));
        Assert.False(await InDbAsync(db =>
            db.FileItemUserMetadata.AnyAsync(m => m.FileItemId == file.Id)));
    }

    [Fact]
    public async Task PermanentDelete_Leaves_Blob_Cleanup_Behavior_Unchanged()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await SeedFileAsync(owner, "doc.txt");
        await AddUserMetadataAsync(owner, file.Id, "My doc");

        var blobId = file.BlobObjectId;
        await SoftDeleteAsync(owner, file.Id);

        // After soft-delete the file blob's reference count is already 0.
        Assert.Equal(0, await InDbAsync(db =>
            db.BlobObjects.Where(b => b.Id == blobId).Select(b => b.ReferenceCount).SingleAsync()));

        var response = await client.DeleteAsync($"/api/trash/files/{file.Id}");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // Permanent delete does NOT touch the BlobObject row or its BlobMetadata
        // — those stay for the BlobJanitor lifecycle (unchanged semantics).
        Assert.True(await InDbAsync(db => db.BlobObjects.AnyAsync(b => b.Id == blobId)));
        Assert.Equal(0, await InDbAsync(db =>
            db.BlobObjects.Where(b => b.Id == blobId).Select(b => b.ReferenceCount).SingleAsync()));
        Assert.True(await InDbAsync(db => db.BlobMetadata.AnyAsync(m => m.BlobObjectId == blobId)));
    }
}
