using System.Text.Json;
using NubArca.Api.PhotoExport;

namespace NubArca.Api.Jobs.Handlers;

// Builds a photo-export session's snapshot in cooperative slices. The payload
// carries only the session id; all options/progress live on the session row.
public sealed class PhotoExportBuildJobHandler : IJobHandler
{
    private readonly PhotoExportService _service;

    public PhotoExportBuildJobHandler(PhotoExportService service)
    {
        _service = service;
    }

    public string JobType => JobTypes.PhotoExportBuild;

    public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Deserialize<PhotoExportJobPayload>(context.PayloadJson);
        if (payload is null || payload.SessionId == Guid.Empty)
        {
            throw new ArgumentException("PhotoExportJobPayload.SessionId is required.");
        }

        await _service.ExecuteSliceAsync(payload.SessionId, context, cancellationToken);
    }
}
