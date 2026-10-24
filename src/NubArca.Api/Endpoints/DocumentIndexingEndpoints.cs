using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NubArca.Api.Access;
using NubArca.Api.Ai;
using NubArca.Api.Ai.Documents;
using NubArca.Api.Ai.Documents.Indexing;
using NubArca.Api.Http;
using NubArca.Api.Rag;

namespace NubArca.Api.Endpoints;

/// "What can search over MY documents do, and how far along is its indexing?"
/// Counts and reason tokens only. Requires `document-search.access`, like the
/// search it reports on.
///
/// The owner comes from the authenticated identity; there is no parameter that
/// could name anybody else. The responses are counts, profile keys and reason
/// tokens — never a document name, an id, a path or profile internals.
public static class DocumentIndexingEndpoints
{
    public static IEndpointRouteBuilder MapDocumentIndexingEndpoints(this IEndpointRouteBuilder app)
    {
        // Safe status only, for the Documents page. Whether the assistant
        // exists, whether its answer stays on this machine, whether anything is
        // indexed, and how much. Never the endpoint URL, the model id, the API
        // key, a model path, a container hostname, a filename or a storage key.
        // It lived under /api/assistant/ until the Help assistant was removed;
        // the document search it describes needs no assistant.
        app.MapGet("/api/documents/status", async (
            HttpContext httpContext,
            [FromServices] PrivateDocumentAssistantService assistant,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var status = await assistant.GetStatusAsync(ownerUserId, cancellationToken);

            // The embedding profile's own readiness (configured, present, model
            // files on disk). Absent on a host with no database.
            var embeddings = httpContext.RequestServices
                .GetService<NubArca.Api.Ai.TextEmbeddings.TextEmbeddingResolver>();
            var embeddingReason = embeddings is null
                ? RagFailureReasons.EmbeddingDisabled
                : (await embeddings.ResolveAsync(RagDomainKey.UserDocuments, cancellationToken)).Reason;
            var registered = httpContext.RequestServices
                .GetService<NubArca.Api.Ai.Documents.Sources.OwnerDocumentSources>();
            var kinds = OwnerDocumentAvailability.Build(
                status, embeddingReason, registered?.Eligibility.Select(k => k.Kind).ToList());

            return Results.Ok(new
            {
                enabled = status.Enabled,
                modelBoundary = status.ModelBoundary,
                knowledgeAvailable = status.KnowledgeAvailable,
                semanticEnabled = status.SemanticEnabled,
                // The profile KEY is an operator-chosen label like
                // `rag-text-multilingual-e5-small-v1`. It names a model, not a
                // path to one, and a person is entitled to know which local
                // model reads their documents.
                embeddingProfile = status.EmbeddingProfileKey,
                documents = status.Documents,
                chunks = status.Chunks,
                reason = status.Reason,
                // Whether the visual pass can run, and if not the sanitized
                // reason token. Separate from `semanticEnabled` because the two
                // degrade independently.
                visualEnabled = status.VisualEnabled,
                visualReason = status.VisualReason,
                // How much of the library is searchable yet. COUNTS ONLY —
                // never a name, never which files are missing.
                indexing = status.Indexing is { } indexing
                    ? new
                    {
                        candidateFiles = indexing.CandidateFiles,
                        indexedFiles = indexing.IndexedFiles,
                        notIndexedFiles = indexing.NotIndexedFiles,
                        chunks = indexing.Chunks,
                        embeddedChunks = indexing.EmbeddedChunks,
                    }
                    : null,
                // Per domain and per source kind: which signals can run, and
                // the reason token when one cannot.
                availability = new
                {
                    domain = RagDomainKey.UserDocuments.Value,
                    kinds = kinds.Select(k => new
                    {
                        kind = k.Kind,
                        lexical = new { available = k.Lexical.Available, reason = k.Lexical.Reason },
                        semantic = new { available = k.Semantic.Available, reason = k.Semantic.Reason },
                        visual = new { available = k.Visual.Available, reason = k.Visual.Reason },
                    }),
                },
            });
        }).WithName("DocumentsStatus").RequirePermission(Permissions.DocumentSearchAccess);

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
        }).WithName("DocumentIndexingStatus").RequirePermission(Permissions.DocumentSearchAccess);

        return app;
    }
}
