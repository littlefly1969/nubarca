using System.Text;
using System.Text.RegularExpressions;

namespace NubArca.Api.Rag.Chunking;

/// Structure-aware chunking for source, configuration and scripts, without a
/// parser.
///
/// The choice here was between adding a real AST dependency per language and
/// writing a deterministic line/region splitter that knows what a declaration
/// LOOKS like. The splitter won, because the job is retrieval rather than
/// refactoring: a chunk boundary that lands one line off produces evidence that
/// is very slightly worse, where five parser dependencies produce a build that
/// breaks when a language adds syntax. If a robust AST for these languages ever
/// arrives inside the repository for another reason, this is a self-contained
/// class to replace.
///
/// Two properties matter more than boundary precision:
///
///  - a DECLARATION starts a chunk, so "where is X declared" retrieves the
///    region that declares it rather than the middle of the method above it;
///  - the comment block ABOVE a declaration stays with it. In this codebase
///    that comment is frequently the best description of what the code is for,
///    and separating them would index the explanation away from the thing it
///    explains.
///
/// Whole-file chunks are never produced. A 700-line service embedded as one
/// vector is one vector's worth of "this file is about several things".
public static partial class SourceCodeRagChunker
{
    /// Overlap, in lines, carried into a chunk that was forced by SIZE rather
    /// than by a declaration. A size-forced boundary is arbitrary, so a little
    /// context is repeated across it; a declaration boundary is meaningful and
    /// gets none.
    private const int SizeBreakOverlapLines = 2;

    public static IReadOnlyList<RagChunkDraft> Chunk(string text, string codeLanguage)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var drafts = new List<RagChunkDraft>();

        var buffer = new List<string>();
        var bufferLength = 0;
        var startLine = 1;
        var currentType = string.Empty;
        var chunkType = string.Empty;
        var chunkMember = string.Empty;
        var symbols = new List<string>();
        var ordinal = 0;

        void Flush(int endLine, bool sizeForced)
        {
            if (buffer.Count == 0) return;
            var body = string.Join("\n", buffer).Trim();
            if (body.Length == 0)
            {
                buffer.Clear();
                bufferLength = 0;
                symbols.Clear();
                return;
            }
            ordinal++;
            drafts.Add(new RagChunkDraft(
                ordinal,
                Heading(chunkType, chunkMember, startLine, endLine),
                body,
                symbols.Distinct(StringComparer.Ordinal).ToList()));

            var carried = sizeForced
                ? buffer.Where(l => l.Trim().Length > 0).TakeLast(SizeBreakOverlapLines).ToList()
                : new List<string>();
            buffer.Clear();
            buffer.AddRange(carried);
            bufferLength = carried.Sum(l => l.Length + 1);
            symbols.Clear();
            startLine = Math.Max(1, endLine - carried.Count + 1);
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var lineNumber = i + 1;
            var declaration = DeclarationOf(line, codeLanguage);

            if (declaration is { } found)
            {
                if (found.IsType) currentType = found.Name;

                // A declaration starts a new chunk once the current one is big
                // enough to stand alone. Below the minimum we keep accumulating,
                // so a file of one-line properties does not become forty chunks.
                if (bufferLength >= RagChunkSizes.MinimumCharacters)
                {
                    var lead = TakeLeadingContext(buffer);
                    Flush(lineNumber - 1 - lead.Count, sizeForced: false);
                    buffer.AddRange(lead);
                    bufferLength += lead.Sum(l => l.Length + 1);
                    startLine = lineNumber - lead.Count;
                    chunkType = currentType;
                    chunkMember = found.IsType ? string.Empty : found.Name;
                }
                else if (buffer.Count == 0)
                {
                    startLine = lineNumber;
                    chunkType = currentType;
                    chunkMember = found.IsType ? string.Empty : found.Name;
                }
                symbols.Add(found.Name);
            }
            else if (buffer.Count == 0)
            {
                startLine = lineNumber;
                chunkType = currentType;
            }

            buffer.Add(line);
            bufferLength += line.Length + 1;

            if (bufferLength >= RagChunkSizes.MaximumCharacters)
            {
                Flush(lineNumber, sizeForced: true);
            }
        }

        Flush(lines.Length, sizeForced: false);

        // A trailing fragment is folded back into the chunk before it, for the
        // same reason it is in prose: on its own it says nothing.
        if (drafts.Count > 1 && drafts[^1].Text.Length < RagChunkSizes.MinimumCharacters)
        {
            var last = drafts[^1];
            var previous = drafts[^2];
            if (previous.Text.Length + last.Text.Length <= RagChunkSizes.HardCharacters)
            {
                drafts[^2] = previous with
                {
                    Text = $"{previous.Text}\n{last.Text}",
                    Symbols = previous.Symbols.Concat(last.Symbols).Distinct(StringComparer.Ordinal).ToList(),
                };
                drafts.RemoveAt(drafts.Count - 1);
            }
        }

