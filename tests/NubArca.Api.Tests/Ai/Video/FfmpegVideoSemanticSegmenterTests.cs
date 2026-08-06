using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NubArca.Api.Ai.Video;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Files;
using NubArca.Api.Tests.Files;
using Xunit;

namespace NubArca.Api.Tests.Ai.Video;

// VSEM-01: the FFmpeg boundary. These tests pin the SAFETY contract of the
// invocation (separate argument tokens, no shell, opaque temp input, bounded
// output, sanitized errors, cancellation, temp cleanup) as much as the parsing.
public sealed class FfmpegVideoSemanticSegmenterTests
{
    private static FfmpegVideoSemanticSegmenter Build(
        IProcessRunner runner, VideoSemanticSegmentationOptions? options = null)
        => new(
            Options.Create(options ?? new VideoSemanticSegmentationOptions()),
            Options.Create(new MediaOptions { FfmpegPath = "ffmpeg" }),
            runner,
            NullLogger<FfmpegVideoSemanticSegmenter>.Instance);

    private static Func<CancellationToken, Task<Stream>> Content(byte[]? bytes = null)
        => _ => Task.FromResult<Stream>(new MemoryStream(bytes ?? new byte[512]));

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    private const string NormalOutput =
        "frame:0    pts:0        pts_time:0\n" +
        "lavfi.scene_score=0.000000\n" +
        "frame:120  pts:122880   pts_time:5.12\n" +
        "lavfi.scene_score=0.512345\n" +
        "frame:640  pts:655360   pts_time:27.3067\n" +
        "lavfi.scene_score=0.734000\n";

    // ---- invocation safety -------------------------------------------------

    [Fact]
    public async Task Arguments_Are_Separate_Tokens_With_No_Shell_Concatenation()
    {
        var runner = new FakeProcessRunner(new ProcessRunResult(0, [], false));
        var segmenter = Build(runner);

        await segmenter.DetectSceneCandidatesAsync(Content(), CancellationToken.None);

        var request = Assert.IsType<ProcessRunRequest>(runner.LastRequest);
        Assert.Equal("ffmpeg", request.Executable);

        // Every flag and its value is its own token — nothing is space-joined,
        // nothing is quoted, so there is no shell to escape from.
        Assert.Contains("-nostdin", request.Arguments);
        Assert.Contains("-i", request.Arguments);
        Assert.Equal("error", request.Arguments[request.Arguments.ToList().IndexOf("-v") + 1]);
        Assert.All(request.Arguments, arg => Assert.DoesNotContain(";", arg));
        Assert.All(request.Arguments, arg => Assert.DoesNotContain("|", arg));
        Assert.All(request.Arguments, arg => Assert.DoesNotContain("&&", arg));
    }

    [Fact]
    public async Task Input_Is_An_Opaque_Temp_Path_Never_A_Url_Or_Other_Protocol()
    {
        var runner = new FakeProcessRunner(new ProcessRunResult(0, [], false));
        var segmenter = Build(runner);

        await segmenter.DetectSceneCandidatesAsync(Content(), CancellationToken.None);

        var args = runner.LastRequest!.Arguments;
        var input = args[args.ToList().IndexOf("-i") + 1];

        Assert.StartsWith(Path.GetTempPath(), input);
        Assert.Contains("nc-vseg-", input);
        Assert.DoesNotContain("://", input);   // no http/rtmp/concat/file protocol
    }

    [Fact]
    public void Filter_Graph_Is_Constant_Apart_From_The_Numeric_Threshold()
    {
        var args = FfmpegVideoSemanticSegmenter.BuildArguments("/tmp/x.tmp", 0.4);
        var filter = args[args.ToList().IndexOf("-filter:v") + 1];

        Assert.Equal("select='gt(scene,0.4)',metadata=print:file=-", filter);
    }

    [Fact]
    public void Threshold_Is_Formatted_Invariantly_Regardless_Of_Culture()
    {
        // A comma decimal separator would produce an unparseable filter graph.
        var args = FfmpegVideoSemanticSegmenter.BuildArguments("/tmp/x.tmp", 0.35);
        var filter = args[args.ToList().IndexOf("-filter:v") + 1];

        Assert.Contains("0.35", filter);
        Assert.DoesNotContain(",0,35", filter);
    }

    [Fact]
    public async Task Timeout_And_Output_Cap_Come_From_Options()
    {
        var runner = new FakeProcessRunner(new ProcessRunResult(0, [], false));
        var segmenter = Build(runner, new VideoSemanticSegmentationOptions
        {
            ProcessTimeoutSeconds = 123,
            MaximumProcessOutputBytes = 4096,
        });

        await segmenter.DetectSceneCandidatesAsync(Content(), CancellationToken.None);

        Assert.Equal(123, runner.LastRequest!.TimeoutSeconds);
        Assert.Equal(4096, runner.LastRequest!.MaxStdoutBytes);
    }

    // ---- output handling ---------------------------------------------------

