using Microsoft.AspNetCore.Mvc;
using NubArca.Api.Ai.Photos;
using NubArca.Api.Albums;
using NubArca.Api.Files;
using NubArca.Api.Folders;
using NubArca.Api.Http;

namespace NubArca.Api.Endpoints;

// Extracted verbatim from Program.cs (modular-monolith cleanup, not a service
// split — same process, same DI container, same middleware pipeline). Route
// paths, HTTP methods, endpoint names, authorization metadata, status codes,
// DTOs, and delivery behavior are unchanged from the original inline
// mappings.
//
// Unified gallery/media query surface — owner-scoped, Private-Vault-excluded,
// deleted-content-excluded. `/api/images` and `/api/videos` are the legacy
// per-kind galleries (kept for compatibility); `/api/media` and
// `/api/albums/{albumId}/media` are the newer unified library/album
// workspace built on the same MediaCollectionQueryBinder/
// IMediaCollectionQueryService contract. Media DELIVERY (thumbnail, preview,
// original content, video streaming) is a separate File/Folder bounded
// context served through `/api/files/{id}/...` in Program.cs — not part of
// this module.
public static class GalleryMediaEndpoints
{
    private const string SemanticSearchRateLimitPolicy = "semantic-search";
    private const string TvPersonalInterpretRateLimitPolicy = "tv-personal-interpret";

