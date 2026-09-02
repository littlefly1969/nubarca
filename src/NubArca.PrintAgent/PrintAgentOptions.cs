namespace NubArca.PrintAgent;

public sealed class PrintAgentOptions
{
    public const string SectionName = "PrintAgent";
    public string ServerOrigin { get; set; } = string.Empty;
    public string CredentialPath { get; set; } = @"%ProgramData%\NubArca\PrintAgent\credential.bin";
    public string JournalPath { get; set; } = @"%ProgramData%\NubArca\PrintAgent\journal.db";
    public string TemporaryPath { get; set; } = @"%ProgramData%\NubArca\PrintAgent\temp";
    public string Adapter { get; set; } = "windows-spooler";
    public string? PrinterName { get; set; }
    public string FakeOutputPath { get; set; } = @"%ProgramData%\NubArca\PrintAgent\fake-output";
    public int IdlePollSeconds { get; set; } = 5;
    public int MaxBackoffSeconds { get; set; } = 60;
    public long MaxArtifactBytes { get; set; } = 32 * 1024 * 1024;
    public long MaxTemporaryBytes { get; set; } = 128 * 1024 * 1024;

    public void NormalizeAndValidate()
    {
        CredentialPath = Environment.ExpandEnvironmentVariables(CredentialPath);
        JournalPath = Environment.ExpandEnvironmentVariables(JournalPath);
        TemporaryPath = Environment.ExpandEnvironmentVariables(TemporaryPath);
        FakeOutputPath = Environment.ExpandEnvironmentVariables(FakeOutputPath);
        if (IdlePollSeconds < 1 || MaxBackoffSeconds < 2 || MaxArtifactBytes < 1
            || MaxTemporaryBytes < MaxArtifactBytes)
            throw new InvalidOperationException("Print Agent bounds are invalid.");
    }
}
