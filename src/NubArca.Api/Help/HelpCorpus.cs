using System.Text.Json.Serialization;

namespace NubArca.Api.Help;

/// One retrievable chunk of PUBLIC product material.
public sealed record HelpCorpusDocument(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("text")] string Text);

/// The bounded public knowledge NubArca is willing to show an external model.
///
/// `Revision` is the source commit the corpus was built from. The running
/// application refuses a corpus whose revision does not match its own, so Help
/// cannot describe a feature the installed release does not have — an operator
/// being told to click something that is not there is worse than no Help.
public sealed record HelpCorpus(
    [property: JsonPropertyName("revision")] string Revision,
    [property: JsonPropertyName("documents")] IReadOnlyList<HelpCorpusDocument> Documents)
{
    public static HelpCorpus Empty { get; } = new(string.Empty, Array.Empty<HelpCorpusDocument>());
}

/// Builds a corpus from a source checkout.
///
/// The eligibility rule is an ALLOWLIST of product documentation paths, not a
/// denylist of secrets. A denylist is a promise to have thought of everything;
/// an allowlist is a statement of what is included, and anything nobody added on
/// purpose — an operator's Compose override, a .env, a build output, a scratch
/// file, a test artifact — is out by construction.
public static class HelpCorpusBuilder
{
    /// Public product material. Everything else in the repository is excluded,
    /// including source, which this slice does not need in order to explain the
    /// product and which would multiply the review surface of what can leave.
    private static readonly string[] AllowedRootFiles =
    {
        "README.md",
        "ARCHITECTURE.md",
    };

    private static readonly string[] AllowedDirectories =
    {
        "docs",
    };

    /// Markdown under an allowed directory is included AUTOMATICALLY; this list
    /// is the set of named exceptions taken back out.
    ///
    /// So a new `docs/**.md` becomes Help knowledge as soon as it is committed,
    /// without anyone opting it in — which is the intended trade for keeping the
    /// product documentation and the assistant's knowledge in step. Adding a
    /// document that describes an INSTALLATION or an internal procedure rather
    /// than the product means adding it here at the same time.
    private static readonly string[] ExcludedNames =
    {
        "current-work.md",   // internal working notes, not user-facing product docs
    };

    private const int MaxChunkCharacters = 4000;

    public static HelpCorpus Build(string sourceRoot, string revision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
        var documents = new List<HelpCorpusDocument>();

        foreach (var relative in EnumerateEligible(sourceRoot))
        {
            var full = System.IO.Path.Combine(sourceRoot, relative);
            string text;
            try
            {
                text = File.ReadAllText(full);
            }
            catch (IOException)
            {
                continue;
            }
            if (string.IsNullOrWhiteSpace(text)) continue;

            var title = FirstHeading(text) ?? System.IO.Path.GetFileNameWithoutExtension(relative);
            var chunks = Chunk(text);
            for (var i = 0; i < chunks.Count; i++)
            {
                documents.Add(new HelpCorpusDocument(
                    Id: chunks.Count == 1 ? relative : $"{relative}#{i + 1}",
                    Title: title,
                    Path: relative,
                    Text: chunks[i]));
            }
        }

        return new HelpCorpus(revision ?? string.Empty, documents);
    }

    /// Public so a test can assert the boundary directly rather than inferring
    /// it from what happened to be indexed.
    public static bool IsEligible(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return false;
        var normalized = relativePath.Replace('\\', '/').TrimStart('/');

        // No traversal, no hidden files or directories (.env, .git, .github).
        if (normalized.Contains("..", StringComparison.Ordinal)) return false;
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return false;
        if (segments.Any(s => s.StartsWith('.'))) return false;
        if (!normalized.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) return false;
        if (ExcludedNames.Contains(segments[^1], StringComparer.OrdinalIgnoreCase)) return false;

        if (segments.Length == 1)
        {
            return AllowedRootFiles.Contains(segments[0], StringComparer.OrdinalIgnoreCase);
        }
        return AllowedDirectories.Contains(segments[0], StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> EnumerateEligible(string sourceRoot)
    {
        var root = System.IO.Path.GetFullPath(sourceRoot);
        foreach (var file in Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories))
        {
            var relative = System.IO.Path.GetRelativePath(root, file).Replace('\\', '/');
            if (IsEligible(relative)) yield return relative;
        }
    }

    private static string? FirstHeading(string text)
        => text.Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.StartsWith("# ", StringComparison.Ordinal))
            ?.TrimStart('#').Trim();

    // Paragraph-aligned chunks: a boundary in the middle of a sentence produces
    // an excerpt that reads as broken to whoever is shown the source.
    private static List<string> Chunk(string text)
    {
        var chunks = new List<string>();
        var current = new System.Text.StringBuilder();
        foreach (var paragraph in text.Replace("\r\n", "\n").Split("\n\n"))
        {
            if (current.Length > 0 && current.Length + paragraph.Length > MaxChunkCharacters)
            {
                chunks.Add(current.ToString().Trim());
                current.Clear();
            }
            current.Append(paragraph).Append("\n\n");
        }
        if (current.Length > 0) chunks.Add(current.ToString().Trim());
        return chunks.Where(c => c.Length > 0).ToList();
    }
}
