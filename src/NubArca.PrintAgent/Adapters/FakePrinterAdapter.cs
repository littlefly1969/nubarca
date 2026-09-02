namespace NubArca.PrintAgent.Adapters;

public sealed class FakePrinterAdapter : IPrinterAdapter
{
    private readonly string _outputPath;
    public FakePrinterAdapter(string outputPath) => _outputPath = outputPath;
    public string Kind => "fake";
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
