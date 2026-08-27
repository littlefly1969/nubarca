using System.Diagnostics;
using System.Text;

namespace NubArca.Api.Rag.Sources;

/// One entry of a Git tree: what it is, where it is, which object holds it, and
/// HOW BIG that object is.
///
/// Deliberately WITHOUT content. The bytes are fetched separately and only for
/// entries the source policy accepted, so a tracked `.env` is never read into
/// this process at all — refusing to index a file and refusing to open it are
/// different strengths of the same statement, and this is the stronger one.
///
/// `Size` is why the listing pays for `-l`. Without it the only way to learn how
/// large a blob is was to read it, so "this file is too large to index" was a
/// verdict reached AFTER allocating it in full — which is not a bound, it is a
/// report. A tracked multi-gigabyte object is now refused from the tree entry,
/// before the object store is asked for anything.
public sealed record RepositorySnapshotEntry(string Path, string Mode, string ObjectId, long Size)
{
    /// Git's mode for a symbolic link. The blob's content is the LINK TARGET,
    /// not a file — see RepositorySourcePolicy for why that is refused rather
    /// than followed.
    public const string SymbolicLinkMode = "120000";

    /// A gitlink: a submodule's commit, recorded in the parent tree. There is
    /// no blob to read.
    public const string SubmoduleMode = "160000";

    /// `ls-tree -l` prints `-` for anything that is not a blob. Size is then
    /// genuinely unknown, and an unknown size must never read as "small".
    public const long UnknownSize = -1;

    public bool IsSymbolicLink => Mode == SymbolicLinkMode;

    public bool IsSubmodule => Mode == SubmoduleMode;
}

/// An open snapshot of one revision: its entries, and a way to read their bytes.
///
/// A session rather than a function, because reading two thousand blobs must not
/// mean starting two thousand processes. One `git cat-file --batch` stays open
/// for the run and is fed object ids.
public interface IRepositorySnapshot : IAsyncDisposable
{
    string Root { get; }

    /// The full commit SHA the entries were read from. Never a branch name and
    /// never "HEAD": what gets stamped on a source has to be a thing that
    /// cannot later mean something else.
    string Revision { get; }

    IReadOnlyList<RepositorySnapshotEntry> Entries { get; }

    Task<byte[]> ReadAsync(RepositorySnapshotEntry entry, CancellationToken cancellationToken = default);
}

/// Reads a repository at an EXACT revision.
///
/// The predecessor listed tracked paths with `git ls-files` and then read the
/// files from the working tree. That stamped `Revision = HEAD` onto bytes that
/// were whatever happened to be on disk — uncommitted edits, a half-finished
/// refactor, a merge in progress — and an explicit `--revision` made the lie
/// larger rather than smaller. An index that says "this is how NubArca works at
/// 943e37b" has to be reading 943e37b.
public interface IRepositorySnapshotReader
{
    /// The checkout's TOP LEVEL, given any path inside it.
    ///
    /// Every path rule in RepositorySourcePolicy — `tests/` is a test,
    /// `scripts/` is a script, `Migrations/` is a migration — is written against
    /// repository-root-relative paths, so the root is resolved rather than taken
    /// as given.
    Task<string> ResolveRootAsync(string path, CancellationToken cancellationToken = default);

    /// Resolve a commit-ish to a full commit SHA. Null or empty means HEAD.
    /// Throws RepositorySnapshotUnavailableException when it does not resolve —
    /// a revision nobody can name is not a snapshot to index.
    Task<string> ResolveRevisionAsync(
        string root, string? revision = null, CancellationToken cancellationToken = default);

    Task<IRepositorySnapshot> OpenAsync(
        string root, string revision, CancellationToken cancellationToken = default);
}

/// A local revision could not be read. Carries a sanitized reason and the
/// commit-ish the caller supplied — never a filesystem path and never git's
/// stderr, which names one.
public sealed class RepositorySnapshotUnavailableException(string reason, string? revision = null)
    : Exception($"Repository snapshot unavailable ({reason}).")
{
    public string Reason { get; } = reason;

    public string? Revision { get; } = revision;
}

/// Local Git plumbing: `ls-tree` for the shape, `cat-file --batch` for the
/// bytes.
///
/// Git runs at INDEX time only. Nothing on the query path starts a process, so
/// answering a question never touches a checkout and `rag query` works inside a
/// container that has no repository in it.
///
/// Nothing from `.git` becomes knowledge. The object store is read through
/// plumbing that returns exactly the blobs a tree names; the reflog, the branch
/// list, the remotes, the config and whatever a credential helper cached are
/// never enumerated. Those describe one person's clone, not NubArca.
public sealed class GitRepositorySnapshotReader : IRepositorySnapshotReader
{
    private const int TimeoutSeconds = 120;

    public async Task<string> ResolveRootAsync(
        string path, CancellationToken cancellationToken = default)
    {
        var output = await RunAsync(path, new[] { "rev-parse", "--show-toplevel" }, cancellationToken);
        var toplevel = output?.Trim();
        return string.IsNullOrEmpty(toplevel) ? path : toplevel;
    }

