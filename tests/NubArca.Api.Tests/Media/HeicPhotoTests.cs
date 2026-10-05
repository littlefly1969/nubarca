using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Print;
using NubArca.Api.Storage;
using NubArca.Api.Tests.Endpoints;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;

namespace NubArca.Api.Tests.Media;

/// <summary>
/// HEIC — the iPhone's photo format — as a photograph: recognised by its
/// signature, decoded by FFmpeg from the original (lossless, upright, tiles
/// assembled, the container's rotation applied), and from there everything a
/// JPEG is: a photograph in the Library, thumbnails, the print's source, the
/// AI's pixels, and a second look for the ones uploaded before.
/// </summary>
public sealed class HeicPhotoTests
{
    // --- the signature ------------------------------------------------------------

    private static byte[] Ftyp(string major, params string[] compatible)
    {
        var size = 16 + (4 * compatible.Length);
        var box = new byte[Math.Max(size, 32)];
        box[0] = 0; box[1] = 0; box[2] = 0; box[3] = (byte)size;
        "ftyp"u8.CopyTo(box.AsSpan(4));
        System.Text.Encoding.ASCII.GetBytes(major).CopyTo(box, 8);
        for (var i = 0; i < compatible.Length; i++)
            System.Text.Encoding.ASCII.GetBytes(compatible[i]).CopyTo(box, 16 + (4 * i));
        return box;
    }

    [Fact]
    public void Heic_Is_Recognised_By_Its_Brands_And_Nothing_Else_Is()
    {
        // Apple: major brand heic, compatible mif1/heic.
        Assert.True(HeifSignature.IsHeif(Ftyp("heic", "mif1", "heic")));
        // A generic HEIF major brand with HEIC among the compatible ones.
        Assert.True(HeifSignature.IsHeif(Ftyp("mif1", "mif1", "heic")));
        Assert.True(HeifSignature.IsHeif(Ftyp("heix", "mif1")));
        // AVIF is HEIF too, but AV1-coded: not this decoder's.
        Assert.False(HeifSignature.IsHeif(Ftyp("avif", "mif1", "miaf")));
        // A video, a JPEG, nothing.
        Assert.False(HeifSignature.IsHeif(Ftyp("isom", "isom", "mp42")));
        Assert.False(HeifSignature.IsHeif(Ftyp("qt  ", "qt  ")));
        Assert.False(HeifSignature.IsHeif([0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, 0x4A, 0x46, 0x49, 0x46, 0, 1, 1, 0, 0, 1]));
        Assert.False(HeifSignature.IsHeif([]));
        Assert.True(OriginalImageReader.IsUprightOnDecode("image/heic"));
        Assert.False(OriginalImageReader.IsUprightOnDecode("image/jpeg"));
    }

    // --- the real thing, through the app --------------------------------------------

