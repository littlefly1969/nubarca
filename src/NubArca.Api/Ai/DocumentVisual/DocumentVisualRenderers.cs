using NubArca.Api.Ai.Documents;

namespace NubArca.Api.Ai.DocumentVisual;

/// Which renderer draws which family, resolved by format and nothing else.
///
/// A registry rather than a switch inside the indexer, for the reason the
/// extraction side already learned: the indexer's job is orchestration, and a
/// class that also rasterises PDFs, lays out text and speaks a subprocess
/// protocol has stopped being that.
///
/// A format with no registered renderer resolves to null rather than throwing.
/// That is the honest shape of an installation where the Office renderer worker
/// is not deployed: DOCX is skipped with a reason and PDFs still work, instead
/// of the whole visual capability refusing to start.
public sealed class DocumentVisualRenderers
{
    private readonly IReadOnlyDictionary<DocumentFormatKind, IDocumentVisualRenderer> _byFormat;

    public DocumentVisualRenderers(IEnumerable<IDocumentVisualRenderer> renderers)
    {
        var map = new Dictionary<DocumentFormatKind, IDocumentVisualRenderer>();
        foreach (var renderer in renderers)
        {
            foreach (var format in renderer.Formats)
            {
                // Last registration wins, deliberately: a host can replace one
                // renderer without removing the default registration.
                map[format] = renderer;
            }
        }
        _byFormat = map;
    }

    public IDocumentVisualRenderer? For(DocumentFormatKind format)
        => _byFormat.TryGetValue(format, out var renderer) ? renderer : null;

    public IReadOnlyCollection<DocumentFormatKind> SupportedFormats => _byFormat.Keys.ToArray();

    /// THE ACTIVE RENDER IDENTITIES, as retrieval's eligibility clause reads
    /// them.
    ///
    /// A visual index whose `RenderProfileKey` is not in this set is not
    /// queryable, which is the invalidation mechanism for a renderer upgrade: a
    /// new LibreOffice bumps `libreoffice-office-pdf-v1` to `-v2`, and every
    /// index drawn by the old one becomes unreachable at once rather than
    /// contributing pixels laid out by an engine this installation no longer
    /// runs.
    ///
    /// Derived from what is REGISTERED, not from what is ready. Readiness is an
    /// environment state that flaps — a worker restarting must not make an
    /// owner's existing DOCX pages vanish from search and come back.
    public IReadOnlyCollection<string> ActiveRenderProfileKeys
        => _byFormat.Values.Select(r => r.RenderProfileKey).Distinct(StringComparer.Ordinal).ToArray();
}
