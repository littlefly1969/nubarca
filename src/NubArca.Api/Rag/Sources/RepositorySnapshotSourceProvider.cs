using System.Runtime.CompilerServices;
using System.Text;
using NubArca.Api.Rag.Domains;

namespace NubArca.Api.Rag.Sources;

/// The `nubarca-repository` domain's source provider: approved tracked files of
/// one checkout, at one revision.
///
/// It answers "what does NubArca's own code say" and nothing else. It does not
/// read `.git`, does not read untracked files, does not follow symlinks out of
/// the tree, and does not reach the network — the repository is a directory on
/// disk at index time and a set of database rows afterwards.
///
/// Every source it emits carries its revision and its content hash, because a
/// repository answer that cannot say which commit it describes is worse than no
/// answer: code moves, and "this is how it works" about a revision nobody is
/// running is a confidently wrong statement.
public sealed class RepositorySnapshotSourceProvider : IRagSourceProvider
{
    private readonly IRepositorySnapshotReader _reader;

    public RepositorySnapshotSourceProvider(IRepositorySnapshotReader reader)
    {
        _reader = reader;
    }

    public string Domain => RagDomains.NubArcaRepository;

    /// Aggregate counts from the last enumeration, for the CLI to report. Not a
    /// per-file log: a run over two thousand files that logs each skip is a run
    /// nobody reads the end of.
    public RepositoryScanTally Tally { get; private set; } = RepositoryScanTally.Empty;

    public async IAsyncEnumerable<RagSourceDescriptor> EnumerateAsync(
        RagSourceRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // The CHECKOUT's root, not whatever directory the caller happened to be
        // in: every path rule below is written against repository-root-relative
        // paths, so resolving it here is what keeps `--source .` from meaning
        // something different depending on where it was typed.
        // The CHECKOUT's root, not whatever directory the caller happened to be
        // in: every path rule below is written against repository-root-relative
        // paths, so resolving it here is what keeps `--source .` from meaning
        // something different depending on where it was typed.
        var root = Path.GetFullPath(
            await _reader.ResolveRootAsync(Path.GetFullPath(request.RootPath), cancellationToken));

        // The bytes come from the COMMIT, not from the working tree. An index
        // that stamps a source with a revision has to have read that revision:
        // otherwise "this is how NubArca works at 943e37b" describes whatever
        // somebody had half-edited on disk when the command ran.
        await using var snapshot = await _reader.OpenAsync(root, request.Revision, cancellationToken);

        var tally = new RepositoryScanTally(snapshot.Entries.Count);

        foreach (var entry in snapshot.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // A symlink's blob is its target path, and following it would import
            // whatever that path names — including something outside the
            // checkout entirely. It is refused by MODE, before anything reads
            // it, and the target is never resolved to decide whether it is safe.
            if (entry.IsSymbolicLink)
            {
                tally.Skip("symlink");
                continue;
            }
            if (entry.IsSubmodule)
            {
                tally.Skip("submodule");
                continue;
            }

            var relativePath = entry.Path;
            var path = RepositorySourcePolicy.CheckPath(relativePath);
            if (!path.IsEligible)
            {
                tally.Skip(path.Reason);
                continue;
            }

            // SIZE BEFORE BYTES. The tree already said how big the blob is, so
            // an oversized one is skipped without allocating it — the same
            // verdict CheckContent would reach, reached before the cost it
            // exists to avoid.
            var size = RepositorySourcePolicy.CheckSize(entry.Size);
            if (!size.IsEligible)
            {
                tally.Skip(size.Reason);
                continue;
            }

            byte[] bytes;
            try
            {
                bytes = await snapshot.ReadAsync(entry, cancellationToken);
            }
            catch (RepositorySnapshotUnavailableException)
            {
                tally.Skip("unreadable");
                continue;
            }

            var content = RepositorySourcePolicy.CheckContent(relativePath, bytes);
            if (!content.IsEligible)
            {
                tally.Skip(content.Reason);
                continue;
            }

            var text = DecodeUtf8(bytes);
            if (text.Trim().Length < RepositorySourcePolicy.MinimumCharacters)
            {
                tally.Skip("too-short");
                continue;
            }

            var codeLanguage = RepositorySourcePolicy.LanguageOf(relativePath) ?? RagCodeLanguages.Text;
            var kind = RepositorySourcePolicy.SourceKindOf(relativePath);

            tally.Include();
            yield return new RagSourceDescriptor(
                SourceKey: relativePath,
                Path: relativePath,
                Title: TitleOf(relativePath, text, codeLanguage),
                SourceKind: kind,
                // The snapshot's own revision, so a descriptor cannot claim one
                // commit while carrying another's bytes.
                Revision: snapshot.Revision,
                ContentHash: RagHash.Sha256Hex(bytes),
                // Prose language is asserted only where a provider knows it.
                // Guessing at the natural language of a C# file would put a
                // wrong `it`/`en` on most of the corpus, and a wrong value is
                // worse than an absent one for a field ranking reads.
                Language: RagLanguages.Unknown,
                CodeLanguage: codeLanguage,
                Text: text,
                Priority: PriorityOf(kind),
                DomainMetadata: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [RagMetadataKeys.SourceKind] = kind,
                });
        }

