using Microsoft.AspNetCore.Mvc;
using NubArca.Api.Help;

namespace NubArca.Api.Endpoints;

// Optional external Smart Help. Authenticated; nothing here reads the caller's
// library, because ExternalHelpService has no way to.
//
// THE REQUEST DTO IS THE CONTRACT. It carries a question and a short
// conversation, and it has no fileId, folderId, albumId, personId, faceId,
// searchId, metadata, currentMedia, context or url field. A client cannot
// attach a private object reference to a Help question because the shape it
// posts into has nowhere to put one — which is a stronger statement than a
// server that receives such a field and promises to ignore it.
public static class HelpEndpoints
{
    /// Same convention as the other endpoint modules: the policy is registered
    /// in Program.cs and named here, so the two cannot drift silently.
    public const string ExternalHelpRateLimitPolicy = "external-help-chat";

    /// One turn the browser is replaying back to us. `fromUser` rather than a
    /// free-text role: a client cannot inject a "system" turn and rewrite the
    /// instructions the provider is given.
    public sealed record HelpChatTurnDto(bool FromUser, string Text);

    public sealed record HelpChatRequestDto(string Message, IReadOnlyList<HelpChatTurnDto>? History);

    public static IEndpointRouteBuilder MapHelpEndpoints(this IEndpointRouteBuilder app)
    {
        // Safe product metadata only. Never the base URL, never the model, never
        // a header, and never the key: an operator can read the configuration,
        // and a user only needs to know whether the feature exists and which
        // provider is involved.
        app.MapGet("/api/help/ai/status", (
            [FromServices] ExternalHelpService help) =>
        {
            var status = help.GetStatus();
            return Results.Ok(new
            {
                enabled = status.Enabled,
                providerLabel = status.ProviderLabel,
                knowledgeAvailable = status.KnowledgeAvailable,
            });
        }).WithName("ExternalHelpStatus").RequireAuthorization();

        app.MapPost("/api/help/ai/chat", async (
            [FromBody] HelpChatRequestDto request,
            [FromServices] ExternalHelpService help,
            CancellationToken cancellationToken) =>
        {
            if (request is null || string.IsNullOrWhiteSpace(request.Message))
            {
                return Results.BadRequest(new { error = "A help question is required." });
            }

            var history = (request.History ?? Array.Empty<HelpChatTurnDto>())
                .Select(t => new HelpTurn(t.FromUser, t.Text ?? string.Empty))
                .ToList();

            var answer = await help.AskAsync(request.Message, history, cancellationToken);
            if (!answer.Ok)
            {
                // 200 with ok=false, not 5xx: an unavailable external provider is
                // a state of an optional feature, not a NubArca error. A failing
                // Help must never look to a browser — or to a probe — like the
                // application being unwell.
                return Results.Ok(new { ok = false, reason = answer.Reason });
            }
            return Results.Ok(new { ok = true, text = answer.Text, sources = answer.Sources });
        }).WithName("ExternalHelpChat")
          .RequireAuthorization()
          .RequireRateLimiting(ExternalHelpRateLimitPolicy);

        return app;
    }
}
