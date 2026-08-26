using System.Text;

namespace NubArca.Api.Rag.ProductHelp;

/// Builds the `product-help` corpus from a source checkout, at image-build time.
///
/// Two things changed from the corpus this replaces, and both are about answer
/// quality rather than about the boundary:
///
///  1. Eligibility comes from ProductHelpSources.Manifest, so a runbook no
///     longer competes with a user guide merely by existing.
///  2. Chunks follow SECTIONS. The predecessor accumulated paragraphs to 4,000
///     characters, which produced excerpts spanning three unrelated topics —
///     bad to retrieve (the matching sentence is diluted by everything around
///     it) and bad to send (most of the context budget is spent on text nobody
///     asked about).
///
/// The boundary itself is unchanged: build-time, public-only, revision-pinned,
/// no runtime repository access.
public static class ProductHelpCorpusBuilder
{
    /// Sections shorter than this are merged forward into the next one: a
    /// heading followed by one sentence is a fragment, not evidence.
    private const int MinChunkCharacters = 400;

    /// The size evidence should normally be — enough to be understandable on its
    /// own, small enough that six of them fit a context budget with room left.
    private const int TargetChunkCharacters = 1200;

    /// A chunk may exceed the target to avoid splitting a paragraph, up to here.
    private const int MaxChunkCharacters = 1800;

    /// And never past here. One indivisible paragraph longer than this is split
    /// at sentence boundaries rather than kept whole.
    private const int HardChunkCharacters = 3000;

    public static ProductHelpCorpus Build(string sourceRoot, string revision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
        var documents = new List<ProductHelpDocument>();

        // Manifest ORDER, not directory order: the corpus is deterministic
        // across machines and filesystems, which is what makes a golden
        // retrieval test meaningful.
        foreach (var source in ProductHelpSources.Manifest)
        {
            var full = Path.Combine(sourceRoot, source.Path.Replace('/', Path.DirectorySeparatorChar));
            string text;
            try
            {
                if (!File.Exists(full)) continue;
                text = File.ReadAllText(full);
            }
            catch (IOException)
            {
                continue;
            }
            if (string.IsNullOrWhiteSpace(text)) continue;

            var title = FirstHeading(text) ?? Path.GetFileNameWithoutExtension(source.Path);
            var index = 0;
            foreach (var (section, body) in Sections(text))
            {
                foreach (var chunk in Chunk(body))
                {
                    index++;
                    documents.Add(new ProductHelpDocument(
                        Id: $"{source.Path}#{index}",
                        Path: source.Path,
                        Title: title,
                        Section: section,
                        Text: chunk,
                        Feature: source.Feature,
                        Intent: source.Intent,
                        Audience: source.Audience,
                        Language: source.Language,
                        SourceKind: source.SourceKind,
                        Aliases: source.Aliases,
                        Priority: source.Priority));
                }
            }
        }

        return new ProductHelpCorpus(
            RagDomainKey.ProductHelp.Value, revision ?? string.Empty, documents);
    }

    /// Which paths may be indexed at all. Public so a test asserts the boundary
    /// directly rather than inferring it from what happened to be indexed.
    public static bool IsEligible(string relativePath) => ProductHelpSources.IsApproved(relativePath);

