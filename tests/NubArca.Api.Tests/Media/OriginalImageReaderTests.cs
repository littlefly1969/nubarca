using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Storage;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace NubArca.Api.Tests.Media;

/// <summary>
/// The one way an original's pixels are opened. For HEIC: a frame FFmpeg
/// decodes into a bounded temporary file — never a PNG held whole by the
/// reader — inside a bounded number of decode slots, cancellable at every
/// step, and leaving no file and no process behind.
/// </summary>
public sealed class OriginalImageReaderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("nubarca-reader-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    // --- with FFmpeg faked ------------------------------------------------------------

    private static readonly byte[] Heic = Ftyp("heic", "mif1", "heic").Concat(new byte[200]).ToArray();

    [Fact]
    public async Task A_Jpeg_Is_Its_Own_Bytes_And_Ffmpeg_Never_Runs()
    {
        var jpeg = Jpeg();
        var runner = new FakeFfmpeg();

        await using var pixels = await Reader(runner).OpenForPixelsAsync(Open(jpeg), default);

        Assert.False(pixels!.IsDecodedFrame);
        Assert.Equal(jpeg, await pixels.ReadAllBytesAsync(default));
        Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public async Task A_Heic_Is_A_Lossless_Png_In_A_Temporary_File_Gone_With_The_Lease()
    {
        var png = Png(6, 4);
        var runner = new FakeFfmpeg(writes: png);
        var gate = Gate(2);

        string output;
        await using (var pixels = await Reader(runner, gate).OpenForPixelsAsync(Open(Heic), default))
        {
            Assert.True(pixels!.IsDecodedFrame);
            // A file, read as a stream — not a PNG held in memory by the reader.
            var file = Assert.IsType<FileStream>(pixels.Content);
            output = file.Name;
            Assert.Equal(png.Length, file.Length);
            var info = await Image.IdentifyAsync(pixels.Content);
            Assert.Equal((6, 4), (info.Width, info.Height));
            Assert.Equal(1, gate.Available);

            var args = runner.Last!.Arguments;
            Assert.Equal(["-frames:v", "1"], args.SkipWhile(a => a != "-frames:v").Take(2));
            Assert.Equal(["-c:v", "png", "-compression_level", "0"], args.SkipWhile(a => a != "-c:v").Take(4));
            Assert.Equal(output, args[^1]);
            // Not a seekable FileStream here, so a temp copy was made — and removed.
            Assert.False(File.Exists(args[args.ToList().IndexOf("-i") + 1]));
        }

        Assert.False(File.Exists(output));
        Assert.Equal(2, gate.Available);
    }

    [Theory]
    [InlineData(1, false, false, false)]
    [InlineData(0, true, false, false)]
    [InlineData(0, false, true, false)]
    [InlineData(0, false, false, true)]
    public async Task A_Heic_Ffmpeg_Cannot_Decode_Or_Too_Large_Is_No_Pixels_And_Leaves_Nothing(
        int exitCode, bool timedOut, bool writesNothing, bool overLimit)
    {
        var runner = new FakeFfmpeg(
            writes: writesNothing ? null : overLimit ? new byte[2048] : Png(2, 2),
            result: new ProcessDirectoryRunResult(exitCode, timedOut));
        var gate = Gate(1);

        var pixels = await Reader(runner, gate, maxOutputBytes: 1024).OpenForPixelsAsync(Open(Heic), default);

        Assert.Null(pixels);
        Assert.False(File.Exists(runner.Output));
        Assert.False(File.Exists(runner.Input));
        Assert.Equal(1, gate.Available);
    }

    [Fact]
    public async Task Cancelled_While_Ffmpeg_Runs_It_Throws_And_Leaves_Nothing()
    {
        var runner = new FakeFfmpeg(writes: Png(2, 2), hangUntilCancelled: true);
        var gate = Gate(1);
        using var cancel = new CancellationTokenSource();

        var decode = Reader(runner, gate).OpenForPixelsAsync(Open(Heic), cancel.Token);
        await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => decode);
        Assert.False(File.Exists(runner.Output));
        Assert.False(File.Exists(runner.Input));
        Assert.Equal(1, gate.Available);
    }

    [Fact]
    public async Task Cancelled_While_The_Original_Is_Read_It_Throws_And_Frees_Its_Slot()
    {
        var runner = new FakeFfmpeg(writes: Png(2, 2));
        var gate = Gate(1);
        using var cancel = new CancellationTokenSource();
        var opens = 0;
        Task<Stream> OpenSlowly(CancellationToken ct) =>
            // The header is read at once; the copy for FFmpeg then stalls.
            Task.FromResult<Stream>(++opens == 1 ? new MemoryStream(Heic) : new StallingStream(Heic, cancel));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Reader(runner, gate).OpenForPixelsAsync(OpenSlowly, cancel.Token));

        Assert.Equal(0, runner.Calls);
        Assert.Equal(1, gate.Available);
    }

    [Fact]
    public async Task No_More_Frames_Than_The_Gate_Allows_Are_Decoded_Or_Held_At_Once()
    {
        var runner = new FakeFfmpeg(writes: Png(2, 2));
        var gate = Gate(1);
        var reader = Reader(runner, gate);

        var first = await reader.OpenForPixelsAsync(Open(Heic), default);
        var second = reader.OpenForPixelsAsync(Open(Heic), default);
        await Task.Delay(200);
        // The first frame is held: the second decode waits for its slot.
        Assert.False(second.IsCompleted);
        Assert.Equal(1, runner.Calls);

        await first!.DisposeAsync();
        await using var secondPixels = await second.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(secondPixels);
        Assert.Equal(2, runner.Calls);
        Assert.Equal(1, runner.MaxConcurrent);
    }

    [Fact]
    public async Task Waiting_For_A_Slot_Can_Be_Cancelled()
    {
        var gate = Gate(1);
        var reader = Reader(new FakeFfmpeg(writes: Png(2, 2)), gate);
        await using var held = await reader.OpenForPixelsAsync(Open(Heic), default);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => reader.OpenForPixelsAsync(Open(Heic), cancel.Token));
        Assert.Equal(0, gate.Available);
    }

    // --- with the real FFmpeg -----------------------------------------------------------

    [SkippableFact]
    public async Task The_Real_Frame_Is_Read_From_Its_File_And_Identified_From_Its_Header()
    {
        RequireHeicFfmpeg();
        var reader = RealReader(Gate(2));

        await using var pixels = await reader.OpenForPixelsAsync(OpenFixture(), default);

        Assert.True(pixels!.IsDecodedFrame);
        Assert.IsType<FileStream>(pixels.Content);
        var info = await Image.IdentifyAsync(pixels.Content);
        // The 800×600 grid, rotated in its container: upright.
        Assert.Equal((600, 800), (info.Width, info.Height));
    }

    [SkippableFact]
    public async Task A_Frame_Over_The_Output_Limit_Is_Refused_And_Deleted()
    {
        RequireHeicFfmpeg();
        var (ffmpeg, _, argsFile) = await RecordingFfmpegAsync(sleepSeconds: 0);
        var gate = Gate(1);

        // The fixture's frame is ~1.4 MB of PNG.
        var pixels = await RealReader(gate, maxOutputBytes: 100_000, ffmpeg: ffmpeg).OpenForPixelsAsync(OpenFixture(), default);

        Assert.Null(pixels);
        Assert.False(File.Exists((await File.ReadAllLinesAsync(argsFile)).Last()));
        Assert.Equal(1, gate.Available);
    }

    [SkippableFact]
    public async Task Cancelling_A_Real_Decode_Leaves_No_Ffmpeg_And_No_File()
    {
        RequireHeicFfmpeg();
        // An FFmpeg that takes its time: it records its pid and arguments, then waits.
        var (slowFfmpeg, pidFile, argsFile) = await RecordingFfmpegAsync(sleepSeconds: 60);
        var gate = Gate(1);
        using var cancel = new CancellationTokenSource();

        var decode = RealReader(gate, ffmpeg: slowFfmpeg).OpenForPixelsAsync(OpenFixture(), cancel.Token);
        var pid = await ReadPidAsync(pidFile);
        var clock = Stopwatch.StartNew();
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => decode);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"took {clock.Elapsed}");
        await AssertGoneAsync(pid);
        var output = (await File.ReadAllLinesAsync(argsFile)).Last();
        Assert.False(File.Exists(output));
        Assert.Equal(1, gate.Available);
    }

    // --- helpers ------------------------------------------------------------------------

    private static HeifDecodeGate Gate(int capacity) =>
        new(Options.Create(new MediaOptions { HeifDecodeMaxConcurrency = capacity }));

    private static OriginalImageReader Reader(FakeFfmpeg runner, HeifDecodeGate? gate = null, long maxOutputBytes = 64 * 1024 * 1024) =>
        new(new NoBlobs(), runner, gate ?? Gate(2),
            Options.Create(new MediaOptions { FfmpegPath = "ffmpeg", HeifDecodeMaxOutputBytes = (int)maxOutputBytes }),
            NullLogger<OriginalImageReader>.Instance);

    private static OriginalImageReader RealReader(HeifDecodeGate gate, int maxOutputBytes = 64 * 1024 * 1024, string ffmpeg = "ffmpeg") =>
        new(new NoBlobs(), new SystemProcessRunner(), gate,
            Options.Create(new MediaOptions { FfmpegPath = ffmpeg, HeifDecodeMaxOutputBytes = maxOutputBytes }),
            NullLogger<OriginalImageReader>.Instance);

    private static Func<CancellationToken, Task<Stream>> Open(byte[] bytes) =>
        _ => Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));

    private static Func<CancellationToken, Task<Stream>> OpenFixture() =>
        _ => Task.FromResult<Stream>(File.OpenRead(Fixture()));

    /// <summary>
    /// The real FFmpeg behind a script that records its pid and arguments —
    /// the output path is the last — and optionally waits first.
    /// </summary>
    private async Task<(string Ffmpeg, string PidFile, string ArgsFile)> RecordingFfmpegAsync(int sleepSeconds)
    {
        var pidFile = Path.Combine(_dir, $"ffmpeg-{sleepSeconds}.pid");
        var argsFile = Path.Combine(_dir, $"ffmpeg-{sleepSeconds}.args");
        var script = Path.Combine(_dir, $"ffmpeg-{sleepSeconds}.sh");
        await File.WriteAllTextAsync(script,
            $"#!/bin/sh\necho $$ > '{pidFile}'\nprintf '%s\\n' \"$@\" > '{argsFile}'\n"
            + (sleepSeconds > 0 ? $"sleep {sleepSeconds}\n" : "")
            + "exec ffmpeg \"$@\"\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return (script, pidFile, argsFile);
    }

    private static byte[] Jpeg()
    {
        using var image = new Image<Rgb24>(4, 3);
        using var ms = new MemoryStream();
        image.Save(ms, new JpegEncoder());
        return ms.ToArray();
    }

    private static byte[] Png(int width, int height)
    {
        using var image = new Image<Rgb24>(width, height);
        using var ms = new MemoryStream();
        image.Save(ms, new PngEncoder());
        return ms.ToArray();
    }

    private static byte[] Ftyp(string major, params string[] compatible)
    {
        var size = 16 + (4 * compatible.Length);
        var box = new byte[Math.Max(size, 32)];
        box[3] = (byte)size;
        "ftyp"u8.CopyTo(box.AsSpan(4));
        System.Text.Encoding.ASCII.GetBytes(major).CopyTo(box, 8);
        for (var i = 0; i < compatible.Length; i++)
            System.Text.Encoding.ASCII.GetBytes(compatible[i]).CopyTo(box, 16 + (4 * i));
        return box;
    }

    private static string Fixture()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NubArca.sln")))
            directory = directory.Parent;
        return Path.Combine(directory!.FullName, "scripts", "media-tools", "fixtures", "iphone-like-grid-rotated.heic");
    }

    private static void RequireHeicFfmpeg()
    {
        var available = OperatingSystem.IsLinux() && FfmpegReadsHeic();
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
            Assert.True(available, "CI must install the media tools (FFmpeg 7.1+ with HEIC)");
        Skip.IfNot(available, "no HEIC-capable FFmpeg on PATH");
    }

    private static bool FfmpegReadsHeic()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("ffmpeg",
                ["-v", "error", "-nostdin", "-i", Fixture(), "-frames:v", "1", "-f", "null", "-"])
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

    private static async Task<int> ReadPidAsync(string pidFile)
    {
        for (var i = 0; i < 400; i++)
        {
            if (File.Exists(pidFile) && int.TryParse((await File.ReadAllTextAsync(pidFile)).Trim(), out var pid)) return pid;
            await Task.Delay(25);
        }
        throw new TimeoutException("FFmpeg never started");
    }

    private static async Task AssertGoneAsync(int pid)
    {
        for (var i = 0; i < 100 && IsRunning(pid); i++) await Task.Delay(50);
        Assert.False(IsRunning(pid), $"process {pid} is still running");
    }

    private static bool IsRunning(int pid)
    {
        var status = $"/proc/{pid}/status";
        if (!File.Exists(status)) return false;
        try { return !File.ReadAllLines(status).Any(l => l.StartsWith("State:") && l.Contains('Z')); }
        catch (IOException) { return false; }
    }

    /// <summary>Plays FFmpeg: writes the given bytes to the output path its arguments name.</summary>
    private sealed class FakeFfmpeg(
        byte[]? writes = null, ProcessDirectoryRunResult? result = null, bool hangUntilCancelled = false) : IDirectoryProcessRunner
    {
        private int _running;

        public ProcessDirectoryRunRequest? Last { get; private set; }
        public int Calls { get; private set; }
        public int MaxConcurrent { get; private set; }
        public string? Input { get; private set; }
        public string? Output { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ProcessDirectoryRunResult> RunAsync(ProcessDirectoryRunRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            Last = request;
            Input = request.Arguments[request.Arguments.ToList().IndexOf("-i") + 1];
            Output = request.Arguments[^1];
            MaxConcurrent = Math.Max(MaxConcurrent, Interlocked.Increment(ref _running));
            try
            {
                if (writes is not null) await File.WriteAllBytesAsync(Output, writes, cancellationToken);
                Started.TrySetResult();
                if (hangUntilCancelled) await Task.Delay(Timeout.Infinite, cancellationToken);
                return result ?? new ProcessDirectoryRunResult(0, false);
            }
            finally
            {
                Interlocked.Decrement(ref _running);
            }
        }
    }

    /// <summary>Gives its first bytes, then stalls — and cancels the caller once it does.</summary>
    private sealed class StallingStream(byte[] bytes, CancellationTokenSource cancel) : MemoryStream(bytes, writable: false)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position >= 64)
            {
                await cancel.CancelAsync();
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            return await base.ReadAsync(buffer[..Math.Min(buffer.Length, 64)], cancellationToken);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    private sealed class NoBlobs : IBlobService
    {
        public Task<Stream> OpenContentAsync(Guid blobObjectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BlobObject> StoreAsync(Stream content, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BlobStoreResult> StoreMeasuredAsync(Stream content, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BlobObject> StoreDerivedAsync(Stream content, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Stream?> OpenDerivedContentAsync(Guid blobObjectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ReleaseAsync(Guid blobObjectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task MarkPurgeEligibleIfUnreferencedAsync(Guid blobObjectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BlobObject> AcquireExistingAsync(Guid blobObjectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> TryRestoreDerivedFromOriginalAsync(Guid blobObjectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
