using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NubArca.Api.Files;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace NubArca.Api.Tests.Media;

/// <summary>
/// An HDR video through the real FFmpeg — the production media tools in CI.
/// The scene is an SDR picture re-coded as HLG the way BT.2408 places SDR in
/// it (white at 203 nits), so what comes out can be held against what went in.
/// </summary>
public sealed class HdrVideoTests : IDisposable
{
    // Skin, sky, foliage and near-white, in a fixed geometry: by default the
    // gradient source draws its own at random.
    private const string Scene =
        "gradients=s=320x240:c0=0xd09070:c1=0x3080e0:c2=0x40a040:c3=0xf0f0f0:n=4:speed=0:x0=0:y0=0:x1=319:y1=239";

    private readonly string _dir = Directory.CreateTempSubdirectory("nubarca-hdr-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [SkippableFact]
    public async Task An_Hdr_Poster_Looks_Like_The_Scene_Not_Washed_Out()
    {
        RequireZscaleFfmpeg();
        var (reference, hlg) = await SceneAsync();
        var probe = new FfprobeVideoColorProbe(
            Options.Create(new MediaOptions()), new SystemProcessRunner(), NullLogger<FfprobeVideoColorProbe>.Instance);
        Assert.True((await probe.ProbeAsync(hlg, default)).IsHdr);

        var derivatives = Options.Create(new MediaDerivativesOptions());
        var provider = new FfmpegVideoPosterProvider(
            Options.Create(new MediaOptions { VideoPosterProvider = "ffmpeg", VideoPosterSeekSeconds = 0 }),
            new SystemProcessRunner(),
            probe,
            new SyntheticVideoPosterProvider(derivatives),
            NullLogger<FfmpegVideoPosterProvider>.Instance,
            derivatives);

        var poster = await provider.TryGetPosterAsync(
            _ => Task.FromResult<Stream>(File.OpenRead(hlg)), default);

        Assert.Equal(VideoPosterSources.Ffmpeg, poster!.Source);
        var expected = Measure(await File.ReadAllBytesAsync(reference));
        var actual = Measure(((MemoryStream)poster.Content).ToArray());
        // HLG read as SDR comes out at about 60% of the scene's colourfulness,
        // grey and hue-shifted; tone-mapped, it is the scene's own.
        Assert.InRange(actual.Colourfulness, expected.Colourfulness * 0.85, expected.Colourfulness * 1.2);
        Assert.InRange(actual.Luma, expected.Luma * 0.9, expected.Luma * 1.1);
    }

    [SkippableFact]
    public async Task An_Hdr_Video_Plays_As_Bt709_Sdr()
    {
        RequireZscaleFfmpeg();
        var (_, hlg) = await SceneAsync();
        var output = Path.Combine(_dir, "ladder");
        Directory.CreateDirectory(output);
        var transcoder = new FfmpegVideoHlsTranscoder(
            Options.Create(new MediaOptions { VideoHlsProvider = "ffmpeg" }),
            new SystemProcessRunner(),
            new FfprobeVideoColorProbe(
                Options.Create(new MediaOptions()), new SystemProcessRunner(),
                NullLogger<FfprobeVideoColorProbe>.Instance),
            NullLogger<FfmpegVideoHlsTranscoder>.Instance);

        var result = await transcoder.TranscodeAsync(
            new VideoHlsTranscodeRequest(hlg, output, CopyVideo: false, CopyAudio: true, HasAudio: false, IncludeLowRendition: false),
            default);

        Assert.True(result.Success, result.ErrorCode);
        // The init segment carries the stream's description, the first media
        // segment its first frames: together they are what a player starts on.
        var high = Path.Combine(output, "high");
        var start = Path.Combine(_dir, "start.mp4");
        await File.WriteAllBytesAsync(start, [
            .. await File.ReadAllBytesAsync(Directory.EnumerateFiles(high, "init*.mp4").Single()),
            .. await File.ReadAllBytesAsync(Path.Combine(high, "seg-0.m4s")),
        ]);
        var stream = await RunAsync("ffprobe", "-v", "error", "-select_streams", "v:0",
            "-show_entries", "stream=pix_fmt,color_transfer,color_primaries,color_space", "-of", "default=noprint_wrappers=1", start);
        Assert.Contains("pix_fmt=yuv420p", stream);
        Assert.Contains("color_transfer=bt709", stream);
        Assert.Contains("color_primaries=bt709", stream);
        Assert.Contains("color_space=bt709", stream);
    }

    [SkippableFact]
    public async Task A_Display_P3_Video_Narrower_Than_The_Cap_And_Odd_Is_Converted_On_Even_Dimensions()
    {
        // A phone video of 465×892 in Display P3: the stream copy used to carry
        // its odd width untouched; converted, the rendition must be even.
        RequireZscaleFfmpeg();
        var source = Path.Combine(_dir, "odd-p3.mkv");
        await RunAsync("ffmpeg", "-v", "error", "-y", "-f", "lavfi", "-i", "color=c=0x3366cc:s=465x892:r=10", "-t", "1",
            "-vf", "format=yuv420p,setparams=color_primaries=smpte432:color_trc=bt709:colorspace=bt709:range=tv",
            "-c:v", "ffv1", source);
        var output = Path.Combine(_dir, "odd-ladder");
        Directory.CreateDirectory(output);
        var transcoder = new FfmpegVideoHlsTranscoder(
            Options.Create(new MediaOptions { VideoHlsProvider = "ffmpeg" }),
            new SystemProcessRunner(),
            new FfprobeVideoColorProbe(
                Options.Create(new MediaOptions()), new SystemProcessRunner(),
                NullLogger<FfprobeVideoColorProbe>.Instance),
            NullLogger<FfmpegVideoHlsTranscoder>.Instance);

        var result = await transcoder.TranscodeAsync(
            new VideoHlsTranscodeRequest(source, output, CopyVideo: true, CopyAudio: true, HasAudio: false, IncludeLowRendition: false),
            default);

        Assert.True(result.Success, result.ErrorCode);
        var high = Path.Combine(output, "high");
        var start = Path.Combine(_dir, "odd-start.mp4");
        await File.WriteAllBytesAsync(start, [
            .. await File.ReadAllBytesAsync(Directory.EnumerateFiles(high, "init*.mp4").Single()),
            .. await File.ReadAllBytesAsync(Path.Combine(high, "seg-0.m4s")),
        ]);
        var stream = await RunAsync("ffprobe", "-v", "error", "-select_streams", "v:0",
            "-show_entries", "stream=width,height,color_primaries", "-of", "default=noprint_wrappers=1", start);
        Assert.Contains("width=464", stream);
        // The other side follows the aspect, even too.
        Assert.Contains("height=890", stream);
        Assert.Contains("color_primaries=bt709", stream);
    }

    private async Task<(string Reference, string Hlg)> SceneAsync()
    {
        var reference = Path.Combine(_dir, "scene.png");
        var hlg = Path.Combine(_dir, "scene-hlg.mkv");
        const string sdr = "format=yuv420p,setparams=colorspace=bt709:color_primaries=bt709:color_trc=bt709:range=tv";
        await RunAsync("ffmpeg", "-v", "error", "-y", "-f", "lavfi", "-i", Scene, "-vf", sdr, "-frames:v", "1", reference);
        await RunAsync("ffmpeg", "-v", "error", "-y", "-f", "lavfi", "-i", Scene, "-t", "1",
            "-vf", sdr + ",zscale=t=linear,format=gbrpf32le,zscale=p=bt2020,"
                + "zscale=t=arib-std-b67:m=bt2020nc:r=tv:npl=203,format=yuv420p10le",
            "-c:v", "ffv1", hlg);
        return (reference, hlg);
    }

    /// <summary>Mean luma, and mean (max − min) of each pixel's RGB — how coloured it is.</summary>
    private static (double Luma, double Colourfulness) Measure(byte[] picture)
    {
        using var image = Image.Load<Rgb24>(picture);
        double luma = 0, colourfulness = 0;
        image.ProcessPixelRows(rows =>
        {
            for (var y = 0; y < rows.Height; y++)
            {
                foreach (var p in rows.GetRowSpan(y))
                {
                    luma += (0.2126 * p.R) + (0.7152 * p.G) + (0.0722 * p.B);
                    colourfulness += Math.Max(p.R, Math.Max(p.G, p.B)) - Math.Min(p.R, Math.Min(p.G, p.B));
                }
            }
        });
        var pixels = (double)image.Width * image.Height;
        return (luma / pixels, colourfulness / pixels);
    }

    private static void RequireZscaleFfmpeg()
    {
        var available = FfmpegHasZscale();
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
            Assert.True(available, "CI must install the media tools (FFmpeg with zimg)");
        Skip.IfNot(available, "no FFmpeg with zscale on PATH");
    }

    private static bool FfmpegHasZscale()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("ffmpeg",
                ["-v", "error", "-nostdin", "-f", "lavfi", "-i", "color=c=gray:s=16x16:d=0.1",
                    "-vf", "zscale=tin=bt709:pin=bt709:min=bt709:rin=tv:t=linear:p=bt709,format=gbrpf32le", "-f", "null", "-"])
            {
                RedirectStandardError = true, RedirectStandardOutput = true,
            })!;
            process.WaitForExit(30_000);
            return process.HasExited && process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<string> RunAsync(string tool, params string[] args)
    {
        var start = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, $"{tool} exited {process.ExitCode}: {stderr}");
        return await stdout;
    }
}
