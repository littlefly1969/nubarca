using NubArca.PrintAgent.Adapters;
using NubArca.PrintAgent.Api;
using NubArca.PrintAgent.Journal;

namespace NubArca.PrintAgent.Execution;

public sealed class AgentExecutionCoordinator
{
    private readonly PrintAgentApiClient _api;
    private readonly IPrinterAdapter _adapter;
    private readonly ExecutionJournal _journal;
    private readonly PrintAgentOptions _options;
    private readonly ILogger<AgentExecutionCoordinator> _logger;

    public AgentExecutionCoordinator(PrintAgentApiClient api, IPrinterAdapter adapter,
        ExecutionJournal journal, PrintAgentOptions options, ILogger<AgentExecutionCoordinator> logger)
    {
        _api = api; _adapter = adapter; _journal = journal; _options = options; _logger = logger;
    }

    /// <summary>At start: whatever the previous process left in the journal.</summary>
    public async Task RecoverAsync(CancellationToken cancellationToken)
    {
        foreach (var entry in await _journal.LoadPendingAsync(cancellationToken))
        {
            if (entry.State == LocalExecutionStates.Submitting)
            {
                // The process died after persisting "about to submit". We cannot
                // prove whether the driver accepted it, so safety wins over an
                // automatic duplicate.
                await _journal.MarkResultAsync(entry, LocalExecutionStates.DeliveryUnknown,
                    "agent_restart_during_submit", entry.SpoolReference, cancellationToken);
                await AcknowledgeAsync(entry with
                {
                    State = LocalExecutionStates.DeliveryUnknown,
                    FailureCode = "agent_restart_during_submit",
                }, cancellationToken);
            }
        }
        await ResumeAsync(cancellationToken);
    }

    /// <summary>
    /// Every cycle, before a new claim: finishes what a failed request left
    /// half done. A job claimed but never submitted is submitted with the claim
    /// already held, seconds after the network is back, instead of waiting on
    /// the server's lease; an outcome the server has not acknowledged is sent
    /// again. True when a held job was taken care of; an outcome still waiting
    /// for its acknowledgement does not hold up new claims.
    /// </summary>
    public async Task<bool> ResumeAsync(CancellationToken cancellationToken)
    {
        var handled = false;
        foreach (var entry in await _journal.LoadPendingAsync(cancellationToken))
        {
            if (entry.State is LocalExecutionStates.Completed or LocalExecutionStates.Failed
                or LocalExecutionStates.DeliveryUnknown)
            {
                await AcknowledgeAsync(entry, cancellationToken);
            }
            else if (entry.State == LocalExecutionStates.Claimed)
            {
                if (FilesOf(entry).All(File.Exists)) await SubmitAsync(entry, cancellationToken);
                else await DropUnsubmittedAsync(entry, cancellationToken);
                handled = true;
            }
        }
        return handled;
    }

