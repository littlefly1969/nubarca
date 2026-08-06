using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NubArca.Api.Files;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using Xunit;

namespace NubArca.Api.Tests.Files;

// Slice 68 — FfmpegVideoPosterProvider unit tests.
// All tests use a FakeProcessRunner: no real ffmpeg binary required.
public sealed class FfmpegVideoPosterProviderTests
{
    private static readonly MediaOptions DefaultOpts = new()
    {
        VideoPosterProvider = "ffmpeg",
        FfmpegPath = "ffmpeg",
        VideoPosterTimeoutSeconds = 10,
        VideoPreviewStripTimeoutSeconds = 45,
        VideoPosterSeekSeconds = 3,
        VideoPosterMaxOutputBytes = 10 * 1024 * 1024,
    };

    private static FfmpegVideoPosterProvider BuildProvider(
        IProcessRunner runner,
        MediaOptions? opts = null,
        MediaDerivativesOptions? derivatives = null)
    {
        var options = Options.Create(opts ?? DefaultOpts);
        var derivativeOptions = Options.Create(derivatives ?? new MediaDerivativesOptions());
        var synthetic = new SyntheticVideoPosterProvider(derivativeOptions);
        return new FfmpegVideoPosterProvider(
            options,
            runner,
            synthetic,
            NullLogger<FfmpegVideoPosterProvider>.Instance,
            derivativeOptions);
    }

    // Regression (prod): a large batch of SHORT clips was permanently stuck on
    // placeholder posters. The provider seeks 3 s in, which is past the end of a
    // 2 s clip, so ffmpeg returns no frame — and every regeneration silently
    // fell back to synthetic. It must now retry from the first frame before
    // giving up.
    [Fact]
    public async Task Retries_From_The_First_Frame_When_The_Seek_Yields_No_Frame()
    {
        var jpeg = MinimalJpeg();
        var runner = new SequencedProcessRunner(
            // 1st attempt (-ss 3 on a shorter clip): no frame.
            new ProcessRunResult(1, [], false),
            // 2nd attempt (-ss 0): a real frame.
            new ProcessRunResult(0, jpeg, false));

        var result = await BuildProvider(runner).TryGetPosterAsync(EmptyVideoFactory(), default);

        Assert.NotNull(result);
        // A REAL frame, not the synthetic placeholder.
        Assert.Equal(VideoPosterSources.Ffmpeg, result!.Source);
        Assert.Equal(2, runner.Requests.Count);
        // The retry seeks to 0; the first attempt used the configured offset.
        Assert.Equal("3", SeekOf(runner.Requests[0]));
        Assert.Equal("0", SeekOf(runner.Requests[1]));
    }

    [Fact]
    public async Task Falls_Back_To_Synthetic_Only_After_The_Retry_Also_Fails()
    {
        var runner = new SequencedProcessRunner(
            new ProcessRunResult(1, [], false),
            new ProcessRunResult(1, [], false));

        var result = await BuildProvider(runner).TryGetPosterAsync(EmptyVideoFactory(), default);

        Assert.NotNull(result);
        Assert.Equal(VideoPosterSources.Synthetic, result!.Source);
        Assert.Equal(2, runner.Requests.Count);
    }

    private static string? SeekOf(ProcessRunRequest request)
    {
        var at = request.Arguments.ToList().IndexOf("-ss");
        return at >= 0 && at + 1 < request.Arguments.Count ? request.Arguments[at + 1] : null;
    }

    private static Func<CancellationToken, Task<Stream>> EmptyVideoFactory()
        => _ => Task.FromResult<Stream>(new MemoryStream(new byte[64]));

    // Generates a minimal 1×1 JPEG (real JPEG bytes for output-validation tests).
    private static byte[] MinimalJpeg()
    {
        using var img = new Image<SixLabors.ImageSharp.PixelFormats.Rgb24>(1, 1);
        using var ms = new MemoryStream();
        img.Save(ms, new JpegEncoder());
        return ms.ToArray();
    }

    // --- Happy path ---

