using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace NubArca.Api.Files;

// Slice 100: chooses the configured image backend, runs it under a timeout,
// validates the output against the no-upscale bounding-box contract, and falls
// back to the always-available ImageSharp backend when the preferred backend is
// unavailable / throws / times out / produces invalid output. Stateless and
// thread-safe (singleton): all metrics flow out via the returned result, so the
// caller aggregates them.
public sealed class ImageDerivativeRenderer
{
    private readonly ImageSharpDerivativeBackend _imageSharp;
    private readonly IImageDerivativeBackend _preferred;
    private readonly IOptions<MediaDerivativesOptions> _options;
    private readonly ILogger<ImageDerivativeRenderer> _logger;

    // Reason token recorded when the preferred backend produced out-of-contract
    // dimensions (kept distinct from the decode/timeout codes).
    private const string InvalidOutputReason = "invalid_output";

    public ImageDerivativeRenderer(
        ImageSharpDerivativeBackend imageSharp,
        IImageDerivativeBackend preferred,
        IOptions<MediaDerivativesOptions> options,
        ILogger<ImageDerivativeRenderer> logger)
    {
        _imageSharp = imageSharp;
        _preferred = preferred;
        _options = options;
        _logger = logger;
    }

    // The backend that WOULD be chosen for a render right now (no I/O). Useful
    // for diagnostics/benchmarks. Honours availability + config.
    public string SelectedBackendName => SelectPrimary().Backend.Name;

    // A dependency-free ImageSharp-only renderer for direct-construction call
    // sites (unit tests, the upload path's null fallback). Behaves exactly like
    // the pre-slice-100 pipeline: no vips, no fallback indirection.
    public static ImageDerivativeRenderer ImageSharpOnly()
    {
        var backend = new ImageSharpDerivativeBackend(NullLogger<ImageSharpDerivativeBackend>.Instance);
        var options = Options.Create(new MediaDerivativesOptions
        {
            ImageBackend = ImageDerivativeBackendNames.ImageSharp,
        });
        return new ImageDerivativeRenderer(backend, backend, options, NullLogger<ImageDerivativeRenderer>.Instance);
    }

    // Never throws for an expected render failure (decode/timeout/invalid
    // output): those come back as all-null Results plus a batch-level
    // FailureCode the caller maps to a diagnostic. OperationCanceledException
    // (real cancellation) still propagates.
    public async Task<DerivativeRenderResult> RenderAsync(
        ReadOnlyMemory<byte> source,
        IReadOnlyList<DerivativeRequest> requests,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var (primary, allowFallback) = SelectPrimary();

        ImageBackendException primaryFailure;
        try
        {
            var results = await RunAsync(primary, source, requests, cancellationToken);
            ValidateOrThrow(results, requests);
            return new DerivativeRenderResult(
                results, primary.Name, FellBack: false, FailureCode: null, ElapsedMillis(started));
        }
        catch (OperationCanceledException)
        {
            throw; // real cancellation — never a fallback trigger
        }
        catch (ImageBackendException ex)
        {
            primaryFailure = ex;
        }

        // The primary failed. Without a distinct fallback target, report it.
        if (!allowFallback || ReferenceEquals(primary, _imageSharp))
        {
            return Failed(requests, primary.Name, fellBack: false, primaryFailure.Code, started);
        }

        _logger.LogWarning(
            "Derivative backend '{Backend}' failed ({Code}); falling back to ImageSharp.",
            primary.Name, primaryFailure.Code);
        try
        {
            var results = await RunAsync(_imageSharp, source, requests, cancellationToken);
            ValidateOrThrow(results, requests);
            return new DerivativeRenderResult(
                results, _imageSharp.Name, FellBack: true, FailureCode: null, ElapsedMillis(started));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ImageBackendException)
        {
            // Both backends failed — surface the PRIMARY's reason (the file is
            // genuinely unprocessable; the fallback confirms it).
            return Failed(requests, _imageSharp.Name, fellBack: true, primaryFailure.Code, started);
        }
    }

    private DerivativeRenderResult Failed(
        IReadOnlyList<DerivativeRequest> requests, string backend, bool fellBack, string code, long started)
        => new(new RenderedDerivative?[requests.Count], backend, fellBack, code, ElapsedMillis(started));

    private (IImageDerivativeBackend Backend, bool AllowFallback) SelectPrimary()
    {
        var opts = _options.Value;
        var wantPreferred = opts.ImageBackend switch
        {
            ImageDerivativeBackendNames.ImageSharp => false,
            _ => true, // "vips" / "auto" / unknown → prefer the optimized backend
        };

        if (wantPreferred
            && opts.VipsEnabled
            && !ReferenceEquals(_preferred, _imageSharp)
            && _preferred.IsAvailable)
        {
            return (_preferred, opts.FallbackToImageSharp);
        }
        return (_imageSharp, false);
    }

    // Run a backend with the configured per-render timeout. A timeout that is
    // NOT caused by the caller's cancellation is reported as a backend failure
    // (code `timeout`) so it can fall back.
    private async Task<IReadOnlyList<RenderedDerivative?>> RunAsync(
        IImageDerivativeBackend backend,
        ReadOnlyMemory<byte> source,
        IReadOnlyList<DerivativeRequest> requests,
        CancellationToken cancellationToken)
    {
        var timeoutSeconds = _options.Value.RenderTimeoutSeconds;
        if (timeoutSeconds <= 0)
        {
            return await backend.RenderAsync(source, requests, cancellationToken);
        }

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        try
        {
            return await backend.RenderAsync(source, requests, linked.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ImageBackendException(
                DerivativeErrorCodes.Timeout,
                $"Backend '{backend.Name}' render exceeded {timeoutSeconds}s.");
        }
    }

    // A produced derivative must fit inside its box (positive dims, no edge
    // exceeded). Violations (e.g. an unexpected orientation swap) are treated as
    // a backend failure so we fall back to ImageSharp. Null entries (per-size
    // failures) are left for the caller to classify.
    private static void ValidateOrThrow(
        IReadOnlyList<RenderedDerivative?> results, IReadOnlyList<DerivativeRequest> requests)
    {
        for (var i = 0; i < results.Count; i++)
        {
            var r = results[i];
            if (r is null)
            {
                continue;
            }
            var edge = requests[i].Edge;
            if (r.Width <= 0 || r.Height <= 0 || r.Width > edge || r.Height > edge)
            {
                throw new ImageBackendException(
                    InvalidOutputReason,
                    $"Backend produced out-of-box dimensions {r.Width}x{r.Height} for edge {edge}.");
            }
        }
    }

    private static long ElapsedMillis(long started) =>
        (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
}

// Outcome of a render: the per-size results (1:1 with the requests), which
// backend produced them, whether a fallback occurred, a batch-level FailureCode
// when the whole render failed (else null), and the wall-clock render time.
// Carries no ids, paths, or sensitive data.
public sealed record DerivativeRenderResult(
    IReadOnlyList<RenderedDerivative?> Results,
    string BackendUsed,
    bool FellBack,
    string? FailureCode,
    long RenderMillis);
