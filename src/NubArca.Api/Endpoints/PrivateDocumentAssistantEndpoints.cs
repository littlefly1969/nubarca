using Microsoft.AspNetCore.Mvc;
using NubArca.Api.Ai.Documents;
using NubArca.Api.Http;

namespace NubArca.Api.Endpoints;

/// "Ask about my own documents." Authenticated, owner-derived, LocalTrusted-only.
///
/// THE REQUEST DTO IS THE CONTRACT, and it is the shortest one in the product: a
/// message and a bounded history. There is no `ownerUserId`, no `domain`, no
/// `fileId`, no `documentTextId`, no `chunkId`, no `blobId`, no `storageKey`,
/// no `path`, no `model`, no `profile`, no `trust` and no `boundary` field.
///
/// A client cannot point this at another person's documents, at Product Help, at
/// the repository, or at a different model, because the shape it posts into has
/// nowhere to put any of that. That is a stronger statement than a server which
/// accepts such fields and promises to ignore them: there is nothing to ignore.
///
/// The owner comes from the authenticated identity on the request, read
/// server-side on every call. The domain is a constant inside the service. Which
/// model answers is operator configuration, and it must be LocalTrusted or the
/// feature is off.
public static class PrivateDocumentAssistantEndpoints
{
    /// A separate rate-limit policy from Help's. Private answers run local
    /// inference on the operator's own hardware, which is a different cost with
    /// a different shape, and sharing a bucket would let one feature starve the
    /// other.
    public const string PrivateChatRateLimitPolicy = "private-document-chat";

    public sealed record PrivateChatTurnDto(bool FromUser, string Text);

    public sealed record PrivateChatRequestDto(
        string Message, IReadOnlyList<PrivateChatTurnDto>? History);

    public static IEndpointRouteBuilder MapPrivateDocumentAssistantEndpoints(
        this IEndpointRouteBuilder app)
    {
        // Safe status only. Whether the feature exists, whether the answer stays
        // on this machine, whether anything is indexed, and how much. Never the
        // endpoint URL, the model id, the API key, a model path, a container
        // hostname, a filename or a storage key.
        app.MapGet("/api/assistant/documents/status", async (
            HttpContext httpContext,
            [FromServices] PrivateDocumentAssistantService assistant,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var status = await assistant.GetStatusAsync(ownerUserId, cancellationToken);
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
            });
        }).WithName("PrivateDocumentAssistantStatus").RequireAuthorization();

        app.MapPost("/api/assistant/documents/chat", async (
            HttpContext httpContext,
            [FromBody] PrivateChatRequestDto request,
            [FromServices] PrivateDocumentAssistantService assistant,
            CancellationToken cancellationToken) =>
        {
            if (request is null || string.IsNullOrWhiteSpace(request.Message))
            {
                return Results.BadRequest(new { error = "A question is required." });
            }

            // THE OWNER, derived here and nowhere else. The body was never
            // consulted for it and has no field that could be.
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;

            var history = (request.History ?? Array.Empty<PrivateChatTurnDto>())
                .Select(t => new PrivateDocumentTurn(t.FromUser, t.Text ?? string.Empty))
                .ToList();

            var answer = await assistant.AskAsync(
                ownerUserId, request.Message, history, cancellationToken);

            if (!answer.Ok)
            {
                // 200 with ok=false, like Help. An unconfigured or unavailable
                // local model is a state of an optional feature, not a NubArca
                // error, and it must not look to a browser — or to a probe —
                // like the application being unwell.
                return Results.Ok(new { ok = false, reason = answer.Reason });
            }

            return Results.Ok(new
            {
                ok = true,
                text = answer.Text,
                sources = answer.Sources.Select(s => new { document = s.Document, section = s.Section }),
            });
        }).WithName("PrivateDocumentAssistantChat")
          .RequireAuthorization()
          .RequireRateLimiting(PrivateChatRateLimitPolicy);

        return app;
    }
}
