using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NubArca.Api.Files;
using Xunit;

namespace NubArca.Api.Tests.Files;

// Video-hls v2 — REAL-ffmpeg regression test for the rotated-source bug
// observed in production: the stream-copied high rendition kept the -90°
// rotation side-data while the encoded low rendition was physically rotated
// with NO side-data, so adaptive switches flipped the picture on ExoPlayer.
// The v2 contract: a rotated source is ALWAYS re-encoded (PlanFor), ffmpeg's
// autorotation bakes the rotation into EVERY rendition physically, and no
// rendition carries rotation side-data — quality switches can never change
// the displayed orientation again.
//
// Runs the actual ffmpeg/ffprobe binaries; when they are not on PATH the test
// exits early as a no-op (same machines that run the media suite have them).
[Trait("Category", "External")]
public sealed class VideoHlsRotationRealFfmpegTests : IDisposable
{
    private readonly string _dir;

    public VideoHlsRotationRealFfmpegTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"nc-hls-rot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Rotated_Source_Produces_Orientation_Consistent_Renditions_Without_Rotation_Tag()
    {
        if (!ToolAvailable("ffmpeg") || !ToolAvailable("ffprobe"))
        {
            return; // environment without the tools — nothing to verify here
        }

        // 1. Synthesize a LANDSCAPE 1920x1080 H.264 clip, then remux it with a
        //    90° display rotation (lossless: only the display matrix changes) —
        //    the exact shape phone cameras produce.
        var flat = Path.Combine(_dir, "flat.mp4");
        var rotated = Path.Combine(_dir, "rotated.mp4");
        await RunToolAsync("ffmpeg",
            "-y", "-v", "error",
            "-f", "lavfi", "-i", "testsrc2=duration=4:size=1920x1080:rate=30",
            "-f", "lavfi", "-i", "sine=frequency=440:duration=4",
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-shortest", flat);
        await RunToolAsync("ffmpeg",
            "-y", "-v", "error", "-display_rotation", "90", "-i", flat,
            "-c", "copy", rotated);
        var src = await ProbeVideoAsync(rotated);
        Assert.True(src.Rotation is int sr && sr % 360 != 0,
            $"fixture must carry rotation side-data (got {src.Rotation?.ToString() ?? "none"})");

        // 2. Run the REAL transcoder with the plan v2 dictates for a rotated
        //    source: re-encode (never copy), both rungs.
        var outDir = Path.Combine(_dir, "out");
        Directory.CreateDirectory(outDir);
        var transcoder = new FfmpegVideoHlsTranscoder(
            Options.Create(new MediaOptions { VideoHlsProvider = "ffmpeg" }),
            new SystemProcessRunner(),
            NullLogger<FfmpegVideoHlsTranscoder>.Instance);
        var result = await transcoder.TranscodeAsync(
            new VideoHlsTranscodeRequest(
                rotated, outDir,
                CopyVideo: false, CopyAudio: true, HasAudio: true, IncludeLowRendition: true),
            CancellationToken.None);
        Assert.True(result.Success, $"transcode failed: {result.ErrorCode}");

        // 3. Both renditions must be PHYSICALLY portrait (rotation baked in),
        //    orientation-consistent with each other, and carry NO rotation
        //    side-data — the invariant that makes quality switches safe.
        var high = await ProbeVideoAsync(FirstInit(outDir, "high"));
        var low = await ProbeVideoAsync(FirstInit(outDir, "low"));

        Assert.True(high.Width < high.Height, $"high not portrait: {high.Width}x{high.Height}");
        Assert.True(low.Width < low.Height, $"low not portrait: {low.Width}x{low.Height}");
        // v2 short-side cap: the rotated FullHD keeps its full 1080×1920.
        Assert.Equal(1080, high.Width);
        Assert.Equal(1920, high.Height);
        Assert.Equal(480, low.Width);
        Assert.True(high.Rotation is null or 0, $"high still carries rotation {high.Rotation}");
        Assert.True(low.Rotation is null or 0, $"low still carries rotation {low.Rotation}");
    }

    private static string FirstInit(string outDir, string rendition)
        => Directory.EnumerateFiles(Path.Combine(outDir, rendition), "init*.mp4").First();

    private sealed record ProbedVideo(int Width, int Height, int? Rotation);

    private async Task<ProbedVideo> ProbeVideoAsync(string path)
    {
        var json = await RunToolAsync("ffprobe",
            "-v", "error", "-print_format", "json", "-show_streams", path);
        using var doc = JsonDocument.Parse(json);
        foreach (var s in doc.RootElement.GetProperty("streams").EnumerateArray())
        {
            if (s.GetProperty("codec_type").GetString() != "video") continue;
            int? rotation = null;
            if (s.TryGetProperty("side_data_list", out var list))
            {
                foreach (var sd in list.EnumerateArray())
                {
                    if (sd.TryGetProperty("rotation", out var rot))
                    {
                        rotation = rot.GetInt32();
                    }
                }
            }
            return new ProbedVideo(
                s.GetProperty("width").GetInt32(),
                s.GetProperty("height").GetInt32(),
                rotation);
        }
        throw new InvalidOperationException("no video stream in probe output");
    }

    private static bool ToolAvailable(string tool)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(tool, "-version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            p!.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<string> RunToolAsync(string tool, params string[] args)
    {
        var psi = new ProcessStartInfo(tool)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = await p.StandardOutput.ReadToEndAsync();
        var stderr = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        Assert.True(p.ExitCode == 0, $"{tool} exited {p.ExitCode}: {stderr}");
        return stdout;
    }
}