    private static string Fixture(string name = "iphone-like-grid-rotated.heic")
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NubArca.sln")))
            directory = directory.Parent;
        return Path.Combine(directory!.FullName, "scripts", "media-tools", "fixtures", name);
    }

    /// <summary>
    /// Skips where no HEIC-capable FFmpeg is on PATH — except in CI, which
    /// installs the production media tools: there a missing one fails, so the
    /// lane can never go green by skipping the very thing it is for.
    /// </summary>
    private static void RequireHeicFfmpeg()
    {
        var available = FfmpegReadsHeic();
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
            Assert.True(available, "CI must install the media tools (FFmpeg 7.1+ with HEIC)");
        Skip.IfNot(available, "no HEIC-capable FFmpeg on PATH");
    }

    /// <summary>An FFmpeg on PATH that decodes HEIC (7.1+); CI installs the production media tools.</summary>
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

    [SkippableFact]
    public async Task An_Uploaded_Heic_Is_A_Photograph_With_Thumbnails_A_Print_Source_And_Upright_Pixels()
    {
        RequireHeicFfmpeg();
        using var factory = new SqliteWebApplicationFactory();
        factory.EnsureDatabaseCreated();
        var (ownerId, client) = await factory.CreateAuthenticatedClientAsync("iphone@example.com");

        var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(await File.ReadAllBytesAsync(Fixture()));
        // What a browser says for it: the server decides by the bytes, not this.
        part.Headers.ContentType = new MediaTypeHeaderValue("image/heif");
        form.Add(part, "file", "IMG_0001.HEIC");
        var uploaded = await client.PostAsync("/api/files", form);
        uploaded.EnsureSuccessStatusCode();
        var fileId = (await uploaded.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var file = await db.FileItems.SingleAsync(f => f.Id == fileId);
            var meta = await db.BlobMetadata.SingleAsync(m => m.BlobObjectId == file.BlobObjectId);
            Assert.Equal("image/heic", meta.DetectedContentType);
            Assert.Equal(MediaCategories.Image, meta.MediaCategory);
            // The 800x600 grid, rotated 90 degrees in its container: displayed upright.
            Assert.Equal((600, 800), (meta.Width!.Value, meta.Height!.Value));
            Assert.Equal((600, 800), (file.Width!.Value, file.Height!.Value));
            // Already upright: no EXIF orientation may turn it again.
            Assert.True(meta.Orientation is null or 1);

            // A photograph by the Library's own rule.
            Assert.True(await db.FileItems.Where(f => f.Id == fileId)
                .Where(NubArca.Api.MediaLibrary.LibraryPhotoRule.IsPhoto(db)).AnyAsync());

            // The print's source: the original's frame, lossless and upright.
            var source = await scope.ServiceProvider.GetRequiredService<IPrintPhotoSourceReader>()
                .ReadAsync(ownerId, fileId, default);
            using var decoded = Image.Load<Rgb24>(source!);
            Assert.Equal((600, 800), (decoded.Width, decoded.Height));
        }

        // A small thumbnail exists and stands upright too.
        var thumbnail = await client.GetAsync($"/api/files/{fileId}/thumbnail?size=small");
        Assert.Equal(HttpStatusCode.OK, thumbnail.StatusCode);
        using var small = Image.Load<Rgb24>(await thumbnail.Content.ReadAsByteArrayAsync());
        Assert.True(small.Height > small.Width, $"thumbnail is {small.Width}x{small.Height}");
    }

    [SkippableFact]
    public async Task A_Bulk_Import_Recognises_A_Heic_As_An_Upload_Does()
    {
        // The bulk upload (a staging session) and the administrator's import
        // both run the import's batch pipeline, which recognises files itself
        // before any database row exists.
        RequireHeicFfmpeg();
        var root = Directory.CreateTempSubdirectory("nubarca-heic-import-").FullName;
        try
        {
            File.Copy(Fixture(), Path.Combine(root, "IMG_0001.HEIC"));
            using var factory = new SqliteWebApplicationFactory(new Dictionary<string, string?>
            {
                ["AdminImport:Enabled"] = "true",
                ["AdminImport:Roots:0"] = root,
            });
            factory.EnsureDatabaseCreated();
            var adminId = await factory.SeedUserAsync("admin@example.com");
            await factory.PromoteToAdminAsync(adminId);
            var admin = await factory.LoginAsync("admin@example.com");
            var targetId = await factory.SeedUserAsync("iphone@example.com");
            var roots = await admin.GetFromJsonAsync<NubArca.Api.Admin.AdminImportRootsResponse>("/api/admin/import/roots");
            (await admin.PostAsJsonAsync("/api/admin/import/run", new
            {
                rootId = roots!.Roots[0].RootId, relativePath = "", targetUserId = targetId,
                destinationFolderId = (Guid?)null,
            })).EnsureSuccessStatusCode();
            await using (var run = factory.Services.CreateAsyncScope())
            {
                await run.ServiceProvider.GetRequiredService<NubArca.Api.Jobs.JobProcessor>().ProcessAvailableAsync(1);
            }

            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var file = await db.FileItems.SingleAsync(f => f.OwnerUserId == targetId);
            var meta = await db.BlobMetadata.SingleAsync(m => m.BlobObjectId == file.BlobObjectId);
            Assert.Equal("image/heic", meta.DetectedContentType);
            Assert.Equal(MediaCategories.Image, meta.MediaCategory);
            Assert.Equal((600, 800), (meta.Width!.Value, meta.Height!.Value));
            Assert.True(await db.FileItems.Where(f => f.Id == file.Id)
                .Where(NubArca.Api.MediaLibrary.LibraryPhotoRule.IsPhoto(db)).AnyAsync());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_Bulk_Import_Cancelled_While_Decoding_A_Heic_Stops_Ffmpeg_At_Once(bool operatorCancel)
    {
        // The worker shutting down (its token) or the operator's cancel
        // request (the heartbeat trips the job's token): either way the decode
        // in flight is stopped, not waited for, and leaves no FFmpeg behind.
        RequireHeicFfmpeg();
        var root = Directory.CreateTempSubdirectory("nubarca-heic-cancel-").FullName;
        var work = Directory.CreateTempSubdirectory("nubarca-heic-cancel-ffmpeg-").FullName;
        try
        {
            File.Copy(Fixture(), Path.Combine(root, "IMG_0001.HEIC"));
            var pidFile = Path.Combine(work, "ffmpeg.pid");
            var argsFile = Path.Combine(work, "ffmpeg.args");
            var slowFfmpeg = Path.Combine(work, "slow-ffmpeg.sh");
            await File.WriteAllTextAsync(slowFfmpeg,
                $"#!/bin/sh\necho $$ > '{pidFile}'\nprintf '%s\\n' \"$@\" > '{argsFile}'\nsleep 60\nexec ffmpeg \"$@\"\n");
            File.SetUnixFileMode(slowFfmpeg, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            using var factory = new SqliteWebApplicationFactory(new Dictionary<string, string?>
            {
                ["AdminImport:Enabled"] = "true",
                ["AdminImport:Roots:0"] = root,
                ["Media:FfmpegPath"] = slowFfmpeg,
                ["Jobs:HeartbeatSeconds"] = "1",
            });
            factory.EnsureDatabaseCreated();
            var adminId = await factory.SeedUserAsync("admin@example.com");
            await factory.PromoteToAdminAsync(adminId);
            var admin = await factory.LoginAsync("admin@example.com");
            var targetId = await factory.SeedUserAsync("iphone@example.com");
            var roots = await admin.GetFromJsonAsync<NubArca.Api.Admin.AdminImportRootsResponse>("/api/admin/import/roots");
            var started = await admin.PostAsJsonAsync("/api/admin/import/run", new
            {
                rootId = roots!.Roots[0].RootId, relativePath = "", targetUserId = targetId,
                destinationFolderId = (Guid?)null,
            });
            started.EnsureSuccessStatusCode();
            var runId = (await started.Content.ReadFromJsonAsync<NubArca.Api.Admin.AdminImportRunResponse>())!.ImportRunId;

            using var shutdown = new CancellationTokenSource();
            var processing = Task.Run(async () =>
            {
                await using var scope = factory.Services.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<NubArca.Api.Jobs.JobProcessor>()
                    .ProcessAvailableAsync(1, cancellationToken: shutdown.Token);
            });
            var pid = await ReadPidAsync(pidFile);
            var clock = Stopwatch.StartNew();
            if (operatorCancel)
                (await admin.PostAsync($"/api/admin/import/runs/{runId}/cancel", null)).EnsureSuccessStatusCode();
            else
                shutdown.Cancel();

            try
            {
                await processing.WaitAsync(TimeSpan.FromSeconds(30));
            }
            catch (OperationCanceledException) when (!operatorCancel)
            {
                // A worker stopping hands its cancellation back to its host.
            }
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"took {clock.Elapsed}");
            await AssertGoneAsync(pid);
            Assert.False(File.Exists((await File.ReadAllLinesAsync(argsFile)).Last()));

            using var check = factory.Services.CreateScope();
            var db = check.ServiceProvider.GetRequiredService<AppDbContext>();
            // Nothing half-imported: the file did not become a FileItem.
            Assert.False(await db.FileItems.AnyAsync(f => f.OwnerUserId == targetId));
            if (operatorCancel)
            {
                Assert.Equal(NubArca.Api.Domain.AdminImportStatuses.Cancelled,
                    await db.AdminImportRuns.Where(r => r.Id == runId).Select(r => r.Status).SingleAsync());
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(work, recursive: true);
        }
    }

    private static async Task<int> ReadPidAsync(string pidFile)
    {
        for (var i = 0; i < 800; i++)
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

    [SkippableFact]
    public async Task An_Iphone_Heic_Whose_Exif_Still_Says_Six_Is_Not_Turned_A_Second_Time()
    {
        // As an iPhone writes it: the container rotates the 800x600 grid 90
        // degrees (displayed 600x800), and the EXIF block STILL reads
        // orientation 6. FFmpeg applies the container's rotation; applying the
        // EXIF value on top would turn every portrait photograph sideways.
        RequireHeicFfmpeg();
        using var factory = new SqliteWebApplicationFactory();
        factory.EnsureDatabaseCreated();
        var (_, client) = await factory.CreateAuthenticatedClientAsync("portrait@example.com");
        var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(await File.ReadAllBytesAsync(Fixture("iphone-like-exif-orientation-6.heic")));
        part.Headers.ContentType = new MediaTypeHeaderValue("image/heif");
        form.Add(part, "file", "IMG_0006.HEIC");
        var uploaded = await client.PostAsync("/api/files", form);
        uploaded.EnsureSuccessStatusCode();
        var fileId = (await uploaded.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var blob = await db.FileItems.Where(f => f.Id == fileId).Select(f => f.BlobObjectId).SingleAsync();
        // The EXIF is actually read — on upload or by the metadata backfill.
        if (await db.BlobMetadata.AnyAsync(m => m.BlobObjectId == blob && m.ExtractionStatus == MetadataStatuses.Pending))
        {
            await scope.ServiceProvider.GetRequiredService<NubArca.Api.Metadata.MetadataBackfillService>()
                .RunAsync(new NubArca.Api.Metadata.MetadataBackfillOptions());
        }
        var meta = await db.BlobMetadata.AsNoTracking().SingleAsync(m => m.BlobObjectId == blob);
        Assert.Equal(MetadataStatuses.Completed, meta.ExtractionStatus);
        Assert.Equal((600, 800), (meta.Width!.Value, meta.Height!.Value));
        Assert.Equal(1, meta.Orientation);
        // So every display shape the server computes stays portrait.
        var (w, h) = NubArca.Api.Metadata.ImageDisplayDimensions.Resolve(meta.Width, meta.Height, meta.Orientation);
        Assert.Equal((600, 800), (w!.Value, h!.Value));
    }

    [SkippableFact]
    public async Task A_Heic_Uploaded_Before_Is_Recognised_By_The_Second_Look_And_Nothing_Else_Is()
    {
        RequireHeicFfmpeg();
        using var factory = new SqliteWebApplicationFactory();
        factory.EnsureDatabaseCreated();
        var (_, client) = await factory.CreateAuthenticatedClientAsync("older@example.com");

        async Task<Guid> UploadAsync(byte[] bytes, string name)
        {
            var form = new MultipartFormDataContent();
            var part = new ByteArrayContent(bytes);
            part.Headers.ContentType = new MediaTypeHeaderValue("image/heif");
            form.Add(part, "file", name);
            var response = await client.PostAsync("/api/files", form);
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        }

        var heic = await UploadAsync(await File.ReadAllBytesAsync(Fixture()), "IMG_0002.HEIC");
        // Bytes that only CLAIM to be HEIC: they stay unrecognised.
        var fake = await UploadAsync(Enumerable.Repeat((byte)7, 512).ToArray(), "NOT_A_PHOTO.HEIC");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // As the HEIC looked when uploaded before the server could decode it:
        // an "image" by its MIME type, nothing detected, EXIF's orientation 6.
        var blob = await db.FileItems.Where(f => f.Id == heic).Select(f => f.BlobObjectId).SingleAsync();
        await db.BlobMetadata.Where(m => m.BlobObjectId == blob).ExecuteUpdateAsync(s => s
            .SetProperty(m => m.DetectedContentType, (string?)null)
            .SetProperty(m => m.DetectedFormat, (string?)null)
            .SetProperty(m => m.Width, (int?)null)
            .SetProperty(m => m.Height, (int?)null)
            .SetProperty(m => m.Orientation, 6));
        await db.FileItems.Where(f => f.Id == heic).ExecuteUpdateAsync(s => s
            .SetProperty(f => f.Width, (int?)null).SetProperty(f => f.Height, (int?)null));

        var service = scope.ServiceProvider.GetRequiredService<ImageRedetectionService>();
        var dry = await service.RunAsync(new ImageRedetectionOptions { DryRun = true }, default);
        Assert.Equal(1, dry.Recognised);
        Assert.Null(await db.BlobMetadata.Where(m => m.BlobObjectId == blob).Select(m => m.DetectedContentType).SingleAsync());

        var run = await service.RunAsync(new ImageRedetectionOptions(), default);
        Assert.Equal(1, run.Recognised);
        Assert.True(run.StillUnreadable >= 1);
        var meta = await db.BlobMetadata.AsNoTracking().SingleAsync(m => m.BlobObjectId == blob);
        Assert.Equal("image/heic", meta.DetectedContentType);
        Assert.Equal((600, 800, 1), (meta.Width!.Value, meta.Height!.Value, meta.Orientation!.Value));
        Assert.Equal((600, 800), await db.FileItems.Where(f => f.Id == heic)
            .Select(f => ValueTuple.Create(f.Width!.Value, f.Height!.Value)).SingleAsync());

        var fakeBlob = await db.FileItems.Where(f => f.Id == fake).Select(f => f.BlobObjectId).SingleAsync();
        Assert.Null(await db.BlobMetadata.Where(m => m.BlobObjectId == fakeBlob).Select(m => m.DetectedContentType).SingleAsync());

        // Done once: a second look changes nothing.
        Assert.Equal(0, (await service.RunAsync(new ImageRedetectionOptions(), default)).Recognised);
    }
}
