using Microsoft.Extensions.Options;
using PDFtoImage;
using SkiaSharp;

namespace NubArca.Api.Ai.Documents;

/// Renders ONE page, and only when OCR needs it.
///
/// Rendering every page of every text-native PDF would be the expensive way to
/// learn nothing: most PDFs carry their text, and a hundred-page rasterisation
/// to discover that is minutes of CPU per document. So the caller decides per
/// page, and this is asked only for the pages whose text is not usable.
///
/// PDFium is not documented as thread-safe, so calls are serialised here rather
/// than assumed safe because something else happens to be. The OCR concurrency
/// gate is a different bound protecting a different resource and proves nothing
/// about this one.
public sealed class PdfPageRenderer
{
    private readonly IOptions<DocumentExtractionOptions> _options;

    /// PDFium is a single native library with process-wide state. One at a time.
    private static readonly SemaphoreSlim Native = new(1, 1);

    public PdfPageRenderer(IOptions<DocumentExtractionOptions> options)
    {
        _options = options;
    }

    /// A PNG of one page, or the sanitized reason there is none.
    ///
    /// `pageIndex` is 0-based, which is what the renderer takes; every locator
    /// and citation above this uses 1-based pages, and the conversion happens at
    /// the caller so this stays the only place that speaks the library's
    /// convention.
    public async Task<(byte[]? Png, string? Reason)> RenderAsync(
        ReadOnlyMemory<byte> pdfBytes, int pageIndex, CancellationToken cancellationToken = default)
    {
        var options = _options.Value;

        await Native.WaitAsync(cancellationToken);
        try
        {
            // The pixel bound, applied as a DPI the page cannot exceed rather
            // than as a check after the allocation. A page declaring an enormous
            // MediaBox would otherwise turn a legal DPI into gigabytes.
            var dpi = options.EffectiveOcrRenderDpi;

            using var input = new MemoryStream(pdfBytes.ToArray(), writable: false);
            using var image = Conversion.ToImage(
                input,
                leaveOpen: false,
                password: null,
                page: new Index(pageIndex),
                options: new RenderOptions(Dpi: dpi, WithAspectRatio: true));

            var pixels = (long)image.Width * image.Height;
            if (pixels > options.EffectiveMaxRenderPixels)
            {
                return (null, DocumentExtractionReasons.PdfRenderFailed);
            }

            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return (data.ToArray(), null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DllNotFoundException)
        {
            // The native library is missing. An environment failure — the next
            // pass on a correctly-built image succeeds.
            return (null, DocumentExtractionReasons.PdfRendererUnavailable);
        }
        catch (TypeInitializationException)
        {
            return (null, DocumentExtractionReasons.PdfRendererUnavailable);
        }
        catch (Exception)
        {
            // Any other renderer failure is sanitized: a native exception
            // message can carry a path, and this reason travels to the CLI.
            return (null, DocumentExtractionReasons.PdfRenderFailed);
        }
        finally
        {
            Native.Release();
        }
    }
}
