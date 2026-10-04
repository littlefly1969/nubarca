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

    private static string Video(string formatTags) => $$"""
        {
          "streams": [ {"codec_type":"video","codec_name":"h264","width":1920,"height":1080,"avg_frame_rate":"30/1"} ],
          "format": {"duration":"5.0","tags":{{formatTags}}}
        }
        """;

    [Fact]
    public async Task An_Iphone_Video_Keeps_Its_Local_Time_Offset_Place_And_Device()
    {
        // The tags an iPhone .mov carries (as found on real uploads): the
        // container's UTC creation_time AND Apple's local creation date.
        var result = await Build(Json(Video("""
            {"creation_time":"2026-10-03T19:15:42.000000Z",
             "com.apple.quicktime.creationdate":"2026-10-03T21:15:42+0200",
             "com.apple.quicktime.location.ISO6709":"+45.4642+009.1900+120.000/",
             "com.apple.quicktime.make":"Apple","com.apple.quicktime.model":"iPhone 13",
             "com.apple.quicktime.software":"18.0"}
            """))).ExtractAsync(Blob(), CancellationToken.None);

        // The photographs' convention: the wall clock, as UTC-kind, and its offset.
        Assert.Equal(new DateTime(2026, 10, 3, 21, 15, 42, DateTimeKind.Utc), result.CreationTime);
        Assert.Equal("+02:00", result.CreationTimeOffset);
        Assert.Equal(VideoMetadataExtractionResult.QuickTimeCreationDateSource, result.CreationTimeSource);
        Assert.Equal(45.4642, result.GpsLatitude);
        Assert.Equal(9.19, result.GpsLongitude);
        Assert.Equal(120.0, result.GpsAltitude);
        Assert.Equal(("Apple", "iPhone 13", "18.0"), (result.CameraMake, result.CameraModel, result.Software));
        Assert.Equal(2, FfprobeVideoMetadataExtractor.Version);
    }

    [Fact]
    public async Task An_Android_Video_Has_Its_Place_And_A_Utc_Time_Without_An_Offset()
    {
        var result = await Build(Json(Video("""
            {"creation_time":"2026-10-03T19:15:42.000000Z","location":"-33.8688+151.2093/",
             "com.android.manufacturer":"Google","com.android.model":"Pixel 9"}
            """))).ExtractAsync(Blob(), CancellationToken.None);

        Assert.Equal(new DateTime(2026, 10, 3, 19, 15, 42, DateTimeKind.Utc), result.CreationTime);
        Assert.Null(result.CreationTimeOffset);
        Assert.Equal(VideoMetadataExtractionResult.ContainerCreationTimeSource, result.CreationTimeSource);
        Assert.Equal((-33.8688, 151.2093), (result.GpsLatitude!.Value, result.GpsLongitude!.Value));
        Assert.Null(result.GpsAltitude);
        Assert.Equal(("Google", "Pixel 9"), (result.CameraMake, result.CameraModel));
    }

    [Fact]
    public async Task A_Negative_Offset_Is_Kept_As_Written()
    {
        var result = await Build(Json(Video("""
            {"com.apple.quicktime.creationdate":"2026-07-04T08:30:00-0500"}
            """))).ExtractAsync(Blob(), CancellationToken.None);
        Assert.Equal(new DateTime(2026, 7, 4, 8, 30, 0, DateTimeKind.Utc), result.CreationTime);
        Assert.Equal("-05:00", result.CreationTimeOffset);
    }

    [Theory]
    [InlineData("+00.0000+000.0000/")]   // a device with no fix
    [InlineData("+95.0000+009.1900/")]   // not a latitude
    [InlineData("+45.4642+190.0000/")]   // not a longitude
    [InlineData("somewhere")]
    public async Task A_Location_That_Is_No_Place_Is_No_Location(string iso6709)
    {
        var result = await Build(Json(Video($$"""{"location":"{{iso6709}}"}"""))).ExtractAsync(Blob(), CancellationToken.None);
        Assert.Null(result.GpsLatitude);
        Assert.Null(result.GpsLongitude);
    }

    [Fact]
    public async Task An_Apple_Date_Without_An_Offset_Falls_Back_To_The_Container_Time()
    {
        var result = await Build(Json(Video("""
            {"creation_time":"2026-10-03T19:15:42.000000Z","com.apple.quicktime.creationdate":"2026-10-03T21:15:42"}
            """))).ExtractAsync(Blob(), CancellationToken.None);
        Assert.Equal(new DateTime(2026, 10, 3, 19, 15, 42, DateTimeKind.Utc), result.CreationTime);
        Assert.Equal(VideoMetadataExtractionResult.ContainerCreationTimeSource, result.CreationTimeSource);
        Assert.Null(result.CreationTimeOffset);
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
