namespace NubArca.Api.Ai.Documents;

/// A wall-clock bound around parsing one document in managed code.
///
/// PdfPig and the OpenXml SDK are managed libraries with no timeout of their
/// own, and a pathological file can keep one of them busy far longer than any
/// real document needs. Managed code cannot be interrupted from outside, so the
/// bound works in two layers:
///
///  1. the parse runs on a thread-pool thread with a token that is CANCELLED at
///     the deadline. Every parser polls its token between pages / paragraphs /
///     rows / slides, so the ordinary slow case stops at the next boundary;
///  2. the caller stops WAITING at the deadline either way and reports
///     `parse-timeout`. If the parser is stuck inside a single library call that
///     never checks the token, that thread is ABANDONED: it keeps running (and
///     keeps its copy of the bytes) until the call returns, and its result is
///     discarded. It writes nothing — parsers have no database or storage
///     access — so abandoning it is safe, but it is not free: it costs one
///     pool thread and the document's memory until it finishes. That is the
///     documented limitation; the alternative (a child process per parse) is a
///     much larger change.
///
/// Caller cancellation is reported as cancellation, never as a timeout.
public static class DocumentParseBudget
{
    public static async Task<(T? Result, bool TimedOut)> RunAsync<T>(
        Func<CancellationToken, T> parse, int timeoutSeconds, CancellationToken cancellationToken)
        where T : class
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var timeout = TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds));
        deadline.CancelAfter(timeout);

        var work = Task.Run(() => parse(deadline.Token), CancellationToken.None);
        try
        {
            var result = await work.WaitAsync(timeout, cancellationToken);
            return (result, false);
        }
        catch (TimeoutException)
        {
            deadline.Cancel();
            Observe(work);
            return (null, true);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The parser observed the deadline token before the wait did.
            return (null, true);
        }
        catch (OperationCanceledException)
        {
            Observe(work);
            throw;
        }
    }

    /// An abandoned parse must not surface later as an unobserved exception.
    private static void Observe(Task task)
        => task.ContinueWith(
            t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
}

/// THE ONE GATE over PDFium in this process.
///
/// PDFium is a single native library with process-wide state and is not
/// documented as thread-safe. The OCR page renderer and the visual page
/// renderer used to hold two different semaphores — two gates over one native
/// library are one gate too few, and once text and visual indexing run in the
/// same worker they would have raced. Both now take this one.
internal static class PdfiumGate
{
    public static readonly SemaphoreSlim Native = new(1, 1);
}
