using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace NubArca.Api.Files;

// Slice 100: the stable, always-available backend — the historical pipeline,
// extracted behind IImageDerivativeBackend. It decodes the source EXACTLY ONCE
// and clones the decoded image per requested size, so producing both small and
// medium costs a single decode (matching the previous bundled behaviour). Output
// bytes are byte-identical to the pre-slice-100 pipeline at the same quality, so
// existing content-addressed derived blobs keep deduping.
public sealed class ImageSharpDerivativeBackend : IImageDerivativeBackend
{
    private readonly ILogger<ImageSharpDerivativeBackend> _logger;

    public ImageSharpDerivativeBackend(ILogger<ImageSharpDerivativeBackend> logger) => _logger = logger;

    public string Name => DerivativeBackends.ImageSharp;

    // The pure-managed decoder is always present.
    public bool IsAvailable => true;

    public async Task<IReadOnlyList<RenderedDerivative?>> RenderAsync(
        ReadOnlyMemory<byte> source,
        IReadOnlyList<DerivativeRequest> requests,
        CancellationToken cancellationToken)
    {
        Image image;
        try
        {
            image = Image.Load(source.Span);
        }
        catch (Exception ex)
        {
            // Whole-source decode failure → let the caller classify + fall back.
            throw new ImageBackendException(
                DerivativeErrorCodes.DecodeFailed,
                $"ImageSharp could not decode the source ({ex.GetType().Name}).",
                ex);
        }

        var results = new RenderedDerivative?[requests.Count];
        using (image)
        {
            for (var i = 0; i < requests.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                results[i] = RenderOne(image, requests[i]);
            }
        }
        return results;
    }

    private RenderedDerivative? RenderOne(Image source, DerivativeRequest request)
    {
        try
        {
            // Clone from the ORIGINAL decode (never from another derivative) so
            // output is independent of request order and byte-identical to the
            // historical single-size path.
            using var resized = CloneResized(source, request.Edge);
            using var encoded = new MemoryStream();
            resized.Save(encoded, new JpegEncoder { Quality = request.Quality });
            return new RenderedDerivative(encoded.ToArray(), resized.Width, resized.Height);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "ImageSharp render ({Size}) failed ({Type}).", request.Size, ex.GetType().Name);
            return null;
        }
    }

    // No-upscale clone: a source already inside the box is re-encoded at native
    // size; otherwise scaled down preserving aspect ratio (ResizeMode.Max).
    private static Image CloneResized(Image source, int edge)
    {
        if (source.Width <= edge && source.Height <= edge)
        {
            return source.Clone(_ => { });
        }
        return source.Clone(ctx => ctx.Resize(new ResizeOptions
        {
            Size = new Size(edge, edge),
            Mode = ResizeMode.Max,
            Sampler = KnownResamplers.Lanczos3,
        }));
    }
}
