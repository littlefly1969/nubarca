using NubArca.Api.Rag;
using NubArca.Api.Rag.Domains;
using NubArca.Api.Rag.Evaluation;
using NubArca.Api.Rag.ProductHelp;
using NubArca.Api.Rag.Sources;
using Xunit;

namespace NubArca.Api.Tests.Rag;

// A benchmark must not be able to find its own questions.
//
// This was not a hypothetical. Once the repository indexed itself, the best
// lexical match for a conceptual golden question became `RagGoldenSet.cs` — the
// file holding that exact sentence — and repository MRR fell from 0.583 to
// 0.395 while nothing about retrieval had got worse. Excluding the evaluation
// directory fixed that instance; this test is what stops the next one, because
// the natural way to document a benchmark is to paste its prompt into a
// document that is itself part of the corpus.
//
// SCOPE IS PER DOMAIN, and that is the whole subtlety. A query is contaminated
// only by the corpus it is MEASURED against:
//
//   * repository queries are measured against every repository-eligible file,
//     so they are guarded against all of them;
//   * Product Help queries are measured against the manifest only, so the
//     Italian canary living in a test file or in an internal design document is
//     not contamination — those files are not in that domain's corpus.
//
// Guarding Product Help questions against the whole repository would be the
// wrong rule and an impossible one: the test that asserts the canary has to
// contain the canary.
public sealed class RagContaminationTests
{
    [SkippableFact]
    public void Guarded_Repository_Queries_Do_Not_Appear_In_Any_Eligible_Source()
    {
        Skip.IfNot(GitIsAvailable(), "git is not available to read the tracked snapshot.");

        var root = RagTestHarness.RepositoryRoot();
        var guarded = RagGoldenSet.Repository.Where(c => c.Conceptual).ToList();
        Assert.NotEmpty(guarded);

        var offences = new List<string>();
        foreach (var (path, text) in EligibleRepositorySources(root))
        {
            var normalized = Normalize(text);
            foreach (var golden in guarded)
            {
                if (!normalized.Contains(Normalize(golden.Query), StringComparison.Ordinal)) continue;
                offences.Add($"{path} repeats: \"{golden.Query}\"");
            }
        }

        Assert.True(offences.Count == 0,
            "A benchmark question appears verbatim in the corpus it is measured against, so retrieval "
            + "can find the question instead of the answer. Describe the benchmark without pasting its "
            + "prompt — do not widen the corpus exclusions to make this pass.\n"
            + string.Join("\n", offences));
    }

    [Fact]
    public void Guarded_Product_Help_Queries_Do_Not_Appear_In_Any_Manifest_Source()
    {
        var root = RagTestHarness.RepositoryRoot();
        var guarded = RagGoldenSet.ProductHelp.Where(c => c.Conceptual).ToList();
        Assert.NotEmpty(guarded);

        var offences = new List<string>();
        foreach (var source in ProductHelpSources.Manifest)
        {
            var full = Path.Combine(root, source.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full)) continue;

            var normalized = Normalize(File.ReadAllText(full));
            foreach (var golden in guarded)
            {
                if (!normalized.Contains(Normalize(golden.Query), StringComparison.Ordinal)) continue;
                offences.Add($"{source.Path} repeats: \"{golden.Query}\"");
            }
        }

