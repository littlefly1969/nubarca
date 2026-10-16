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

// Region: where the cutout sits in the photo, normalized (0..1), so a client can
// map a tap on the cutout back onto the photo.
public sealed record SubjectCutoutImage(byte[] Png, int Width, int Height, SubjectCutoutRegion Region);

public readonly record struct SubjectCutoutRegion(float X, float Y, float Width, float Height);

// A touched point, normalized to the photo: (0,0) top-left, (1,1) bottom-right.
public readonly record struct SubjectPoint(float X, float Y)
{
    public bool IsValid => float.IsFinite(X) && float.IsFinite(Y) && X is >= 0 and <= 1 && Y is >= 0 and <= 1;
}

public sealed record SubjectCutoutResult(SubjectCutoutStatus Status, SubjectCutoutImage? Image = null)
{
    public static readonly SubjectCutoutResult NotFound = new(SubjectCutoutStatus.NotFound);
    public static readonly SubjectCutoutResult Unavailable = new(SubjectCutoutStatus.Unavailable);
    public static readonly SubjectCutoutResult Busy = new(SubjectCutoutStatus.Busy);
    public static readonly SubjectCutoutResult NoSubject = new(SubjectCutoutStatus.NoSubject);
}

// The subject's alpha over the whole photo; whoever holds an Ok result owns
// and disposes the mask.
public sealed record SubjectMaskResult(SubjectCutoutStatus Status, Image<L8>? Mask = null) : IDisposable
{
    public void Dispose() => Mask?.Dispose();
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

// The subject of one of the owner's photographs as a mask, for a feature that
// draws with it (the "Esce dalla foto" print). The seam tests replace.
public interface ISubjectMaskSource
{
    Task<SubjectMaskResult> CreateMaskAsync(Guid fileItemId, Guid ownerUserId, CancellationToken cancellationToken);
}

// "Copy subject" for the owner's own photographs.
//
// The source is the photo's MEDIUM preview, never the original: it is upright,
// decodable whatever the original format (HEIC included), already at a size a
// clipboard or a sticker wants, and it is the same derivative the viewer is
// showing. A cutout is computed on request and not stored; keeping one is an
// explicit "save", which files it as a new ordinary PNG next to the photo —
// the original blob is never touched.
public sealed class SubjectCutoutService : ISubjectMaskSource
{
    private readonly AppDbContext _db;
    private readonly IFileThumbnailService _thumbnails;
    private readonly IOwnerUploadIngestion _ingestion;
    private readonly ISubjectMaskModel _model;
    private readonly ISubjectPointModel _pointModel;
    private readonly SubjectCutoutGate _gate;
    private readonly IOptions<AiOptions> _options;
    private readonly ILogger<SubjectCutoutService> _logger;

    public SubjectCutoutService(
        AppDbContext db,
        IFileThumbnailService thumbnails,
        IOwnerUploadIngestion ingestion,
        ISubjectMaskModel model,
        ISubjectPointModel pointModel,
        SubjectCutoutGate gate,
        IOptions<AiOptions> options,
        ILogger<SubjectCutoutService> logger)
    {
        _db = db;
        _thumbnails = thumbnails;
        _ingestion = ingestion;
        _model = model;
        _pointModel = pointModel;
        _gate = gate;
        _options = options;
        _logger = logger;
    }

    public bool IsAvailable() => _model.UnavailableReason() is null;

    // "Tap a subject" needs the cutout AND the point model.
    public bool IsPointSelectionAvailable() => IsAvailable() && _pointModel.UnavailableReason() is null;

