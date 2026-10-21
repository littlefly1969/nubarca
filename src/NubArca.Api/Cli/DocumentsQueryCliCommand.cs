using System.Diagnostics;
using NubArca.Api.Ai.Documents;

namespace NubArca.Api.Cli;

/// `documents query --owner <id> [--limit N] [--expect name[,name]] "<question>"`
///
/// Runs the SAME owner document search the Documenti page runs, and prints how
/// it ranked: status, retrieval modes, and per document and passage the ranks
/// and the typed locator. It never calls a generative model.
///
/// IT NEVER PRINTS a document name, a heading, a locator label (a slide title or
/// sheet name is owner content), an excerpt, the question, or an id. A document
/// is printed as its rank. `--expect` takes the names the operator EXPECTS and
/// compares them in process, printing only whether each result matched and the
/// first matching rank — so a benchmark can be scored without a single
/// filename reaching the terminal.
internal static class DocumentsQueryCliCommand
{
    public static async Task<int> RunAsync(
        string[] args, IServiceProvider sp, TextWriter stdout, TextWriter stderr)
    {
        Guid owner = Guid.Empty;
        int? limit = null;
        string[]? expected = null;
        var words = new List<string>();

        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--owner" when i + 1 < args.Length:
                    if (!Guid.TryParse(args[++i], out owner)) owner = Guid.Empty;
                    break;
                case "--limit" when i + 1 < args.Length:
                    limit = int.TryParse(args[++i], out var parsed) && parsed > 0 ? parsed : null;
                    break;
                case "--expect" when i + 1 < args.Length:
                    expected = args[++i].Split(
                        ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    break;
                default:
                    words.Add(args[i]);
                    break;
            }
        }

        // `--owner` is REQUIRED, as for every `documents` verb: a command that
        // defaulted to anybody is one typo from somebody else's library.
        if (owner == Guid.Empty)
        {
            stderr.WriteLine("documents query: --owner <user-id> is required.");
            return 2;
        }
        var question = string.Join(' ', words).Trim();
        if (question.Length == 0)
        {
            stderr.WriteLine("usage: documents query --owner <user-id> [--limit N] "
                + "[--expect name[,name]] \"<question>\"");
            return 2;
        }

        var watch = Stopwatch.StartNew();
        var result = await sp.GetRequiredService<OwnerDocumentSearchService>()
            .SearchAsync(owner, new OwnerDocumentSearchRequest(question, limit));
        watch.Stop();

        Print(stdout, result, expected, watch.ElapsedMilliseconds);
        return 0;
    }

    internal static void Print(
        TextWriter stdout, OwnerDocumentSearchResult result, string[]? expected, long elapsedMs)
    {
        stdout.WriteLine($"status={result.Status}");
        if (result.Reason is not null) stdout.WriteLine($"reason={result.Reason}");
        stdout.WriteLine($"mode={result.TextMode}");
        stdout.WriteLine($"visual_mode={result.VisualMode}");
        stdout.WriteLine($"visual_used={(result.VisualUsed ? "true" : "false")}");
        stdout.WriteLine($"documents={result.Documents.Count}");

        int? firstExpected = null;
        foreach (var doc in result.Documents)
        {
            var match = expected is null
                ? "n/a"
                : expected.Contains(doc.Name, StringComparer.Ordinal) ? "true" : "false";
            if (match == "true") firstExpected ??= doc.Rank;

            stdout.WriteLine(
                $"doc rank={doc.Rank} kind={doc.SourceKind} passages={doc.Passages.Count} expected={match}");
            foreach (var p in doc.Passages)
            {
                stdout.WriteLine(
                    $"  passage rank={p.Rank} lexical_rank={Show(p.LexicalRank)} "
                    + $"vector_rank={Show(p.VectorRank)} page={Show(p.Page)} "
                    + $"locator={p.LocatorKind ?? "-"}:{Show(p.LocatorIndex)} "
                    + $"ordinal={Show(p.Ordinal)} snippet_chars={p.Snippet.Length}");
            }
        }

        if (expected is not null)
        {
            stdout.WriteLine($"expected_first_rank={Show(firstExpected)}");
        }
        stdout.WriteLine($"latency_ms={elapsedMs}");
    }

    private static string Show(int? value) => value?.ToString() ?? "-";
}
