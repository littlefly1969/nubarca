using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Metadata;
using NubArca.Api.Tests.Files; // FakeProcessRunner
using Xunit;

namespace NubArca.Api.Tests.Metadata;

// ffprobe extractor unit tests. All use a FakeProcessRunner feeding canned
// ffprobe JSON — no real binary required.
public sealed class FfprobeVideoMetadataExtractorTests
{
    private static readonly MediaOptions DefaultOpts = new()
    {
        VideoMetadataProvider = "ffprobe",
        FfprobePath = "ffprobe",
        VideoProbeTimeoutSeconds = 10,
        VideoProbeMaxOutputBytes = 4 * 1024 * 1024,
    };

    private static FfprobeVideoMetadataExtractor Build(FakeProcessRunner runner, MediaOptions? opts = null)
        => new(Options.Create(opts ?? DefaultOpts), runner, NullLogger<FfprobeVideoMetadataExtractor>.Instance);

    private static Func<CancellationToken, Task<Stream>> Blob()
        => _ => Task.FromResult<Stream>(new MemoryStream(new byte[64]));

    private static FakeProcessRunner Json(string json)
        => new(new ProcessRunResult(0, Encoding.UTF8.GetBytes(json), false));

    [Fact]
    public async Task Maps_Video_And_Audio_Streams()
    {
        const string json = """
        {
          "streams": [
            {"codec_type":"video","codec_name":"h264","width":1920,"height":1080,
             "avg_frame_rate":"30000/1001","bit_rate":"5000000"},
            {"codec_type":"audio","codec_name":"aac","channels":2,"sample_rate":"48000"}
          ],
          "format": {"duration":"12.500","bit_rate":"5200000",
                     "tags":{"creation_time":"2023-01-02T03:04:05.000000Z"}}
        }
        """;
        var result = await Build(Json(json)).ExtractAsync(Blob(), CancellationToken.None);

        Assert.Equal(MetadataStatuses.Completed, result.Status);
        Assert.Null(result.ErrorCode);
        Assert.Equal(1920, result.Width);
        Assert.Equal(1080, result.Height);
        Assert.Equal("h264", result.VideoCodec);
        Assert.Equal(5000000L, result.VideoBitrate);
        Assert.Equal(12.5, result.DurationSeconds);
        Assert.NotNull(result.FrameRate);
        Assert.Equal(29.97, result.FrameRate!.Value, precision: 2);
        Assert.True(result.HasAudio);
        Assert.Equal("aac", result.AudioCodec);
        Assert.Equal(2, result.AudioChannels);
        Assert.Equal(48000, result.AudioSampleRate);
        Assert.Equal(new DateTime(2023, 1, 2, 3, 4, 5, DateTimeKind.Utc), result.CreationTime);
        Assert.Equal(FfprobeVideoMetadataExtractor.Version, result.Version);
    }

    [Fact]
    public async Task Video_Only_Has_No_Audio()
    {
        const string json = """
        {"streams":[{"codec_type":"video","codec_name":"hevc","width":640,"height":480,
          "avg_frame_rate":"25/1"}],"format":{"duration":"3.0"}}
        """;
        var result = await Build(Json(json)).ExtractAsync(Blob(), CancellationToken.None);

        Assert.Equal(MetadataStatuses.Completed, result.Status);
        Assert.False(result.HasAudio);
        Assert.Null(result.AudioCodec);
        Assert.Null(result.AudioChannels);
        Assert.Equal(25.0, result.FrameRate);
    }

    [Fact]
    public async Task Reads_Rotation_From_Side_Data_And_Normalizes_Negative()
    {
        const string json = """
        {"streams":[{"codec_type":"video","codec_name":"h264","width":1080,"height":1920,
          "side_data_list":[{"rotation":-90}]}],"format":{"duration":"1.0"}}
        """;
        var result = await Build(Json(json)).ExtractAsync(Blob(), CancellationToken.None);

        Assert.Equal(270, result.Rotation);
    }

    [Fact]
    public async Task Reads_Rotation_From_Tags_When_No_Side_Data()
    {
        const string json = """
        {"streams":[{"codec_type":"video","codec_name":"h264","width":100,"height":200,
          "tags":{"rotate":"90"}}],"format":{"duration":"1.0"}}
        """;
        var result = await Build(Json(json)).ExtractAsync(Blob(), CancellationToken.None);

        Assert.Equal(90, result.Rotation);
    }