    // The whole subject, or — with `point` — only the subject the owner touched.
    public async Task<SubjectCutoutResult> CreateAsync(
        Guid fileItemId, Guid ownerUserId, SubjectPoint? point, CancellationToken cancellationToken)
    {
        using var lifted = await LiftAsync(fileItemId, ownerUserId, point, cancellationToken);
        if (lifted.Status != SubjectCutoutStatus.Ok) return new SubjectCutoutResult(lifted.Status);
        var (photo, alpha) = (lifted.Photo!, lifted.Alpha!);

        using var cutout = SubjectCutoutImaging.Trim(photo, alpha, out var box);
        if (cutout is null) return SubjectCutoutResult.NoSubject;

        using var png = new MemoryStream();
        await cutout.SaveAsync(png, new PngEncoder { ColorType = PngColorType.RgbWithAlpha }, cancellationToken);
        _logger.LogInformation(
            "Subject cutout {Width}x{Height} (point: {Point}) in {ElapsedMs} ms",
            cutout.Width, cutout.Height, point is not null, lifted.ElapsedMs);
        return new SubjectCutoutResult(
            SubjectCutoutStatus.Ok,
            new SubjectCutoutImage(png.ToArray(), cutout.Width, cutout.Height, new SubjectCutoutRegion(
                (float)box.X / photo.Width, (float)box.Y / photo.Height,
                (float)box.Width / photo.Width, (float)box.Height / photo.Height)));
    }

    // The subject as an alpha mask over the WHOLE photo (upright, at the medium
    // preview's resolution), for a renderer that draws the original's own
    // pixels through it — a print, never a response. The caller disposes it.
    public async Task<SubjectMaskResult> CreateMaskAsync(
        Guid fileItemId, Guid ownerUserId, CancellationToken cancellationToken)
    {
        var lifted = await LiftAsync(fileItemId, ownerUserId, point: null, cancellationToken);
        if (lifted.Status != SubjectCutoutStatus.Ok || SubjectCutoutImaging.SubjectBounds(lifted.Alpha!) is null)
        {
            var status = lifted.Status == SubjectCutoutStatus.Ok ? SubjectCutoutStatus.NoSubject : lifted.Status;
            lifted.Dispose();
            return new SubjectMaskResult(status);
        }
        lifted.Photo!.Dispose();
        return new SubjectMaskResult(SubjectCutoutStatus.Ok, lifted.Alpha);
    }

