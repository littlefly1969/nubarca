namespace NubArca.Api.TvUpdates;

public static class TvUpdateEndpoints
{
    public static IEndpointRouteBuilder MapTvUpdateEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/tv-app/updates", (HttpContext context, TvUpdateStore store) =>
        {
            SetProtocolHeaders(context.Response);
            var protocol = context.Request.Headers["Expo-Protocol-Version"].ToString();
            var platform = context.Request.Headers["Expo-Platform"].ToString().ToLowerInvariant();
            var runtime = context.Request.Headers["Expo-Runtime-Version"].ToString();
            var channel = context.Request.Headers["expo-channel-name"].ToString();
            if (protocol != "1") return Results.StatusCode(StatusCodes.Status406NotAcceptable);
            if (platform != "android" || !TvUpdateStore.IsSafe(runtime)) return Results.BadRequest();
            if (string.IsNullOrEmpty(channel)) channel = "production";
            if (!TvUpdateStore.IsSafe(channel)) return Results.BadRequest();
            var publication = store.FindManifest(platform, runtime, channel);
            if (publication is null) return Results.NoContent();
            context.Response.Headers["Expo-Signature"] = publication.Signature;
            return Results.Text(publication.Body, "application/expo+json", System.Text.Encoding.UTF8);
        }).WithName("GetNativeTvUpdate").AllowAnonymous();

        endpoints.MapGet("/api/tv-app/updates/assets/{runtime}/{updateId}/{**assetPath}",
            (string runtime, string updateId, string assetPath, HttpContext context, TvUpdateStore store) =>
            {
                var asset = store.FindAsset(runtime, updateId, assetPath);
                if (asset is null) return Results.NotFound();
                context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
                return Results.File(asset.Path, asset.ContentType, enableRangeProcessing: true);
            }).WithName("GetNativeTvUpdateAsset").AllowAnonymous();
        return endpoints;
    }

    private static void SetProtocolHeaders(HttpResponse response)
    {
        response.Headers["Expo-Protocol-Version"] = "1";
        response.Headers["Expo-SFV-Version"] = "0";
        response.Headers["Expo-Manifest-Filters"] = "";
        response.Headers["Expo-Server-Defined-Headers"] = "";
        response.Headers.CacheControl = "private, max-age=0, no-cache";
        response.Headers.Pragma = "no-cache";
    }
}
