using System.Text.Json;
using NubArca.Api.Files;

namespace NubArca.Api.Jobs.Handlers;

// Video-hls slice 1: drives VideoHlsGenerationService for ONE source blob.
// Bounded point work (a single transcode), so no slicing/checkpointing — the
// worker's heartbeat renews the lease while the (possibly minutes-long) ffmpeg
// run executes, and cancellation propagates into the service, which kills the
// child process, rolls the pending row back and never records a failure for a
// cancel (job rules). Log lines carry the outcome only — never storage keys,
// paths, or hashes.
public sealed class VideoHlsGenerateJobHandler : IJobHandler
{
    private readonly VideoHlsGenerationService _service;

    public VideoHlsGenerateJobHandler(VideoHlsGenerationService service)
    {
        _service = service;
    }

    public string JobType => JobTypes.MediaVideoHlsGenerate;

    public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Deserialize<VideoHlsGenerateJobPayload>(context.PayloadJson)
            ?? throw new ArgumentException("media.video.hls.generate requires a payload with BlobObjectId.");
        if (payload.BlobObjectId == Guid.Empty)
        {
            throw new ArgumentException("media.video.hls.generate requires a non-empty BlobObjectId.");
        }

        var outcome = await _service.EnsureGeneratedAsync(
            payload.BlobObjectId, payload.Force, cancellationToken);
        context.Log($"hls generate: blob {payload.BlobObjectId:N} → {outcome}");
    }
}