    public async Task ExecuteAsync(AgentClaim claim, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_options.TemporaryPath);
        await EnforceTemporaryBoundAsync(cancellationToken);
        var extension = claim.ContentType == "image/png" ? ".png" : ".jpg";
        var path = Path.Combine(_options.TemporaryPath, $"{claim.JobId:N}{extension}");
        var files = JobArtifactFiles.Of(path, claim.Format);
        foreach (var file in files) if (File.Exists(file)) File.Delete(file);
        if (claim.ArtifactParts is { Count: > 0 } parts)
        {
            // A joined 20x15: each 10x15 is its own file, downloaded on its own.
            if (parts.Count != files.Count)
                throw new InvalidDataException("The job's files are not the ones its format prints.");
            // Both or neither: a 10x15 whose twin did not arrive is never printed alone.
            try
            {
                for (var i = 0; i < parts.Count; i++)
                    await _api.DownloadAsync(claim, parts[i].Url, parts[i].ByteLength, files[i],
                        _options.MaxArtifactBytes, cancellationToken);
            }
            catch
            {
                foreach (var file in files) if (File.Exists(file)) File.Delete(file);
                throw;
            }
        }
        else
        {
            await _api.DownloadAsync(claim, path, _options.MaxArtifactBytes, cancellationToken);
        }
        var entry = new JournalEntry(claim.JobId, claim.ClaimToken, path, claim.DeviceKey,
            claim.ContentType, claim.Format, LocalExecutionStates.Claimed, null, null, claim.Simulated);
        await _journal.UpsertClaimedAsync(entry, cancellationToken);
        await SubmitAsync(entry, cancellationToken);
    }

    private async Task SubmitAsync(JournalEntry entry, CancellationToken cancellationToken)
    {
        if (!await _api.MarkSubmittingAsync(entry.JobId, entry.ClaimToken, cancellationToken))
        {
            await DropUnsubmittedAsync(entry, cancellationToken);
            return;
        }
        await _journal.MarkSubmittingAsync(entry, cancellationToken);
        JournalEntry resultEntry;
        try
        {
            var result = await _adapter.SubmitAsync(new PrintSubmission(entry.JobId,
                entry.DeviceKey, entry.ArtifactPath, entry.ContentType, entry.Format, entry.Simulated,
                FilesOf(entry)), cancellationToken);
            resultEntry = result.Accepted
                ? entry with { State = LocalExecutionStates.Completed, SpoolReference = result.SpoolReference }
                : entry with { State = LocalExecutionStates.Failed,
                    FailureCode = result.FailureCode ?? "submit_failed" };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            resultEntry = entry with { State = LocalExecutionStates.DeliveryUnknown,
                FailureCode = "shutdown_during_submit" };
        }
        catch (Exception ex)
        {
            _logger.LogError("Printer submission ended ambiguously ({ExceptionType}).", ex.GetType().Name);
            resultEntry = entry with { State = LocalExecutionStates.DeliveryUnknown,
                FailureCode = "adapter_submission_ambiguous" };
        }
        await _journal.MarkResultAsync(resultEntry, resultEntry.State, resultEntry.FailureCode,
            resultEntry.SpoolReference, CancellationToken.None);
        await AcknowledgeAsync(resultEntry, CancellationToken.None);
    }

    private async Task AcknowledgeAsync(JournalEntry entry, CancellationToken cancellationToken)
    {
        var outcome = entry.State switch
        {
            LocalExecutionStates.Completed => "completed",
            LocalExecutionStates.Failed => "failed",
            _ => "delivery-unknown",
        };
        try
        {
            if (!await _api.ReportAsync(entry.JobId, entry.ClaimToken, outcome, entry.FailureCode,
                    entry.SpoolReference, cancellationToken))
                _logger.LogWarning("The server no longer knows job {JobId}; its outcome is not sent again.", entry.JobId);
            await _journal.MarkAcknowledgedAsync(entry.JobId, cancellationToken);
            DeleteFiles(entry);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Keep the terminal row and artifact. Recovery retries only this ACK.
            _logger.LogWarning("Print result ACK will be retried ({ExceptionType}).", ex.GetType().Name);
        }
    }

    /// <summary>
    /// A claim the server no longer holds for us, or whose artifact is gone,
    /// and that never reached the printer: nothing was printed, so it is
    /// forgotten here and the server hands the job out again.
    /// </summary>
    private async Task DropUnsubmittedAsync(JournalEntry entry, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Job {JobId} was not submitted and its claim is no longer held; the server hands it out again.",
            entry.JobId);
        await _journal.MarkAcknowledgedAsync(entry.JobId, cancellationToken);
        DeleteFiles(entry);
    }

    private static IReadOnlyList<string> FilesOf(JournalEntry entry) => JobArtifactFiles.Of(entry.ArtifactPath, entry.Format);

    private static void DeleteFiles(JournalEntry entry)
    {
        foreach (var file in FilesOf(entry)) if (File.Exists(file)) File.Delete(file);
    }

    private async Task EnforceTemporaryBoundAsync(CancellationToken cancellationToken)
    {
        var referenced = (await _journal.LoadPendingAsync(cancellationToken))
            .SelectMany(FilesOf).Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var files = new DirectoryInfo(_options.TemporaryPath).EnumerateFiles()
            .OrderBy(x => x.LastWriteTimeUtc).ToList();
        var total = files.Sum(x => x.Length);
        foreach (var file in files)
        {
            if (total <= _options.MaxTemporaryBytes) break;
            if (referenced.Contains(file.FullName)) continue;
            total -= file.Length;
            file.Delete();
        }
        if (total + _options.MaxArtifactBytes > _options.MaxTemporaryBytes)
            throw new IOException("Temporary print storage is full.");
    }
}
