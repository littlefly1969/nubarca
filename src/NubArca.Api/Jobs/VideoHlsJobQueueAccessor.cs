using NubArca.Api.Files;

namespace NubArca.Api.Jobs;

// Video-hls slice 2: the Files-layer serving service enqueues generation work
// through this seam (same payload + per-blob idempotency key as the CLI path,
// so endpoint retries and repeated CLI calls collapse onto one queued job).
public sealed class VideoHlsJobQueueAccessor : IJobQueueAccessor
{
    private readonly IJobQueue _queue;

    public VideoHlsJobQueueAccessor(IJobQueue queue)
    {
        _queue = queue;
    }

    public async Task EnqueueVideoHlsGenerateAsync(
        Guid blobObjectId, CancellationToken cancellationToken)
    {
        await _queue.EnqueueAsync(
            JobTypes.MediaVideoHlsGenerate,
            new VideoHlsGenerateJobPayload(blobObjectId),
            idempotencyKey: $"{JobTypes.MediaVideoHlsGenerate}:{blobObjectId:N}",
            cancellationToken: cancellationToken);
    }
}
