using NubArca.Api.Ai.Jobs;

namespace NubArca.Api.Tests.Ai;

public sealed class AiBackfillCheckpointTests
{
    [Fact]
    public void Roundtrips_Through_Serialize_And_TryParse()
    {
        var checkpoint = new AiBackfillCheckpoint(
            AiBackfillCheckpoint.CurrentVersion, Guid.NewGuid(), 42);

        var parsed = AiBackfillCheckpoint.TryParse(checkpoint.Serialize());

        Assert.Equal(checkpoint, parsed);
    }

    [Fact]
    public void TryParse_Returns_Null_For_Null_Or_Blank()
    {
        Assert.Null(AiBackfillCheckpoint.TryParse(null));
        Assert.Null(AiBackfillCheckpoint.TryParse("   "));
    }

    [Fact]
    public void TryParse_Returns_Null_For_Unknown_Version()
    {
        var future = new AiBackfillCheckpoint(V: 999, CursorBlobId: null, Processed: 1);
        Assert.Null(AiBackfillCheckpoint.TryParse(future.Serialize()));
    }

    [Fact]
    public void TryParse_Returns_Null_For_Malformed_Json()
    {
        Assert.Null(AiBackfillCheckpoint.TryParse("{not valid json"));
    }
}
