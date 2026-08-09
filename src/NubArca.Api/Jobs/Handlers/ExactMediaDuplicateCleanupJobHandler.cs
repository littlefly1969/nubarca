using System.Text.Json;
using NubArca.Api.Files;

namespace NubArca.Api.Jobs.Handlers;

public sealed class ExactMediaDuplicateCleanupJobHandler : IJobHandler
{
    private readonly ExactMediaDuplicateCleanupService _service;

    public ExactMediaDuplicateCleanupJobHandler(ExactMediaDuplicateCleanupService service)
    {
        _service = service;
    }

    public string JobType => JobTypes.MediaExactDuplicateCleanup;

    public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Deserialize<MediaDuplicateCleanupJobPayload>(context.PayloadJson);
        if (payload is null || payload.RunId == Guid.Empty)
        {
            throw new ArgumentException("MediaDuplicateCleanupJobPayload.RunId is required.");
        }

        await _service.ExecuteSliceAsync(payload.RunId, context, cancellationToken);
    }
}
