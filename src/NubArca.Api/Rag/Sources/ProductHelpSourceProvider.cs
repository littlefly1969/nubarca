using System.Runtime.CompilerServices;
using System.Text.Json;
using NubArca.Api.Rag.Domains;
using NubArca.Api.Rag.ProductHelp;

namespace NubArca.Api.Rag.Sources;

/// The `product-help` domain as a PROJECTION of the same checkout.
///
/// Product Help is not a second ingestion system and does not hold a second copy
/// of anything. It emits the SAME source keys the repository provider does —
/// repository-relative paths — so a document that is both repository knowledge
/// and approved product help ends up as one source row, one set of chunks and
/// one embedding per profile, with two membership rows.
///
/// What it adds is CLASSIFICATION. Slice 1's manifest says which documents may
/// answer a product question and what each one is: its feature, the words people
/// use for that feature in both interface languages, who it is written for, and
/// what kind of answer it gives. That metadata lives on the MEMBERSHIP rather
/// than on the source, because it is this domain's opinion — the same file is
/// just a Markdown document to the repository domain, and giving every C# file
/// an `intent` so the schema looks uniform would be inventing data.
///
/// Unclassified still means NOT A MEMBER — not "a low-priority member". That
/// rule is the reason an operations runbook stopped outranking the guidance
/// somebody asking "how do I use faces?" needs, and it only holds if the
/// manifest remains an allowlist.
public sealed class ProductHelpSourceProvider : IRagSourceProvider
{
    private readonly IRepositorySnapshotReader _reader;

    public ProductHelpSourceProvider(IRepositorySnapshotReader reader)
    {
        _reader = reader;
    }

    public string Domain => RagDomains.ProductHelp;

    /// Manifest entries whose file was not found in the last enumeration. A
    /// rename nobody noticed silently removes knowledge, so it is reported
    /// rather than inferred from a smaller index.
    public IReadOnlyList<string> MissingSources { get; private set; } = Array.Empty<string>();

    public async IAsyncEnumerable<RagSourceDescriptor> EnumerateAsync(
        RagSourceRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // The SAME exact-revision snapshot the repository provider reads. A
        // source shared by both domains has to be the same bytes at the same
        // commit, and it cannot be if one provider reads Git objects and the
        // other reads the working tree.
        var root = Path.GetFullPath(
            await _reader.ResolveRootAsync(Path.GetFullPath(request.RootPath), cancellationToken));
        await using var snapshot = await _reader.OpenAsync(root, request.Revision, cancellationToken);

        var byPath = snapshot.Entries.ToDictionary(e => e.Path, StringComparer.Ordinal);
        var missing = new List<string>();

        // Manifest ORDER, not tree order: the index is deterministic across
        // machines and filesystems, which is what makes a golden retrieval test
        // meaningful.
        foreach (var source in ProductHelpSources.Manifest)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!byPath.TryGetValue(source.Path, out var entry)
                || !RepositorySourcePolicy.CheckGitMode(entry.Mode).IsEligible)
            {
                missing.Add(source.Path);
                continue;
            }

            byte[] bytes;
            try
            {
                bytes = await snapshot.ReadAsync(entry, cancellationToken);
            }
            catch (RepositorySnapshotUnavailableException)
            {
                missing.Add(source.Path);
                continue;
            }

            var text = System.Text.Encoding.UTF8.GetString(bytes).TrimStart('\ufeff');
            if (string.IsNullOrWhiteSpace(text)) { missing.Add(source.Path); continue; }

            var title = MarkdownTitle(text) ?? Path.GetFileNameWithoutExtension(source.Path);

            yield return new RagSourceDescriptor(
                SourceKey: source.Path,
                Path: source.Path,
                Title: title,
                SourceKind: source.SourceKind,
                Revision: snapshot.Revision,
                ContentHash: RagHash.Sha256Hex(bytes),
                Language: source.Language,
                CodeLanguage: RagCodeLanguages.Markdown,
                Text: text,
                Priority: source.Priority,
                DomainMetadata: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [RagMetadataKeys.Feature] = source.Feature,
                    [RagMetadataKeys.Aliases] = JsonSerializer.Serialize(source.Aliases),
                    [RagMetadataKeys.Audience] = source.Audience,
                    [RagMetadataKeys.Intent] = source.Intent,
                    [RagMetadataKeys.SourceKind] = source.SourceKind,
                    [RagMetadataKeys.Language] = source.Language,
                });
        }

        MissingSources = missing;
    }

    private static string? MarkdownTitle(string text)
        => text.Replace("\r\n", "\n").Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.StartsWith("# ", StringComparison.Ordinal))
            ?.TrimStart('#').Trim();
}
