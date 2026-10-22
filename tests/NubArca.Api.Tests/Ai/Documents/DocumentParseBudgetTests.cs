using System.Reflection;
using NubArca.Api.Ai.Documents;
using NubArca.Api.Cli;
using Xunit;

namespace NubArca.Api.Tests.Ai.Documents;

public sealed class DocumentParseBudgetTests
{
    [Fact]
    public async Task A_Parse_That_Finishes_Returns_Its_Result()
    {
        var (result, timedOut) = await DocumentParseBudget.RunAsync(_ => "ok", 5, CancellationToken.None);
        Assert.False(timedOut);
        Assert.Equal("ok", result);
    }

    [Fact]
    public async Task A_Parse_That_Never_Checks_Its_Token_Is_Abandoned_At_The_Deadline()
    {
        using var release = new ManualResetEventSlim(false);
        var started = DateTime.UtcNow;
        var (result, timedOut) = await DocumentParseBudget.RunAsync<string>(
            _ => { release.Wait(TimeSpan.FromSeconds(30)); return "late"; }, 1, CancellationToken.None);
        release.Set();

        Assert.True(timedOut);
        Assert.Null(result);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task A_Parse_That_Observes_The_Deadline_Token_Is_A_Timeout()
    {
        var (result, timedOut) = await DocumentParseBudget.RunAsync<string>(
            token => { token.WaitHandle.WaitOne(TimeSpan.FromSeconds(30)); token.ThrowIfCancellationRequested(); return "x"; },
            1, CancellationToken.None);
        Assert.True(timedOut);
        Assert.Null(result);
    }

    [Fact]
    public async Task Caller_Cancellation_Is_Cancellation_Not_A_Timeout()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DocumentParseBudget.RunAsync<string>(
            token => { token.WaitHandle.WaitOne(TimeSpan.FromSeconds(30)); token.ThrowIfCancellationRequested(); return "x"; },
            60, cts.Token));
    }

    [Fact]
    public async Task Both_Pdfium_Renderers_Share_The_One_Gate()
    {
        // Neither renderer may carry a private semaphore of its own again: two
        // gates over one native library are one gate too few.
        foreach (var type in new[] { typeof(PdfPageRenderer), typeof(NubArca.Api.Ai.DocumentVisual.PdfVisualRenderer) })
        {
            var gates = type.GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                .Where(f => f.FieldType == typeof(SemaphoreSlim));
            Assert.Empty(gates);
        }
        // Capacity one, proved without racing the PDF tests that run in
        // parallel and may hold the gate right now: once we hold it, nobody
        // else can.
        Assert.True(await PdfiumGate.Native.WaitAsync(TimeSpan.FromSeconds(60)));
        try
        {
            Assert.Equal(0, PdfiumGate.Native.CurrentCount);
        }
        finally
        {
            PdfiumGate.Native.Release();
        }
    }

    [Fact]
    public void Documents_Is_A_Cli_Invocation()
        => Assert.True(CliEntryPoint.IsCliInvocation(new[] { "documents", "progress" }));
}