    public async Task<string> ResolveRevisionAsync(
        string root, string? revision = null, CancellationToken cancellationToken = default)
    {
        var commitish = string.IsNullOrWhiteSpace(revision) ? "HEAD" : revision.Trim();

        // `^{commit}` makes this fail for a tree, a blob or a tag that does not
        // point at a commit, rather than resolving to something a snapshot
        // cannot be taken of.
        var output = await RunAsync(
            root, new[] { "rev-parse", "--verify", "--quiet", $"{commitish}^{{commit}}" }, cancellationToken);

        var resolved = output?.Trim();
        if (string.IsNullOrEmpty(resolved) || resolved.Length != 40 || !IsHex(resolved))
        {
            throw new RepositorySnapshotUnavailableException("revision-unresolved", commitish);
        }
        return resolved;
    }

    public async Task<IRepositorySnapshot> OpenAsync(
        string root, string revision, CancellationToken cancellationToken = default)
    {
        // `-z` because a repository is allowed to contain a filename with a
        // newline in it, and splitting on '\n' would turn one such file into two
        // paths that do not exist. `--full-tree` because ls-tree is otherwise
        // relative to the current directory, which is how the predecessor
        // silently indexed one subdirectory. `-l` for the blob SIZE, which is
        // what lets an oversized object be refused before it is allocated
        // instead of after.
        var listing = await RunBytesAsync(
            root, new[] { "ls-tree", "-r", "-z", "-l", "--full-tree", revision }, cancellationToken)
            ?? throw new RepositorySnapshotUnavailableException("tree-unreadable", revision);

        var entries = ParseTree(listing);
        GitCatFileSession? session = null;
        try
        {
            session = GitCatFileSession.Start(root);
            return new GitRepositorySnapshot(root, revision, entries, session);
        }
        catch
        {
            if (session is not null) await session.DisposeAsync();
            throw;
        }
    }

    /// `<mode> SP <type> SP <object> SP <size> TAB <path> NUL`, repeated.
    ///
    /// `-l` right-aligns the size in a padded column, so the header is split on
    /// runs of spaces rather than on single ones. A non-blob prints `-` there,
    /// and a listing written by a git without `-l` has no fourth field at all —
    /// both parse to UnknownSize rather than to zero, because a size that is not
    /// known must not read as a size that is small.
    internal static IReadOnlyList<RepositorySnapshotEntry> ParseTree(byte[] listing)
    {
        var entries = new List<RepositorySnapshotEntry>();
        var text = Encoding.UTF8.GetString(listing);

        foreach (var record in text.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = record.IndexOf('\t');
            if (tab < 0) continue;

            var header = record[..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (header.Length < 3) continue;

            var size = header.Length >= 4 && long.TryParse(header[3], out var parsed) && parsed >= 0
                ? parsed
                : RepositorySnapshotEntry.UnknownSize;

            entries.Add(new RepositorySnapshotEntry(
                Path: record[(tab + 1)..].Replace('\\', '/'),
                Mode: header[0],
                ObjectId: header[2],
                Size: size));
        }

        // Ordinal by path, so an index run visits the tree the same way on every
        // machine and a diagnostic listing is comparable between two runs.
        return entries.OrderBy(e => e.Path, StringComparer.Ordinal).ToList();
    }

    private static bool IsHex(string value)
    {
        foreach (var c in value)
        {
            if (!char.IsAsciiHexDigitLower(c) && !char.IsAsciiHexDigitUpper(c)) return false;
        }
        return true;
    }

    private static async Task<string?> RunAsync(
        string workingDirectory, IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        var bytes = await RunBytesAsync(workingDirectory, arguments, cancellationToken);
        return bytes is null ? null : Encoding.UTF8.GetString(bytes);
    }

    private static async Task<byte[]?> RunBytesAsync(
        string workingDirectory, IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(workingDirectory)) return null;

        var info = GitStartInfo(workingDirectory, arguments);
        try
        {
            using var process = Process.Start(info);
            if (process is null) return null;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));

            using var buffer = new MemoryStream();
            var copy = process.StandardOutput.BaseStream.CopyToAsync(buffer, timeout.Token);
            // Drained and discarded: git's stderr names filesystem paths, and
            // this class has no business publishing one. Not draining it at all
            // would deadlock a chatty command on a full pipe.
            var drain = process.StandardError.ReadToEndAsync(timeout.Token);
            await Task.WhenAll(copy, drain);
            await process.WaitForExitAsync(timeout.Token);

            return process.ExitCode == 0 ? buffer.ToArray() : null;
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

    /// ArgumentList, never a command STRING. A repository-relative path or a
    /// commit-ish reaches this method from a CLI argument, and a shell would be
    /// one quoting mistake away from executing part of it.
    internal static ProcessStartInfo GitStartInfo(
        string workingDirectory, IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return info;
    }
}