    [Fact]
    public async Task Returns_Jpeg_On_Zero_Exit_With_Valid_Jpeg_Output()
    {
        var jpegBytes = MinimalJpeg();
        var runner = new FakeProcessRunner(new ProcessRunResult(0, jpegBytes, false));
        var provider = BuildProvider(runner);

        var result = await provider.TryGetPosterAsync(EmptyVideoFactory(), CancellationToken.None);

        Assert.NotNull(result);
        // Slice 95: a real extraction is marked with the ffmpeg source.
        Assert.Equal(VideoPosterSources.Ffmpeg, result!.Source);
        result.Content.Position = 0;
        var data = result.Content.ToArray();
        // Verify it starts with FF D8 (JPEG magic).
        Assert.Equal(0xFF, data[0]);
        Assert.Equal(0xD8, data[1]);
        var args = runner.LastRequest!.Arguments;
        Assert.DoesNotContain("-s", args);
        var filter = args[args.ToList().IndexOf("-vf") + 1];
        // Source-aspect poster: a plain fit-within-box scale, NO 16:9 staging
        // (no blurred backdrop baked in, no crop/pad) — the client draws the
        // backdrop, so portrait/square videos keep their real shape.
        Assert.Contains("force_original_aspect_ratio=decrease", filter);
        Assert.DoesNotContain("gblur", filter);
        Assert.DoesNotContain("crop", filter);
        Assert.DoesNotContain("overlay", filter);
    }

    [Fact]
    public async Task Preview_Strip_Uses_Six_Direct_Seeks_And_Contained_Frames_In_One_Jpeg()
    {
        var runner = new FakeProcessRunner(new ProcessRunResult(0, MinimalJpeg(), false));
        var provider = BuildProvider(runner);

        var result = await provider.TryGetPreviewStripAsync(
            EmptyVideoFactory(), durationSeconds: 60, CancellationToken.None);

        Assert.NotNull(result);
        var spec = new MediaDerivativesOptions().VideoPreviewStripSize;
        Assert.Equal(spec.FrameCount, result!.FrameCount);
        Assert.Equal(spec.Width, result.Width);
        Assert.Equal(spec.Height, result.Height);
        var request = runner.LastRequest!;
        var args = request.Arguments.ToList();
        var filter = args[args.IndexOf("-filter_complex") + 1];
        Assert.Contains($"hstack=inputs={spec.FrameCount}", filter);
        Assert.Contains(
            $"scale={spec.FrameWidth}:{spec.FrameHeight}:force_original_aspect_ratio=decrease",
            filter);
        Assert.Contains("gblur", filter);
        Assert.DoesNotContain("fps=", filter);
        Assert.DoesNotContain("-t", args);
        Assert.Equal(spec.FrameCount, args.Count(a => a == "-ss"));
        Assert.Equal(spec.FrameCount, args.Count(a => a == "-i"));
        Assert.Equal(45, request.TimeoutSeconds);

        var seeks = new List<double>();
        for (var index = 0; index < args.Count; index++)
        {
            if (args[index] != "-ss") continue;
            seeks.Add(double.Parse(args[index + 1], CultureInfo.InvariantCulture));
            Assert.Equal("-i", args[index + 2]);
        }
        Assert.Equal(new[] { 7.5, 16.5, 25.5, 34.5, 43.5, 52.5 }, seeks);
    }

    [Fact]
    public async Task Poster_And_Strip_Geometry_Comes_From_MediaDerivatives_Configuration()
    {
        var configured = new MediaDerivativesOptions
        {
            PosterWidth = 640,
            PosterHeight = 360,
            VideoPreviewFrameWidth = 160,
            VideoPreviewFrameHeight = 90,
        };
        var runner = new FakeProcessRunner(new ProcessRunResult(0, MinimalJpeg(), false));
        var provider = BuildProvider(runner, derivatives: configured);

        await provider.TryGetPosterAsync(EmptyVideoFactory(), CancellationToken.None);
        var posterArgs = runner.LastRequest!.Arguments.ToList();
        var posterFilter = posterArgs[posterArgs.IndexOf("-vf") + 1];
        // The poster fits within a maxEdge×maxEdge box (maxEdge = the larger of
        // the configured poster width/height) preserving the source aspect ratio.
        Assert.Contains("scale=640:640", posterFilter);
        Assert.DoesNotContain("crop=", posterFilter);

        var strip = await provider.TryGetPreviewStripAsync(
            EmptyVideoFactory(), durationSeconds: 60, CancellationToken.None);
        Assert.NotNull(strip);
        Assert.Equal(960, strip!.Width);
        Assert.Equal(90, strip.Height);
        Assert.Equal(6, strip.FrameCount);
        var stripArgs = runner.LastRequest!.Arguments.ToList();
        var stripFilter = stripArgs[stripArgs.IndexOf("-filter_complex") + 1];
        Assert.Contains("scale=160:90", stripFilter);
        Assert.Contains("hstack=inputs=6", stripFilter);
        Assert.Equal(6, stripArgs.Count(a => a == "-ss"));
    }

