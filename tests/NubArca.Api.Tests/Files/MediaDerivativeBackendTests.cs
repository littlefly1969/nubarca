using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NubArca.Api.Files;
using NubArca.Api.Tests.Metadata;
using SixLabors.ImageSharp;
using Xunit;

namespace NubArca.Api.Tests.Files;

// Slice 100: backend + renderer unit tests (no DB). Cover the ImageSharp
// backend, the libvips backend (skipped when the native lib is unavailable),
// and the renderer's selection / fallback / timeout logic via a stub backend.
public sealed class MediaDerivativeBackendTests
{
    private static readonly DerivativeRequest Small = new(
        ThumbnailSizes.Small, ThumbnailSizes.GetEdge(ThumbnailSizes.Small), 80);
    private static readonly DerivativeRequest Medium = new(
        ThumbnailSizes.Medium, ThumbnailSizes.GetEdge(ThumbnailSizes.Medium), 80);

    private static VipsDerivativeBackend NewVips()
    {
        var runtime = new VipsRuntime(
            Options.Create(new MediaDerivativesOptions()), NullLogger<VipsRuntime>.Instance);
        return new VipsDerivativeBackend(runtime, NullLogger<VipsDerivativeBackend>.Instance);
    }

    private static ImageSharpDerivativeBackend NewImageSharp()
        => new(NullLogger<ImageSharpDerivativeBackend>.Instance);

    private static async Task<(int W, int H)> DimsAsync(byte[] jpeg)
    {
        using var ms = new MemoryStream(jpeg);
        var info = await Image.IdentifyAsync(ms);
        return (info.Width, info.Height);
    }

    // ---- backends ----------------------------------------------------------

