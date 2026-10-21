using Microsoft.Extensions.Options;
using NubArca.Api.Storage;

namespace NubArca.Api.MailArchive;

// Where an import keeps its transient files: per run, a read-only working copy
// of the PST and the engine's per-message staging directory. Nothing here is
// durable data — a run's directory is deleted when the run ends, and a missing
// directory is simply recreated on resume. Paths never leave the server.
public sealed class MailWorkspace
{
    private readonly string? _root;

    public MailWorkspace(IOptions<MailArchiveOptions> options, IOptions<BlobStorageOptions> storage)
    {
        var configured = options.Value.WorkRootPath;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            _root = Path.GetFullPath(configured);
        }
        else if (!string.IsNullOrWhiteSpace(storage.Value.RootPath))
        {
            _root = Path.GetFullPath(Path.Combine(storage.Value.RootPath, "mail-work"));
        }
    }

    public bool IsConfigured => _root is not null;

    public string RunDirectory(Guid runId) => Path.Combine(Root, "runs", runId.ToString("N"));

    public string SourcePath(Guid runId) => Path.Combine(RunDirectory(runId), "source.pst");

    public string ServeDirectory(Guid runId) => Path.Combine(RunDirectory(runId), "serve");

    public long AvailableFreeBytes()
    {
        Directory.CreateDirectory(Root);
        return new DriveInfo(Root).AvailableFreeSpace;
    }

    public void DeleteRun(Guid runId)
    {
        var dir = RunDirectory(runId);
        if (!Directory.Exists(dir))
        {
            return;
        }
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            // The working copy is made read-only; clear that before deleting.
            File.SetAttributes(file, FileAttributes.Normal);
        }
        Directory.Delete(dir, recursive: true);
    }

    // Resolves an engine-relative file reference ("r42/att-0.bin") strictly
    // inside `baseDirectory/requestId/`. Anything else is a protocol error.
    public static string ResolveInside(string baseDirectory, string requestId, string relative)
    {
        var requestDir = Path.GetFullPath(Path.Combine(baseDirectory, requestId)) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(baseDirectory, relative));
        if (!full.StartsWith(requestDir, StringComparison.Ordinal))
        {
            throw new PstEngineException("protocol_error", "The PST engine referenced a file outside its request directory.");
        }
        return full;
    }

    private string Root => _root
        ?? throw new InvalidOperationException("MailArchive has no work root (set MailArchive:WorkRootPath or Storage:RootPath).");
}
