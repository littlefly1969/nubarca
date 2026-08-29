using System.Buffers.Binary;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using NubArca.Api.Ai.Documents;

namespace NubArca.Api.Ai.DocumentVisual;

/// DOCX, XLSX and PPTX, laid out somewhere else.
///
/// Open XML parsing is not rendering. Slice 4 reads the text of an Office
/// package by walking its XML, which is exactly the right way to extract words
/// and tells you nothing about where anything sits on a page — and where things
/// sit is the whole signal visual retrieval exists to capture. Producing that
/// requires a layout engine, and the only credible local one is an office
/// suite.
///
/// SO THE ENGINE DOES NOT RUN HERE. This class is a CLIENT. It sends bounded
/// bytes and a format ordinal over a Unix socket to a container that holds no
/// credentials, no owner identity and no network route, and receives a PDF. The
/// PDF is then rasterised by the PDFium this process already runs for OCR — a
/// component that has been reading owner-supplied PDFs since Slice 4, so
/// nothing new enters the API's trust boundary.
///
/// WHAT THE PROTOCOL CANNOT SAY is the security argument. There is no operation
/// for a path, a filename, a command, an import filter, a binary or a URL. Not
/// "those are validated" — they cannot be expressed, so there is nothing to
/// validate and nothing for a future edit to loosen.
public sealed class OfficeVisualRenderer : IDocumentVisualRenderer
{
    private const int Magic = 0x4E424452; // "NBDR"
    private const byte Version = 1;
    private const byte OpReadiness = 1;
    private const byte OpRender = 2;

    private const byte StatusOk = 0;
    private const byte StatusRejected = 1;
    private const byte StatusUnavailable = 2;

    private const int RequestHeaderBytes = 20;
    private const int ResponseHeaderBytes = 12;

    private readonly IOptions<DocumentVisualOptions> _options;
    private readonly PdfVisualRenderer _pdf;
    private readonly ILogger<OfficeVisualRenderer> _log;

    public OfficeVisualRenderer(
        IOptions<DocumentVisualOptions> options,
        PdfVisualRenderer pdf,
        ILogger<OfficeVisualRenderer> log)
    {
        _options = options;
        _pdf = pdf;
        _log = log;
    }

    public string RenderProfileKey => DocumentVisualRenderProfiles.LibreOfficePdf;

    public IReadOnlyCollection<DocumentFormatKind> Formats { get; } = new[]
    {
        DocumentFormatKind.WordOpenXml,
        DocumentFormatKind.SpreadsheetOpenXml,
        DocumentFormatKind.PresentationOpenXml,
    };

    public DocumentVisualRendererReadiness CheckReadiness()
    {
        var options = _options.Value;
        if (!options.RenderOfficeEnabled)
        {
            return DocumentVisualRendererReadiness.NotReady(DocumentVisualReasons.Disabled);
        }

        var path = (options.OfficeRendererSocketPath ?? string.Empty).Trim();
        if (path.Length == 0 || !File.Exists(path))
        {
            // The worker is not deployed. An ENVIRONMENT state: Office documents
            // stay text-only and no verdict is recorded against any of them.
            return DocumentVisualRendererReadiness.NotReady(DocumentVisualReasons.RendererUnavailable);
        }

        // The socket exists; ask whether anything is listening and whether the
        // engine is actually there. A dead socket file left by a stopped
        // container looks identical to a live one until something connects.
        try
        {
            var (status, _, _) = ExchangeAsync(
                path, OpReadiness, 0, 5, 0, ReadOnlyMemory<byte>.Empty,
                CancellationToken.None).GetAwaiter().GetResult();

            return status == StatusOk
                ? DocumentVisualRendererReadiness.Available
                : DocumentVisualRendererReadiness.NotReady(DocumentVisualReasons.RendererUnavailable);
        }
        catch (Exception)
        {
            return DocumentVisualRendererReadiness.NotReady(DocumentVisualReasons.RendererUnavailable);
        }
    }

