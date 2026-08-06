using Microsoft.Extensions.Logging;
using NetVips;

namespace NubArca.Api.Files;

// Slice 100: the high-performance libvips backend. It uses libvips' streaming
// `thumbnail` operation, which SHRINKS ON LOAD (the JPEG/TIFF/WebP decoder reads
// only the resolution it needs) and resizes in one low-memory pass — typically
// several times faster than a full decode + resize, especially for large source
// images.
//
// Semantics are matched to the ImageSharp backend so output is interchangeable:
//   * width=height=edge with size=Down → fit inside the box, never upscale;
//   * noRotate=true → do NOT bake EXIF orientation into the pixels, so output
//     dimensions equal the identified (un-rotated) source dimensions, exactly
//     like ImageSharp; the orientation tag is preserved for the browser;
//   * failOn=Error → reject truncated/corrupt input instead of emitting garbage,
//     so the caller can fall back to ImageSharp's more lenient decoder.
//
// This file deliberately imports ONLY NetVips, so `Image` here is NetVips.Image
// (no clash with SixLabors.ImageSharp.Image).
public sealed class VipsDerivativeBackend : IImageDerivativeBackend
{
    private readonly VipsRuntime _runtime;
    private readonly ILogger<VipsDerivativeBackend> _logger;

    public VipsDerivativeBackend(VipsRuntime runtime, ILogger<VipsDerivativeBackend> logger)
    {
        _runtime = runtime;
        _logger = logger;
    }

    public string Name => DerivativeBackends.Vips;

    public bool IsAvailable => _runtime.IsAvailable;

    public Task<IReadOnlyList<RenderedDerivative?>> RenderAsync(
        ReadOnlyMemory<byte> source,
        IReadOnlyList<DerivativeRequest> requests,
        CancellationToken cancellationToken)
    {
        // libvips is CPU-bound native code; run it off the request/worker thread
        // so the timeout (a linked CTS at the renderer) can abandon the await.
        var buffer = source.ToArray();
        return Task.Run(() => RenderCore(buffer, requests, cancellationToken), cancellationToken);
    }

    private IReadOnlyList<RenderedDerivative?> RenderCore(
        byte[] buffer, IReadOnlyList<DerivativeRequest> requests, CancellationToken cancellationToken)
    {
        var results = new RenderedDerivative?[requests.Count];
        for (var i = 0; i < requests.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = requests[i];
            try
            {
                using var thumb = Image.ThumbnailBuffer(
                    buffer,
                    request.Edge,
                    height: request.Edge,
                    size: Enums.Size.Down,
                    noRotate: true,
                    failOn: Enums.FailOn.Error);
                // Keep metadata (matches ImageSharp's encoder, preserving the
                // EXIF orientation tag the browser uses to display rotated
                // photos upright).
                var jpeg = thumb.JpegsaveBuffer(q: request.Quality);
                results[i] = new RenderedDerivative(jpeg, thumb.Width, thumb.Height);
            }
            catch (VipsException ex)
            {
                _logger.LogWarning(
                    "libvips render ({Size}) failed ({Type}).", request.Size, ex.GetType().Name);
                // libvips re-loads per size, so a decode failure affects every
                // size identically. Signal a whole-source failure on the FIRST
                // size so the caller falls back to ImageSharp ONCE for the whole
                // batch; a later-only failure (rare) is a per-size null.
                if (i == 0)
                {
                    throw new ImageBackendException(
                        DerivativeErrorCodes.DecodeFailed,
                        $"libvips could not decode the source ({ex.GetType().Name}).",
                        ex);
                }
                results[i] = null;
            }
        }
        return results;
    }
}