    [Fact]
    public async Task ImageSharp_Renders_Box_Fit_And_No_Upscale()
    {
        var backend = NewImageSharp();
        var source = ImageFixtures.PlainPng(800, 400);

        var results = await backend.RenderAsync(source, new[] { Small, Medium }, CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.NotNull(results[0]);
        Assert.NotNull(results[1]);
        Assert.Equal((768, 384), await DimsAsync(results[0]!.Jpeg));
        Assert.Equal(768, results[0]!.Width);
        Assert.Equal(384, results[0]!.Height);
        Assert.Equal((800, 400), await DimsAsync(results[1]!.Jpeg));
    }

    [Fact]
    public async Task ImageSharp_Undecodable_Throws_Backend_Exception()
    {
        var backend = NewImageSharp();
        var ex = await Assert.ThrowsAsync<ImageBackendException>(() =>
            backend.RenderAsync(ImageFixtures.UndecodablePng(), new[] { Small }, CancellationToken.None));
        Assert.Equal(DerivativeErrorCodes.DecodeFailed, ex.Code);
    }

    [Fact]
    public async Task Vips_Renders_Same_Dimensions_As_ImageSharp()
    {
        var vips = NewVips();
        if (!vips.IsAvailable)
        {
            return; // native libvips not present on this RID — covered by fallback tests
        }

        var source = ImageFixtures.PlainPng(800, 400);
        var vipsResults = await vips.RenderAsync(source, new[] { Small, Medium }, CancellationToken.None);
        var sharpResults = await NewImageSharp().RenderAsync(source, new[] { Small, Medium }, CancellationToken.None);

        // Dimension parity (semantics preserved across backends).
        Assert.Equal(sharpResults[0]!.Width, vipsResults[0]!.Width);
        Assert.Equal(sharpResults[0]!.Height, vipsResults[0]!.Height);
        Assert.Equal(sharpResults[1]!.Width, vipsResults[1]!.Width);
        Assert.Equal(sharpResults[1]!.Height, vipsResults[1]!.Height);
        // Output is a decodable JPEG of the expected size.
        Assert.Equal((768, 384), await DimsAsync(vipsResults[0]!.Jpeg));
    }

    [Fact]
    public async Task Vips_NoUpscale_Keeps_Small_Source_Native()
    {
        var vips = NewVips();
        if (!vips.IsAvailable) return;

        var results = await vips.RenderAsync(ImageFixtures.PlainPng(100, 80), new[] { Small }, CancellationToken.None);
        Assert.Equal(100, results[0]!.Width);
        Assert.Equal(80, results[0]!.Height);
    }

    // ---- renderer selection + fallback ------------------------------------

    private static ImageDerivativeRenderer NewRenderer(
        IImageDerivativeBackend preferred, MediaDerivativesOptions options)
        => new(NewImageSharp(), preferred, Options.Create(options), NullLogger<ImageDerivativeRenderer>.Instance);

    [Fact]
    public async Task Renderer_Uses_Preferred_When_Available()
    {
        var preferred = new StubBackend("vips", available: true, renderTo: (256, 128));
        var renderer = NewRenderer(preferred, new MediaDerivativesOptions { ImageBackend = "vips" });

        var result = await renderer.RenderAsync(ImageFixtures.PlainPng(400, 200), new[] { Small }, CancellationToken.None);

        Assert.Equal("vips", result.BackendUsed);
        Assert.False(result.FellBack);
        Assert.NotNull(result.Results[0]);
    }

    [Fact]
    public async Task Renderer_Falls_Back_To_ImageSharp_When_Preferred_Throws()
    {
        var preferred = new StubBackend("vips", available: true); // throws decode_failed
        var renderer = NewRenderer(preferred, new MediaDerivativesOptions
        {
            ImageBackend = "vips",
            FallbackToImageSharp = true,
        });

        var result = await renderer.RenderAsync(ImageFixtures.PlainPng(400, 200), new[] { Small }, CancellationToken.None);

        Assert.Equal(DerivativeBackends.ImageSharp, result.BackendUsed);
        Assert.True(result.FellBack);
        Assert.Null(result.FailureCode);
        Assert.Equal((400, 200), await DimsAsync(result.Results[0]!.Jpeg));
    }

    [Fact]
    public async Task Renderer_Does_Not_Fall_Back_When_Disabled()
    {
        var preferred = new StubBackend("vips", available: true); // throws
        var renderer = NewRenderer(preferred, new MediaDerivativesOptions
        {
            ImageBackend = "vips",
            FallbackToImageSharp = false,
        });

        var result = await renderer.RenderAsync(ImageFixtures.PlainPng(400, 200), new[] { Small }, CancellationToken.None);

        Assert.Equal("vips", result.BackendUsed);
        Assert.False(result.FellBack);
        Assert.Equal(DerivativeErrorCodes.DecodeFailed, result.FailureCode);
        Assert.Null(result.Results[0]);
    }

    [Fact]
    public async Task Renderer_Reports_Failure_When_Both_Backends_Fail()
    {
        var preferred = new StubBackend("vips", available: true); // throws
        var renderer = NewRenderer(preferred, new MediaDerivativesOptions { ImageBackend = "vips" });

        // ImageSharp fallback also cannot decode this (genuinely corrupt) source.
        var result = await renderer.RenderAsync(ImageFixtures.UndecodablePng(), new[] { Small }, CancellationToken.None);

        Assert.True(result.FellBack);
        Assert.Equal(DerivativeErrorCodes.DecodeFailed, result.FailureCode);
        Assert.Null(result.Results[0]);
    }

    [Fact]
    public async Task Renderer_ImageSharp_Config_Ignores_Preferred()
    {
        // Even a healthy preferred backend is bypassed when imagesharp is forced.
        var preferred = new StubBackend("vips", available: true, renderTo: (256, 128));
        var renderer = NewRenderer(preferred, new MediaDerivativesOptions { ImageBackend = "imagesharp" });

        var result = await renderer.RenderAsync(ImageFixtures.PlainPng(400, 200), new[] { Small }, CancellationToken.None);

        Assert.Equal(DerivativeBackends.ImageSharp, result.BackendUsed);
        Assert.False(result.FellBack);
    }

    [Fact]
    public async Task Renderer_Unavailable_Preferred_Uses_ImageSharp()
    {
        var preferred = new StubBackend("vips", available: false);
        var renderer = NewRenderer(preferred, new MediaDerivativesOptions { ImageBackend = "auto" });

        var result = await renderer.RenderAsync(ImageFixtures.PlainPng(400, 200), new[] { Small }, CancellationToken.None);

        Assert.Equal(DerivativeBackends.ImageSharp, result.BackendUsed);
        Assert.False(result.FellBack);
    }

    [Fact]
    public async Task Renderer_Timeout_Falls_Back_To_ImageSharp()
    {
        // The preferred backend hangs; a 1s render timeout abandons it and the
        // result comes from ImageSharp.
        var preferred = new StubBackend("vips", available: true, delay: TimeSpan.FromSeconds(30));
        var renderer = NewRenderer(preferred, new MediaDerivativesOptions
        {
            ImageBackend = "vips",
            FallbackToImageSharp = true,
            RenderTimeoutSeconds = 1,
        });

        var result = await renderer.RenderAsync(ImageFixtures.PlainPng(400, 200), new[] { Small }, CancellationToken.None);

        Assert.Equal(DerivativeBackends.ImageSharp, result.BackendUsed);
        Assert.True(result.FellBack);
        Assert.NotNull(result.Results[0]);
    }

    [Fact]
    public async Task Renderer_Honours_Real_Cancellation()
    {
        var preferred = new StubBackend("vips", available: true, delay: TimeSpan.FromSeconds(30));
        var renderer = NewRenderer(preferred, new MediaDerivativesOptions { ImageBackend = "vips" });
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            renderer.RenderAsync(ImageFixtures.PlainPng(400, 200), new[] { Small }, cts.Token));
    }

    // A controllable backend double: optionally unavailable, optionally slow,
    // returns a fixed-size blank JPEG or (default) throws decode_failed.
    private sealed class StubBackend : IImageDerivativeBackend
    {
        private readonly (int W, int H)? _renderTo;
        private readonly TimeSpan? _delay;

        public StubBackend(string name, bool available, (int W, int H)? renderTo = null, TimeSpan? delay = null)
        {
            Name = name;
            IsAvailable = available;
            _renderTo = renderTo;
            _delay = delay;
        }

        public string Name { get; }
        public bool IsAvailable { get; }

        public async Task<IReadOnlyList<RenderedDerivative?>> RenderAsync(
            ReadOnlyMemory<byte> source, IReadOnlyList<DerivativeRequest> requests, CancellationToken cancellationToken)
        {
            if (_delay is { } d)
            {
                await Task.Delay(d, cancellationToken);
            }
            if (_renderTo is not { } rt)
            {
                throw new ImageBackendException(DerivativeErrorCodes.DecodeFailed, "stub failure");
            }
            // Content irrelevant — only dims/validation matter to the renderer.
            var jpeg = ImageFixtures.PlainPng(rt.W, rt.H);
            return requests.Select(_ => (RenderedDerivative?)new RenderedDerivative(jpeg, rt.W, rt.H)).ToArray();
        }
    }
}