    [Fact]
    public async Task Normal_Output_Yields_The_Scene_Timestamps()
    {
        var segmenter = Build(new FakeProcessRunner(new ProcessRunResult(0, Utf8(NormalOutput), false)));

        var result = await segmenter.DetectSceneCandidatesAsync(Content(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Null(result.ErrorCode);
        Assert.Equal(new[] { 0d, 5.12d, 27.3067d }, result.CandidateSeconds);
    }

    [Fact]
    public async Task Empty_Output_Is_A_Success_With_No_Candidates()
    {
        // A single-shot video is not a failure — it is the input to the uniform
        // fallback.
        var segmenter = Build(new FakeProcessRunner(new ProcessRunResult(0, [], false)));

        var result = await segmenter.DetectSceneCandidatesAsync(Content(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Empty(result.CandidateSeconds);
    }

    [Fact]
    public async Task Malformed_Output_Is_Ignored_Line_By_Line()
    {
        const string garbage =
            "not a metadata line at all\n" +
            "pts_time:\n" +                    // marker with no value
            "pts_time:abc\n" +                 // unparseable value
            "pts_time:NaN\n" +                 // non-finite
            "frame:9 pts:1 pts_time:3.5\n" +   // the one good line
            "\n";

        var segmenter = Build(new FakeProcessRunner(new ProcessRunResult(0, Utf8(garbage), false)));

        var result = await segmenter.DetectSceneCandidatesAsync(Content(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(new[] { 3.5d }, result.CandidateSeconds);
    }

    [Fact]
    public async Task Partial_Final_Line_Is_Discarded()
    {
        // A process killed mid-write leaves a truncated last line. "pts_time:1"
        // could really be "…:12.5" — parsing it would invent a boundary.
        const string partial =
            "frame:1 pts:1 pts_time:4.0\n" +
            "frame:2 pts:2 pts_time:1";

        var segmenter = Build(new FakeProcessRunner(new ProcessRunResult(0, Utf8(partial), false)));

        var result = await segmenter.DetectSceneCandidatesAsync(Content(), CancellationToken.None);

        Assert.Equal(new[] { 4.0d }, result.CandidateSeconds);
    }

    // ---- failure classification --------------------------------------------

    [Fact]
    public async Task Excessive_Output_Is_A_Retryable_Output_Limit_Failure()
    {
        // The runner caps stdout and reports truncation: the result must NOT be
        // read as "no candidates" (which would silently produce a uniform
        // manifest for a video that really has scenes).
        var segmenter = Build(new FakeProcessRunner(new ProcessRunResult(0, [], false, OutputTruncated: true)));

        var result = await segmenter.DetectSceneCandidatesAsync(Content(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(VideoSemanticErrorCodes.ProcessOutputLimit, result.ErrorCode);
        Assert.Empty(result.CandidateSeconds);
    }

    [Fact]
    public async Task Non_Zero_Exit_Reports_Process_Exit_With_A_Safe_Exit_Code()
    {
        var segmenter = Build(new FakeProcessRunner(new ProcessRunResult(69, Utf8("ignored"), false)));

        var result = await segmenter.DetectSceneCandidatesAsync(Content(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(VideoSemanticErrorCodes.ProcessExit, result.ErrorCode);
        Assert.Equal(69, result.ProcessExitCode);
    }

    [Fact]
    public async Task Process_Start_Failure_Is_Classified_And_Sanitized()
    {
        // The message deliberately contains a path: it must not survive into
        // the error code (the only thing the caller ever sees).
        var segmenter = Build(new FakeProcessRunner(
            new System.ComponentModel.Win32Exception("/usr/bin/ffmpeg: No such file or directory")));

        var result = await segmenter.DetectSceneCandidatesAsync(Content(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(VideoSemanticErrorCodes.ProcessStart, result.ErrorCode);
        Assert.DoesNotContain("/usr/bin", result.ErrorCode);
    }

    [Fact]
    public async Task Timeout_Is_Classified_As_Process_Timeout()
    {
        var segmenter = Build(new FakeProcessRunner(new ProcessRunResult(-1, [], TimedOut: true)));

        var result = await segmenter.DetectSceneCandidatesAsync(Content(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(VideoSemanticErrorCodes.ProcessTimeout, result.ErrorCode);
    }

    [Fact]
    public async Task Blob_Open_Failure_Is_Classified_As_Blob_Storage()
    {
        var segmenter = Build(new FakeProcessRunner(new ProcessRunResult(0, [], false)));

        var result = await segmenter.DetectSceneCandidatesAsync(
            _ => throw new FileNotFoundException("/storage/objects/ab/cd/deadbeef"),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(VideoSemanticErrorCodes.BlobStorage, result.ErrorCode);
    }

    [Fact]
    public async Task Cancellation_Propagates_And_Is_Never_Converted_To_A_Failure()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var segmenter = Build(new FakeProcessRunner(new ProcessRunResult(0, [], false)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            segmenter.DetectSceneCandidatesAsync(
                ct => { ct.ThrowIfCancellationRequested(); return Task.FromResult<Stream>(new MemoryStream()); },
                cts.Token));
    }

    // ---- housekeeping ------------------------------------------------------

    [Fact]
    public async Task Temporary_Input_File_Is_Deleted_On_Success_And_On_Failure()
    {
        var before = CountTempInputs();

        var ok = Build(new FakeProcessRunner(new ProcessRunResult(0, Utf8(NormalOutput), false)));
        await ok.DetectSceneCandidatesAsync(Content(), CancellationToken.None);

        var bad = Build(new FakeProcessRunner(new ProcessRunResult(1, [], false)));
        await bad.DetectSceneCandidatesAsync(Content(), CancellationToken.None);

        Assert.Equal(before, CountTempInputs());
    }

    [Fact]
    public async Task No_Frame_Is_Ever_Written_To_Disk()
    {
        var runner = new FakeProcessRunner(new ProcessRunResult(0, Utf8(NormalOutput), false));
        var segmenter = Build(runner);

        await segmenter.DetectSceneCandidatesAsync(Content(), CancellationToken.None);

        // `-f null -` discards the decoded frames; there is no output path
        // argument at all, so no persistent frame derivative can be created.
        var args = runner.LastRequest!.Arguments;
        Assert.Equal("null", args[args.ToList().IndexOf("-f") + 1]);
        Assert.Equal("-", args[^1]);
    }

    private static int CountTempInputs()
        => Directory.EnumerateFiles(Path.GetTempPath(), "nc-vseg-*.tmp").Count();
}
