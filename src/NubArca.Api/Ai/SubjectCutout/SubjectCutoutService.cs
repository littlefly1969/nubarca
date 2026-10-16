using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NubArca.Api.Ai.Onnx;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Uploads;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace NubArca.Api.Ai.SubjectCutout;

public enum SubjectCutoutStatus
{
    Ok,
    // Missing, foreign, trashed, in the Private Vault, or not a photograph:
    // one answer for all of them (no-leak).
    NotFound,
    // The feature is off or its model cannot run here. Environment state,
    // never something about the photo.
    Unavailable,
    // Another cutout holds the inference slot past the queue timeout.
    Busy,
    // The model found nothing to lift.
    NoSubject,
}

public sealed record SubjectCutoutImage(byte[] Png, int Width, int Height);

public sealed record SubjectCutoutResult(SubjectCutoutStatus Status, SubjectCutoutImage? Image = null)
{
    public static readonly SubjectCutoutResult NotFound = new(SubjectCutoutStatus.NotFound);
    public static readonly SubjectCutoutResult Unavailable = new(SubjectCutoutStatus.Unavailable);
    public static readonly SubjectCutoutResult Busy = new(SubjectCutoutStatus.Busy);
    public static readonly SubjectCutoutResult NoSubject = new(SubjectCutoutStatus.NoSubject);
}

public sealed record SubjectCutoutSaveResult(SubjectCutoutStatus Status, FileItem? File = null);

// One cutout at a time per process: the model takes seconds and gigabytes, and
// a second concurrent run would only make both slower. Singleton.
public sealed class SubjectCutoutGate : IDisposable
{
    private readonly SemaphoreSlim _slot = new(1, 1);

    public async Task<IDisposable?> TryEnterAsync(TimeSpan wait, CancellationToken cancellationToken)
        => await _slot.WaitAsync(wait, cancellationToken) ? new Lease(_slot) : null;

    public void Dispose() => _slot.Dispose();

    private sealed class Lease(SemaphoreSlim slot) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) slot.Release();
        }
    }
}

// "Copy subject" for the owner's own photographs.
//
// The source is the photo's MEDIUM preview, never the original: it is upright,
// decodable whatever the original format (HEIC included), already at a size a
// clipboard or a sticker wants, and it is the same derivative the viewer is
// showing. A cutout is computed on request and not stored; keeping one is an
// explicit "save", which files it as a new ordinary PNG next to the photo —
// the original blob is never touched.
public sealed class SubjectCutoutService
{
    private readonly AppDbContext _db;
    private readonly IFileThumbnailService _thumbnails;
    private readonly IOwnerUploadIngestion _ingestion;
    private readonly ISubjectMaskModel _model;
    private readonly SubjectCutoutGate _gate;
    private readonly IOptions<AiOptions> _options;
    private readonly ILogger<SubjectCutoutService> _logger;

    public SubjectCutoutService(
        AppDbContext db,
        IFileThumbnailService thumbnails,
        IOwnerUploadIngestion ingestion,
        ISubjectMaskModel model,
        SubjectCutoutGate gate,
        IOptions<AiOptions> options,
        ILogger<SubjectCutoutService> logger)
    {
        _db = db;
        _thumbnails = thumbnails;
        _ingestion = ingestion;
        _model = model;
        _gate = gate;
        _options = options;
        _logger = logger;
    }

    public bool IsAvailable() => _model.UnavailableReason() is null;

