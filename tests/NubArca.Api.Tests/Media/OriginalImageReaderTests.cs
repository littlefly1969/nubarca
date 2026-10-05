using System.Diagnostics;
using System.Runtime.InteropServices;
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
/// streams into a temporary file with its output limit enforced as it arrives,
/// inside a bounded number of decode slots that cover the frame's whole use —
/// bytes included — cancellable at every step, leaving no file and no process
/// behind.
/// </summary>
public sealed class OriginalImageReaderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("nubarca-reader-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static readonly byte[] Heic = Ftyp("heic", "mif1", "heic").Concat(new byte[200]).ToArray();

    // --- the lease, with FFmpeg faked -------------------------------------------------

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
    public async Task A_Heic_Is_A_Png_Streamed_To_A_Temporary_File_Gone_With_The_Lease()
    {
        var png = Png(6, 4);
        var runner = new FakeFfmpeg(writes: png);
        var gate = Gate(2);

        string output;
        await using (var pixels = await Reader(runner, gate).OpenForPixelsAsync(Open(Heic), default))
        {
            Assert.True(pixels!.IsDecodedFrame);
            var file = Assert.IsType<FileStream>(pixels.Content);
            output = file.Name;
            Assert.Equal(png.Length, file.Length);
            var info = await Image.IdentifyAsync(pixels.Content);
            Assert.Equal((6, 4), (info.Width, info.Height));
            Assert.Equal(1, gate.Available);

            var request = runner.Last!;
            Assert.Equal(output, request.OutputPath);
            // On stdout, through the capped sink — never -fs, never a file FFmpeg writes itself.
            Assert.Equal(["-f", "image2pipe", "-"], request.Arguments.TakeLast(3));
            Assert.DoesNotContain("-fs", request.Arguments);
            Assert.Equal(["-c:v", "png", "-compression_level", "0"], request.Arguments.SkipWhile(a => a != "-c:v").Take(4));
            Assert.Equal(1024 * 1024, request.MaxOutputBytes);
            // Not a seekable FileStream here, so a temp copy was made — and removed.
            Assert.False(File.Exists(request.Arguments[request.Arguments.ToList().IndexOf("-i") + 1]));
        }

        Assert.False(File.Exists(output));
        Assert.Equal(2, gate.Available);
    }

    [Theory]
    [InlineData(1, false, false, false)]
    [InlineData(-1, true, false, false)]
    [InlineData(-1, false, true, false)]
    [InlineData(0, false, false, true)]
    public async Task A_Heic_Ffmpeg_Cannot_Decode_Is_No_Pixels_And_Leaves_Nothing(
        int exitCode, bool timedOut, bool limitExceeded, bool writesNothing)
    {
        var runner = new FakeFfmpeg(
            writes: writesNothing ? null : Png(2, 2),
            outcome: new ProcessFileRunResult(exitCode, timedOut, limitExceeded, 0));
        var gate = Gate(1);

        var pixels = await Reader(runner, gate).OpenForPixelsAsync(Open(Heic), default);

        Assert.Null(pixels);
        Assert.False(File.Exists(runner.Last!.OutputPath));
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
        Assert.False(File.Exists(runner.Last!.OutputPath));
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

    // --- one array, read straight into ------------------------------------------------

    [Fact]
    public async Task ReadAllBytes_Reads_Straight_Into_The_One_Array_It_Returns()
    {
        var content = new byte[300_000];
        Random.Shared.NextBytes(content);
        var spy = new SpyStream(content);
        await using var lease = Lease(spy, maxBytes: content.Length);

        var bytes = await lease.ReadAllBytesAsync(default);

        Assert.Equal(content, bytes);
        // Every read landed in the returned array itself: no intermediate
        // buffer, no MemoryStream, no second copy.
        Assert.NotEmpty(spy.Targets);
        Assert.All(spy.Targets.Take(spy.Targets.Count - 1), target => Assert.Same(bytes, target));
    }

    [Fact]
    public async Task ReadAllBytes_Refuses_A_Content_Over_Its_Limit_Before_Allocating()
    {
        var spy = new SpyStream(new byte[1001]);
        await using var lease = Lease(spy, maxBytes: 1000);

        await Assert.ThrowsAsync<InvalidDataException>(() => lease.ReadAllBytesAsync(default));
        Assert.Empty(spy.Targets);
    }

    [Fact]
    public async Task ReadAllBytes_Refuses_A_Content_Shorter_Or_Longer_Than_It_Said()
    {
        await using (var shorter = Lease(new SpyStream(new byte[100], claimedLength: 120), maxBytes: 1000))
        {
            await Assert.ThrowsAsync<EndOfStreamException>(() => shorter.ReadAllBytesAsync(default));
        }
        await using (var longer = Lease(new SpyStream(new byte[120], claimedLength: 100), maxBytes: 1000))
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => longer.ReadAllBytesAsync(default));
        }
    }

    // --- the gate covers the bytes' whole use -----------------------------------------

    [Fact]
    public async Task With_One_Slot_A_Second_Heic_Cannot_Start_While_The_First_Bytes_Are_In_Use()
    {
        var runner = new FakeFfmpeg(writes: Png(2, 2));
        var gate = Gate(1);
        var reader = Reader(runner, gate);
        var blobs = new HeicBlobs();

        var first = await OriginalPixels.OpenAsync(blobs, reader, Guid.NewGuid(), default);
        // The consumer is still using the first frame's bytes …
        Assert.NotEmpty(first.Bytes);
        var second = OriginalPixels.OpenAsync(blobs, reader, Guid.NewGuid(), default);
        await Task.Delay(200);
        // … so the second decode has not even started.
        Assert.False(second.IsCompleted);
        Assert.Equal(1, runner.Calls);

        await first.DisposeAsync();
        await using var secondBytes = await second.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, runner.Calls);
        Assert.Equal(1, runner.MaxConcurrent);
    }

    [Fact]
    public async Task The_Slot_Comes_Back_After_Use_After_A_Consumer_Failure_After_Cancellation_And_After_A_Failed_Decode()
    {
        var gate = Gate(1);
        var blobs = new HeicBlobs();

        // Used and done.
        await using (await OriginalPixels.OpenAsync(blobs, Reader(new FakeFfmpeg(writes: Png(2, 2)), gate), Guid.NewGuid(), default))
        {
            Assert.Equal(0, gate.Available);
        }
        Assert.Equal(1, gate.Available);

        // The consumer throws while using the bytes.
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var pixels = await OriginalPixels.OpenAsync(blobs, Reader(new FakeFfmpeg(writes: Png(2, 2)), gate), Guid.NewGuid(), default);
            throw new InvalidOperationException("the model fell over");
        });
        Assert.Equal(1, gate.Available);

        // Cancelled while decoding.
        using var cancel = new CancellationTokenSource();
        var hanging = new FakeFfmpeg(writes: Png(2, 2), hangUntilCancelled: true);
        var decode = OriginalPixels.OpenAsync(blobs, Reader(hanging, gate), Guid.NewGuid(), cancel.Token);
        await hanging.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => decode);
        Assert.Equal(1, gate.Available);

        // FFmpeg could not decode it.
        await Assert.ThrowsAsync<InvalidDataException>(() => OriginalPixels.OpenAsync(
            blobs, Reader(new FakeFfmpeg(outcome: new ProcessFileRunResult(1, false, false, 0)), gate), Guid.NewGuid(), default));
        Assert.Equal(1, gate.Available);
    }

    [Fact]
    public async Task A_Sheet_Takes_Its_Slots_Together_And_Never_Deadlocks()
    {
        var gate = Gate(2);

        // A sheet of three frames on two slots runs alone, holding both.
        var sheet = await gate.EnterAsync(3, default);
        Assert.Equal(0, gate.Available);
        var single = gate.EnterAsync(default);
        await Task.Delay(100);
        Assert.False(single.IsCompleted);
        sheet.Dispose();
        (await single.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
        Assert.Equal(2, gate.Available);

        // Sheets and single frames racing for the slots all get through.
        var work = Enumerable.Range(0, 20).Select(async i =>
        {
            using var held = await gate.EnterAsync(i % 3 == 0 ? 3 : i % 3, default);
            await Task.Delay(5);
        });
        await Task.WhenAll(work).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(2, gate.Available);
    }

    [Fact]
    public async Task A_Frame_Decoded_Under_Held_Slots_Does_Not_Take_Or_Free_Them()
    {
        var gate = Gate(1);
        var reader = Reader(new FakeFfmpeg(writes: Png(2, 2)), gate);
        using var held = await gate.EnterAsync(2, default);

        await using (var pixels = await reader.OpenForPixelsAsync(Guid.NewGuid(), held, default))
        {
            Assert.True(pixels!.IsDecodedFrame);
        }

        // Still the caller's: only its own dispose frees them.
        Assert.Equal(0, gate.Available);
    }

    // --- with the real FFmpeg -----------------------------------------------------------

    [SkippableFact]
    public async Task The_Real_Frame_Is_Read_From_Its_File_And_Identified_From_Its_Header()
    {
        RequireHeicFfmpeg();
        await using var pixels = await RealReader(Gate(2)).OpenForPixelsAsync(OpenFixture(), default);

        Assert.True(pixels!.IsDecodedFrame);
        Assert.IsType<FileStream>(pixels.Content);
        var info = await Image.IdentifyAsync(pixels.Content);
        // The 800×600 grid, rotated in its container: upright.
        Assert.Equal((600, 800), (info.Width, info.Height));
    }

    [SkippableFact]
    public async Task A_Real_Frame_Over_The_Limit_Is_Stopped_As_It_Arrives_And_Leaves_Nothing()
    {
        RequireHeicFfmpeg();
        var (ffmpeg, pidFile, _) = await RecordingFfmpegAsync("ffmpeg", sleepSeconds: 0);
        var gate = Gate(1);
        var outputs = new RecordingFileRunner(new SystemProcessRunner());

        // The fixture's frame is ~1.4 MB of PNG.
        var pixels = await RealReader(gate, maxOutputBytes: 100_000, ffmpeg: ffmpeg, runner: outputs)
            .OpenForPixelsAsync(OpenFixture(), default);

        Assert.Null(pixels);
        var result = outputs.Results.Single();
        Assert.True(result.OutputLimitExceeded);
        Assert.True(result.BytesWritten <= 100_000);
        Assert.False(File.Exists(outputs.Requests.Single().OutputPath));
        await AssertGoneAsync(await ReadPidAsync(pidFile));
        Assert.Equal(1, gate.Available);
    }

    [SkippableTheory]
    [InlineData(4096, true)]
    [InlineData(4097, false)]
    public async Task A_Producer_At_The_Limit_Is_Kept_And_One_Byte_Past_It_Is_Not(int bytes, bool kept)
    {
        Skip.IfNot(OperatingSystem.IsLinux());
        var producer = await ProducerAsync($"head -c {bytes} /dev/zero");

        var pixels = await RealReader(Gate(1), maxOutputBytes: 4096, ffmpeg: producer).OpenForPixelsAsync(Open(Heic), default);

        Assert.Equal(kept, pixels is not null);
        if (pixels is not null)
        {
            Assert.Equal(bytes, (await pixels.ReadAllBytesAsync(default)).Length);
            await pixels.DisposeAsync();
        }
    }

    [SkippableFact]
    public async Task A_Producer_That_Never_Stops_Is_Stopped_By_The_Cap_Not_The_Timeout()
    {
        Skip.IfNot(OperatingSystem.IsLinux());
        var pidFile = Path.Combine(_dir, "writer.pid");
        var producer = await ProducerAsync($"yes & echo $! > '{pidFile}'; wait");
        var outputs = new RecordingFileRunner(new SystemProcessRunner());
        var clock = Stopwatch.StartNew();

        var pixels = await RealReader(Gate(1), maxOutputBytes: 1024 * 1024, ffmpeg: producer, runner: outputs, timeoutSeconds: 60)
            .OpenForPixelsAsync(Open(Heic), default);

        Assert.Null(pixels);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"took {clock.Elapsed}");
        Assert.True(outputs.Results.Single().OutputLimitExceeded);
        Assert.False(File.Exists(outputs.Requests.Single().OutputPath));
        await AssertGoneAsync(await ReadPidAsync(pidFile));
    }

    [SkippableFact]
    public async Task Cancelling_A_Real_Decode_Leaves_No_Ffmpeg_And_No_File()
    {
        RequireHeicFfmpeg();
        var (slowFfmpeg, pidFile, _) = await RecordingFfmpegAsync("ffmpeg", sleepSeconds: 60);
        var outputs = new RecordingFileRunner(new SystemProcessRunner());
        var gate = Gate(1);
        using var cancel = new CancellationTokenSource();

        var decode = RealReader(gate, ffmpeg: slowFfmpeg, runner: outputs).OpenForPixelsAsync(OpenFixture(), cancel.Token);
        var pid = await ReadPidAsync(pidFile);
        var clock = Stopwatch.StartNew();
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => decode);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"took {clock.Elapsed}");
        await AssertGoneAsync(pid);
        Assert.False(File.Exists(outputs.Requests.Single().OutputPath));
        Assert.Equal(1, gate.Available);
    }

    // --- helpers ------------------------------------------------------------------------

    private static HeifDecodeGate Gate(int capacity) =>
        new(Options.Create(new MediaOptions { HeifDecodeMaxConcurrency = capacity }));

    private static OriginalImageReader Reader(FakeFfmpeg runner, HeifDecodeGate? gate = null) =>
        new(new HeicBlobs(), runner, gate ?? Gate(2),
            Options.Create(new MediaOptions { FfmpegPath = "ffmpeg", HeifDecodeMaxOutputBytes = 1024 * 1024 }),
            NullLogger<OriginalImageReader>.Instance);

    private static OriginalImageReader RealReader(
        HeifDecodeGate gate, int maxOutputBytes = 64 * 1024 * 1024, string ffmpeg = "ffmpeg",
        IProcessFileRunner? runner = null, int timeoutSeconds = 120) =>
        new(new HeicBlobs(), runner ?? new SystemProcessRunner(), gate,
            Options.Create(new MediaOptions
            {
                FfmpegPath = ffmpeg, HeifDecodeMaxOutputBytes = maxOutputBytes, HeifDecodeTimeoutSeconds = timeoutSeconds,
            }),
            NullLogger<OriginalImageReader>.Instance);

    private static OriginalPixelsLease Lease(Stream content, long maxBytes)
    {
        // The lease's constructor is the reader's own; a frame-shaped lease for
        // ReadAllBytes alone is built the same way the reader builds one.
        var ctor = typeof(OriginalPixelsLease).GetConstructors(
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Single();
        return (OriginalPixelsLease)ctor.Invoke([content, true, maxBytes, null]);
    }

    private static Func<CancellationToken, Task<Stream>> Open(byte[] bytes) =>
        _ => Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));

    private static Func<CancellationToken, Task<Stream>> OpenFixture() =>
        _ => Task.FromResult<Stream>(File.OpenRead(Fixture()));

    private async Task<string> ProducerAsync(string body)
    {
        var script = Path.Combine(_dir, $"producer-{Guid.NewGuid():N}.sh");
        await File.WriteAllTextAsync(script, $"#!/bin/sh\n{body}\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return script;
    }

    /// <summary>
    /// An FFmpeg behind a script that records its pid and arguments, then
    /// optionally waits, then runs the real one with them.
    /// </summary>
    private async Task<(string Ffmpeg, string PidFile, string ArgsFile)> RecordingFfmpegAsync(string real, int sleepSeconds)
    {
        var id = Guid.NewGuid().ToString("N");
        var pidFile = Path.Combine(_dir, $"ffmpeg-{id}.pid");
        var argsFile = Path.Combine(_dir, $"ffmpeg-{id}.args");
        var script = Path.Combine(_dir, $"ffmpeg-{id}.sh");
        await File.WriteAllTextAsync(script,
            $"#!/bin/sh\necho $$ > '{pidFile}'\nprintf '%s\\n' \"$@\" > '{argsFile}'\n"
            + (sleepSeconds > 0 ? $"sleep {sleepSeconds}\n" : "")
            + $"exec {real} \"$@\"\n");
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
        throw new TimeoutException("the producer never started");
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

    /// <summary>
    /// Plays the capped sink: writes the given bytes to the output path — or,
    /// past the cap, deletes nothing it never wrote and says so.
    /// </summary>
    private sealed class FakeFfmpeg(
        byte[]? writes = null, ProcessFileRunResult? outcome = null, bool hangUntilCancelled = false) : IProcessFileRunner
    {
        private int _running;

        public ProcessFileRunRequest? Last { get; private set; }
        public int Calls { get; private set; }
        public int MaxConcurrent { get; private set; }
        public string? Input { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ProcessFileRunResult> RunAsync(ProcessFileRunRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            Last = request;
            Input = request.Arguments[request.Arguments.ToList().IndexOf("-i") + 1];
            MaxConcurrent = Math.Max(MaxConcurrent, Interlocked.Increment(ref _running));
            try
            {
                Started.TrySetResult();
                if (hangUntilCancelled) await Task.Delay(Timeout.Infinite, cancellationToken);
                if (outcome is { } stopped && (stopped.TimedOut || stopped.OutputLimitExceeded)) return stopped;
                if (writes is not null) await File.WriteAllBytesAsync(request.OutputPath, writes, cancellationToken);
                return outcome ?? new ProcessFileRunResult(0, false, false, writes?.Length ?? 0);
            }
            finally
            {
                Interlocked.Decrement(ref _running);
            }
        }
    }

    private sealed class RecordingFileRunner(IProcessFileRunner inner) : IProcessFileRunner
    {
        public List<ProcessFileRunRequest> Requests { get; } = [];
        public List<ProcessFileRunResult> Results { get; } = [];

        public async Task<ProcessFileRunResult> RunAsync(ProcessFileRunRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var result = await inner.RunAsync(request, cancellationToken);
            Results.Add(result);
            return result;
        }
    }

    /// <summary>A seekable stream that remembers the array each read was asked to fill.</summary>
    private sealed class SpyStream(byte[] content, long? claimedLength = null) : MemoryStream(content, writable: false)
    {
        public List<byte[]> Targets { get; } = [];

        public override long Length => claimedLength ?? base.Length;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (MemoryMarshal.TryGetArray<byte>(buffer, out var segment)) Targets.Add(segment.Array!);
            return base.ReadAsync(buffer, cancellationToken);
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

    /// <summary>Every blob is the same small HEIC-signed original.</summary>
    private sealed class HeicBlobs : IBlobService
    {
        public Task<Stream> OpenContentAsync(Guid blobObjectId, CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream>(new MemoryStream(Heic, writable: false));
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
