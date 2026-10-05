using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Jobs;
using NubArca.Api.Storage;

namespace NubArca.Api.Files;

public sealed record VideoColorRegenerationOptions
{
    public int? Limit { get; init; }
    public bool DryRun { get; init; }
    public int PageSize { get; init; } = 100;
}

/// <summary>Numbers only: never a name, a path or a key.</summary>
public sealed record VideoColorRegenerationResult(
    int Examined, int Converted, int PicturesReplaced, int PicturesFailed, int LaddersQueued);

/// <summary>
/// Redoes what a video already has from before its colours were converted.
/// An HDR video's posters, preview strips and H.264 rendition were made by
/// reading HDR as if it were SDR — flat, grey, washed out — and a wide-gamut
/// one's came out duller than it is. Now that every conversion makes them
/// BT.709 SDR (<see cref="VideoColorFormat"/>), those are made again: the
/// pictures of every live file of the video, replaced in place, and its HLS
/// ladder queued for regeneration if it has one. A ladder is also redone for
/// an H.264 source that is not 8-bit 4:2:0, which it used to copy as it was
/// and players cannot decode.
///
/// The format is read from each video's own stream: it is nowhere in the
/// database. Keyset-paged by blob id: candidates are never all loaded at
/// once. What the AI took from such a video is left as it is.
/// </summary>
public sealed class VideoColorRegenerationService
{
    private static readonly string[] PictureSizes = [ThumbnailSizes.Poster, ThumbnailSizes.VideoPreviewStrip];

    private readonly AppDbContext _db;
    private readonly IBlobService _blobs;
    private readonly IVideoColorProbe _color;
    private readonly IFileThumbnailService _thumbnails;
    private readonly IJobQueue _jobs;

    public VideoColorRegenerationService(
        AppDbContext db, IBlobService blobs, IVideoColorProbe color,
        IFileThumbnailService thumbnails, IJobQueue jobs)
    {
        _db = db;
        _blobs = blobs;
        _color = color;
        _thumbnails = thumbnails;
        _jobs = jobs;
    }

    public async Task<VideoColorRegenerationResult> RunAsync(
        VideoColorRegenerationOptions options, CancellationToken cancellationToken)
    {
        int examined = 0, converted = 0, replaced = 0, failed = 0, ladders = 0;
        Guid? after = null;
        while (options.Limit is not int limit || examined < limit)
        {
            var take = options.Limit is int cap ? Math.Min(options.PageSize, cap - examined) : options.PageSize;
            var page = await _db.BlobMetadata.AsNoTracking()
                .Where(m => m.MediaCategory == MediaCategories.Video
                    && (after == null || m.BlobObjectId.CompareTo(after.Value) > 0))
                .OrderBy(m => m.BlobObjectId)
                .Select(m => new { m.BlobObjectId, m.VideoCodec })
                .Take(take)
                .ToListAsync(cancellationToken);
            if (page.Count == 0) break;

            foreach (var video in page)
            {
                examined++;
                after = video.BlobObjectId;
                var format = await ProbeAsync(video.BlobObjectId, cancellationToken);
                // A probe that could not answer says nothing about the video:
                // nothing is redone on its account — a forced ladder that then
                // failed would leave a playable video unplayable.
                if (format.PixelFormat is null) continue;
                var pictures = format.ToBt709Filter() is not null;
                var ladder = pictures
                    || (string.Equals(video.VideoCodec, "h264", StringComparison.OrdinalIgnoreCase) && !format.CanStreamCopy);
                if (!pictures && !ladder) continue;
                if (pictures) converted++;
                if (options.DryRun) continue;

                if (pictures)
                {
                    var (ok, notOk) = await ReplacePicturesAsync(video.BlobObjectId, cancellationToken);
                    replaced += ok;
                    failed += notOk;
                }
                if (ladder && await _db.BlobHlsDerivatives.AnyAsync(h => h.BlobObjectId == video.BlobObjectId, cancellationToken))
                {
                    // The worker's own job: a transcode can take minutes. Same
                    // per-blob key as every other enqueue, so it never doubles.
                    await _jobs.EnqueueAsync(
                        JobTypes.MediaVideoHlsGenerate,
                        new VideoHlsGenerateJobPayload(video.BlobObjectId, Force: true),
                        idempotencyKey: $"{JobTypes.MediaVideoHlsGenerate}:{video.BlobObjectId:N}",
                        cancellationToken: cancellationToken);
                    ladders++;
                }
            }
        }
        return new VideoColorRegenerationResult(examined, converted, replaced, failed, ladders);
    }

    private async Task<(int Replaced, int Failed)> ReplacePicturesAsync(Guid blobId, CancellationToken cancellationToken)
    {
        var files = await _db.FileItems.AsNoTracking()
            .Where(f => f.BlobObjectId == blobId && f.DeletedAt == null)
            .Select(f => new { f.Id, f.OwnerUserId })
            .ToListAsync(cancellationToken);
        int replaced = 0, failed = 0;
        foreach (var file in files)
        {
            foreach (var size in PictureSizes)
            {
                // Forced: the old picture is replaced, and stays served if the
                // new one cannot be made.
                var outcome = await _thumbnails.RegenerateGalleryDerivativeAsync(
                    file.Id, file.OwnerUserId, size, force: true, cancellationToken);
                if (outcome is GalleryDerivativeReplacementOutcome.Replaced or GalleryDerivativeReplacementOutcome.CreatedMissing)
                    replaced++;
                else if (outcome is GalleryDerivativeReplacementOutcome.Failed)
                    failed++;
            }
        }
        return (replaced, failed);
    }

    private async Task<VideoColorFormat> ProbeAsync(Guid blobId, CancellationToken cancellationToken)
    {
        // ffprobe reads the stored file itself when storage hands out its
        // read-only FileStream, as production's does; anything else is copied
        // to a temp file first.
        string? temp = null;
        try
        {
            await using var source = await _blobs.OpenContentAsync(blobId, cancellationToken);
            var input = source is FileStream file && File.Exists(file.Name) ? file.Name : null;
            if (input is null)
            {
                temp = Path.Combine(Path.GetTempPath(), $"nubarca-color-{Guid.NewGuid():N}");
                await using (var destination = File.Create(temp))
                {
                    await source.CopyToAsync(destination, cancellationToken);
                }
                input = temp;
            }
            return await _color.ProbeAsync(input, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return VideoColorFormat.Unknown;
        }
        finally
        {
            if (temp is not null)
            {
                try { File.Delete(temp); } catch (IOException) { }
            }
        }
    }
}
