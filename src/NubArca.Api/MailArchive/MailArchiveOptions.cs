namespace NubArca.Api.MailArchive;

// MailArchive configuration (section "MailArchive").
//
//   MailArchive__Enabled=true                       (default true; the feature
//                                                    still reports unavailable
//                                                    until the engine exists)
//   MailArchive__ImporterPath=/opt/nubarca/bin/nubarca-pst-importer
//   MailArchive__WorkRootPath=                      (default: <Storage:RootPath>/mail-work)
//
// The work root holds, per running import, a read-only working copy of the
// PST and the engine's per-message staging directory. It must have free space
// for the largest PST being imported plus MaxAttachmentBytes.
public sealed class MailArchiveOptions
{
    public const string SectionName = "MailArchive";

    public bool Enabled { get; set; } = true;

    public string ImporterPath { get; set; } = "/opt/nubarca/bin/nubarca-pst-importer";

    public string WorkRootPath { get; set; } = string.Empty;

    // Engine limits (contract §2). Larger items are classified, never loaded.
    public long MaxAttachmentBytes { get; set; } = 512L * 1024 * 1024;
    public long MaxBodyBytes { get; set; } = 16L * 1024 * 1024;
    public long MaxDocumentBytes { get; set; } = 32L * 1024 * 1024;
    public int MaxEmbeddedDepth { get; set; } = 3;
    public int MaxAttachmentsPerMessage { get; set; } = 500;

    // Engine confinement (docs/mailarchive/operations.md, "Parser isolation").
    // Address-space and CPU-time ceilings per engine process (0 = none), and
    // whether a kernel without Landlock filesystem confinement is refused
    // (the import then fails with `sandbox_unavailable` instead of parsing
    // unconfined).
    public long MaxEngineMemoryBytes { get; set; } = 2L * 1024 * 1024 * 1024;
    public long MaxEngineCpuSeconds { get; set; } = 3600;
    public bool RequireFilesystemSandbox { get; set; } = true;

    // "uid:gid" the engine switches to before parsing when the worker runs
    // as root (the container default). Empty: no switch (a worker that is
    // already unprivileged needs none). The user must be able to traverse
    // to the work root; the engine fails with `sandbox_unavailable` if not.
    public string EngineRunAs { get; set; } = "65534:65534";

    // One message's extraction must answer within this many seconds; a stuck
    // engine is killed and the message is recorded as failed (io_error).
    public int ExtractTimeoutSeconds { get; set; } = 300;

    // probe/list watchdog. A list of a very large PST streams for a while.
    public int ListTimeoutMinutes { get; set; } = 120;

    // Manifest rows written per database batch during the scan.
    public int ScanBatchSize { get; set; } = 500;

    // Top-level folder in the owner's Files that receives the attachments.
    public string AttachmentRootFolderName { get; set; } = "Posta";
}
