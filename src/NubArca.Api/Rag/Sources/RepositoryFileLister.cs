using System.Diagnostics;
using System.Text;

namespace NubArca.Api.Rag.Sources;

/// Which files a repository checkout considers its own.
///
/// An interface with exactly one production implementation, because the
/// production implementation shells out to `git` and a unit test must not. A
/// fake lister lets the eligibility rules be tested against a directory of
/// fixture files, which is the part worth testing exhaustively; that `git
/// ls-files` returns tracked files is git's job.
public interface IRepositoryFileLister
{
    /// The checkout's TOP LEVEL, given any path inside it.
    ///
    /// Every path rule in RepositorySourcePolicy — `tests/` is a test,
    /// `scripts/` is a script, `src/NubArca.Api/Data/Migrations/` is a
    /// migration — is written against repository-root-relative paths. So the
    /// root is resolved rather than taken as given: an operator running the
    /// indexer from inside `src/` should index NubArca, not a subtree whose
    /// paths mean something different.
    Task<string> ResolveRootAsync(string path, CancellationToken cancellationToken = default);

    /// Repository-root-relative paths, `/`-separated. Empty when the root is not
    /// a usable checkout — which is an availability answer, never a reason to
    /// fall back to walking the directory.
    Task<IReadOnlyList<string>> ListTrackedAsync(string root, CancellationToken cancellationToken = default);

    /// The checked-out revision, or empty when it cannot be determined.
    Task<string> ResolveRevisionAsync(string root, CancellationToken cancellationToken = default);
}

/// `git ls-files` and `git rev-parse HEAD`, run locally against a checkout.
///
/// THE INDEX IS A SNAPSHOT OF TRACKED FILES, NOT A READ OF `.git`. The object
/// store, the reflog, the local branch list, the remotes and whatever
/// credentials a helper cached are not knowledge about NubArca — they are
/// knowledge about one person's clone, and the repository domain is deliberately
/// not that. Nor are untracked working-tree files: an experiment, an editor
/// backup and a downloaded model all live there.
///
/// Git is used at INDEX time only. Nothing on the query path runs a process, and
/// answering a question never touches a checkout — which is what makes the
/// indexed database the whole dependency and keeps `rag query` usable inside a
/// container that has no repository in it.
public sealed class GitRepositoryFileLister : IRepositoryFileLister
{
    private const int TimeoutSeconds = 60;

    public async Task<string> ResolveRootAsync(
        string path, CancellationToken cancellationToken = default)
    {
        var output = await RunGitAsync(path, new[] { "rev-parse", "--show-toplevel" }, cancellationToken);
        var toplevel = output?.Trim();
        return string.IsNullOrEmpty(toplevel) ? path : toplevel;
    }

    public async Task<IReadOnlyList<string>> ListTrackedAsync(
        string root, CancellationToken cancellationToken = default)
    {
        // `-z` because a repository is allowed to contain a filename with a
        // newline in it, and splitting on '\n' would turn one such file into two
        // paths that do not exist.
        //
        // `--full-name` because git lists paths relative to the CURRENT
        // directory by default. Run from `src/NubArca.Api`, that silently
        // produced source keys like `Program.cs` — which no path rule
        // recognises and no citation can be traced back to a file.
        var output = await RunGitAsync(
            root, new[] { "ls-files", "-z", "--full-name" }, cancellationToken);
        if (output is null) return Array.Empty<string>();

        return output
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Replace('\\', '/'))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<string> ResolveRevisionAsync(
        string root, CancellationToken cancellationToken = default)
    {
        var output = await RunGitAsync(root, new[] { "rev-parse", "HEAD" }, cancellationToken);
        return output?.Trim() ?? string.Empty;
    }

    private static async Task<string?> RunGitAsync(
        string root, IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root)) return null;

        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(info);
            if (process is null) return null;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));

            var stdout = await process.StandardOutput.ReadToEndAsync(timeout.Token);
            // Drained and discarded: git's stderr can name a filesystem path,
            // and this class has no business publishing one.
            _ = await process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return process.ExitCode == 0 ? stdout : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is OperationCanceledException or SystemException)
        {
            // No git, no checkout, or a timeout. All of them mean the same thing
            // to a caller: this root cannot be enumerated.
            return null;
        }
    }
}
