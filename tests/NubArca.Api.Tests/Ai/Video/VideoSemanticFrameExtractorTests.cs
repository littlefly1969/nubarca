using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NubArca.Api.Ai.Video;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Files;
using NubArca.Api.Tests.Files;
using Xunit;

namespace NubArca.Api.Tests.Ai.Video;

// VSEM-02: the FFmpeg frame-extraction boundary. Pins the SAFETY contract
// (separate tokens, opaque staged input, stdout-only frames, autorotation on,
// aspect preserved, bounded sequential processes, cleanup) and the per-sample
// failure isolation. A fake process runner stands in for FFmpeg — same
// convention as the scene-detector and poster tests.
public sealed class VideoSemanticFrameExtractorTests
{
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10];

    // The extractor no longer reads a resolution from configuration: every
    // caller supplies its own. These tests pass an explicit edge, and
    // Frame_Resolution_Comes_From_The_Caller pins that it is honoured.
    private const int Edge = 768;

    private static FfmpegVideoSemanticFrameExtractor Build(
        IProcessRunner runner, VideoVisualEmbeddingOptions? options = null)
        => new(
            Options.Create(options ?? new VideoVisualEmbeddingOptions()),
            Options.Create(new MediaOptions { FfmpegPath = "ffmpeg" }),
            runner,
            NullLogger<FfmpegVideoSemanticFrameExtractor>.Instance);

    private static Func<CancellationToken, Task<Stream>> Content(byte[]? bytes = null)
        => _ => Task.FromResult<Stream>(new MemoryStream(bytes ?? new byte[512]));

    private static VideoSemanticFrameRequest Request(long ms)
        => new(Guid.NewGuid(), ms);

    // ---- invocation safety -------------------------------------------------

    [Fact]
    public async Task Arguments_Are_Separate_Tokens_With_Stdout_Output_And_Autorotation_On()
    {
        var runner = new SequencedProcessRunner(new ProcessRunResult(0, Jpeg, false));
        await Build(runner).ExtractFramesAsync(Content(), [Request(12_345)], Edge, CancellationToken.None);

        var request = Assert.Single(runner.Requests);
        Assert.Equal("ffmpeg", request.Executable);
        Assert.Contains("-nostdin", request.Arguments);
        Assert.All(request.Arguments, a => Assert.DoesNotContain(";", a));
        Assert.All(request.Arguments, a => Assert.DoesNotContain("|", a));

        // Autorotation must stay ON: the flag that disables it never appears.
        Assert.DoesNotContain("-noautorotate", request.Arguments);

        // The frame goes to stdout — no output filename exists at all, so no
        // persistent frame derivative can be created.
        Assert.Equal("pipe:1", request.Arguments[^1]);
    }

    [Fact]
    public async Task Seek_Is_Input_Side_And_Invariantly_Formatted()
    {
        var runner = new SequencedProcessRunner(new ProcessRunResult(0, Jpeg, false));
        await Build(runner).ExtractFramesAsync(Content(), [Request(12_345)], Edge, CancellationToken.None);

        var args = runner.Requests[0].Arguments.ToList();
        var ss = args.IndexOf("-ss");
        var input = args.IndexOf("-i");

        Assert.True(ss >= 0 && ss < input, "-ss must precede -i (accurate input-side seek).");
        Assert.Equal("12.345", args[ss + 1]);      // invariant, millisecond precision
        Assert.StartsWith(Path.GetTempPath(), args[input + 1]);
        Assert.Contains("nc-vframe-", args[input + 1]);
        Assert.DoesNotContain("://", args[input + 1]);
    }

    [Theory]
    [InlineData(0, "0")]
    [InlineData(59_999, "59.999")]
    public async Task First_And_Final_Timestamps_Format_Exactly(long ms, string expected)
    {
        var runner = new SequencedProcessRunner(new ProcessRunResult(0, Jpeg, false));
        await Build(runner).ExtractFramesAsync(Content(), [Request(ms)], Edge, CancellationToken.None);

        var args = runner.Requests[0].Arguments.ToList();
        Assert.Equal(expected, args[args.IndexOf("-ss") + 1]);
    }

    [Fact]
    public void Scale_Filter_Preserves_Source_Aspect_With_No_Crop()
    {
        var args = FfmpegVideoSemanticFrameExtractor.BuildFrameArguments("/tmp/x.tmp", 500, 768);
        var filter = args[args.ToList().IndexOf("-vf") + 1];

        Assert.Equal("scale=768:768:force_original_aspect_ratio=decrease,setsar=1", filter);
        Assert.DoesNotContain("crop", filter);
        Assert.DoesNotContain("pad", filter);
    }

    [Theory]
    [InlineData(640)]
    [InlineData(768)]
    [InlineData(1280)]
    public async Task Frame_Resolution_Comes_From_The_Caller_Not_From_Configuration(int edge)
    {
        // VFACE-01C: the extractor is shared by pipelines with different
        // resolution needs, so the edge is an ARGUMENT. A configuration object
        // whose own historical FrameMaxEdge disagrees must not influence it.
        var runner = new SequencedProcessRunner(new ProcessRunResult(0, Jpeg, false));
        var options = new VideoVisualEmbeddingOptions { FrameMaxEdge = 4096 };

        await Build(runner, options).ExtractFramesAsync(
            Content(), [Request(1000)], edge, CancellationToken.None);

        var args = runner.Requests[0].Arguments.ToList();
        Assert.Equal(
            $"scale={edge}:{edge}:force_original_aspect_ratio=decrease,setsar=1",
            args[args.IndexOf("-vf") + 1]);
        Assert.DoesNotContain("4096", args[args.IndexOf("-vf") + 1]);
    }

    [Fact]
    public async Task The_Streaming_Form_Honours_The_Caller_Resolution_Too()
    {
        var runner = new SequencedProcessRunner(new ProcessRunResult(0, Jpeg, false));

        await Build(runner).ExtractFramesStreamingAsync(
            Content(), [Request(1000)], 1024, (_, _) => Task.CompletedTask, CancellationToken.None);

        var args = runner.Requests[0].Arguments.ToList();
        Assert.Equal(
            "scale=1024:1024:force_original_aspect_ratio=decrease,setsar=1",
            args[args.IndexOf("-vf") + 1]);
    }

    [Fact]
    public async Task Timeout_And_Output_Cap_Come_From_Options()
    {
        var runner = new SequencedProcessRunner(new ProcessRunResult(0, Jpeg, false));
        await Build(runner, new VideoVisualEmbeddingOptions
        {
            FrameTimeoutSeconds = 42, MaximumFrameOutputBytes = 8192,
        }).ExtractFramesAsync(Content(), [Request(1000)], Edge, CancellationToken.None);

        Assert.Equal(42, runner.Requests[0].TimeoutSeconds);
        Assert.Equal(8192, runner.Requests[0].MaxStdoutBytes);
    }

    // ---- staging + process bounds ------------------------------------------

    [Fact]
    public async Task Source_Is_Staged_Once_And_Reused_For_Every_Sample()
    {
        var opened = 0;
        Func<CancellationToken, Task<Stream>> content = _ =>
        {
            opened++;
            return Task.FromResult<Stream>(new MemoryStream(new byte[64]));
        };

        var runner = new SequencedProcessRunner(
            new ProcessRunResult(0, Jpeg, false),
            new ProcessRunResult(0, Jpeg, false),
            new ProcessRunResult(0, Jpeg, false));
        await Build(runner).ExtractFramesAsync(
            content, [Request(1000), Request(2000), Request(3000)], Edge, CancellationToken.None);

        Assert.Equal(1, opened);   // one staging, three seeks
        var inputs = runner.Requests
            .Select(r => r.Arguments[r.Arguments.ToList().IndexOf("-i") + 1])
            .Distinct().ToList();
        Assert.Single(inputs);     // every process reads the SAME staged file
    }

    [Fact]
    public async Task Process_Count_Equals_The_Requested_Sample_Count()
    {
        var runner = new SequencedProcessRunner(
            new ProcessRunResult(0, Jpeg, false),
            new ProcessRunResult(0, Jpeg, false));
        var result = await Build(runner).ExtractFramesAsync(
            Content(), [Request(1000), Request(2000)], Edge, CancellationToken.None);

        Assert.Equal(2, runner.Requests.Count);
        Assert.Equal(2, result.Frames.Count);
        Assert.All(result.Frames, f => Assert.True(f.Succeeded));
    }

    [Fact]
    public async Task Staging_Failure_Runs_No_Process_And_Is_A_Batch_Level_Blob_Storage_Failure()
    {
        var runner = new SequencedProcessRunner();
        var result = await Build(runner).ExtractFramesAsync(
            _ => throw new FileNotFoundException("/storage/objects/ab/cd/deadbeef"),
            [Request(1000)], Edge, CancellationToken.None);

        Assert.False(result.Staged);
        Assert.Equal(VideoSemanticErrorCodes.BlobStorage, result.StagingErrorCode);
        Assert.Empty(result.Frames);
        Assert.Empty(runner.Requests);
    }

    // ---- per-sample failure isolation --------------------------------------

    [Fact]
    public async Task A_Failed_Seek_Never_Poisons_The_Other_Samples()
    {
        var runner = new SequencedProcessRunner(
            new ProcessRunResult(0, Jpeg, false),
            new ProcessRunResult(1, [], false),                  // decode failure
            new ProcessRunResult(0, Jpeg, false));
        var result = await Build(runner).ExtractFramesAsync(
            Content(), [Request(1000), Request(2000), Request(3000)], Edge, CancellationToken.None);

        Assert.True(result.Frames[0].Succeeded);
        Assert.False(result.Frames[1].Succeeded);
        Assert.Equal(VideoSemanticErrorCodes.FrameExtraction, result.Frames[1].ErrorCode);
        Assert.True(result.Frames[2].Succeeded);
    }

    [Fact]
    public async Task Timeout_Truncation_And_Garbage_Output_Are_Classified_Per_Sample()
    {
        var runner = new SequencedProcessRunner(
            new ProcessRunResult(-1, [], TimedOut: true),
            new ProcessRunResult(0, [], false, OutputTruncated: true),
            new ProcessRunResult(0, [0x00, 0x01, 0x02, 0x03], false));   // not a JPEG
        var result = await Build(runner).ExtractFramesAsync(
            Content(), [Request(1000), Request(2000), Request(3000)], Edge, CancellationToken.None);

        Assert.Equal(VideoSemanticErrorCodes.ProcessTimeout, result.Frames[0].ErrorCode);
        Assert.Equal(VideoSemanticErrorCodes.ProcessOutputLimit, result.Frames[1].ErrorCode);
        Assert.Equal(VideoSemanticErrorCodes.FrameExtraction, result.Frames[2].ErrorCode);
    }

    [Fact]
    public async Task Invalid_Timestamp_Is_Rejected_Without_A_Process_Run()
    {
        var runner = new SequencedProcessRunner(new ProcessRunResult(0, Jpeg, false));
        var result = await Build(runner).ExtractFramesAsync(
            Content(), [Request(-1), Request(1000)], Edge, CancellationToken.None);

        Assert.False(result.Frames[0].Succeeded);
        Assert.Equal(VideoSemanticErrorCodes.FrameExtraction, result.Frames[0].ErrorCode);
        Assert.Single(runner.Requests);   // only the valid timestamp ran
        Assert.True(result.Frames[1].Succeeded);
    }

    [Fact]
    public async Task A_Start_Failure_Stops_Spawning_And_Marks_The_Remaining_Samples()
    {
        var runner = new CountingThrowingRunner();
        var result = await Build(runner).ExtractFramesAsync(
            Content(), [Request(1000), Request(2000), Request(3000)], Edge, CancellationToken.None);

        Assert.Equal(1, runner.Calls);   // the missing binary is not retried per sample
        Assert.All(result.Frames, f =>
            Assert.Equal(VideoSemanticErrorCodes.ProcessStart, f.ErrorCode));
    }

    // ---- cancellation + cleanup --------------------------------------------

    [Fact]
    public async Task Cancellation_Propagates_And_Cleans_The_Staged_File()
    {
        var before = CountTempInputs();
        using var cts = new CancellationTokenSource();

        var runner = new CancellingRunner(cts);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Build(runner).ExtractFramesAsync(
                Content(), [Request(1000), Request(2000)], Edge, cts.Token));

        Assert.Equal(before, CountTempInputs());
    }

    [Fact]
    public async Task Staged_File_Is_Deleted_On_Success_And_On_Failure()
    {
        var before = CountTempInputs();

        await Build(new SequencedProcessRunner(new ProcessRunResult(0, Jpeg, false)))
            .ExtractFramesAsync(Content(), [Request(1000)], Edge, CancellationToken.None);
        await Build(new SequencedProcessRunner(new ProcessRunResult(1, [], false)))
            .ExtractFramesAsync(Content(), [Request(1000)], Edge, CancellationToken.None);

        Assert.Equal(before, CountTempInputs());
    }

    [Fact]
    public async Task An_Empty_Request_List_Is_A_No_Op()
    {
        var opened = 0;
        Func<CancellationToken, Task<Stream>> content = _ =>
        {
            opened++;
            return Task.FromResult<Stream>(new MemoryStream());
        };

        var runner = new SequencedProcessRunner();
        var result = await Build(runner).ExtractFramesAsync(content, [], Edge, CancellationToken.None);

        Assert.True(result.Staged);
        Assert.Empty(result.Frames);
        Assert.Equal(0, opened);          // nothing staged
        Assert.Empty(runner.Requests);    // nothing spawned
    }

    // ---- helpers -----------------------------------------------------------

    private static int CountTempInputs()
        => Directory.EnumerateFiles(Path.GetTempPath(), "nc-vframe-*.tmp").Count();

    private sealed class CountingThrowingRunner : IProcessRunner
    {
        public int Calls { get; private set; }

        public Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            throw new System.ComponentModel.Win32Exception("/usr/bin/ffmpeg: No such file or directory");
        }
    }

    // Succeeds on the first sample, then cancels the batch: the second
    // iteration must observe the token and throw.
    private sealed class CancellingRunner : IProcessRunner
    {
        private readonly CancellationTokenSource _cts;

        public CancellingRunner(CancellationTokenSource cts) => _cts = cts;

        public Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken)
        {
            _cts.Cancel();
            return Task.FromResult(new ProcessRunResult(0, [0xFF, 0xD8, 0xFF, 0xE0], false));
        }
    }
}
