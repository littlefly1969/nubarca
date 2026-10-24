using Microsoft.AspNetCore.Mvc;
using NubArca.Api.Access;
using NubArca.Api.Ai.Documents;
using NubArca.Api.Http;
using NubArca.Api.Rag;

namespace NubArca.Api.Endpoints;

/// "Ask about my own documents." Requires `document-search.access`; owner-derived,
/// LocalTrusted-only.
///
/// THE REQUEST DTO IS THE CONTRACT, and it is the shortest one in the product: a
/// message and a bounded history. There is no `ownerUserId`, no `domain`, no
/// `fileId`, no `documentTextId`, no `chunkId`, no `blobId`, no `storageKey`,
/// no `path`, no `model`, no `profile`, no `trust` and no `boundary` field.
///
/// A client cannot point this at another person's documents, at another
/// domain, or at a different model, because the shape it posts into has
/// nowhere to put any of that. That is a stronger statement than a server which
/// accepts such fields and promises to ignore them: there is nothing to ignore.
///
/// The owner comes from the authenticated identity on the request, read
/// server-side on every call. The domain is a constant inside the service. Which
/// model answers is operator configuration, and it must be LocalTrusted or the
/// feature is off.
public static class PrivateDocumentAssistantEndpoints
{
    /// Its own per-user rate-limit policy: private answers run local inference
    /// on the operator's own hardware.
    public const string PrivateChatRateLimitPolicy = "private-document-chat";

    public sealed record PrivateChatTurnDto(bool FromUser, string Text);

    public sealed record PrivateChatRequestDto(
        string Message, IReadOnlyList<PrivateChatTurnDto>? History);

    public static IEndpointRouteBuilder MapPrivateDocumentAssistantEndpoints(
        this IEndpointRouteBuilder app)
    {
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
                // 200 with ok=false. An unconfigured or unavailable
                // local model is a state of an optional feature, not a NubArca
                // error, and it must not look to a browser — or to a probe —
                // like the application being unwell.
                return Results.Ok(new { ok = false, reason = answer.Reason });
            }

            return Results.Ok(new
            {
                ok = true,
                text = answer.Text,
                // Name, section and WHERE — never an id. See
                // docs/rag-platform.md "What a citation may say".
                sources = answer.Sources.Select(s => new
                {
                    document = s.Document,
                    // `file`, or a kind registered later: a category for the
                    // label, never an id.
                    sourceKind = s.SourceKind,
                    section = s.Section,
                    page = s.Page,
                    locator = LocatorDto(s.LocatorKind, s.LocatorIndex, s.LocatorLabel),
                }),
            });
        }).WithName("PrivateDocumentAssistantChat")
          .RequirePermission(Permissions.DocumentSearchAccess)
          .RequireRateLimiting(PrivateChatRateLimitPolicy);

        return app;
    }

    /// The typed locator as it goes on the wire, or null for a chunk with no
    /// position. Shared by the chat citations and the document search.
    public sealed record LocatorWireDto(string Kind, int Index, string? Label);

    public static LocatorWireDto? LocatorDto(string? kind, int? index, string? label)
        => string.IsNullOrWhiteSpace(kind) || index is null
           || kind == Ai.Documents.DocumentLocatorKinds.Text
            ? null
            : new LocatorWireDto(kind, index.Value, string.IsNullOrWhiteSpace(label) ? null : label);
}
