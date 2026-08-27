using System.Diagnostics;
using NubArca.Api.Rag.Sources;
using Xunit;

namespace NubArca.Api.Tests.Rag;

// The snapshot reader, against a REAL temporary Git repository.
//
// This is the one part of the substrate a fake cannot prove. The defect it
// replaced was precisely that the implementation looked correct: it resolved a
// revision with Git and then read the bytes from the working tree, so
// `Revision = HEAD` was stamped onto whatever happened to be on disk — an
// uncommitted edit, a half-finished refactor, a merge in progress. An index that
// says "this is how NubArca works at 943e37b" has to have read 943e37b, and only
// a repository with a dirty working tree can demonstrate that it did.
//
// Skipped where git is unavailable rather than failed: it is a tool this test
// needs, not a product dependency.
public sealed class GitRepositorySnapshotTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "nubarca-git-snap-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly bool _available;

    public GitRepositorySnapshotTests()
    {
        _available = Git("--version") is not null;
        if (!_available) return;

        Directory.CreateDirectory(_root);
        Git("init", "--initial-branch=main");
        Git("config", "user.email", "test@example.invalid");
        Git("config", "user.name", "Test");
        Git("config", "commit.gpgsign", "false");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) DeleteTree(_root); }
        catch (IOException) { /* best effort */ }
    }

    [SkippableFact]
    public async Task RepositorySnapshot_UsesCommittedBytes_NotDirtyWorkingTree()
    {
        Skip.IfNot(_available, "git is not available.");

        Write("docs/guide.md", "# Guida\n\nIl contenuto originale, committato.\n");
        Git("add", "-A");
        Git("commit", "-m", "first");

        // The edit that must NOT be indexed.
        Write("docs/guide.md", "# Guida\n\nUNCOMMITTED_SENTINEL_DIRTY_TREE\n");

        var reader = new GitRepositorySnapshotReader();
        var revision = await reader.ResolveRevisionAsync(_root);
        await using var snapshot = await reader.OpenAsync(_root, revision);

        var entry = snapshot.Entries.Single(e => e.Path == "docs/guide.md");
        var text = System.Text.Encoding.UTF8.GetString(await snapshot.ReadAsync(entry));

        Assert.Contains("Il contenuto originale, committato.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("UNCOMMITTED_SENTINEL_DIRTY_TREE", text, StringComparison.Ordinal);
        Assert.Equal(40, snapshot.Revision.Length);
    }

    [SkippableFact]
    public async Task ExplicitRevision_ReadsThatRevisionNotCurrentHead()
    {
        Skip.IfNot(_available, "git is not available.");

        Write("docs/guide.md", "# Guida\n\nPRIMA_VERSIONE\n");
        Git("add", "-A");
        Git("commit", "-m", "first");
        var first = (Git("rev-parse", "HEAD") ?? string.Empty).Trim();

        Write("docs/guide.md", "# Guida\n\nSECONDA_VERSIONE\n");
        Git("add", "-A");
        Git("commit", "-m", "second");

        var reader = new GitRepositorySnapshotReader();
        await using var snapshot = await reader.OpenAsync(
            _root, await reader.ResolveRevisionAsync(_root, first));

        var entry = snapshot.Entries.Single(e => e.Path == "docs/guide.md");
        var text = System.Text.Encoding.UTF8.GetString(await snapshot.ReadAsync(entry));

        Assert.Contains("PRIMA_VERSIONE", text, StringComparison.Ordinal);
        Assert.DoesNotContain("SECONDA_VERSIONE", text, StringComparison.Ordinal);
        Assert.Equal(first, snapshot.Revision);
    }

    [SkippableFact]
    public async Task InvalidRevision_FailsExplicitly()
    {
        Skip.IfNot(_available, "git is not available.");

        Write("a.md", "# A\n\nqualcosa\n");
        Git("add", "-A");
        Git("commit", "-m", "first");

        var reader = new GitRepositorySnapshotReader();

        var failure = await Assert.ThrowsAsync<RepositorySnapshotUnavailableException>(
            () => reader.ResolveRevisionAsync(_root, "not-a-real-revision"));

        Assert.Equal("revision-unresolved", failure.Reason);
        Assert.Equal("not-a-real-revision", failure.Revision);
        // Sanitized: a reason token and the commit-ish the operator typed —
        // never git's stderr, which names a filesystem path.
        Assert.DoesNotContain(_root, failure.Message, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task A_Branch_Name_Resolves_To_A_Full_Commit_Sha()
    {
        Skip.IfNot(_available, "git is not available.");

        Write("a.md", "# A\n\nqualcosa\n");
        Git("add", "-A");
        Git("commit", "-m", "first");

        var reader = new GitRepositorySnapshotReader();
        var resolved = await reader.ResolveRevisionAsync(_root, "main");

        // A snapshot stamped `main` would mean something different next week.
        Assert.Equal(40, resolved.Length);
        Assert.Equal((Git("rev-parse", "HEAD") ?? string.Empty).Trim(), resolved);
    }

    [SkippableFact]
    public async Task Untracked_And_Git_Internal_Files_Are_Not_In_The_Snapshot()
    {
        Skip.IfNot(_available, "git is not available.");

        Write("tracked.md", "# Tracked\n\nnel commit\n");
        Git("add", "-A");
        Git("commit", "-m", "first");
        Write("untracked-experiment.md", "# Untracked\n\nmai committato\n");

        var reader = new GitRepositorySnapshotReader();
        await using var snapshot = await reader.OpenAsync(
            _root, await reader.ResolveRevisionAsync(_root));

        var paths = snapshot.Entries.Select(e => e.Path).ToList();
        Assert.Contains("tracked.md", paths);
        Assert.DoesNotContain("untracked-experiment.md", paths);
        Assert.DoesNotContain(paths, p => p.StartsWith(".git", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task Paths_Needing_Nul_Safe_Enumeration_Survive()
    {
        Skip.IfNot(_available, "git is not available.");
        Skip.If(OperatingSystem.IsWindows(), "Windows filenames cannot contain these characters.");

        // A repository may legally contain a filename with a newline, a quote or
        // a space in it. Splitting ls-tree output on '\n' turned one such file
        // into two paths that do not exist, which is why the reader uses `-z`.
        const string awkward = "docs/a file\nwith newline.md";
        Write(awkward, "# Awkward\n\nun nome scomodo\n");
        Write("docs/quote\"name.md", "# Quote\n\nun altro\n");
        Git("add", "-A");
        Git("commit", "-m", "awkward");

        var reader = new GitRepositorySnapshotReader();
        await using var snapshot = await reader.OpenAsync(
            _root, await reader.ResolveRevisionAsync(_root));

        var paths = snapshot.Entries.Select(e => e.Path).ToList();
        Assert.Contains(awkward, paths);
        Assert.Contains("docs/quote\"name.md", paths);
        Assert.Equal(2, paths.Count);
    }

    [SkippableFact]
    public async Task TrackedSymlinkOutsideCheckout_IsClassifiedAndNeverRead()
    {
        Skip.IfNot(_available, "git is not available.");
        Skip.If(OperatingSystem.IsWindows(), "Symlink creation needs privileges on Windows.");

        Write("docs/guide.md", "# Guida\n\nun documento vero\n");
        var link = Path.Combine(_root, "docs", "escape.md");
        try
        {
            File.CreateSymbolicLink(link, "/etc/passwd");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Skip.If(true, "symlinks are not creatable here.");
            return;
        }
        Git("add", "-A");
        Git("commit", "-m", "link");

        var reader = new GitRepositorySnapshotReader();
        await using var snapshot = await reader.OpenAsync(
            _root, await reader.ResolveRevisionAsync(_root));

        var entry = snapshot.Entries.Single(e => e.Path == "docs/escape.md");
        Assert.True(entry.IsSymbolicLink);
        Assert.False(RepositorySourcePolicy.CheckGitMode(entry.Mode).IsEligible);

        // Not "returns the target's content" and not "returns the target path" —
        // the reader refuses. Following a link is how content from outside the
        // checkout enters the corpus.
        await Assert.ThrowsAsync<InvalidOperationException>(() => snapshot.ReadAsync(entry));

        // …and the provider skips it without ever asking.
        var provider = new RepositorySnapshotSourceProvider(reader);
        var keys = new List<string>();
        await foreach (var source in provider.EnumerateAsync(
            new RagSourceRequest(_root, await reader.ResolveRevisionAsync(_root))))
        {
            keys.Add(source.SourceKey);
        }
        Assert.DoesNotContain("docs/escape.md", keys);
    }

    [SkippableFact]
    public async Task The_Reader_Returns_Committed_Bytes_For_Every_Entry_It_Lists()
    {
        Skip.IfNot(_available, "git is not available.");

        // Exercises the `cat-file --batch` framing over many objects: a header
        // parsed one byte short, or a trailing newline left unread, desynchronises
        // the stream and every subsequent blob comes back as somebody else's.
        for (var i = 0; i < 40; i++)
        {
            Write($"docs/file-{i:D2}.md", $"# File {i}\n\n{new string('x', i * 37)}\ncontenuto {i}\n");
        }
        Git("add", "-A");
        Git("commit", "-m", "many");

        var reader = new GitRepositorySnapshotReader();
        await using var snapshot = await reader.OpenAsync(
            _root, await reader.ResolveRevisionAsync(_root));

        Assert.Equal(40, snapshot.Entries.Count);
        foreach (var entry in snapshot.Entries)
        {
            var text = System.Text.Encoding.UTF8.GetString(await snapshot.ReadAsync(entry));
            var index = int.Parse(entry.Path[^5..^3]);
            Assert.Contains($"contenuto {index}\n", text, StringComparison.Ordinal);
            Assert.EndsWith("\n", text, StringComparison.Ordinal);
        }
    }

    [SkippableFact]
    public async Task Entries_Carry_The_Blob_Size_Git_Reported()
    {
        Skip.IfNot(_available, "git is not available.");

        // The size is what lets an oversized object be refused before it is
        // allocated, so it has to be REAL. `ls-tree -l` right-aligns it in a
        // padded column and prints `-` for anything that is not a blob, which is
        // exactly the kind of format a fake fixture gets wrong in the direction
        // that looks fine.
        var small = "# A\n\nun documento breve.\n";
        var larger = "# B\n\n" + new string('x', 5000) + "\n";
        Write("docs/small.md", small);
        Write("docs/larger.md", larger);
        Git("add", "-A");
        Git("commit", "-m", "sizes");

        var reader = new GitRepositorySnapshotReader();
        await using var snapshot = await reader.OpenAsync(
            _root, await reader.ResolveRevisionAsync(_root));

        var smallEntry = snapshot.Entries.Single(e => e.Path == "docs/small.md");
        var largerEntry = snapshot.Entries.Single(e => e.Path == "docs/larger.md");

        Assert.Equal(System.Text.Encoding.UTF8.GetByteCount(small), smallEntry.Size);
        Assert.Equal(System.Text.Encoding.UTF8.GetByteCount(larger), largerEntry.Size);

        // …and it agrees with what reading them actually produces, which is the
        // property the bound depends on.
        Assert.Equal(smallEntry.Size, (await snapshot.ReadAsync(smallEntry)).LongLength);
        Assert.Equal(largerEntry.Size, (await snapshot.ReadAsync(largerEntry)).LongLength);
    }

    [SkippableFact]
    public async Task A_Symlink_Entry_Carries_A_Size_And_Is_Still_Never_Read()
    {
        Skip.IfNot(_available, "git is not available.");
        Skip.If(OperatingSystem.IsWindows(), "Symlink creation needs privileges on Windows.");

        // A link's blob is its target path, so `ls-tree -l` reports a perfectly
        // ordinary small size for it. Passing the size gate must not be mistaken
        // for being eligible.
        Write("docs/guide.md", "# Guida\n\nun documento vero\n");
        try
        {
            File.CreateSymbolicLink(Path.Combine(_root, "docs", "escape.md"), "/etc/passwd");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Skip.If(true, "symlinks are not creatable here.");
            return;
        }
        Git("add", "-A");
        Git("commit", "-m", "link");

        var reader = new GitRepositorySnapshotReader();
        await using var snapshot = await reader.OpenAsync(
            _root, await reader.ResolveRevisionAsync(_root));

        var link = snapshot.Entries.Single(e => e.Path == "docs/escape.md");
        Assert.True(link.IsSymbolicLink);
        Assert.Equal("/etc/passwd".Length, link.Size);
        Assert.True(RepositorySourcePolicy.CheckSize(link.Size).IsEligible);
        await Assert.ThrowsAsync<InvalidOperationException>(() => snapshot.ReadAsync(link));
    }

    // ---- fixture -------------------------------------------------------------

    private void Write(string relativePath, string content)
    {
        var full = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private string? Git(params string[] arguments)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = Directory.Exists(_root) ? _root : Path.GetTempPath(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(info);
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEnd();
            _ = process.StandardError.ReadToEnd();
            process.WaitForExit(30_000);
            return process.ExitCode == 0 ? output : null;
        }
        catch (Exception ex) when (ex is SystemException)
        {
            return null;
        }
    }

    /// `.git` objects are read-only on some platforms, so a plain recursive
    /// delete fails on cleanup.
    private static void DeleteTree(string root)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            try { File.SetAttributes(file, FileAttributes.Normal); } catch { /* best effort */ }
        }
        Directory.Delete(root, recursive: true);
    }
}
