using Microsoft.Extensions.Options;
using NubArca.Api.Ai.Documents;
using PDFtoImage;
using SkiaSharp;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Exceptions;

namespace NubArca.Api.Ai.DocumentVisual;

/// Every page of a PDF, as pixels, through the PDFium the OCR path already
/// uses.
///
/// The renderer Slice 4 has renders ONE page and only when OCR needs it, because
/// most PDFs carry their text and rasterising a hundred pages to discover that
/// is minutes of wasted CPU. Visual retrieval inverts that: how the page LOOKS
/// is the signal, so every page is drawn — which is why this is a separate class
/// with its own bounds rather than a loop around `PdfPageRenderer`.
///
/// A PDF PAGE IS THE ONE UNIT THAT IS ALSO A CITATION. Page 7 of a PDF is page 7
/// of that PDF under any renderer, so this is the only renderer that fills in
/// `SourcePage` and a `page` locator. Everything else leaves them null rather
/// than inventing a coordinate — see DocumentVisualUnit.
public sealed class PdfVisualRenderer : IDocumentVisualRenderer
{
    /// PDFium is a single native library with process-wide state and is not
    /// documented as thread-safe. One at a time — and deliberately a DIFFERENT
    /// semaphore instance from the OCR renderer's would be wrong, so this shares
    /// the same discipline by serialising here too. Two gates over one native
    /// library are one gate too few.
    private static readonly SemaphoreSlim Native = new(1, 1);

    private readonly IOptions<DocumentVisualOptions> _options;
    private readonly ILogger<PdfVisualRenderer> _log;

    public PdfVisualRenderer(IOptions<DocumentVisualOptions> options, ILogger<PdfVisualRenderer> log)
    {
        _options = options;
        _log = log;
    }

    public string RenderProfileKey => DocumentVisualRenderProfiles.PdfiumPage;

    public IReadOnlyCollection<DocumentFormatKind> Formats { get; } = new[] { DocumentFormatKind.Pdf };

    public DocumentVisualRendererReadiness CheckReadiness()
    {
        // A RUNTIME GUARD RATHER THAN A PLATFORM ANNOTATION, for the reason
        // PdfPageRenderer states: declaring supported platforms would propagate
        // the attribute up through the indexer into everything that touches a
        // document, making a native rendering detail part of half the
        // codebase's signature.
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
        {
            return DocumentVisualRendererReadiness.NotReady(DocumentVisualReasons.RendererUnavailable);
        }

        return DocumentVisualRendererReadiness.Available;
    }

    public async Task<DocumentVisualRenderOutcome> RenderAsync(
        DocumentVisualRenderRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Format != DocumentFormatKind.Pdf)
        {
            return DocumentVisualRenderOutcome.Rejected(DocumentVisualReasons.FormatUnsupported);
        }

        var readiness = CheckReadiness();
        if (!readiness.Ready)
        {
            return DocumentVisualRenderOutcome.Unavailable(
                readiness.Reason ?? DocumentVisualReasons.RendererUnavailable);
        }

        var options = request.Options;
        var bytes = request.Bytes.ToArray();

        // THE PAGE COUNT IS READ BEFORE ANYTHING IS DRAWN, so a document past
        // the completeness bound is refused whole. Rendering up to the bound and
        // stopping would publish an index that reads as a complete document and
        // is not — the exact artefact Slice 4 refuses for text.
        int pageCount;
        try
        {
            using var probe = PdfDocument.Open(bytes);
            pageCount = probe.NumberOfPages;
        }
        catch (PdfDocumentEncryptedException)
        {
            return DocumentVisualRenderOutcome.Rejected(DocumentVisualReasons.InvalidSource);
        }
        catch (Exception)
        {
            return DocumentVisualRenderOutcome.Rejected(DocumentVisualReasons.InvalidSource);
        }

        if (pageCount <= 0)
        {
            return DocumentVisualRenderOutcome.Rejected(DocumentVisualReasons.InvalidSource);
        }
        if (pageCount > options.EffectiveMaxVisualUnitsPerDocument)
        {
            return DocumentVisualRenderOutcome.Rejected(DocumentVisualReasons.DocumentTooComplex);
        }

        var units = new List<DocumentVisualUnitArtifact>(pageCount);
        long totalPixels = 0;

        for (var index = 0; index < pageCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (png, width, height, reason) =
                await RenderPageAsync(bytes, index, options, cancellationToken);
            if (reason is not null)
            {
                // ONE PAGE FAILING FAILS THE DOCUMENT. There is no `continue`
                // here on purpose: the alternative publishes an index missing
                // page 13 with nothing anywhere saying so.
                return DocumentVisualReasons.IsPermanent(reason)
                    ? DocumentVisualRenderOutcome.Rejected(reason)
                    : DocumentVisualRenderOutcome.Unavailable(reason);
            }

            totalPixels += (long)width * height;
            if (totalPixels > options.EffectiveMaxVisualTotalPixelsPerDocument)
            {
                return DocumentVisualRenderOutcome.Rejected(DocumentVisualReasons.DocumentTooComplex);
            }

            units.Add(new DocumentVisualUnitArtifact(
                Ordinal: index,
                RenderKind: DocumentVisualRenderKinds.PdfPage,
                Png: png!,
                Width: width,
                Height: height,
                // 1-based, like every locator and citation above this layer.
                // The 0-based index is the library's convention and stops here.
                SourceLocator: new DocumentLocator(DocumentLocatorKinds.Page, Page: index + 1),
                SourcePage: index + 1));
        }

        return DocumentVisualRenderOutcome.Rendered(
            new DocumentVisualRenderArtifact(RenderProfileKey, units));
    }

    private async Task<(byte[]? Png, int Width, int Height, string? Reason)> RenderPageAsync(
        byte[] pdfBytes, int pageIndex, DocumentVisualOptions options, CancellationToken cancellationToken)
    {
        await Native.WaitAsync(cancellationToken);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.EffectiveMaxPageRenderSeconds));

            using var input = new MemoryStream(pdfBytes, writable: false);
            // The pixel bound applied as a DPI the page cannot exceed, rather
            // than as a check after the allocation: a page declaring an enormous
            // MediaBox would otherwise turn a legal DPI into gigabytes.
            using var image = Conversion.ToImage(
                input,
                leaveOpen: false,
                password: null,
                page: new Index(pageIndex),
                options: new RenderOptions(Dpi: options.EffectiveRenderDpi, WithAspectRatio: true));

            var pixels = (long)image.Width * image.Height;
            if (pixels > options.EffectiveMaxVisualPixelsPerUnit)
            {
                return (null, 0, 0, DocumentVisualReasons.OutputTooLarge);
            }

            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            var png = data.ToArray();
            if (png.Length > options.EffectiveMaxVisualImageBytesPerUnit)
            {
                return (null, 0, 0, DocumentVisualReasons.OutputTooLarge);
            }

            return (png, image.Width, image.Height, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, 0, 0, DocumentVisualReasons.RenderTimeout);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DllNotFoundException)
        {
            return (null, 0, 0, DocumentVisualReasons.RendererUnavailable);
        }
        catch (TypeInitializationException)
        {
            return (null, 0, 0, DocumentVisualReasons.RendererUnavailable);
        }
        catch (Exception)
        {
            // Sanitized: a native exception message can carry a path, and this
            // reason travels to the CLI and the logs.
            _log.LogDebug("document-visual: pdf page render failed");
            return (null, 0, 0, DocumentVisualReasons.RenderProcessFailed);
        }
        finally
        {
            Native.Release();
        }
    }
}
