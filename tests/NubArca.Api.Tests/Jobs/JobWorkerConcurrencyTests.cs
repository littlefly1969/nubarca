using NubArca.Api.Jobs;

namespace NubArca.Api.Tests.Jobs;

public sealed class JobWorkerConcurrencyTests
{
    [Fact]
    public async Task RunWorkerSlots_StartsConfiguredSlotsConcurrently()
    {
        const int expected = 2;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        var active = 0;
        var maxActive = 0;
        var ids = new System.Collections.Concurrent.ConcurrentBag<int>();

        async Task Slot(int id, CancellationToken _)
        {
            ids.Add(id);
            var nowActive = Interlocked.Increment(ref active);
            UpdateMax(ref maxActive, nowActive);
            if (Interlocked.Increment(ref started) == expected) allStarted.TrySetResult();
            await release.Task;
            Interlocked.Decrement(ref active);
        }

        var running = JobWorker.RunWorkerSlotsAsync(expected, Slot, CancellationToken.None);
        await allStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(expected, maxActive);
        Assert.Equal([1, 2], ids.Order());

        release.TrySetResult();
        await running;
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(99, 8)]
    public async Task RunWorkerSlots_ClampsConfiguredSlots(int configured, int expected)
    {
        var ids = new System.Collections.Concurrent.ConcurrentBag<int>();

        await JobWorker.RunWorkerSlotsAsync(configured, (id, _) =>
        {
            ids.Add(id);
            return Task.CompletedTask;
        }, CancellationToken.None);

        Assert.Equal(expected, ids.Count);
        Assert.Equal(Enumerable.Range(1, expected), ids.Order());
    }

    private static void UpdateMax(ref int target, int value)
    {
        while (true)
        {
            var current = Volatile.Read(ref target);
            if (value <= current || Interlocked.CompareExchange(ref target, value, current) == current) return;
        }
    }
}