    [Fact]
    public async Task Zero_Over_Zero_Frame_Rate_Is_Null()
    {
        const string json = """
        {"streams":[{"codec_type":"video","codec_name":"h264","width":100,"height":100,
          "avg_frame_rate":"0/0"}],"format":{"duration":"1.0"}}
        """;
        var result = await Build(Json(json)).ExtractAsync(Blob(), CancellationToken.None);

        Assert.Null(result.FrameRate);
    }

    [Fact]
    public async Task No_Video_Stream_Is_Skipped_Unsupported()
    {
        const string json = """
        {"streams":[{"codec_type":"audio","codec_name":"mp3","channels":2}],
         "format":{"duration":"180.0"}}
        """;
        var result = await Build(Json(json)).ExtractAsync(Blob(), CancellationToken.None);

        Assert.Equal(MetadataStatuses.Skipped, result.Status);
        Assert.Equal(MetadataErrorCodes.UnsupportedFormat, result.ErrorCode);
    }

    [Fact]
    public async Task Attached_Picture_Is_Not_Treated_As_Video()
    {
        // Audio file with embedded cover art: the "video" stream is an
        // attached_pic and must be ignored → no real video → Skipped.
        const string json = """
        {"streams":[
          {"codec_type":"video","codec_name":"mjpeg","width":300,"height":300,
           "disposition":{"attached_pic":1}},
          {"codec_type":"audio","codec_name":"aac","channels":2}],
         "format":{"duration":"200.0"}}
        """;
        var result = await Build(Json(json)).ExtractAsync(Blob(), CancellationToken.None);

        Assert.Equal(MetadataStatuses.Skipped, result.Status);
    }

    [Fact]
    public async Task Non_Zero_Exit_Is_Probe_Failed()
    {
        var runner = new FakeProcessRunner(new ProcessRunResult(1, [], false));
        var result = await Build(runner).ExtractAsync(Blob(), CancellationToken.None);

        Assert.Equal(MetadataStatuses.Failed, result.Status);
        Assert.Equal(MetadataErrorCodes.ProbeFailed, result.ErrorCode);
    }

    [Fact]
    public async Task Timeout_Is_Recorded_As_Timeout_Failure()
    {
        var runner = new FakeProcessRunner(new ProcessRunResult(-1, [], TimedOut: true));
        var result = await Build(runner).ExtractAsync(Blob(), CancellationToken.None);

        Assert.Equal(MetadataStatuses.Failed, result.Status);
        Assert.Equal(MetadataErrorCodes.Timeout, result.ErrorCode);
    }

    [Fact]
    public async Task Garbage_Json_Is_Failed()
    {
        var runner = new FakeProcessRunner(new ProcessRunResult(0, Encoding.UTF8.GetBytes("not json{"), false));
        var result = await Build(runner).ExtractAsync(Blob(), CancellationToken.None);

        Assert.Equal(MetadataStatuses.Failed, result.Status);
    }

    [Fact]
    public async Task Runner_Exception_Is_Io_Error()
    {
        var runner = new FakeProcessRunner(new InvalidOperationException("ffprobe not found"));
        var result = await Build(runner).ExtractAsync(Blob(), CancellationToken.None);

        Assert.Equal(MetadataStatuses.Failed, result.Status);
        Assert.Equal(MetadataErrorCodes.IoError, result.ErrorCode);
    }

    [Fact]
    public async Task Uses_Configured_Ffprobe_Path_And_Temp_Input()
    {
        const string json = """
        {"streams":[{"codec_type":"video","codec_name":"h264","width":10,"height":10}],
         "format":{"duration":"1.0"}}
        """;
        var opts = new MediaOptions
        {
            VideoMetadataProvider = "ffprobe",
            FfprobePath = "/usr/local/bin/ffprobe",
            VideoProbeTimeoutSeconds = 10,
            VideoProbeMaxOutputBytes = 4 * 1024 * 1024,
        };
        var runner = Json(json);
        await Build(runner, opts).ExtractAsync(Blob(), CancellationToken.None);

        Assert.Equal("/usr/local/bin/ffprobe", runner.LastRequest!.Executable);
        var args = runner.LastRequest.Arguments;
        // Last arg is the temp input file — in the system temp dir, no storage shard.
        var input = args[^1];
        Assert.StartsWith(Path.GetTempPath(), input, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("objects/", input, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("json", args);
    }

    [Fact]
    public async Task Falls_Back_To_Format_Bitrate_When_Stream_Has_None()
    {
        const string json = """
        {"streams":[{"codec_type":"video","codec_name":"h264","width":100,"height":100}],
         "format":{"duration":"1.0","bit_rate":"999000"}}
        """;
        var result = await Build(Json(json)).ExtractAsync(Blob(), CancellationToken.None);

        Assert.Equal(999000L, result.VideoBitrate);
    }
}