        return drafts;
    }

    private static string Heading(string type, string member, int startLine, int endLine)
    {
        var range = $"L{startLine}–L{Math.Max(startLine, endLine)}";
        if (type.Length > 0 && member.Length > 0) return $"{type} › {member} ({range})";
        if (type.Length > 0) return $"{type} ({range})";
        if (member.Length > 0) return $"{member} ({range})";
        return range;
    }

    /// Comment, attribute and blank lines immediately above a declaration, moved
    /// OUT of the finished chunk and into the new one so the explanation travels
    /// with the thing it explains.
    private static List<string> TakeLeadingContext(List<string> buffer)
    {
        var take = 0;
        for (var i = buffer.Count - 1; i >= 0; i--)
        {
            var trimmed = buffer[i].Trim();
            var isContext = trimmed.Length == 0
                            || trimmed.StartsWith("//", StringComparison.Ordinal)
                            || trimmed.StartsWith("///", StringComparison.Ordinal)
                            || trimmed.StartsWith("#", StringComparison.Ordinal)
                            || trimmed.StartsWith("*", StringComparison.Ordinal)
                            || trimmed.StartsWith("/*", StringComparison.Ordinal)
                            || trimmed.StartsWith("[", StringComparison.Ordinal);
            if (!isContext) break;
            take++;
        }
        // Never move the ENTIRE buffer: that would flush an empty chunk and lose
        // a file whose every line is a comment.
        take = Math.Min(take, Math.Max(0, buffer.Count - 1));
        if (take == 0) return new List<string>();

        var lead = buffer.GetRange(buffer.Count - take, take);
        buffer.RemoveRange(buffer.Count - take, take);
        return lead;
    }

    private readonly record struct Declaration(string Name, bool IsType);

    private static Declaration? DeclarationOf(string line, string codeLanguage) => codeLanguage switch
    {
        RagCodeLanguages.CSharp or RagCodeLanguages.Kotlin => CurlyBraceDeclaration(line),
        RagCodeLanguages.TypeScript or RagCodeLanguages.Tsx or RagCodeLanguages.JavaScript
            => ScriptDeclaration(line),
        RagCodeLanguages.Sql => SqlDeclaration(line),
        RagCodeLanguages.Shell => ShellDeclaration(line),
        RagCodeLanguages.Yaml or RagCodeLanguages.Json or RagCodeLanguages.Toml => TopLevelKey(line),
        _ => null,
    };

    private static Declaration? CurlyBraceDeclaration(string line)
    {
        var type = CSharpTypeRegex().Match(line);
        if (type.Success) return new Declaration(type.Groups[1].Value, IsType: true);

        var member = CSharpMemberRegex().Match(line);
        return member.Success ? new Declaration(member.Groups[1].Value, IsType: false) : null;
    }

    private static Declaration? ScriptDeclaration(string line)
    {
        var type = ScriptTypeRegex().Match(line);
        if (type.Success)
        {
            var name = type.Groups[2].Value;
            var isType = type.Groups[1].Value is "class" or "interface" or "type" or "enum";
            return new Declaration(name, isType);
        }
        var arrow = ScriptArrowRegex().Match(line);
        return arrow.Success ? new Declaration(arrow.Groups[1].Value, IsType: false) : null;
    }

    private static Declaration? SqlDeclaration(string line)
    {
        var match = SqlObjectRegex().Match(line);
        return match.Success
            ? new Declaration(match.Groups[3].Value.Trim('"'), IsType: true)
            : null;
    }

    private static Declaration? ShellDeclaration(string line)
    {
        var match = ShellFunctionRegex().Match(line);
        return match.Success ? new Declaration(match.Groups[1].Value, IsType: false) : null;
    }

    private static Declaration? TopLevelKey(string line)
    {
        if (line.Length == 0 || char.IsWhiteSpace(line[0])) return null;
        var match = TopLevelKeyRegex().Match(line);
        return match.Success ? new Declaration(match.Groups[1].Value.Trim('"'), IsType: true) : null;
    }

    [GeneratedRegex(
        @"\b(?:class|interface|record|struct|enum)\s+([A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.CultureInvariant)]
    private static partial Regex CSharpTypeRegex();

    // A member is a modifier-led line that opens a parameter list before any
    // `=` or `;`. Deliberately conservative: missing a declaration costs a
    // slightly worse boundary, where matching a call inside a method body would
    // shatter every method into fragments.
    [GeneratedRegex(
        @"^\s{0,12}(?:\[[^\]]*\]\s*)*(?:public|private|protected|internal|static|sealed|abstract|virtual|override|async|partial|extern|unsafe|new|fun|suspend)\b[^;=(]*?\b([A-Za-z_][A-Za-z0-9_]*)\s*(?:<[^<>()]*>)?\s*\(",
        RegexOptions.CultureInvariant)]
    private static partial Regex CSharpMemberRegex();

    [GeneratedRegex(
        @"^\s{0,8}(?:export\s+)?(?:default\s+)?(?:declare\s+)?(?:abstract\s+)?(?:async\s+)?(function|const|let|var|class|interface|type|enum)\s+([A-Za-z_$][A-Za-z0-9_$]*)",
        RegexOptions.CultureInvariant)]
    private static partial Regex ScriptTypeRegex();

    [GeneratedRegex(
        @"^\s{0,8}([A-Za-z_$][A-Za-z0-9_$]*)\s*[:=]\s*(?:async\s*)?\([^)]*\)\s*(?::[^=]+)?=>",
        RegexOptions.CultureInvariant)]
    private static partial Regex ScriptArrowRegex();

    [GeneratedRegex(
        @"^\s*(CREATE|ALTER|DROP)\s+(?:OR\s+REPLACE\s+)?(TABLE|INDEX|EXTENSION|FUNCTION|VIEW|TYPE|SCHEMA)\s+(?:IF\s+(?:NOT\s+)?EXISTS\s+)?([\w"".]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SqlObjectRegex();

    [GeneratedRegex(
        @"^\s*(?:function\s+)?([A-Za-z_][A-Za-z0-9_-]*)\s*\(\)\s*\{",
        RegexOptions.CultureInvariant)]
    private static partial Regex ShellFunctionRegex();

    [GeneratedRegex(
        @"^(""?[A-Za-z_][A-Za-z0-9_.-]*""?)\s*[:=]",
        RegexOptions.CultureInvariant)]
    private static partial Regex TopLevelKeyRegex();
}