        Assert.True(offences.Count == 0,
            "An approved Product Help document repeats a benchmark question verbatim, so the corpus "
            + "can answer the benchmark with a copy of its own prompt.\n" + string.Join("\n", offences));
    }

    [Fact]
    public void Exact_Identifier_Queries_Are_Deliberately_Not_Guarded()
    {
        // The other half of the rule, asserted so nobody "fixes" it by guarding
        // everything. `PhotoVectorIndexService` is SUPPOSED to occur in the file
        // that should win — that is the behaviour the case tests, and guarding
        // it would forbid the expected answer from existing.
        var unguarded = RagGoldenSet.Repository.Where(c => !c.Conceptual).Select(c => c.Query).ToList();

        Assert.Contains("PhotoVectorIndexService", unguarded);
        Assert.Contains("RevisionMismatch_DoesNotCallModel", unguarded);
        Assert.All(unguarded, q => Assert.DoesNotContain('?', q));
    }

    [SkippableFact]
    public void The_Evaluation_Implementation_Is_Not_Repository_Eligible()
    {
        Skip.IfNot(GitIsAvailable(), "git is not available to read the tracked snapshot.");

        var root = RagTestHarness.RepositoryRoot();

        Assert.DoesNotContain(
            EligibleRepositorySources(root).Select(s => s.Path),
            p => p.StartsWith("src/NubArca.Api/Rag/Evaluation/", StringComparison.Ordinal));
    }

    [SkippableFact]
    public void An_Untracked_File_Is_Not_Part_Of_The_Guarded_Corpus()
    {
        // The reason this guard reads the SNAPSHOT rather than the working tree.
        // A developer's stray file is not in any installation's corpus, so it
        // must not be able to fail this test — and, symmetrically, must not be
        // able to hide contamination by being the thing that was checked.
        Skip.IfNot(GitIsAvailable(), "git is not available to read the tracked snapshot.");

        var root = RagTestHarness.RepositoryRoot();
        var stray = Path.Combine(root, $"untracked-contamination-probe-{Guid.NewGuid():N}.md");
        var guarded = RagGoldenSet.Repository.First(c => c.Conceptual).Query;

        try
        {
            File.WriteAllText(stray, $"# Scratch\n\n{guarded}\n");

            var paths = EligibleRepositorySources(root).Select(s => s.Path).ToList();

            Assert.DoesNotContain(paths, p => p.Contains("untracked-contamination-probe", StringComparison.Ordinal));
        }
        finally
        {
            try { File.Delete(stray); } catch (IOException) { }
        }
    }

    [Fact]
    public void Every_Guarded_Query_Is_A_Prose_Question()
    {
        // A sanity check on the SET rather than on the corpus: the guard is
        // meaningful only if what it guards is actually prose. An identifier
        // that drifted into the guarded list would silently start forbidding
        // its own expected answer.
        foreach (var golden in RagGoldenSet.Repository.Concat(RagGoldenSet.ProductHelp)
                     .Where(c => c.Conceptual))
        {
            Assert.Contains(' ', golden.Query);
            Assert.True(golden.Query.Split(' ').Length >= 3, golden.Query);
        }
    }

    // ---- corpus enumeration --------------------------------------------------

    /// Every tracked-and-eligible file, decided by the SAME policy the indexer
    /// uses. A private copy of the rules here would drift, and the drift would
    /// always be in the direction of the test passing.
    /// The TRACKED SNAPSHOT, read exactly the way the indexer reads it.
    ///
    /// The predecessor walked the working tree. That enumerated files the corpus
    /// does not contain — an untracked scratch file, a half-finished note, a
    /// local experiment — so a developer with a stray `.cs` in their checkout
    /// could fail this guard for something no installation would ever index, and
    /// the corpus this benchmark is measured against could differ from the one
    /// the benchmark is guarded against. A guard measuring a different corpus
    /// than the evaluation is a guard that is right by coincidence.
    ///
    /// `git ls-tree` at HEAD, the mode check, the path policy and the size
    /// bound, in that order, are what `RepositorySnapshotSourceProvider` does.
    /// Reusing the reader rather than re-implementing it is the point: a private
    /// copy of the rules would drift, and the drift would always be in the
    /// direction of the test passing.
    private static IReadOnlyList<(string Path, string Text)> EligibleRepositorySources(string root)
    {
        var reader = new GitRepositorySnapshotReader();
        var revision = reader.ResolveRevisionAsync(root).GetAwaiter().GetResult();
        using var snapshotHandle = new SnapshotHandle(
            reader.OpenAsync(root, revision).GetAwaiter().GetResult());
        var snapshot = snapshotHandle.Snapshot;

        var sources = new List<(string, string)>();
        foreach (var entry in snapshot.Entries)
        {
            if (entry.IsSymbolicLink || entry.IsSubmodule) continue;
            if (!RepositorySourcePolicy.CheckPath(entry.Path).IsEligible) continue;
            if (!RepositorySourcePolicy.CheckSize(entry.Size).IsEligible) continue;

            byte[] bytes;
            try
            {
                bytes = snapshot.ReadAsync(entry).GetAwaiter().GetResult();
            }
            catch (RepositorySnapshotUnavailableException)
            {
                continue;
            }

            if (!RepositorySourcePolicy.CheckContent(entry.Path, bytes).IsEligible) continue;
            sources.Add((entry.Path, System.Text.Encoding.UTF8.GetString(bytes)));
        }
        return sources;
    }

    /// `IAsyncDisposable` in a synchronous test method, without an async void.
    private sealed class SnapshotHandle : IDisposable
    {
        public SnapshotHandle(IRepositorySnapshot snapshot) => Snapshot = snapshot;

        public IRepositorySnapshot Snapshot { get; }

        public void Dispose() => Snapshot.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private static bool GitIsAvailable()
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("git")
            {
                ArgumentList = { "--version" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null) return false;
            process.WaitForExit(10_000);
            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is SystemException)
        {
            return false;
        }
    }

    private static string Relative(string root, string file)
        => Path.GetRelativePath(root, file).Replace('\\', '/');

    /// Case- and whitespace-insensitive, so reflowing a paragraph or changing a
    /// quotation's capitalisation cannot smuggle a question back in.
    private static string Normalize(string text)
        => string.Join(' ', text.ToLowerInvariant().Split(
            (char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