    private static string? FirstHeading(string text)
        => text.Replace("\r\n", "\n").Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.StartsWith("# ", StringComparison.Ordinal))
            ?.TrimStart('#').Trim();

    /// Split on ATX headings, carrying the heading trail so a chunk knows where
    /// in the document it came from — `Deploying › Rollback` is a citation a
    /// person can act on, where a file name alone is not.
    ///
    /// Headings inside a fenced code block are text, not structure.
    private static IEnumerable<(string Section, string Body)> Sections(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var trail = new List<string>();
        var body = new StringBuilder();
        var current = string.Empty;
        var inFence = false;

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal)) inFence = !inFence;

            var level = inFence ? 0 : HeadingLevel(trimmed);
            if (level is >= 1 and <= 4)
            {
                if (body.Length > 0)
                {
                    yield return (current, body.ToString());
                    body.Clear();
                }
                var heading = trimmed.TrimStart('#').Trim();
                while (trail.Count >= level) trail.RemoveAt(trail.Count - 1);
                while (trail.Count < level - 1) trail.Add(string.Empty);
                trail.Add(heading);
                current = string.Join(" › ", trail.Where(t => t.Length > 0));
                continue;
            }
            body.Append(line).Append('\n');
        }
        if (body.Length > 0) yield return (current, body.ToString());
    }

    private static int HeadingLevel(string trimmed)
    {
        var hashes = 0;
        while (hashes < trimmed.Length && trimmed[hashes] == '#') hashes++;
        if (hashes == 0 || hashes >= trimmed.Length) return 0;
        return trimmed[hashes] == ' ' ? hashes : 0;
    }

    /// Paragraph-aligned chunks inside one section, sized for retrieval.
    ///
    /// A boundary in the middle of a sentence produces an excerpt that reads as
    /// broken to whoever is shown the source, so paragraphs are the unit and
    /// sentences are the fallback for the one paragraph that is too long to keep.
    private static List<string> Chunk(string sectionBody)
    {
        var chunks = new List<string>();
        var current = new StringBuilder();

        foreach (var paragraph in Paragraphs(sectionBody))
        {
            if (paragraph.Length > HardChunkCharacters)
            {
                if (current.Length > 0) { chunks.Add(current.ToString().Trim()); current.Clear(); }
                chunks.AddRange(SplitLongParagraph(paragraph));
                continue;
            }
            if (current.Length > 0 && current.Length + paragraph.Length > MaxChunkCharacters)
            {
                chunks.Add(current.ToString().Trim());
                current.Clear();
            }
            if (current.Length > 0) current.Append("\n\n");
            current.Append(paragraph);
            if (current.Length >= TargetChunkCharacters)
            {
                chunks.Add(current.ToString().Trim());
                current.Clear();
            }
        }
        if (current.Length > 0) chunks.Add(current.ToString().Trim());

        // A trailing fragment is folded back into the chunk before it rather
        // than shipped as evidence that says nothing on its own.
        if (chunks.Count > 1
            && chunks[^1].Length < MinChunkCharacters
            && chunks[^2].Length + chunks[^1].Length <= HardChunkCharacters)
        {
            chunks[^2] = $"{chunks[^2]}\n\n{chunks[^1]}";
            chunks.RemoveAt(chunks.Count - 1);
        }
        return chunks.Where(c => c.Trim().Length > 0).ToList();
    }

    /// Paragraphs, with fenced code blocks kept whole: a split inside a fence
    /// produces two chunks that are each syntactically nonsense.
    private static IEnumerable<string> Paragraphs(string body)
    {
        var buffer = new StringBuilder();
        var inFence = false;
        foreach (var line in body.Split('\n'))
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal)) inFence = !inFence;
            if (!inFence && line.Trim().Length == 0)
            {
                if (buffer.Length > 0) { yield return buffer.ToString().Trim(); buffer.Clear(); }
                continue;
            }
            buffer.Append(line).Append('\n');
        }
        if (buffer.ToString().Trim().Length > 0) yield return buffer.ToString().Trim();
    }

    private static IEnumerable<string> SplitLongParagraph(string paragraph)
    {
        var current = new StringBuilder();
        foreach (var sentence in SplitSentences(paragraph))
        {
            if (current.Length > 0 && current.Length + sentence.Length > TargetChunkCharacters)
            {
                yield return current.ToString().Trim();
                current.Clear();
            }
            current.Append(sentence);
            // A single sentence longer than the hard bound is a code block or a
            // table; cut it rather than emit an unbounded chunk.
            while (current.Length > HardChunkCharacters)
            {
                yield return current.ToString(0, HardChunkCharacters).Trim();
                current.Remove(0, HardChunkCharacters);
            }
        }
        if (current.ToString().Trim().Length > 0) yield return current.ToString().Trim();
    }

    private static IEnumerable<string> SplitSentences(string text)
    {
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is not ('.' or '!' or '?' or '\n')) continue;
            if (i + 1 < text.Length && !char.IsWhiteSpace(text[i + 1])) continue;
            yield return text[start..(i + 1)];
            start = i + 1;
        }
        if (start < text.Length) yield return text[start..];
    }
}
