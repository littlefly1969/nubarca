using NubArca.Api.Ai.Photos;

namespace NubArca.Api.Tests.Ai;

public sealed class PhotoEmbeddingBackfillCheckpointTests
{
    [Fact]
    public void Roundtrips_Through_Serialize_And_TryParse()
    {
        var checkpoint = new PhotoEmbeddingBackfillCheckpoint(
            PhotoEmbeddingBackfillCheckpoint.CurrentVersion, Guid.NewGuid(), 7, 1, 2);

        Assert.Equal(checkpoint, PhotoEmbeddingBackfillCheckpoint.TryParse(checkpoint.Serialize()));
    }

    [Fact]
    public void TryParse_Returns_Null_For_Null_Blank_Or_Malformed()
    {
        Assert.Null(PhotoEmbeddingBackfillCheckpoint.TryParse(null));
        Assert.Null(PhotoEmbeddingBackfillCheckpoint.TryParse("  "));
        Assert.Null(PhotoEmbeddingBackfillCheckpoint.TryParse("{nope"));
    }

    [Fact]
    public void TryParse_Returns_Null_For_Unknown_Version()
    {
        var future = new PhotoEmbeddingBackfillCheckpoint(V: 999, CursorBlobId: null, 0, 0, 0);
        Assert.Null(PhotoEmbeddingBackfillCheckpoint.TryParse(future.Serialize()));
    }
}