    // The medium preview and the subject's alpha over it, by the one gated
    // inference every cutout runs.
    private async Task<Lifted> LiftAsync(
        Guid fileItemId, Guid ownerUserId, SubjectPoint? point, CancellationToken cancellationToken)
    {
        var reason = _model.UnavailableReason() ?? (point is null ? null : _pointModel.UnavailableReason());
        if (reason is not null)
        {
            _logger.LogInformation("Subject cutout unavailable: {Reason}", reason);
            return new Lifted(SubjectCutoutStatus.Unavailable);
        }

        // Owner-scoped, trash- and vault-aware, images only: null otherwise.
        var preview = await _thumbnails.EnsureAsync(
            fileItemId, ownerUserId, ThumbnailSizes.Medium, cancellationToken);
        if (preview is null) return new Lifted(SubjectCutoutStatus.NotFound);

        Image<Rgb24> photo;
        await using (preview.Content)
        {
            photo = await Image.LoadAsync<Rgb24>(preview.Content, cancellationToken);
        }

        var keep = false;
        try
        {
            var settings = _options.Value.SubjectCutout;
            var slot = await _gate.TryEnterAsync(
                TimeSpan.FromSeconds(Math.Max(0, settings.QueueTimeoutSeconds)), cancellationToken);
            if (slot is null) return new Lifted(SubjectCutoutStatus.Busy);

            var size = settings.InputSize;
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var input = SubjectCutoutImaging.ToInputTensor(photo, size);
            var pointInput = point is null ? null
                : size == SubjectPointModelShape.InputEdge ? input
                : SubjectCutoutImaging.ToInputTensor(photo, SubjectPointModelShape.InputEdge);
            // A started inference cannot be interrupted, so the slot is held until
            // it really ends — even when this request stops waiting for it.
            var work = Task.Run(() =>
            {
                var logits = _model.PredictLogits(input, size);
                if (point is not { } p) return new Prediction(logits, null, 0);
                var candidates = _pointModel.PredictCandidates(pointInput!, p.X, p.Y, out var count);
                return new Prediction(logits, candidates, count);
            }, CancellationToken.None);
            _ = work.ContinueWith(done =>
            {
                _ = done.Exception; // observed here when nobody is waiting any more
                slot.Dispose();
            }, TaskScheduler.Default);
            Prediction prediction;
            try
            {
                prediction = settings.TimeoutSeconds > 0
                    ? await work.WaitAsync(TimeSpan.FromSeconds(settings.TimeoutSeconds), cancellationToken)
                    : await work.WaitAsync(cancellationToken);
            }
            catch (OnnxSessionUnavailableException ex)
            {
                _logger.LogWarning("Subject cutout model could not run: {Reason}", ex.ReasonCode);
                return new Lifted(SubjectCutoutStatus.Unavailable);
            }
            catch (Microsoft.ML.OnnxRuntime.OnnxRuntimeException)
            {
                // Native text can carry paths; only a fixed token is logged.
                _logger.LogWarning("Subject cutout model could not run: {Reason}", "onnx-run-failed");
                return new Lifted(SubjectCutoutStatus.Unavailable);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("Subject cutout timed out after {Seconds}s", settings.TimeoutSeconds);
                return new Lifted(SubjectCutoutStatus.Busy);
            }

            var alpha = SubjectCutoutImaging.ToAlphaMask(photo, prediction.Logits, size);
            if (prediction.Candidates is { } candidates)
            {
                var chosen = SubjectCutoutImaging.SelectSubject(
                    alpha, candidates, prediction.CandidateCount, SubjectPointModelShape.CandidateEdge,
                    point!.Value.X, point.Value.Y);
                if (chosen is null)
                {
                    alpha.Dispose();
                    return new Lifted(SubjectCutoutStatus.NoSubject);
                }
                SubjectCutoutImaging.RestrictTo(alpha, chosen);
            }

            keep = true;
            return new Lifted(SubjectCutoutStatus.Ok, photo, alpha,
                (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        finally
        {
            if (!keep) photo.Dispose();
        }
    }

    // Computes the cutout and files it as a new PNG in the photo's own folder,
    // through the one owner ingest (dedup, quota, audit, media pipeline). A name
    // already taken gets the usual " (2)" suffix. Ingest failures (quota, size)
    // propagate exactly as they do for an upload.
    public async Task<SubjectCutoutSaveResult> SaveAsync(
        Guid fileItemId, Guid ownerUserId, SubjectPoint? point, string? ipAddress,
        CancellationToken cancellationToken)
    {
        var source = await _db.FileItems.AsNoTracking()
            .Where(f => f.Id == fileItemId && f.OwnerUserId == ownerUserId && f.DeletedAt == null)
            .Select(f => new { f.Name, f.ParentFolderId })
            .FirstOrDefaultAsync(cancellationToken);
        if (source is null) return new SubjectCutoutSaveResult(SubjectCutoutStatus.NotFound);

        var result = await CreateAsync(fileItemId, ownerUserId, point, cancellationToken);
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

    private sealed record Prediction(float[] Logits, float[]? Candidates, int CandidateCount);

    private sealed record Lifted(
        SubjectCutoutStatus Status, Image<Rgb24>? Photo = null, Image<L8>? Alpha = null, long ElapsedMs = 0)
        : IDisposable
    {
        public void Dispose()
        {
            Photo?.Dispose();
            Alpha?.Dispose();
        }
    }

    // "IMG_0001.HEIC" → "IMG_0001-cutout.png".
    internal static string CutoutFileName(string sourceName)
    {
        var stem = Path.GetFileNameWithoutExtension(sourceName);
        return $"{(string.IsNullOrWhiteSpace(stem) ? "photo" : stem)}-cutout.png";
    }
}
