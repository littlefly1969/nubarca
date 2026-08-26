using System.Text;

namespace NubArca.Api.Rag.Chunking;

/// Section-aware chunking for Markdown.
///
/// Slice 1's corpus builder, lifted out so both domains use it. The rule it
/// replaced accumulated paragraphs to 4,000 characters, which produced excerpts
/// spanning three unrelated topics — bad to retrieve, because the sentence that
/// matched is diluted by everything around it, and bad to send, because most of
/// the context budget goes on text nobody asked about.
///
/// Headings become a TRAIL rather than a label: `Deploying › Rollback` is a
/// citation a person can act on, where a file name alone is not.
public static class MarkdownRagChunker
{
    public static IReadOnlyList<RagChunkDraft> Chunk(string text)
    {
        var drafts = new List<RagChunkDraft>();
        var ordinal = 0;
        foreach (var (section, body) in Sections(text))
        {
            foreach (var chunk in ChunkSection(body))
            {
                ordinal++;
                drafts.Add(new RagChunkDraft(
                    ordinal, section, chunk, HeadingSymbols(section)));
            }
        }
        return drafts;
    }

    /// The first `# ` heading, which is the document's own name far more often
    /// than its file name is.
    public static string? FirstHeading(string text)
        => text.Replace("\r\n", "\n").Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.StartsWith("# ", StringComparison.Ordinal))
            ?.TrimStart('#').Trim();

    private static IReadOnlyList<string> HeadingSymbols(string section)
        => string.IsNullOrEmpty(section)
            ? Array.Empty<string>()
            : section.Split('›', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// Split on ATX headings, carrying the heading trail. Headings inside a
    /// fenced code block are text, not structure — a shell comment starting with
    /// `#` is not a section.
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

    /// Paragraph-aligned chunks inside one section.
    ///
    /// A boundary in the middle of a sentence produces an excerpt that reads as
    /// broken to whoever is shown the source, so paragraphs are the unit and
    /// sentences are the fallback for the one paragraph too long to keep whole.
    private static List<string> ChunkSection(string sectionBody)
    {
        var chunks = new List<string>();
        var current = new StringBuilder();

        foreach (var paragraph in Paragraphs(sectionBody))
        {
            if (paragraph.Length > RagChunkSizes.HardCharacters)
            {
                if (current.Length > 0) { chunks.Add(current.ToString().Trim()); current.Clear(); }
                chunks.AddRange(SplitLongParagraph(paragraph));
                continue;
            }
            if (current.Length > 0 && current.Length + paragraph.Length > RagChunkSizes.MaximumCharacters)
            {
                chunks.Add(current.ToString().Trim());
                current.Clear();
            }
            if (current.Length > 0) current.Append("\n\n");
            current.Append(paragraph);
            if (current.Length >= RagChunkSizes.TargetCharacters)
            {
                chunks.Add(current.ToString().Trim());
                current.Clear();
            }
        }
        if (current.Length > 0) chunks.Add(current.ToString().Trim());

        // A trailing fragment is folded back rather than shipped as evidence
        // that says nothing on its own.
        if (chunks.Count > 1
            && chunks[^1].Length < RagChunkSizes.MinimumCharacters
            && chunks[^2].Length + chunks[^1].Length <= RagChunkSizes.HardCharacters)
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
            if (current.Length > 0 && current.Length + sentence.Length > RagChunkSizes.TargetCharacters)
            {
                yield return current.ToString().Trim();
                current.Clear();
            }
            current.Append(sentence);
            // A single sentence past the hard bound is a code block or a table;
            // cut it rather than emit an unbounded chunk.
            while (current.Length > RagChunkSizes.HardCharacters)
            {
                yield return current.ToString(0, RagChunkSizes.HardCharacters).Trim();
                current.Remove(0, RagChunkSizes.HardCharacters);
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
