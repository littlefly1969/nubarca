using NubArca.Api.Ai.Documents;

namespace NubArca.Api.Ai.DocumentVisual;

/// One rendered surface, in memory, on its way to an encoder.
///
/// `Png` is the whole reason this record is short-lived: the caller embeds it
/// and drops it, and nothing between here and the vector table has a place to
/// put it. It is not written to disk, not cached, not returned through an API,
/// and not logged.
public sealed record DocumentVisualUnitArtifact(
    int Ordinal,
    string RenderKind,
    byte[] Png,
    int Width,
    int Height,
    /// The document's own coordinates, where the renderer can state them
    /// EXACTLY. Null everywhere they would be invented — see
    /// DocumentVisualUnit for why an office page ordinal is not provenance.
    DocumentLocator? SourceLocator = null,
    int? SourcePage = null);

/// Everything one renderer produced from one document: an ordered, complete
/// sequence of units, or nothing at all.
public sealed record DocumentVisualRenderArtifact(
    string RenderProfileKey,
    IReadOnlyList<DocumentVisualUnitArtifact> Units);

/// A renderer's answer.
///
/// Two kinds of refusal, kept apart the way `DocumentExtractionOutcome` keeps
/// them apart: `Rejected` is a verdict about the document that the same bytes
/// will earn again, and `Unavailable` is a statement about this installation
/// that the next pass may not.
public sealed record DocumentVisualRenderOutcome(
    DocumentVisualRenderArtifact? Artifact,
    string? Reason,
    bool IsPermanent)
{
    public static DocumentVisualRenderOutcome Rendered(DocumentVisualRenderArtifact artifact)
        => new(artifact, null, false);

    public static DocumentVisualRenderOutcome Rejected(string reason) => new(null, reason, true);

    public static DocumentVisualRenderOutcome Unavailable(string reason) => new(null, reason, false);

    public bool Ok => Artifact is not null;
}

/// Whether a renderer can run here, right now.
public sealed record DocumentVisualRendererReadiness(bool Ready, string? Reason)
{
    public static readonly DocumentVisualRendererReadiness Available = new(true, null);

    public static DocumentVisualRendererReadiness NotReady(string reason) => new(false, reason);
}

/// What a renderer is given.
///
/// Notice the absences, which are the same ones `DocumentExtractionRequest`
/// makes and matter more here because rendering an Office document means
/// running a layout engine over hostile input. No owner id: a component that
/// cannot identify a person cannot leak one. No storage key and no path: the
/// bytes arrive as bytes, so there is nothing for a renderer bug — or a
/// document that asks to follow an external relationship — to turn into a
/// filesystem read. No database, no HTTP client, no credentials.
///
/// `FileName` is absent too, unlike extraction, and deliberately: no renderer
/// here needs the extension to disambiguate, the format is already decided, and
/// an original filename is the one piece of owner-authored text most likely to
/// end up in a temp path or a subprocess argument.
public sealed record DocumentVisualRenderRequest(
    ReadOnlyMemory<byte> Bytes,
    DocumentFormatKind Format,
    DocumentVisualOptions Options);

/// ONE WAY OF DRAWING ONE FAMILY OF DOCUMENT.
///
/// The seam exists so that adding a format, or replacing LibreOffice with
/// something better, does not touch owner authorization, the visual index
/// lifecycle, retrieval or Assistant policy.
///
/// What an implementation may NOT do is the interesting half:
///
///  - it does not write any database row;
///  - it does not embed anything, query retrieval, or reach the Assistant;
///  - it does not make authorization decisions — it is handed bytes only after
///    live owner eligibility and the source-size bound have both passed;
///  - it does not reach the network, for any reason, including an external
///    relationship the document asks it to follow;
///  - it does not execute anything the document contains.
///
/// Drawing a document means laying out visible document information. It never
/// means executing document behaviour.
public interface IDocumentVisualRenderer
{
    /// The render identity this renderer's output is recorded under. Two
    /// renderers must not share one: changing how spreadsheets are laid out
    /// should not invalidate every PDF in every library.
    string RenderProfileKey { get; }

    /// Which formats this renderer claims.
    IReadOnlyCollection<DocumentFormatKind> Formats { get; }

    DocumentVisualRendererReadiness CheckReadiness();

    Task<DocumentVisualRenderOutcome> RenderAsync(
        DocumentVisualRenderRequest request, CancellationToken cancellationToken = default);
}
