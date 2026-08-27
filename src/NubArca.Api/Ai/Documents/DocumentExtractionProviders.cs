namespace NubArca.Api.Ai.Documents;

/// Which parser reads which family, resolved by format and nothing else.
///
/// A dictionary rather than a switch inside the indexer, because the switch is
/// where the formats accumulate: the indexer's job is orchestration — decide the
/// file is eligible, bound the read, probe, extract, canonicalize, chunk — and a
/// class that also walks DOCX XML, decodes spreadsheet cells, parses PDF text
/// and speaks a subprocess protocol has stopped being that.
///
/// A format with no registered provider resolves to null rather than throwing.
/// That is the honest shape of an installation where a parser is compiled out or
/// not yet written: the file is skipped with a reason, not a crash.
public sealed class DocumentExtractionProviders
{
    private readonly IReadOnlyDictionary<DocumentFormatKind, IDocumentExtractionProvider> _byFormat;

    public DocumentExtractionProviders(IEnumerable<IDocumentExtractionProvider> providers)
    {
        var map = new Dictionary<DocumentFormatKind, IDocumentExtractionProvider>();
        foreach (var provider in providers)
        {
            // Last registration wins, deliberately: it lets a host replace one
            // parser without removing the default registration, and two
            // providers claiming one format is a wiring mistake rather than a
            // runtime condition worth an exception on every request.
            map[provider.Format] = provider;
        }
        _byFormat = map;
    }

    public IDocumentExtractionProvider? For(DocumentFormatKind format)
        => _byFormat.TryGetValue(format, out var provider) ? provider : null;

    public IReadOnlyCollection<DocumentFormatKind> SupportedFormats => _byFormat.Keys.ToArray();
}