    [Fact]
    public async Task Preview_Strip_Uses_Local_FileStream_Directly_Without_Copying_Source()
    {
        var sourcePath = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(sourcePath, new byte[64]);
            var runner = new FakeProcessRunner(new ProcessRunResult(0, MinimalJpeg(), false));
            var provider = BuildProvider(runner);

            await provider.TryGetPreviewStripAsync(
                _ => Task.FromResult<Stream>(new FileStream(
                    sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read)),
                durationSeconds: 60,
                CancellationToken.None);

            var args = runner.LastRequest!.Arguments;
            var inputs = args
                .Select((argument, index) => (argument, index))
                .Where(item => item.argument == "-i")
                .Select(item => args[item.index + 1])
                .ToArray();
            Assert.Equal(6, inputs.Length);
            Assert.All(inputs, input => Assert.Equal(sourcePath, input));
            Assert.True(File.Exists(sourcePath));
        }
        finally
        {
            File.Delete(sourcePath);
        }
    }

    // --- Failure / fallback cases ---

    [Fact]
    public async Task Non_Zero_Exit_Falls_Back_To_Synthetic()
    {
        var runner = new FakeProcessRunner(new ProcessRunResult(1, [], false));
        var provider = BuildProvider(runner);

        var result = await provider.TryGetPosterAsync(EmptyVideoFactory(), CancellationToken.None);

        // Fallback to synthetic: must return a non-null JPEG marked synthetic.
        Assert.NotNull(result);
        Assert.Equal(VideoPosterSources.Synthetic, result!.Source);
        result.Content.Position = 0;
        Assert.Equal(0xFF, (byte)result.Content.ReadByte());
        Assert.Equal(0xD8, (byte)result.Content.ReadByte());
    }

    [Fact]
    public async Task Timeout_Falls_Back_To_Synthetic()
    {
        var runner = new FakeProcessRunner(new ProcessRunResult(-1, [], TimedOut: true));
        var provider = BuildProvider(runner);

        var result = await provider.TryGetPosterAsync(EmptyVideoFactory(), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(VideoPosterSources.Synthetic, result!.Source);
        result.Content.Position = 0;
        Assert.Equal(0xFF, (byte)result.Content.ReadByte());
    }

    [Fact]
    public async Task Empty_Output_Falls_Back_To_Synthetic()
    {
        var runner = new FakeProcessRunner(new ProcessRunResult(0, [], false));
        var provider = BuildProvider(runner);

        var result = await provider.TryGetPosterAsync(EmptyVideoFactory(), CancellationToken.None);

        Assert.NotNull(result);
    }

    [Fact]
    public async Task Non_Jpeg_Output_Falls_Back_To_Synthetic()
    {
        // PNG magic bytes — valid image but not JPEG.
        var pngMagic = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        var runner = new FakeProcessRunner(new ProcessRunResult(0, pngMagic, false));
        var provider = BuildProvider(runner);

        var result = await provider.TryGetPosterAsync(EmptyVideoFactory(), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(VideoPosterSources.Synthetic, result!.Source);
        result.Content.Position = 0;
        // Synthetic fallback gives FF D8
        Assert.Equal(0xFF, (byte)result.Content.ReadByte());
        Assert.Equal(0xD8, (byte)result.Content.ReadByte());
    }

    [Fact]
    public async Task Process_Runner_Exception_Falls_Back_To_Synthetic()
    {
        var runner = new FakeProcessRunner(new InvalidOperationException("ffmpeg not found"));
        var provider = BuildProvider(runner);

        var result = await provider.TryGetPosterAsync(EmptyVideoFactory(), CancellationToken.None);

        Assert.NotNull(result);
    }

    // --- Arguments ---

    [Fact]
    public async Task Uses_Configured_Seek_Seconds_In_Arguments()
    {
        var opts = new MediaOptions
        {
            VideoPosterProvider = "ffmpeg",
            FfmpegPath = "ffmpeg",
            VideoPosterTimeoutSeconds = 10,
            VideoPosterSeekSeconds = 7,
            VideoPosterMaxOutputBytes = 10 * 1024 * 1024,
        };
        var jpegBytes = MinimalJpeg();
        var runner = new FakeProcessRunner(new ProcessRunResult(0, jpegBytes, false));
        var provider = BuildProvider(runner, opts);

        await provider.TryGetPosterAsync(EmptyVideoFactory(), CancellationToken.None);

        var args = runner.LastRequest!.Arguments;
        var ssIndex = args.ToList().IndexOf("-ss");
        Assert.True(ssIndex >= 0, "Expected -ss argument");
        Assert.Equal("7", args[ssIndex + 1]);
    }

    [Fact]
    public async Task Uses_Configured_Ffmpeg_Path()
    {
        var opts = new MediaOptions
        {
            VideoPosterProvider = "ffmpeg",
            FfmpegPath = "/usr/local/bin/ffmpeg",
            VideoPosterTimeoutSeconds = 10,
            VideoPosterSeekSeconds = 3,
            VideoPosterMaxOutputBytes = 10 * 1024 * 1024,
        };
        var jpegBytes = MinimalJpeg();
        var runner = new FakeProcessRunner(new ProcessRunResult(0, jpegBytes, false));
        var provider = BuildProvider(runner, opts);

        await provider.TryGetPosterAsync(EmptyVideoFactory(), CancellationToken.None);

        Assert.Equal("/usr/local/bin/ffmpeg", runner.LastRequest!.Executable);
    }

    // --- No-leak: temp file name must not be in logged/process args ---

    [Fact]
    public async Task Temp_File_Argument_Has_No_Storage_Key_Pattern()
    {
        var jpegBytes = MinimalJpeg();
        var runner = new FakeProcessRunner(new ProcessRunResult(0, jpegBytes, false));
        var provider = BuildProvider(runner);

        await provider.TryGetPosterAsync(EmptyVideoFactory(), CancellationToken.None);

        // The -i argument should point to a temp file with no path containing
        // "objects/" (the storage shard pattern) or "sha256"-style 64-char hex.
        var args = runner.LastRequest!.Arguments.ToList();
        var iIndex = args.IndexOf("-i");
        Assert.True(iIndex >= 0);
        var inputArg = args[iIndex + 1];
        Assert.DoesNotContain("objects/", inputArg, StringComparison.OrdinalIgnoreCase);
        // Temp file name should be a GUID-based name, not a SHA-256.
        // It should be in the system temp dir.
        Assert.StartsWith(Path.GetTempPath(), inputArg, StringComparison.OrdinalIgnoreCase);
    }
}

// ---------------------------------------------------------------------------
// Test helpers

/// <summary>
/// Fake IProcessRunner that returns a pre-configured result or throws.
/// </summary>
public sealed class FakeProcessRunner : IProcessRunner
{
    private readonly ProcessRunResult? _result;
    private readonly Exception? _exception;

    public ProcessRunRequest? LastRequest { get; private set; }

    public FakeProcessRunner(ProcessRunResult result)
    {
        _result = result;
    }

    public FakeProcessRunner(Exception exception)
    {
        _exception = exception;
    }

    public Task<ProcessRunResult> RunAsync(
        ProcessRunRequest request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        if (_exception is not null)
            throw _exception;
        return Task.FromResult(_result!);
    }
}

/// <summary>
/// IProcessRunner returning a different result per call (and recording every
/// request), so a retry path can be asserted.
/// </summary>
public sealed class SequencedProcessRunner : IProcessRunner
{
    private readonly Queue<ProcessRunResult> _results;

    public List<ProcessRunRequest> Requests { get; } = [];

    public SequencedProcessRunner(params ProcessRunResult[] results)
        => _results = new Queue<ProcessRunResult>(results);

    public Task<ProcessRunResult> RunAsync(
        ProcessRunRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(_results.Count > 0
            ? _results.Dequeue()
            : new ProcessRunResult(1, [], false));
    }
}