    public async Task<SubjectCutoutResult> CreateAsync(
        Guid fileItemId, Guid ownerUserId, CancellationToken cancellationToken)
    {
        var reason = _model.UnavailableReason();
        if (reason is not null)
        {
            _logger.LogInformation("Subject cutout unavailable: {Reason}", reason);
            return SubjectCutoutResult.Unavailable;
        }

        // Owner-scoped, trash- and vault-aware, images only: null otherwise.
        var preview = await _thumbnails.EnsureAsync(
            fileItemId, ownerUserId, ThumbnailSizes.Medium, cancellationToken);
        if (preview is null) return SubjectCutoutResult.NotFound;

        Image<Rgb24> photo;
        await using (preview.Content)
        {
            photo = await Image.LoadAsync<Rgb24>(preview.Content, cancellationToken);
        }

        using (photo)
        {
            var settings = _options.Value.SubjectCutout;
            var slot = await _gate.TryEnterAsync(
                TimeSpan.FromSeconds(Math.Max(0, settings.QueueTimeoutSeconds)), cancellationToken);
            if (slot is null) return SubjectCutoutResult.Busy;

            var size = settings.InputSize;
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var input = SubjectCutoutImaging.ToInputTensor(photo, size);
            // A started inference cannot be interrupted, so the slot is held until
            // it really ends — even when this request stops waiting for it.
            var work = Task.Run(() => _model.PredictLogits(input, size), CancellationToken.None);
            _ = work.ContinueWith(done =>
            {
                _ = done.Exception; // observed here when nobody is waiting any more
                slot.Dispose();
            }, TaskScheduler.Default);
            float[] logits;
            try
            {
                logits = settings.TimeoutSeconds > 0
                    ? await work.WaitAsync(TimeSpan.FromSeconds(settings.TimeoutSeconds), cancellationToken)
                    : await work.WaitAsync(cancellationToken);
            }
            catch (OnnxSessionUnavailableException ex)
            {
                _logger.LogWarning("Subject cutout model could not run: {Reason}", ex.ReasonCode);
                return SubjectCutoutResult.Unavailable;
            }
            catch (Microsoft.ML.OnnxRuntime.OnnxRuntimeException)
            {
                // Native text can carry paths; only a fixed token is logged.
                _logger.LogWarning("Subject cutout model could not run: {Reason}", "onnx-run-failed");
                return SubjectCutoutResult.Unavailable;
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("Subject cutout timed out after {Seconds}s", settings.TimeoutSeconds);
                return SubjectCutoutResult.Busy;
            }

            using var cutout = SubjectCutoutImaging.ToCutout(photo, logits, size);
            if (cutout is null) return SubjectCutoutResult.NoSubject;

            using var png = new MemoryStream();
            await cutout.SaveAsync(png, new PngEncoder { ColorType = PngColorType.RgbWithAlpha }, cancellationToken);
            _logger.LogInformation(
                "Subject cutout {Width}x{Height} in {ElapsedMs} ms",
                cutout.Width, cutout.Height,
                (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return new SubjectCutoutResult(
                SubjectCutoutStatus.Ok, new SubjectCutoutImage(png.ToArray(), cutout.Width, cutout.Height));
        }
    }

    // Computes the cutout and files it as a new PNG in the photo's own folder,
    // through the one owner ingest (dedup, quota, audit, media pipeline). A name
    // already taken gets the usual " (2)" suffix. Ingest failures (quota, size)
    // propagate exactly as they do for an upload.
    public async Task<SubjectCutoutSaveResult> SaveAsync(
        Guid fileItemId, Guid ownerUserId, string? ipAddress, CancellationToken cancellationToken)
    {
        var source = await _db.FileItems.AsNoTracking()
            .Where(f => f.Id == fileItemId && f.OwnerUserId == ownerUserId && f.DeletedAt == null)
            .Select(f => new { f.Name, f.ParentFolderId })
            .FirstOrDefaultAsync(cancellationToken);
        if (source is null) return new SubjectCutoutSaveResult(SubjectCutoutStatus.NotFound);

        var result = await CreateAsync(fileItemId, ownerUserId, cancellationToken);
        if (result.Status != SubjectCutoutStatus.Ok) return new SubjectCutoutSaveResult(result.Status);

        using var content = new MemoryStream(result.Image!.Png, writable: false);
        var created = await _ingestion.IngestAsync(
            new OwnerUploadRequest(
                ownerUserId, source.ParentFolderId, CutoutFileName(source.Name), RelativePath: null,
                ContentType: "image/png", UploadNameConflictPolicy.Rename,
                UploadOperationClaimToken: null, ipAddress),
            content,
            cancellationToken);
        return new SubjectCutoutSaveResult(SubjectCutoutStatus.Ok, created);
    }

    // "IMG_0001.HEIC" → "IMG_0001-cutout.png".
    internal static string CutoutFileName(string sourceName)
    {
        var stem = Path.GetFileNameWithoutExtension(sourceName);
        return $"{(string.IsNullOrWhiteSpace(stem) ? "photo" : stem)}-cutout.png";
    }
}
