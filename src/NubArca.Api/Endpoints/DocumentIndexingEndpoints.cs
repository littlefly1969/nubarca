using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NubArca.Api.Ai;
using NubArca.Api.Ai.Documents.Indexing;
using NubArca.Api.Http;

namespace NubArca.Api.Endpoints;

/// "How far along is the indexing of MY documents?" Counts only.
///
/// The owner comes from the authenticated identity; there is no parameter that
/// could name anybody else. The response is counts per phase and state plus
/// reason tokens with counts — never a document name, an id, a path or a
/// profile internals beyond what the status endpoint already states.
public static class DocumentIndexingEndpoints
{
    public static IEndpointRouteBuilder MapDocumentIndexingEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/documents/indexing/status", async (
            HttpContext httpContext,
            [FromServices] IOptions<AiOptions> ai,
            CancellationToken cancellationToken) =>
        {
            // Registered only on hosts with a database.
            var progress = httpContext.RequestServices.GetService<DocumentIndexProgressService>();
            if (progress is null) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var options = ai.Value;
            var result = await progress.GetAsync(ownerUserId, cancellationToken);
            return Results.Ok(new
            {
                automaticIndexing = options.Enabled && options.DocumentExtractionEnabled,
                eligibleSources = result.EligibleSources,
                phases = result.Phases,
                reasons = result.ReasonCounts,
            });
        }).WithName("DocumentIndexingStatus").RequireAuthorization();

        return app;
    }
}