    public async Task<DocumentVisualRenderOutcome> RenderAsync(
        DocumentVisualRenderRequest request, CancellationToken cancellationToken = default)
    {
        var format = FormatOrdinal(request.Format);
        if (format == 0)
        {
            return DocumentVisualRenderOutcome.Rejected(DocumentVisualReasons.FormatUnsupported);
        }

        var options = request.Options;
        var configured = _options.Value;
        if (!configured.RenderOfficeEnabled)
        {
            return DocumentVisualRenderOutcome.Unavailable(DocumentVisualReasons.Disabled);
        }

        var path = (configured.OfficeRendererSocketPath ?? string.Empty).Trim();
        if (path.Length == 0)
        {
            return DocumentVisualRenderOutcome.Unavailable(DocumentVisualReasons.RendererUnavailable);
        }

        byte status;
        ushort reason;
        byte[] pdf;
        try
        {
            (status, reason, pdf) = await ExchangeAsync(
                path,
                OpRender,
                format,
                options.EffectiveMaxOfficeRenderSeconds,
                options.EffectiveMaxRenderedPdfBytes,
                request.Bytes,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // A socket that is gone, refused or reset. Never a verdict about
            // somebody's document.
            _log.LogDebug("document-visual: office renderer unreachable");
            return DocumentVisualRenderOutcome.Unavailable(DocumentVisualReasons.RendererUnavailable);
        }

        if (status != StatusOk)
        {
            var mapped = MapReason(reason);
            return status == StatusRejected
                ? DocumentVisualRenderOutcome.Rejected(mapped)
                : DocumentVisualRenderOutcome.Unavailable(mapped);
        }

        if (pdf.Length == 0)
        {
            return DocumentVisualRenderOutcome.Rejected(DocumentVisualReasons.InvalidSource);
        }

        // THE INTERMEDIATE PDF IS NEVER PERSISTED. It exists in memory for as
        // long as it takes PDFium to turn it into pages, and it is not a
        // document the owner has — it is a rendering of one, and storing it
        // would be a second copy of their paperwork with its own deletion and
        // share-boundary problems.
        var pages = await _pdf.RenderAsync(
            new DocumentVisualRenderRequest(pdf, DocumentFormatKind.Pdf, options), cancellationToken);

        if (!pages.Ok) return pages;

        // RE-STAMPED WITH THIS RENDERER'S IDENTITY, and stripped of the page
        // locators PDFium filled in.
        //
        // A PDF page number is provenance when the PDF is the owner's document.
        // Here the PDF is an artefact of LibreOffice's pagination, so "page 4"
        // describes where this build's layout engine broke a DOCX — a different
        // build breaks it elsewhere. Citing it would attribute NubArca's
        // rendering to the author. Slice 4's typed text provenance stays the
        // authority: heading/section for DOCX, sheet/range for XLSX, slide for
        // PPTX.
        var units = pages.Artifact!.Units
            .Select(u => u with
            {
                RenderKind = DocumentVisualRenderKinds.OfficeRenderedPage,
                SourceLocator = null,
                SourcePage = null,
            })
            .ToList();

        return DocumentVisualRenderOutcome.Rendered(
            new DocumentVisualRenderArtifact(RenderProfileKey, units));
    }

    private static byte FormatOrdinal(DocumentFormatKind format) => format switch
    {
        DocumentFormatKind.WordOpenXml => 1,
        DocumentFormatKind.SpreadsheetOpenXml => 2,
        DocumentFormatKind.PresentationOpenXml => 3,
        _ => 0,
    };

    /// The worker's reason ordinals, mapped into NubArca's sanitized tokens.
    ///
    /// An ordinal on the wire rather than a string, so nothing the worker
    /// produces — including a native error message carrying a path — can become
    /// a reason NubArca reports.
    private static string MapReason(ushort reason) => reason switch
    {
        1 => DocumentVisualReasons.FormatUnsupported,
        2 => DocumentVisualReasons.InvalidSource,
        3 => DocumentVisualReasons.OutputTooLarge,
        4 => DocumentVisualReasons.RenderTimeout,
        5 => DocumentVisualReasons.RenderProcessFailed,
        _ => DocumentVisualReasons.RendererUnavailable,
    };

    private static async Task<(byte Status, ushort Reason, byte[] Payload)> ExchangeAsync(
        string socketPath,
        byte op,
        byte format,
        int timeoutSeconds,
        int maxOutputBytes,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // The client's own deadline is deliberately LONGER than the worker's, so
        // an ordinary conversion timeout comes back as the worker's own answer
        // rather than as a torn connection the client has to guess about.
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds + 15, 5, 1_200)));

        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), timeout.Token);

        var header = new byte[RequestHeaderBytes];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0, 4), Magic);
        header[4] = Version;
        header[5] = op;
        header[6] = format;
        header[7] = 0;
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8, 4), (uint)Math.Max(1, timeoutSeconds));
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(12, 4), (uint)Math.Max(0, maxOutputBytes));
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16, 4), (uint)payload.Length);

        await socket.SendAsync(header, SocketFlags.None, timeout.Token);
        if (!payload.IsEmpty)
        {
            await socket.SendAsync(payload, SocketFlags.None, timeout.Token);
        }

        var response = new byte[ResponseHeaderBytes];
        await ReceiveExactlyAsync(socket, response, timeout.Token);

        if (BinaryPrimitives.ReadInt32BigEndian(response.AsSpan(0, 4)) != Magic
            || response[4] != Version)
        {
            throw new InvalidOperationException("Unexpected renderer response.");
        }

        var status = response[5];
        var reason = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(6, 2));
        var length = BinaryPrimitives.ReadUInt32BigEndian(response.AsSpan(8, 4));

        // THE DECLARED LENGTH IS BOUNDED BEFORE IT IS ALLOCATED. A worker that
        // announced four gigabytes — compromised, or simply wrong — must not be
        // able to make the API allocate it.
        if (length > (uint)Math.Max(1, maxOutputBytes))
        {
            throw new InvalidOperationException("Renderer response exceeds the configured bound.");
        }

        if (length == 0) return (status, reason, Array.Empty<byte>());

        var body = new byte[length];
        await ReceiveExactlyAsync(socket, body, timeout.Token);
        return (status, reason, body);
    }

    private static async Task ReceiveExactlyAsync(
        Socket socket, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await socket.ReceiveAsync(buffer[offset..], SocketFlags.None, cancellationToken);
            if (read == 0) throw new IOException("The renderer closed the connection.");
            offset += read;
        }
    }
}
