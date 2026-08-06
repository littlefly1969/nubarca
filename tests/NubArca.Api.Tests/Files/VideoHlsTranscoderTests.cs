using NubArca.Api.Files;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace NubArca.Api.Tests.Files;

// Video-hls slice 1 — ffmpeg HLS transcoder: argument shapes (validated
// against a real ffmpeg run), output validation, and failure mapping.
public sealed class VideoHlsTranscoderTests : IDisposable
{
    private readonly string _tempDir;

    public VideoHlsTranscoderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"nc-hls-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private static MediaOptions Options() => new()
    {
        VideoHlsProvider = "ffmpeg",
        VideoHlsSegmentSeconds = 4,
        VideoHlsHighMaxHeight = 1080,
        VideoHlsLowHeight = 480,
    };

    private static VideoHlsTranscodeRequest Request(
        string outputDir,
        bool copyVideo = true,
        bool copyAudio = true,
        bool hasAudio = true,
        bool includeLow = true)
        => new("/tmp/src.tmp", outputDir, copyVideo, copyAudio, hasAudio, includeLow);

    // ---- BuildArguments -----------------------------------------------------

    [Fact]
    public void Copy_With_Audio_And_Low_Builds_Two_Rendition_Copy_Ladder()
    {
        var args = FfmpegVideoHlsTranscoder.BuildArguments(Request(_tempDir), Options());
        var joined = string.Join(" ", args);

        Assert.Contains("-c:v:0 copy", joined);
        Assert.Contains("-c:a:0 copy", joined);
        // Low rung is always encoded (aspect-aware 480-class cap).
        Assert.Contains("-c:v:1 libx264", joined);
        Assert.Contains("-filter:v:1 scale=w='if(gt(a,1),-2,480)':h='if(gt(a,1),480,-2)'", joined);
        Assert.Contains("-c:a:1 aac", joined);
        Assert.Contains("v:0,a:0,name:high v:1,a:1,name:low", joined);
        // Two renditions with audio → four -map entries.
        Assert.Equal(4, args.Count(a => a == "-map"));
        Assert.Equal("%v/stream.m3u8", args[^1]);
    }

    [Fact]
    public void Encode_Without_Audio_Or_Low_Builds_Single_Video_Rendition()
    {
        var args = FfmpegVideoHlsTranscoder.BuildArguments(
            Request(_tempDir, copyVideo: false, copyAudio: false, hasAudio: false, includeLow: false),
            Options());
        var joined = string.Join(" ", args);

        Assert.Contains("-c:v:0 libx264", joined);
        // v2 aspect-aware cap: landscape caps the height, portrait the width.
        Assert.Contains("scale=w='if(gt(a,1),-2,min(1080,iw))':h='if(gt(a,1),min(1080,ih),-2)'", joined);
        Assert.Contains("-pix_fmt:v:0 yuv420p", joined);
        Assert.Contains("-var_stream_map v:0,name:high", joined);
        Assert.DoesNotContain("0:a:0", joined);
        Assert.DoesNotContain("aac", joined);
        Assert.DoesNotContain("name:low", joined);
        Assert.Single(args, a => a == "-map");
    }

    [Fact]
    public void Copy_Video_With_NonAac_Audio_Encodes_Audio_Only()
    {
        var args = FfmpegVideoHlsTranscoder.BuildArguments(
            Request(_tempDir, copyVideo: true, copyAudio: false), Options());
        var joined = string.Join(" ", args);

        Assert.Contains("-c:v:0 copy", joined);
        Assert.Contains("-c:a:0 aac", joined);
        Assert.DoesNotContain("-c:a:0 copy", joined);
    }

    [Fact]
    public void Arguments_Never_Use_Shell_Interpolation_Sensitive_Master_Name()
    {
        var args = FfmpegVideoHlsTranscoder.BuildArguments(Request(_tempDir), Options());
        // Fixed, non-derived output names only.
        Assert.Contains("master.m3u8", args);
        Assert.Contains("%v/seg-%d.m4s", args);
    }

    // ---- IsValidLadder ------------------------------------------------------