        Tally = tally;
    }

    /// A file's own name, plus its Markdown title where it has one. The name is
    /// the identifier somebody searches for — `PeoplePage`, `ExternalHelpService`
    /// — so it is indexed in the title field whatever the file is.
    private static string TitleOf(string relativePath, string text, string codeLanguage)
    {
        var name = relativePath[(relativePath.LastIndexOf('/') + 1)..];
        if (codeLanguage != RagCodeLanguages.Markdown) return name;
        var heading = MarkdownHeading(text);
        return string.IsNullOrEmpty(heading) ? name : $"{heading} ({name})";
    }

    private static string MarkdownHeading(string text)
        => text.Replace("\r\n", "\n").Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.StartsWith("# ", StringComparison.Ordinal))
            ?.TrimStart('#').Trim() ?? string.Empty;

    /// The repository domain's own editorial order. Documentation explains
    /// intent, code states behaviour, and generated-ish configuration is mostly
    /// noise — but all of it stays retrievable, because an exact match on a
    /// configuration key is exactly the kind of question this domain exists for.
    private static int PriorityOf(string sourceKind) => sourceKind switch
    {
        RagSourceKinds.Documentation => 70,
        RagSourceKinds.SourceCode => 65,
        RagSourceKinds.Test => 55,
        RagSourceKinds.Migration => 50,
        RagSourceKinds.Script => 45,
        RagSourceKinds.ExampleConfiguration => 45,
        RagSourceKinds.Configuration => 35,
        _ => 40,
    };

    /// Strict UTF-8. A file that is not valid UTF-8 is not text this repository
    /// writes, and decoding it leniently would index replacement characters.
    private static string DecodeUtf8(byte[] bytes)
    {
        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes).TrimStart('﻿');
        }
        catch (DecoderFallbackException)
        {
            return string.Empty;
        }
    }
}

/// Aggregate outcome of one repository scan, with skips grouped by reason.
///
/// Counts and reason CATEGORIES only. A per-file log of what was skipped would
/// be a list of every path in the repository, which is both unreadable and a
/// more detailed disclosure than the summary anybody actually needs.
public sealed class RepositoryScanTally
{
    public RepositoryScanTally(int tracked)
    {
        Tracked = tracked;
    }

    public static RepositoryScanTally Empty { get; } = new(0);

    public int Tracked { get; }
    public int Included { get; private set; }
    public int Skipped { get; private set; }

    public IReadOnlyDictionary<string, int> SkipReasons => _skipReasons;

    private readonly Dictionary<string, int> _skipReasons = new(StringComparer.Ordinal);

    internal void Include() => Included++;

    internal void Skip(string reason)
    {
        Skipped++;
        _skipReasons[reason] = _skipReasons.GetValueOrDefault(reason) + 1;
    }
}
