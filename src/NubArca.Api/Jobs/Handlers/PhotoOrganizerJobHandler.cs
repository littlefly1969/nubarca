using System.Text.Json;
using NubArca.Api.Organizer;

namespace NubArca.Api.Jobs.Handlers;

// Phase 2: drives one cooperative slice of a photo date-taken organizer run.
// The payload references the PhotoOrganizerRun row by id; the service reads its
// options, performs DB-only logical moves, records the manifest, and checkpoints
// between slices so a foreground import can run in between. No business logic is
// duplicated here.
public sealed class PhotoOrganizerJobHandler : IJobHandler
{
    private readonly PhotoDateTakenOrganizerService _service;

    public PhotoOrganizerJobHandler(PhotoDateTakenOrganizerService service)
    {
        _service = service;
    }

    public string JobType => JobTypes.PhotoOrganizerDateTaken;

    public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Deserialize<PhotoOrganizerJobPayload>(context.PayloadJson);
        if (payload is null || payload.RunId == Guid.Empty)
        {
            throw new ArgumentException("PhotoOrganizerJobPayload.RunId is required.");
        }

        await _service.ExecuteSliceAsync(payload.RunId, context, cancellationToken);
    }
}
