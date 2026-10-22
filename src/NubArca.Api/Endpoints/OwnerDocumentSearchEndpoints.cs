using Microsoft.AspNetCore.Mvc;
using NubArca.Api.Access;
using NubArca.Api.Ai.Documents;
using NubArca.Api.Http;
using NubArca.Api.Rag;

namespace NubArca.Api.Endpoints;

/// "Find it in my documents." Requires `document-search.access`; owner-derived,
/// no model.
///
/// THE REQUEST DTO IS THE CONTRACT: a query and a result count. No owner, no
/// domain, no file id, no folder, no model, no mode. A client cannot widen the
/// search to somebody else's library or narrow it onto a file it names, because
/// the shape it posts into has nowhere to put either.
///
/// THE RESPONSE CARRIES ONE KIND OF ID, deliberately: the caller's OWN source
/// id (`sourceKind: "file"`, `sourceId` = their FileItemId) for each document
/// found, so the page can open the file. That is
/// a reviewed exception to "no id on the wire" — see docs/rag-platform.md
/// "Search returns the owner's own file id". Never a chunk id, a DocumentText
/// id, a blob id, a hash, a storage key, an owner id or a score.
public static class OwnerDocumentSearchEndpoints
{
    /// Its own bucket: a search embeds the query (when semantic is on) and
    /// scans one person's corpus, which is cheaper than a chat answer but not
    /// free, and a busy chat user must not starve somebody's search.
    public const string SearchRateLimitPolicy = "private-document-search";

    public sealed record DocumentSearchRequestDto(string Query, int? Limit);

    public static IEndpointRouteBuilder MapOwnerDocumentSearchEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/documents/search", async (
            HttpContext httpContext,
            [FromBody] DocumentSearchRequestDto request,
            CancellationToken cancellationToken) =>
        {
            if (request is null || string.IsNullOrWhiteSpace(request.Query))
            {
                return Results.BadRequest(new { error = "A query is required." });
            }

            // THE OWNER, from the authenticated identity and nowhere else.
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;

            // Absent on a host with no database, which is a supported
            // configuration with no private documents in it.
            var search = httpContext.RequestServices.GetService<OwnerDocumentSearchService>();
            var result = search is null
                ? OwnerDocumentSearchResult.Empty(
                    OwnerDocumentSearchStatuses.NotIndexed, null,
                    RagRetrievalModes.Lexical, Ai.DocumentVisual.DocumentVisualModes.Unavailable)
                // No SourceKinds, no AllowedSourceIds: the Documenti page
                // searches the caller's whole eligible library.
                : await search.SearchAsync(
                    ownerUserId, new OwnerDocumentSearchRequest(request.Query, request.Limit),
                    cancellationToken);

            return Results.Ok(ToWire(result));
        }).WithName("OwnerDocumentSearch")
          .RequirePermission(Permissions.DocumentSearchAccess)
          .RequireRateLimiting(SearchRateLimitPolicy);

        return app;
    }

    /// The wire shape. Built field by field so nothing internal can ride along
    /// by being added to a record later.
    public static object ToWire(OwnerDocumentSearchResult result)
    {
        var semantic = result.TextMode == RagRetrievalModes.Hybrid;
        const string fallbackPrefix = "lexical-fallback-";
        var fallback = result.TextMode.StartsWith(fallbackPrefix, StringComparison.Ordinal)
            ? result.TextMode[fallbackPrefix.Length..]
            : null;

        return new
        {
            status = result.Status,
            reason = result.Reason,
            // `lexical`, `hybrid` or `lexical-fallback-<reason>`: the page says
            // which, discreetly, so "semantic off" never reads as "broken".
            mode = result.TextMode,
            semantic,
            fallbackReason = fallback,
            visualMode = result.VisualMode,
            visualUsed = result.VisualUsed,
            documents = result.Documents.Select(d => new
            {
                // The caller's OWN source id — for `file`, their FileItemId.
                // The reviewed exception, and the only identifier here.
                sourceKind = d.SourceKind,
                sourceId = d.SourceId,
                name = d.Name,
                rank = d.Rank,
                passages = d.Passages.Select(p => new
                {
                    rank = p.Rank,
                    snippet = p.Snippet,
                    section = p.Section,
                    page = p.Page,
                    ordinal = p.Ordinal,
                    locator = PrivateDocumentAssistantEndpoints.LocatorDto(
                        p.LocatorKind, p.LocatorIndex, p.LocatorLabel),
                }),
            }),
            // Pages that LOOK like the question, with no text behind them:
            // where to look, never evidence. Same reviewed exception — the
            // caller's own file id — and nothing else that addresses anything.
            visualMatches = result.VisualOnly.Select(v => new
            {
                sourceKind = v.SourceKind,
                sourceId = v.SourceId,
                name = v.Name,
                rank = v.Rank,
                page = v.Page,
                locator = PrivateDocumentAssistantEndpoints.LocatorDto(
                    v.LocatorKind, v.LocatorIndex, v.LocatorLabel),
            }),
        };
    }
}