    private void WriteLadder(bool includeLow, bool withSegments = true)
    {
        File.WriteAllText(Path.Combine(_tempDir, "master.m3u8"), "#EXTM3U");
        foreach (var name in includeLow ? new[] { "high", "low" } : ["high"])
        {
            var dir = Path.Combine(_tempDir, name);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "stream.m3u8"), "#EXTM3U");
            File.WriteAllBytes(Path.Combine(dir, "init_0.mp4"), [0x00]);
            if (withSegments)
            {
                File.WriteAllBytes(Path.Combine(dir, "seg-0.m4s"), [0x00]);
            }
        }
    }

    [Fact]
    public void Complete_Two_Rendition_Ladder_Is_Valid()
    {
        WriteLadder(includeLow: true);
        Assert.True(FfmpegVideoHlsTranscoder.IsValidLadder(_tempDir, includeLowRendition: true));
    }

    [Fact]
    public void Missing_Master_Playlist_Is_Invalid()
    {
        WriteLadder(includeLow: true);
        File.Delete(Path.Combine(_tempDir, "master.m3u8"));
        Assert.False(FfmpegVideoHlsTranscoder.IsValidLadder(_tempDir, includeLowRendition: true));
    }

    [Fact]
    public void Missing_Segments_Are_Invalid()
    {
        WriteLadder(includeLow: false, withSegments: false);
        Assert.False(FfmpegVideoHlsTranscoder.IsValidLadder(_tempDir, includeLowRendition: false));
    }

    [Fact]
    public void Missing_Low_Rendition_Is_Invalid_Only_When_Expected()
    {
        WriteLadder(includeLow: false);
        Assert.True(FfmpegVideoHlsTranscoder.IsValidLadder(_tempDir, includeLowRendition: false));
        Assert.False(FfmpegVideoHlsTranscoder.IsValidLadder(_tempDir, includeLowRendition: true));
    }

    // ---- TranscodeAsync outcome mapping ------------------------------------

    private FfmpegVideoHlsTranscoder Transcoder(FakeDirectoryProcessRunner runner)
        => new(
            Microsoft.Extensions.Options.Options.Create(Options()),
            runner,
            NullLogger<FfmpegVideoHlsTranscoder>.Instance);

    [Fact]
    public async Task Timeout_Maps_To_Timeout_Code()
    {
        var runner = new FakeDirectoryProcessRunner(new ProcessDirectoryRunResult(-1, TimedOut: true));
        var result = await Transcoder(runner).TranscodeAsync(Request(_tempDir), default);
        Assert.False(result.Success);
        Assert.Equal(VideoHlsErrorCodes.Timeout, result.ErrorCode);
    }

    [Fact]
    public async Task NonZero_Exit_Maps_To_Transcode_Failed()
    {
        var runner = new FakeDirectoryProcessRunner(new ProcessDirectoryRunResult(1, TimedOut: false));
        var result = await Transcoder(runner).TranscodeAsync(Request(_tempDir), default);
        Assert.False(result.Success);
        Assert.Equal(VideoHlsErrorCodes.TranscodeFailed, result.ErrorCode);
    }

    [Fact]
    public async Task Zero_Exit_With_Empty_Output_Maps_To_Invalid_Output()
    {
        var runner = new FakeDirectoryProcessRunner(new ProcessDirectoryRunResult(0, TimedOut: false));
        var result = await Transcoder(runner).TranscodeAsync(Request(_tempDir), default);
        Assert.False(result.Success);
        Assert.Equal(VideoHlsErrorCodes.InvalidOutput, result.ErrorCode);
    }

    [Fact]
    public async Task Zero_Exit_With_Complete_Ladder_Succeeds()
    {
        var runner = new FakeDirectoryProcessRunner(
            new ProcessDirectoryRunResult(0, TimedOut: false),
            onRun: _ => WriteLadder(includeLow: true));
        var result = await Transcoder(runner).TranscodeAsync(Request(_tempDir), default);
        Assert.True(result.Success);
        Assert.Null(result.ErrorCode);
        // The run used the configured working directory + timeout.
        Assert.Equal(_tempDir, runner.LastRequest!.WorkingDirectory);
    }
}

/// <summary>
/// Fake IDirectoryProcessRunner returning a pre-configured result, optionally
/// materializing output files first (like a real ffmpeg run would).
/// </summary>
public sealed class FakeDirectoryProcessRunner : IDirectoryProcessRunner
{
    private readonly ProcessDirectoryRunResult _result;
    private readonly Action<ProcessDirectoryRunRequest>? _onRun;

    public ProcessDirectoryRunRequest? LastRequest { get; private set; }
    public int RunCount { get; private set; }

    public FakeDirectoryProcessRunner(
        ProcessDirectoryRunResult result,
        Action<ProcessDirectoryRunRequest>? onRun = null)
    {
        _result = result;
        _onRun = onRun;
    }

    public Task<ProcessDirectoryRunResult> RunAsync(
        ProcessDirectoryRunRequest request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        RunCount++;
        _onRun?.Invoke(request);
        return Task.FromResult(_result);
    }
}