    public static IEndpointRouteBuilder MapGalleryMediaEndpoints(this IEndpointRouteBuilder app)
    {
        // Relevance-ranked text-to-image retrieval. This is deliberately a separate
        // route from /api/images and from similarTo: one request is either semantic
        // text search or image similarity, never a fused query.
        app.MapGet("/api/images/semantic", async (
            [FromQuery] string? q,
            [FromQuery] int? limit,
            [FromQuery] string? cursor,
            HttpContext httpContext,
            [FromServices] PhotoSemanticSearchService semantic,
            [FromServices] IFileItemService files,
            CancellationToken cancellationToken) =>
        {
            var query = q?.Trim() ?? string.Empty;
            if (query.Length == 0 || query.Length > PhotoSemanticSearchService.MaxQueryLength)
            {
                return Results.BadRequest(new { error = $"'q' must contain 1-{PhotoSemanticSearchService.MaxQueryLength} characters." });
            }

            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var pageSize = Math.Clamp(limit ?? PhotoSemanticSearchService.DefaultPageSize, 1, PhotoSemanticSearchService.MaxPageSize);
            SemanticPhotosPage page;
            try
            {
                page = await semantic.SearchAsync(ownerUserId, query, pageSize, cursor, cancellationToken);
            }
            catch (SemanticSearchCursorException)
            {
                return Results.BadRequest(new { error = "Invalid or mismatched semantic-search cursor." });
            }
            if (!page.ProfileAvailable || !page.TextModelAvailable)
            {
                return Results.Json(
                    new { error = "semantic_search_unavailable", reason = page.UnavailableReason },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var items = await files.ListGalleryImagesByRankAsync(
                ownerUserId, page.Items.Select(x => x.FileItemId).ToList(), cancellationToken);
            return Results.Ok(new ImageListResponse(
                items, pageSize, 0, items.Count, page.NextCursor, page.HasMore));
        }).WithName("SearchImagesSemantically")
            .RequireAuthorization()
            .RequireRateLimiting(SemanticSearchRateLimitPolicy);

        app.MapGet("/api/images", async (
            [FromQuery] int? limit,
            [FromQuery] int? offset,
            [FromQuery] string? cursor,
            [FromQuery] Guid? folderId,
            [FromQuery] string? q,
            [FromQuery] string? sort,
            [FromQuery] string? direction,
            [FromQuery] bool? favorite,
            [FromQuery] int? minRating,
            [FromQuery] bool? hasGps,
            [FromQuery] DateTime? dateTakenFrom,
            [FromQuery] DateTime? dateTakenTo,
            [FromQuery] bool? collapseDuplicates,
            [FromQuery] Guid? albumId,
            [FromQuery] string? albumMembership,
            [FromQuery] Guid? similarTo,
            [FromQuery] string? includePeople,
            [FromQuery] string? excludePeople,
            [FromQuery] string? includePeopleMode,
            [FromQuery] string? semanticQuery,
            [FromQuery] int? semanticTopK,
            [FromQuery] string? mediaScope,
            HttpContext httpContext,
            [FromServices] IFileItemService files,
            [FromServices] IFolderService folders,
            [FromServices] IAlbumService albums,
            [FromServices] NubArca.Api.Ai.Photos.PhotoSimilarityService similarity,
            [FromServices] NubArca.Api.Ai.Photos.GallerySemanticQueryService semantic,
            CancellationToken cancellationToken) =>
        {
            // Bounded candidate set for the "similar photos in the gallery" bridge.
            const int SimilarRestrictCap = 500;

            var ownerUserId = httpContext.GetCurrentUserId()!.Value;

            // Common gallery-query surface (limit / q / sort / filters / people) is
            // parsed by the shared GalleryQueryParser — the same semantics the TV
            // personal-gallery projection uses, so the two surfaces cannot drift.
            if (!GalleryQueryParser.TryParseCommon(
                limit, q, sort, direction, favorite, minRating, hasGps,
                dateTakenFrom, dateTakenTo, collapseDuplicates,
                includePeople, excludePeople, includePeopleMode,
                out var common, out var commonError))
            {
                return Results.BadRequest(new { error = commonError });
            }
            var effectiveLimit = common.Limit;
            var sortField = common.Sort;
            var sortDirection = common.Direction;

            var effectiveOffset = offset ?? 0;
            if (effectiveOffset < 0) effectiveOffset = 0;

            if (!GalleryQueryParser.TryParseAlbumMembership(
                albumMembership, out var membership, out var membershipError))
            {
                return Results.BadRequest(new { error = membershipError });
            }

            // Slice 3: Active (normal gallery) vs Excluded ("Esclusi" tab).
            if (!GalleryQueryParser.TryParseMediaScope(mediaScope, out var scope, out var scopeError))
            {
                return Results.BadRequest(new { error = scopeError });
            }

            // `albumId` ("in THIS album") and `albumMembership=unassigned` ("in no
            // album") are mutually exclusive by construction. Answering with an empty
            // gallery would look like a data problem, so say what is wrong instead.
            if (albumId is not null && membership == AlbumMembershipFilter.Unassigned)
            {
                return Results.BadRequest(new
                {
                    error = "'albumId' cannot be combined with 'albumMembership=unassigned'.",
                });
            }

            // Album constraint: owner-validate up front so a foreign/missing album is a
            // clean 404 (no existence leak) rather than a silently-empty gallery.
            if (albumId is Guid constrainAlbumId)
            {
                var album = await albums.GetByIdAsync(constrainAlbumId, ownerUserId, cancellationToken);
                if (album is null)
                {
                    return Results.NotFound();
                }
            }

            // Similar-photo bridge: resolve the query file's owner-scoped similar set
            // into a bounded restrict list. A foreign/unknown query file (service
            // returns null) → restrict to nothing (empty gallery), never a leak.
            IReadOnlyList<Guid>? restrictToFileIds = null;
            if (similarTo is Guid similarToId)
            {
                var similarResult = await similarity.FindSimilarAsync(
                    ownerUserId, similarToId, SimilarRestrictCap, profileKeyOverride: null,
                    cancellationToken: cancellationToken);
                restrictToFileIds = similarResult is null
                    ? Array.Empty<Guid>()
                    : similarResult.Items.Select(i => i.FileItemId).Distinct().ToList();
            }

            // Endpoint-specific filter dimensions composed on top of the common set.
            var filters = common.Filters with
            {
                FolderId = folderId,
                AlbumId = albumId,
                AlbumMembership = membership,
                SimilarToFileId = similarTo,
                RestrictToFileIds = restrictToFileIds,
                Scope = scope,
            };

            // Slice 100: physical-filter-first + semantic Top-K ranking (web NL search).
            // When a visual semantic residual is present, the physical filters build the
            // owner-scoped candidate set FIRST and the active text tower ranks INSIDE it
            // — the SAME GallerySemanticQueryService the TV surface uses, unchanged. The
            // service owns its own (score, id) cursor + fingerprint, so we do NOT bind
            // the metadata-sort cursor here.
            if (!string.IsNullOrWhiteSpace(semanticQuery))
            {
                var semanticFilters = filters with { SemanticQuery = semanticQuery, SemanticTopK = semanticTopK };
                NubArca.Api.Ai.Photos.GallerySemanticPage semanticPage;
                try
                {
                    semanticPage = await semantic.SearchAsync(
                        ownerUserId, effectiveLimit, cursor, semanticFilters, cancellationToken);
                }
                catch (NubArca.Api.Ai.Photos.SemanticSearchCursorException)
                {
                    return Results.BadRequest(
                        new { error = "'cursor' was issued for a different filter / query set." });
                }
                var semanticStatus = !semanticPage.Available ? "unavailable"
                    : semanticPage.StillIndexingManyItems ? "indexing" : "ok";
                return Results.Ok(new ImageListResponse(
                    semanticPage.Items, effectiveLimit, 0, semanticPage.Items.Count,
                    semanticPage.NextCursor, semanticPage.HasMore)
                {
                    SemanticActive = true,
                    SemanticTopK = semanticPage.SemanticTopK,
                    SemanticStatus = semanticStatus,
                    // Reduced semantic result-set size (≤ Top-K): the correct denominator.
                    Total = semanticPage.TotalCount,
                });
            }

            // Cursor + offset cannot be mixed. A non-zero offset alongside a cursor
            // is ambiguous (does the cursor anchor or the offset?), so we reject
            // loudly rather than guess.
            var cursorProvided = !string.IsNullOrWhiteSpace(cursor);
            var explicitOffset = offset is int o && o > 0;
            if (cursorProvided && explicitOffset)
            {
                return Results.BadRequest(new { error = "'cursor' and 'offset' cannot be used together." });
            }

            if (!GalleryQueryParser.TryBindCursor(
                cursor, filters, sortField, sortDirection, out var parsedCursor, out var cursorError))
            {
                return Results.BadRequest(new { error = cursorError });
            }

            if (folderId is Guid parentId)
            {
                // Owner-scoped + soft-delete-aware: returns null for missing / foreign
                // / soft-deleted, all of which collapse to 404 (no-leak).
                var parent = await folders.GetByIdAsync(parentId, ownerUserId, cancellationToken);
                if (parent is null)
                {
                    return Results.NotFound();
                }
            }

            // Cursor mode: prefer the seek-paginated query (Slice 60) which avoids
            // OFFSET scans on large libraries. The legacy offset path is preserved
            // for explicit-offset requests so older clients continue working byte-
            // for-byte.
            if (cursorProvided || !explicitOffset)
            {
                var page = await files.ListImagesPageAsync(
                    ownerUserId, effectiveLimit, parsedCursor, filters,
                    sortField, sortDirection, cancellationToken);
                return Results.Ok(new ImageListResponse(
                    page.Items, effectiveLimit, 0, page.Items.Count, page.NextCursor, page.HasMore)
                {
                    // Server-authoritative filtered total (paging-independent), surfaced
                    // so the workspace never counts loaded pages. Engine already computes it.
                    Total = page.TotalCount,
                });
            }

            // Legacy offset path (slice 60). Slice 61 filters apply only to the
            // cursor path; the offset path only respects q + folder (existing
            // contract). New filters are accepted but ignored here — the response
            // shape is unchanged for legacy callers.
            var items = await files.ListImagesAsync(
                ownerUserId, folderId, effectiveLimit, effectiveOffset, common.Filters.Query,
                sortField, sortDirection, cancellationToken);
            return Results.Ok(new ImageListResponse(
                items, effectiveLimit, effectiveOffset, items.Count, NextCursor: null, HasMore: false));
        }).WithName("ListImages").RequireAuthorization();

        // Slice 100: LOCAL natural-language command interpretation for the AUTHENTICATED
        // owner web gallery. Reuses the EXACT same application service (interpreter +
        // validator + owner-scoped person resolver + date resolver) as the TV endpoint —
        // no interpretation logic is duplicated and the TV endpoint is not called over
        // HTTP. Owner is the authenticated principal; no TV session / unlock grant. The
        // draft is only PROPOSED (never applied here); command text is never persisted
        // or logged. no-store; owner rate limit (shared interpretation policy).
        app.MapPost("/api/images/interpret-command", async (
            [FromBody] NubArca.Api.Ai.NaturalGallery.InterpretCommandRequest request,
            HttpContext httpContext,
            [FromServices] NubArca.Api.Ai.NaturalGallery.NaturalGalleryCommandService interpreter,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var outcome = await interpreter.InterpretAsync(ownerUserId, request ?? new(), cancellationToken);
            return outcome.Kind switch
            {
                NubArca.Api.Ai.NaturalGallery.InterpretOutcomeKind.Ok => Results.Ok(outcome.Response),
                NubArca.Api.Ai.NaturalGallery.InterpretOutcomeKind.Unsupported =>
                    Results.Json(new { error = "unsupported_command" }, statusCode: StatusCodes.Status422UnprocessableEntity),
                NubArca.Api.Ai.NaturalGallery.InterpretOutcomeKind.ModelBusy =>
                    Results.Json(new { error = "model_busy" }, statusCode: StatusCodes.Status429TooManyRequests),
                NubArca.Api.Ai.NaturalGallery.InterpretOutcomeKind.Timeout =>
                    Results.Json(new { error = "model_timeout" }, statusCode: StatusCodes.Status504GatewayTimeout),
                NubArca.Api.Ai.NaturalGallery.InterpretOutcomeKind.ModelUnavailable =>
                    Results.Json(new { error = "model_unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable),
                _ => // Malformed
                    Results.Json(new { error = "interpretation_failed" }, statusCode: StatusCodes.Status422UnprocessableEntity),
            };
        }).WithName("InterpretGalleryCommand")
          .RequireAuthorization()
          .RequireRateLimiting(TvPersonalInterpretRateLimitPolicy);

        // Slice 86: video gallery — cursor-only (no legacy offset path). Mirrors the
        // /api/images validation; returns safe VideoItem DTOs with a poster URL.
        app.MapGet("/api/videos", async (
            [FromQuery] int? limit,
            [FromQuery] string? cursor,
            [FromQuery] Guid? folderId,
            [FromQuery] string? q,
            [FromQuery] string? sort,
            [FromQuery] string? direction,
            [FromQuery] bool? favorite,
            [FromQuery] int? minRating,
            [FromQuery] DateTime? dateTakenFrom,
            [FromQuery] DateTime? dateTakenTo,
            [FromQuery] bool? collapseDuplicates,
            [FromQuery] double? durationMin,
            [FromQuery] double? durationMax,
            [FromQuery] int? minWidth,
            [FromQuery] int? minHeight,
            [FromQuery] string? codec,
            [FromQuery] bool? hasAudio,
            [FromQuery] string? albumMembership,
            [FromQuery] string? mediaScope,
            HttpContext httpContext,
            [FromServices] IFileItemService files,
            [FromServices] IFolderService folders,
            CancellationToken cancellationToken) =>
        {
            const int DefaultLimit = 50;
            const int MaxCodecLength = 64;
            const int MaxLimit = 200;
            const int MaxQueryLength = 256;

            var ownerUserId = httpContext.GetCurrentUserId()!.Value;

            var effectiveLimit = limit ?? DefaultLimit;
            if (effectiveLimit < 1) effectiveLimit = 1;
            if (effectiveLimit > MaxLimit) effectiveLimit = MaxLimit;

            string? effectiveQuery = null;
            if (!string.IsNullOrWhiteSpace(q))
            {
                if (q.Length > MaxQueryLength)
                {
                    return Results.BadRequest(new { error = $"'q' must be {MaxQueryLength} characters or fewer." });
                }
                effectiveQuery = q;
            }

            if (!ImageSort.TryParseField(sort, out var sortField))
            {
                return Results.BadRequest(new { error = "'sort' must be one of: created, name, size, datetaken." });
            }
            if (!ImageSort.TryParseDirection(direction, out var sortDirection))
            {
                return Results.BadRequest(new { error = "'direction' must be 'asc' or 'desc'." });
            }
            if (minRating is int r && (r < 0 || r > 5))
            {
                return Results.BadRequest(new { error = "'minRating' must be between 0 and 5." });
            }
            if (dateTakenFrom is DateTime df && dateTakenTo is DateTime dt && df > dt)
            {
                return Results.BadRequest(new
                {
                    error = "'dateTakenFrom' must be earlier than or equal to 'dateTakenTo'.",
                });
            }
            if (durationMin is double dmin && dmin < 0)
            {
                return Results.BadRequest(new { error = "'durationMin' must be non-negative." });
            }
            if (durationMax is double dmax && dmax < 0)
            {
                return Results.BadRequest(new { error = "'durationMax' must be non-negative." });
            }
            if (durationMin is double a && durationMax is double b && a > b)
            {
                return Results.BadRequest(new { error = "'durationMin' must be <= 'durationMax'." });
            }
            if (minWidth is int mw && mw < 0)
            {
                return Results.BadRequest(new { error = "'minWidth' must be non-negative." });
            }
            if (minHeight is int mh && mh < 0)
            {
                return Results.BadRequest(new { error = "'minHeight' must be non-negative." });
            }
            string? effectiveCodec = null;
            if (!string.IsNullOrWhiteSpace(codec))
            {
                if (codec.Length > MaxCodecLength)
                {
                    return Results.BadRequest(new { error = $"'codec' must be {MaxCodecLength} characters or fewer." });
                }
                effectiveCodec = codec.Trim();
            }

            // Same shared album-membership vocabulary as /api/images. The video gallery
            // has no `albumId` parameter, so the contradictory combination cannot arise.
            if (!GalleryQueryParser.TryParseAlbumMembership(
                albumMembership, out var membership, out var membershipError))
            {
                return Results.BadRequest(new { error = membershipError });
            }

            // Slice 3: Active (normal video gallery) vs Excluded ("Esclusi" tab).
            if (!GalleryQueryParser.TryParseMediaScope(mediaScope, out var scope, out var scopeError))
            {
                return Results.BadRequest(new { error = scopeError });
            }

            var filters = new ImageFilters
            {
                Query = effectiveQuery,
                Favorite = favorite,
                MinRating = minRating,
                DateTakenFrom = dateTakenFrom,
                DateTakenTo = dateTakenTo,
                FolderId = folderId,
                CollapseDuplicates = collapseDuplicates ?? false,
                DurationMinSeconds = durationMin,
                DurationMaxSeconds = durationMax,
                MinWidth = minWidth,
                MinHeight = minHeight,
                VideoCodec = effectiveCodec,
                HasAudio = hasAudio,
                AlbumMembership = membership,
                Scope = scope,
            };
            var currentFingerprint = filters.Fingerprint();

            ImageCursor? parsedCursor = null;
            if (!string.IsNullOrWhiteSpace(cursor))
            {
                if (!ImageCursor.TryParse(cursor, out var parsed))
                {
                    return Results.BadRequest(new { error = "'cursor' is malformed." });
                }
                if (!parsed.MatchesSort(sortField, sortDirection))
                {
                    return Results.BadRequest(new { error = "'cursor' was issued for a different sort/direction." });
                }
                if (!parsed.MatchesFilter(currentFingerprint))
                {
                    return Results.BadRequest(new { error = "'cursor' was issued for a different filter / query set." });
                }
                parsedCursor = parsed;
            }

            if (folderId is Guid parentId)
            {
                var parent = await folders.GetByIdAsync(parentId, ownerUserId, cancellationToken);
                if (parent is null)
                {
                    return Results.NotFound();
                }
            }

            var page = await files.ListVideosPageAsync(
                ownerUserId, effectiveLimit, parsedCursor, filters,
                sortField, sortDirection, cancellationToken);
            return Results.Ok(new VideoListResponse(
                page.Items, effectiveLimit, 0, page.Items.Count, page.NextCursor, page.HasMore));
        }).WithName("ListVideos").RequireAuthorization();

        // Distinct video codecs across the owner's active videos — powers the video
        // gallery's codec filter dropdown (data-driven, owner-scoped). Returns a plain
        // string list; no ids, counts, or storage internals.
        app.MapGet("/api/videos/codecs", async (
            HttpContext httpContext,
            [FromServices] IFileItemService files,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var codecs = await files.ListVideoCodecsAsync(ownerUserId, cancellationToken);
            return Results.Ok(new { codecs });
        }).WithName("ListVideoCodecs").RequireAuthorization();

        // Slice 5: unified media workspace — one contract for the library ("Tutti |
        // Foto | Video") and (below) for album browsing. `kind` selects images, videos,
        // or both; `scope` is active|excluded. Photo params are valid only with
        // kind=image and video params only with kind=video (else 400); album-membership
        // is library-only. Owner-scoped, Vault-excluded, cursor-paginated. The legacy
        // /api/images and /api/videos remain for compatibility.
        static IResult MapMediaCollectionResult(
            NubArca.Api.Media.MediaCollectionResult result, int limit)
        {
            switch (result.Status)
            {
                case NubArca.Api.Media.MediaCollectionStatus.Ok:
                    var page = result.Page!;
                    return Results.Ok(new MediaListResponse(
                        page.Items, limit, page.Items.Count, page.NextCursor, page.HasMore,
                        page.TotalCount, page.PhotoCount, page.VideoCount));
                case NubArca.Api.Media.MediaCollectionStatus.AlbumNotFound:
                    return Results.NotFound();
                default:
                    return Results.BadRequest(new { error = result.Error });
            }
        }

        app.MapGet("/api/media", async (
            [FromQuery] int? limit,
            [FromQuery] string? cursor,
            [FromQuery] string? scope,
            [FromQuery] string? kind,
            [FromQuery] string? q,
            [FromQuery] bool? favorite,
            [FromQuery] int? minRating,
            [FromQuery] DateTime? dateTakenFrom,
            [FromQuery] DateTime? dateTakenTo,
            [FromQuery] string? albumMembership,
            [FromQuery] string? sort,
            [FromQuery] string? direction,
            [FromQuery] bool? hasGps,
            [FromQuery] bool? collapseDuplicates,
            [FromQuery] Guid? similarTo,
            [FromQuery] string? includePeople,
            [FromQuery] string? excludePeople,
            [FromQuery] string? includePeopleMode,
            [FromQuery] double? durationMin,
            [FromQuery] double? durationMax,
            [FromQuery] int? minHeight,
            [FromQuery] string? codec,
            [FromQuery] bool? hasAudio,
            HttpContext httpContext,
            [FromServices] NubArca.Api.Media.IMediaCollectionQueryService media,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            if (!NubArca.Api.Media.MediaCollectionQueryBinder.TryBind(
                new NubArca.Api.Media.MediaCollectionSource.Library(),
                limit, cursor, scope, kind, q, favorite, minRating, dateTakenFrom, dateTakenTo,
                albumMembership, sort, direction, hasGps, collapseDuplicates, similarTo,
                includePeople, excludePeople, includePeopleMode,
                durationMin, durationMax, minHeight, codec, hasAudio,
                out var query, out var bindError))
            {
                return Results.BadRequest(new { error = bindError });
            }
            var result = await media.QueryAsync(ownerUserId, query, cancellationToken);
            return MapMediaCollectionResult(result, query.Limit);
        }).WithName("ListMedia").RequireAuthorization();

        // VSEM-03: unified photo+video semantic search. Additive next to
        // /api/images/semantic (unchanged): ONE query embedding ranks photos
        // and VSEM-02 video samples in the same paired-SigLIP2 profile space,
        // owner-visible candidates FIRST, bounded temporal evidence per video.
        // Supported filters in this slice: favorite, minRating, dateTakenFrom/
        // dateTakenTo. Folder/album/people/GPS/codec/duration/scope filters are
        // NOT semantic-aware here (documented; the parameters do not exist on
        // this route, so they cannot be silently ignored).
        app.MapGet("/api/media/semantic", async (
            [FromQuery] string? q,
            [FromQuery] string? kind,
            [FromQuery] int? limit,
            [FromQuery] string? cursor,
            [FromQuery] bool? favorite,
            [FromQuery] int? minRating,
            [FromQuery] DateTime? dateTakenFrom,
            [FromQuery] DateTime? dateTakenTo,
            // SEARCH-SEM-01: album membership is a PHYSICAL filter, so it must
            // reach the candidate scope and shrink the set BEFORE ranking —
            // never be applied to an already-ranked page. It is part of the
            // ImageFilters fingerprint, so it also binds the msv2 cursor and
            // the ranking-cache key automatically: a ranking built with the
            // filter off can never be served with it on.
            [FromQuery] string? albumMembership,
            HttpContext httpContext,
            [FromServices] NubArca.Api.Media.Semantic.MediaSemanticSearchService semantic,
            CancellationToken cancellationToken) =>
        {
            var query = q?.Trim() ?? string.Empty;
            if (query.Length == 0
                || query.Length > NubArca.Api.Media.Semantic.MediaSemanticSearchService.MaxQueryLength)
            {
                return Results.BadRequest(new
                {
                    error = $"'q' must contain 1-{NubArca.Api.Media.Semantic.MediaSemanticSearchService.MaxQueryLength} characters.",
                });
            }

            if (!MediaKindScopeParser.TryParse(kind, out var kindScope))
            {
                return Results.BadRequest(new { error = "'kind' must be one of: all, image, video." });
            }

            if (minRating is int r && (r < 0 || r > 5))
            {
                return Results.BadRequest(new { error = "'minRating' must be between 0 and 5." });
            }

            if (dateTakenFrom is DateTime df && dateTakenTo is DateTime dt && df > dt)
            {
                return Results.BadRequest(new
                {
                    error = "'dateTakenFrom' must be earlier than or equal to 'dateTakenTo'.",
                });
            }

            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var pageSize = Math.Clamp(
                limit ?? NubArca.Api.Media.Semantic.MediaSemanticSearchService.DefaultPageSize,
                1, NubArca.Api.Media.Semantic.MediaSemanticSearchService.MaxPageSize);
            if (!GalleryQueryParser.TryParseAlbumMembership(
                albumMembership, out var semanticMembership, out var semanticMembershipError))
            {
                return Results.BadRequest(new { error = semanticMembershipError });
            }

            var filters = new ImageFilters
            {
                Favorite = favorite,
                MinRating = minRating,
                DateTakenFrom = dateTakenFrom,
                DateTakenTo = dateTakenTo,
                AlbumMembership = semanticMembership,
            };

            NubArca.Api.Media.Semantic.SemanticMediaPage page;
            try
            {
                page = await semantic.SearchAsync(
                    ownerUserId, query, kindScope, pageSize, cursor, filters, cancellationToken);
            }
            catch (SemanticSearchCursorException)
            {
                return Results.BadRequest(new { error = "Invalid or mismatched semantic-search cursor." });
            }

            if (!page.Available)
            {
                return Results.Json(
                    new { error = "semantic_search_unavailable", reason = page.UnavailableReason },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            return Results.Ok(new NubArca.Api.Media.Semantic.SemanticMediaSearchResponse(
                page.Items, page.NextCursor, page.HasMore,
                page.StillIndexingManyItems ? "indexing" : "ok",
                page.Total));
        }).WithName("SearchMediaSemantically")
            .RequireAuthorization()
            .RequireRateLimiting(SemanticSearchRateLimitPolicy);

        app.MapGet("/api/albums/{albumId:guid}/media", async (
            Guid albumId,
            [FromQuery] int? limit,
            [FromQuery] string? cursor,
            [FromQuery] string? scope,
            [FromQuery] string? kind,
            [FromQuery] string? q,
            [FromQuery] bool? favorite,
            [FromQuery] int? minRating,
            [FromQuery] DateTime? dateTakenFrom,
            [FromQuery] DateTime? dateTakenTo,
            [FromQuery] string? sort,
            [FromQuery] string? direction,
            [FromQuery] bool? hasGps,
            [FromQuery] bool? collapseDuplicates,
            [FromQuery] Guid? similarTo,
            [FromQuery] string? includePeople,
            [FromQuery] string? excludePeople,
            [FromQuery] string? includePeopleMode,
            [FromQuery] double? durationMin,
            [FromQuery] double? durationMax,
            [FromQuery] int? minHeight,
            [FromQuery] string? codec,
            [FromQuery] bool? hasAudio,
            HttpContext httpContext,
            [FromServices] NubArca.Api.Media.IMediaCollectionQueryService media,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            // Album membership is meaningless inside a specific album, so it is not a
            // parameter here (the service also rejects it defensively).
            if (!NubArca.Api.Media.MediaCollectionQueryBinder.TryBind(
                new NubArca.Api.Media.MediaCollectionSource.Album(albumId),
                limit, cursor, scope, kind, q, favorite, minRating, dateTakenFrom, dateTakenTo,
                albumMembership: null, sort, direction, hasGps, collapseDuplicates, similarTo,
                includePeople, excludePeople, includePeopleMode,
                durationMin, durationMax, minHeight, codec, hasAudio,
                out var query, out var bindError))
            {
                return Results.BadRequest(new { error = bindError });
            }
            var result = await media.QueryAsync(ownerUserId, query, cancellationToken);
            return MapMediaCollectionResult(result, query.Limit);
        }).WithName("ListAlbumMedia").RequireAuthorization();

        return app;
    }

    // Duplicated from Program.cs's local SetNoStore helper (used by dozens of
    // other still-inline endpoints there, so it stays put) — same logic.
    private static void SetNoStore(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
    }
}
