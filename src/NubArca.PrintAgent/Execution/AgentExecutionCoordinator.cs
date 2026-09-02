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
            else if (entry.State is LocalExecutionStates.Completed or LocalExecutionStates.Failed
                or LocalExecutionStates.DeliveryUnknown)
            {
                await AcknowledgeAsync(entry, cancellationToken);
            }
            else if (entry.State == LocalExecutionStates.Claimed && File.Exists(entry.ArtifactPath))
            {
                await SubmitAsync(entry, cancellationToken);
            }
        }
    }

    public async Task ExecuteAsync(AgentClaim claim, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_options.TemporaryPath);
        await EnforceTemporaryBoundAsync(cancellationToken);
        var extension = claim.ContentType == "image/png" ? ".png" : ".jpg";
        var path = Path.Combine(_options.TemporaryPath, $"{claim.JobId:N}{extension}");
        if (File.Exists(path)) File.Delete(path);
        await _api.DownloadAsync(claim, path, _options.MaxArtifactBytes, cancellationToken);
        var entry = new JournalEntry(claim.JobId, claim.ClaimToken, path, claim.DeviceKey,
            claim.ContentType, claim.Format, LocalExecutionStates.Claimed, null, null);
        await _journal.UpsertClaimedAsync(entry, cancellationToken);
        await SubmitAsync(entry, cancellationToken);
    }

    private async Task SubmitAsync(JournalEntry entry, CancellationToken cancellationToken)
    {
        await _api.MarkSubmittingAsync(entry.JobId, entry.ClaimToken, cancellationToken);
        await _journal.MarkSubmittingAsync(entry, cancellationToken);
        JournalEntry resultEntry;
        try
        {
            var result = await _adapter.SubmitAsync(new PrintSubmission(entry.JobId,
                entry.DeviceKey, entry.ArtifactPath, entry.ContentType, entry.Format), cancellationToken);
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
            await _api.ReportAsync(entry.JobId, entry.ClaimToken, outcome, entry.FailureCode,
                entry.SpoolReference, cancellationToken);
            await _journal.MarkAcknowledgedAsync(entry.JobId, cancellationToken);
            if (File.Exists(entry.ArtifactPath)) File.Delete(entry.ArtifactPath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Keep the terminal row and artifact. Recovery retries only this ACK.
            _logger.LogWarning("Print result ACK will be retried ({ExceptionType}).", ex.GetType().Name);
        }
    }

    private async Task EnforceTemporaryBoundAsync(CancellationToken cancellationToken)
    {
        var referenced = (await _journal.LoadPendingAsync(cancellationToken))
            .Select(x => Path.GetFullPath(x.ArtifactPath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
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
