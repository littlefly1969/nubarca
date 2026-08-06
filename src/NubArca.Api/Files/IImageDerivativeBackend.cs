namespace NubArca.Api.Files;

// Slice 100: a pluggable engine that turns source image bytes into JPEG
// derivatives. It owns ONLY decode → resize → encode. Everything else (owner
// scoping, safety gates, blob store + refcount, FileThumbnail rows, diagnostics,
// race handling) stays in FileThumbnailService, so a backend swap can never
// affect storage semantics.
//
// Implementations must be thread-safe and stateless: a single instance is
// shared (singleton) and may be called concurrently.
public interface IImageDerivativeBackend
{
    // Stable name recorded in diagnostics/metrics (see DerivativeBackends).
    string Name { get; }

    // False when the backend cannot run in this process (e.g. the libvips
    // native library is missing). An unavailable backend is never selected and
    // never used as a fallback.
    bool IsAvailable { get; }

    // Render every requested size from the SAME source bytes. The returned list
    // is 1:1 with `requests` (index-aligned). A null entry means that one size
    // failed to render although the source decoded; a whole-source failure
    // (cannot decode at all) throws ImageBackendException so the caller can fall
    // back. OperationCanceledException always propagates (cancellation/timeout).
    //
    // Implementations MUST honour the no-upscale bounding-box contract: fit the
    // image inside edge×edge preserving aspect ratio, and never enlarge a source
    // already within the box. EXIF auto-rotation is intentionally disabled so
    // output dimensions match the identified source dimensions.
    Task<IReadOnlyList<RenderedDerivative?>> RenderAsync(
        ReadOnlyMemory<byte> source,
        IReadOnlyList<DerivativeRequest> requests,
        CancellationToken cancellationToken);
}

// One derivative to produce: a size label, its bounding-box edge in pixels, and
// the JPEG quality.
public readonly record struct DerivativeRequest(string Size, int Edge, int Quality);

// A produced JPEG derivative: the encoded bytes plus the final dimensions
// (validated by the caller against the no-upscale contract).
public sealed record RenderedDerivative(byte[] Jpeg, int Width, int Height);

// Thrown by a backend when it cannot decode the source at all (as opposed to a
// per-size failure, which is a null entry). Carries a stable, sanitized code
// (see DerivativeErrorCodes) — never a raw message that could echo bytes/paths.
public sealed class ImageBackendException : Exception
{
    public string Code { get; }

    public ImageBackendException(string code, string message) : base(message) => Code = code;

    public ImageBackendException(string code, string message, Exception inner)
        : base(message, inner) => Code = code;
}
