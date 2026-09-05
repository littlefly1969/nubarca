namespace NubArca.PrintAgent.Adapters;

/// <summary>
/// A printer that produces files instead of paper.
///
/// It takes TIME to do it, on purpose. A simulator that returns instantly is a
/// poor model of the thing it stands for: a real 10x15 takes the better part of
/// a minute, and everything downstream of that duration — a queue with depth in
/// it, a guest told how many sheets are ahead of theirs, a job observably in
/// `submitting` rather than blinking through it — is untestable against a
/// printer that never makes anybody wait.
/// </summary>
public sealed class FakePrinterAdapter : IPrinterAdapter
{
    /// <summary>How long one sheet takes. An operator can tune it; this is a
    /// pace that makes a queue visible without making a demo tedious.</summary>
    public static readonly TimeSpan DefaultSheetDuration = TimeSpan.FromSeconds(10);

    private readonly string _outputPath;
    private readonly TimeSpan _sheetDuration;

    public FakePrinterAdapter(string outputPath, TimeSpan? sheetDuration = null)
    {
        _outputPath = outputPath;
        _sheetDuration = sheetDuration ?? DefaultSheetDuration;
    }
    public string Kind => PrintAdapterKinds.Fake;
    public bool FailNextSubmission { get; set; }
    public int SubmissionCount { get; private set; }

    public Task<IReadOnlyList<DiscoveredPrinter>> DiscoverAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DiscoveredPrinter>>([
            new("fake-10x15", "NubArca Fake 10x15", "NubArca", "CI Simulator", Kind),
        ]);

    public Task<PrinterCapabilities> GetCapabilitiesAsync(DiscoveredPrinter printer,
        CancellationToken cancellationToken) =>
        Task.FromResult(new PrinterCapabilities(["10x15"], Color: true));

    public Task<PrinterObservedStatus> GetStatusAsync(DiscoveredPrinter printer,
        CancellationToken cancellationToken) =>
        Task.FromResult(new PrinterObservedStatus("ready"));

    public async Task<PrintSubmissionResult> SubmitAsync(PrintSubmission submission,
        CancellationToken cancellationToken)
    {
        SubmissionCount++;
        if (FailNextSubmission)
        {
            FailNextSubmission = false;
            return new(false, null, "fake_submit_failed");
        }

        // The sheet takes as long as a sheet takes. Before the file is written,
        // so a job spends this time in `submitting` exactly as it would while a
        // real printer pulled paper — and so a cancelled run leaves no output
        // for a sheet that never finished.
        if (_sheetDuration > TimeSpan.Zero)
        {
            await Task.Delay(_sheetDuration, cancellationToken);
        }

        Directory.CreateDirectory(_outputPath);
        var extension = Path.GetExtension(submission.ArtifactPath);
        if (string.IsNullOrEmpty(extension)) extension = ".bin";
        var destination = Path.Combine(_outputPath, $"{submission.JobId:N}{extension}");
        await using var source = File.OpenRead(submission.ArtifactPath);
        await using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await source.CopyToAsync(target, cancellationToken);
        await target.FlushAsync(cancellationToken);
        return new(true, $"fake:{submission.JobId:N}", null);
    }
}
