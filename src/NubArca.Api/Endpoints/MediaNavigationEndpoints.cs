using Microsoft.AspNetCore.Mvc;
using NubArca.Api.Http;
using NubArca.Api.Media;

namespace NubArca.Api.Endpoints;

public static class MediaNavigationEndpoints
{
    public static void MapMediaNavigationEndpoints(this IEndpointRouteBuilder app)
    {
        foreach (var prefix in new[] { "/api/media", "/api/albums/{albumId:guid}/media" })
        {
            app.MapGet(prefix + "/navigation", async ([AsParameters] NavigationRequest request,
                HttpContext context, [FromServices] IMediaCollectionQueryService media, CancellationToken cancellationToken) =>
            {
                SetNoStore(context);
                if (!request.TryBind(context, out var query, out var error)) return Results.BadRequest(new { error });
                return Map(await media.NavigationAsync(context.GetCurrentUserId()!.Value, query, cancellationToken));
            }).RequireAuthorization();
            app.MapGet(prefix + "/window", async ([AsParameters] NavigationRequest request,
                HttpContext context, [FromServices] IMediaCollectionQueryService media, CancellationToken cancellationToken) =>
            {
                SetNoStore(context);
                if (!request.TryBind(context, out var query, out var error)) return Results.BadRequest(new { error });
                return Map(await media.WindowAsync(context.GetCurrentUserId()!.Value, query,
                    request.Target, request.Before ?? false, cancellationToken));
            }).RequireAuthorization();
        }
    }

    private static IResult Map(MediaNavigationResult result) => result.Status switch
    {
        MediaCollectionStatus.Ok => Results.Ok(result.Data),
        MediaCollectionStatus.AlbumNotFound => Results.NotFound(),
        _ => Results.BadRequest(new { error = result.Error }),
    };

    private static void SetNoStore(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
    }

    public sealed class NavigationRequest
    {
        [FromQuery] public int? Limit { get; set; }
        [FromQuery] public string? Cursor { get; set; }
        [FromQuery] public string? Scope { get; set; }
        [FromQuery] public string? Kind { get; set; }
        [FromQuery] public string? Q { get; set; }
        [FromQuery] public bool? Favorite { get; set; }
        [FromQuery] public int? MinRating { get; set; }
        [FromQuery] public DateTime? DateTakenFrom { get; set; }
        [FromQuery] public DateTime? DateTakenTo { get; set; }
        [FromQuery] public string? AlbumMembership { get; set; }
        [FromQuery] public string? Sort { get; set; }
        [FromQuery] public string? Direction { get; set; }
        [FromQuery] public bool? HasGps { get; set; }
        [FromQuery] public bool? CollapseDuplicates { get; set; }
        [FromQuery] public Guid? SimilarTo { get; set; }
        [FromQuery] public string? IncludePeople { get; set; }
        [FromQuery] public string? ExcludePeople { get; set; }
        [FromQuery] public string? IncludePeopleMode { get; set; }
        [FromQuery] public double? DurationMin { get; set; }
        [FromQuery] public double? DurationMax { get; set; }
        [FromQuery] public int? MinHeight { get; set; }
        [FromQuery] public string? Codec { get; set; }
        [FromQuery] public bool? HasAudio { get; set; }
        [FromQuery] public string? Target { get; set; }
        [FromQuery] public bool? Before { get; set; }

        public bool TryBind(HttpContext context, out MediaCollectionQuery query, out string? error)
        {
            var source = context.Request.RouteValues.TryGetValue("albumId", out var value)
                ? (MediaCollectionSource)new MediaCollectionSource.Album(Guid.Parse(value!.ToString()!))
                : new MediaCollectionSource.Library();
            return MediaCollectionQueryBinder.TryBind(source, Limit, Cursor, Scope, Kind, Q,
                Favorite, MinRating, DateTakenFrom, DateTakenTo, AlbumMembership, Sort, Direction,
                HasGps, CollapseDuplicates, SimilarTo, IncludePeople, ExcludePeople, IncludePeopleMode,
                DurationMin, DurationMax, MinHeight, Codec, HasAudio, out query, out error);
        }
    }
}
