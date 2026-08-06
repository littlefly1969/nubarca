using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Domain.Ai;
using Xunit;

namespace NubArca.Api.Tests.Ai.Video;

// VSEM-02: shape guarantees of the canonical embedding model. The entity shape
// IS the privacy guarantee — there is nowhere to put an owner, a FileItem, a
// person, a filename, a path or a storage key.
public sealed class VideoSemanticSampleEmbeddingModelTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;

    public VideoSemanticSampleEmbeddingModelTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public void Embedding_And_Aggregate_Status_Store_No_Owner_Specific_Field()
    {
        var properties = _db.Model.FindEntityType(typeof(VideoSemanticSampleEmbedding))!
            .GetProperties().Select(p => p.Name)
            .Concat(_db.Model.FindEntityType(typeof(VideoSemanticEmbeddingStatus))!
                .GetProperties().Select(p => p.Name))
            .ToList();

        Assert.DoesNotContain("OwnerUserId", properties);
        Assert.DoesNotContain("FileItemId", properties);
        Assert.DoesNotContain("PersonId", properties);
        Assert.DoesNotContain("Name", properties);
        Assert.DoesNotContain("StorageKey", properties);
        Assert.DoesNotContain("Path", properties);
    }

    [Fact]
    public void Aggregate_Status_Is_A_Separate_Entity_From_The_Manifest_Head()
    {
        // Segmentation readiness and embedding readiness are independent axes:
        // the aggregate must never be a column on VideoSemanticIndex.
        var manifest = _db.Model.FindEntityType(typeof(VideoSemanticIndex))!
            .GetProperties().Select(p => p.Name).ToList();

        Assert.DoesNotContain("ExpectedSampleCount", manifest);
        Assert.DoesNotContain("CompletedSampleCount", manifest);
        Assert.NotNull(_db.Model.FindEntityType(typeof(VideoSemanticEmbeddingStatus)));
    }
}
