using NubArca.Api.Ai.Jobs;
using NubArca.Api.Ai.Photos;

namespace NubArca.Api.Tests.Ai;

public sealed class AiPhotoEmbeddingJobOutcomeTests
{
    [Fact]
    public void Terminal_All_Failed_Is_A_Job_Failure()
    {
        var result = new PhotoEmbeddingBackfillResult(
            Examined: 0, Indexed: 0, Skipped: 0, Failed: 0, DryRun: false,
            MoreWorkRemaining: false, IndexedTotal: 0, FailedTotal: 12);

        Assert.True(AiPhotosEmbeddingsBackfillJobHandler.IsTerminalAllFailed(result));
    }

    [Theory]
    [InlineData(true, false, 0, 12)]  // continuation is not terminal
    [InlineData(false, false, 1, 12)] // partial success remains usable
    [InlineData(false, false, 0, 0)]  // empty/no-op run is valid
    [InlineData(false, true, 0, 12)]  // dry-run never fails
    public void Other_Outcomes_Do_Not_Fail(
        bool moreWork, bool dryRun, int indexedTotal, int failedTotal)
    {
        var result = new PhotoEmbeddingBackfillResult(
            Examined: 0, Indexed: 0, Skipped: 0, Failed: 0, DryRun: dryRun,
            MoreWorkRemaining: moreWork,
            IndexedTotal: indexedTotal, FailedTotal: failedTotal);

        Assert.False(AiPhotosEmbeddingsBackfillJobHandler.IsTerminalAllFailed(result));
    }
}
