using System.Net;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using Microsoft.AspNetCore.Authorization;
using NubArca.Api.Access;
using NubArca.Api.Admin;
using NubArca.Api.Aesthetics;
using NubArca.Api.Ai;
using NubArca.Api.Ai.Photos;
using NubArca.Api.Albums;
using NubArca.Api.Audit;
using NubArca.Api.Auth;
using NubArca.Api.Auth.Recovery;
using NubArca.Api.Cast;
using NubArca.Api.Cli;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Endpoints;
using NubArca.Api.Files;
using NubArca.Api.Folders;
using NubArca.Api.Http;
using NubArca.Api.Ingestion;
using NubArca.Api.Jobs;
using NubArca.Api.Jobs.Handlers;
using NubArca.Api.MediaLibrary;
using NubArca.Api.Metadata;
using NubArca.Api.Organizer;
using NubArca.Api.PhotoExport;
using NubArca.Api.Plates;
using NubArca.Api.Rag;
using NubArca.Api.Security;
using NubArca.Api.ShareLinks;
using NubArca.Api.Storage;
using NubArca.Api.Tv;
using NubArca.Api.TvUpdates;
using NubArca.Api.Uploads;
using NubArca.Api.Users;
using NubArca.Api.Vault;

const string LoginRateLimitPolicy = "login";
const string SharePublicRateLimitPolicy = "share-public";
const string ExportCreateRateLimitPolicy = "export-create";
const string VaultUnlockRateLimitPolicy = "vault-unlock";
const string TvPairingStartRateLimitPolicy = "tv-pairing-start";
const string TvPersonalUnlockRateLimitPolicy = "tv-personal-unlock";
const string PartyPublicRateLimitPolicy = "party-public";
const string PartyPublicMediaRateLimitPolicy = "party-public-media";
const string PartyUploadRateLimitPolicy = "party-upload";
const string PartyFaceSearchRateLimitPolicy = "party-face-search";
const string PartyMessageRateLimitPolicy = "party-message";
const string SemanticSearchRateLimitPolicy = "semantic-search";
const string TvPersonalInterpretRateLimitPolicy = "tv-personal-interpret";
const string BeautyLabUploadRateLimitPolicy = "beauty-lab-upload";
// Public password-recovery request/reset. The SECOND axis (per normalized
// email) lives in PasswordRecoveryThrottle: an IP limit alone lets a
// distributed caller hammer one mailbox, an email limit alone lets one host
// walk an address list.
const string PasswordRecoveryRateLimitPolicy = "password-recovery";

// Operator CLI fast-path. When invoked with a recognised subcommand we run
// the one-shot command and exit without ever building the web host. See
// Cli/CliEntryPoint.cs for the available commands.
if (CliEntryPoint.IsCliInvocation(args))
{
    return await CliEntryPoint.RunAsync(args);
}

var builder = WebApplication.CreateBuilder(args);

// Slice 78: configure Kestrel and FormOptions upload limits from
// Uploads:MaxRequestBodySizeBytes / Uploads:MaxFileSizeBytes so operators
// can tune them without rebuilding the image (env-var override in .env).
// This must happen before any middleware is registered.
{
    var uploadSection = builder.Configuration.GetSection(UploadOptions.SectionName);
    var uploadOpts = uploadSection.Get<UploadOptions>() ?? new UploadOptions();

    builder.WebHost.ConfigureKestrel(kestrel =>
    {
        // Kestrel default is 30 MB — far too small for video uploads.
        // Setting null means "unlimited" but we use the configured value
        // (10 GiB default) so the host is still protected.
        kestrel.Limits.MaxRequestBodySize = uploadOpts.MaxRequestBodySizeBytes > 0
            ? uploadOpts.MaxRequestBodySizeBytes
            : null;
    });

    builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(form =>
    {
        // MultipartBodyLengthLimit is the per-part ceiling ASP.NET Core
        // checks when reading multipart form data. Keep it in sync with
        // Kestrel's whole-request limit.
        form.MultipartBodyLengthLimit = uploadOpts.MaxFileSizeBytes > 0
            ? uploadOpts.MaxFileSizeBytes
            : long.MaxValue;
    });
    builder.Services.Configure<UploadOptions>(uploadSection);
}

// The OpenAPI document metadata MUST be set explicitly. With a bare
// `AddOpenApi()` BOTH the document title AND the default tag on every untagged
// endpoint fall back to the ASSEMBLY / APPLICATION NAME, publishing an internal
// assembly identifier ("NubArca.Api") to every API consumer where a product name
// belongs. The assembly name is a build artifact and the container ENTRYPOINT
// (`dotnet NubArca.Api.dll`); the document is branded here rather than letting
// either one leak into the public contract.
const string OpenApiAssemblyDefaultTag = "NubArca.Api";
const string OpenApiDefaultTag = "NubArca API";
builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer((document, _, _) =>
    {
        document.Info.Title = OpenApiDefaultTag;
        document.Info.Version = "v1";
        document.Info.Description =
            "Owner-private API for NubArca — your files, your hardware, your private cloud.";

        // Minimal-API endpoints that never call WithTags() are tagged with the
        // application name. Rewrite that one default tag (both the document
        // tag list and each operation's reference to it); explicit tags set by
        // an endpoint are left untouched.
        if (document.Tags is not null)
        {
            foreach (var tag in document.Tags)
            {
                if (string.Equals(tag.Name, OpenApiAssemblyDefaultTag, StringComparison.Ordinal))
                {
                    tag.Name = OpenApiDefaultTag;
                }
            }
        }

        foreach (var pathItem in document.Paths.Values)
        {
            // OpenApi 2.0 models Operations as nullable; a path item that
            // declares no operation has nothing to retag.
            if (pathItem.Operations is null)
            {
                continue;
            }

            foreach (var operation in pathItem.Operations.Values)
            {
                if (operation.Tags is null
                    || !operation.Tags.Any(t => string.Equals(
                        t.Name, OpenApiAssemblyDefaultTag, StringComparison.Ordinal)))
                {
                    continue;
                }

                operation.Tags = operation.Tags
                    .Select(t => string.Equals(t.Name, OpenApiAssemblyDefaultTag, StringComparison.Ordinal)
                        ? new OpenApiTagReference(OpenApiDefaultTag, document)
                        : t)
                    .ToHashSet();
            }
        }

        return Task.CompletedTask;
    });
});

// Persist DataProtection keys to a named volume so auth cookies survive
// container restarts and image rebuilds. The path is configured via
// ASPNETCORE_DataProtection__KeyPath (empty in dev → default in-memory,
// which is fine because dev sessions are throwaway anyway).
var dpKeyPath = builder.Configuration["DataProtection:KeyPath"];
if (!string.IsNullOrWhiteSpace(dpKeyPath))
{
    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo(dpKeyPath));
}

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        // The auth cookie name is the wire contract with every live browser
        // session, so the 0.3.0 identity cutover deliberately signed the fleet out
        // once rather than carrying the former name forward. Renaming it again
        // costs another global sign-out.
        options.Cookie.Name = "NubArca.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.SlidingExpiration = true;
        options.ExpireTimeSpan = TimeSpan.FromDays(14);

        // JSON API: don't redirect to a login page on 401/403; just return status.
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
        // Re-check the user on every authenticated request: a cookie issued
        // before the user was disabled or deleted must not keep working.
        options.Events.OnValidatePrincipal = CookieSessionValidator.ValidateAsync;
    });
builder.Services.AddAuthorization(options =>
{
    // One policy per permission key, plus the two composite Laboratory
    // policies. The former single `Admin` role policy is gone: authorization
    // is expressed in permissions now, and an endpoint names the permission it
    // needs through .RequirePermission(...) rather than a role string.
    options.AddNubArcaPermissionPolicies();
});
// SCOPED, not singleton. ASP.NET Core resolves IAuthorizationHandler from
// HttpContext.RequestServices, so a scoped handler receives the REQUEST's
// service provider — which is what makes its IUserPermissionService (and that
// service's DbContext and per-request memo) belong to this request. Registered
// as a singleton it would capture the ROOT provider instead, and the memo would
// then answer every future request with the first one's permissions: a role
// change would appear not to take effect at all.
builder.Services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();
builder.Services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();
// Private Vault passwords use the same PBKDF2 hasher (registered unconditionally
// so both the web host and the SQLite test host resolve it).
builder.Services.AddSingleton<IPasswordHasher<NubArca.Api.Domain.PrivateVault>,
    PasswordHasher<NubArca.Api.Domain.PrivateVault>>();
// TV Personal Area PINs use the same PBKDF2 hasher (registered unconditionally
// so both the web host and the SQLite test host resolve it).
builder.Services.AddSingleton<IPasswordHasher<NubArca.Api.Domain.TvPersonalPin>,
    PasswordHasher<NubArca.Api.Domain.TvPersonalPin>>();

// NUBARCA-GOOGLE-CAST-01: Cast configuration + the ONE CORS policy NubArca has.
//
// CORS is emphatically NOT enabled globally. This policy is attached to the
// grant-scoped Cast media routes and to nothing else, because those are the only
// URLs a foreign document (Google's Default Media Receiver, running on its own
// origin) ever fetches. Every other NubArca endpoint stays same-origin-only.
//
// The allowlist is exact and comes from operator configuration. There is no
// wildcard: `Access-Control-Allow-Origin: *` on a URL that carries a bearer
// secret would let any page on the internet read protected media the moment it
// learned the URL. An unconfigured or unlisted origin simply gets no CORS
// permission — grant creation still works, so the failure shows up on the
// television rather than as a silently permissive server.
builder.Services.Configure<NubArca.Api.Cast.CastOptions>(
    builder.Configuration.GetSection(NubArca.Api.Cast.CastOptions.SectionName));
var castReceiverOrigins = builder.Configuration
    .GetSection(NubArca.Api.Cast.CastOptions.SectionName)
    .Get<NubArca.Api.Cast.CastOptions>()?.NormalizedReceiverOrigins ?? [];
builder.Services.AddCors(options =>
{
    options.AddPolicy(NubArca.Api.Endpoints.CastEndpoints.MediaCorsPolicy, policy =>
    {
        if (castReceiverOrigins.Count == 0)
        {
            // Fail closed: no origin is echoed at all.
            policy.SetIsOriginAllowed(_ => false);
        }
        else
        {
            policy.WithOrigins([.. castReceiverOrigins]);
        }

        // Playback only. A grant is never a write surface.
        policy.WithMethods("GET", "HEAD", "OPTIONS");
        // What a media player actually sends on a cross-origin fetch.
        policy.WithHeaders("Range", "Accept", "Accept-Encoding", "Content-Type");
        // What a media player has to be able to READ back to seek correctly.
        policy.WithExposedHeaders(
            "Content-Type", "Content-Length", "Content-Range", "Accept-Ranges");
        // Deliberately no AllowCredentials: the receiver has no cookie and must
        // never be able to send one.
    });
});

var loginPermitLimit = builder.Configuration.GetValue<int?>("RateLimits:Login:PermitLimit") ?? 10;
var loginWindowSeconds = builder.Configuration.GetValue<int?>("RateLimits:Login:WindowSeconds") ?? 60;
var sharePermitLimit = builder.Configuration.GetValue<int?>("RateLimits:Share:PermitLimit") ?? 60;
var shareWindowSeconds = builder.Configuration.GetValue<int?>("RateLimits:Share:WindowSeconds") ?? 60;
// Creating an export session is cheap but should not be spammable; per-IP cap.
// File/manifest streaming is NOT rate-limited (a large archive is thousands of
// requests; entry ids are unguessable Guids scoped to a token-bound session).
var exportCreatePermitLimit = builder.Configuration.GetValue<int?>("RateLimits:ExportCreate:PermitLimit") ?? 20;
var exportCreateWindowSeconds = builder.Configuration.GetValue<int?>("RateLimits:ExportCreate:WindowSeconds") ?? 60;
// Private Vault unlock is a password check → brute-forceable; cap per IP.
var vaultUnlockPermitLimit = builder.Configuration.GetValue<int?>("RateLimits:VaultUnlock:PermitLimit") ?? 10;
var vaultUnlockWindowSeconds = builder.Configuration.GetValue<int?>("RateLimits:VaultUnlock:WindowSeconds") ?? 60;
var tvPairingStartPermitLimit = builder.Configuration.GetValue<int?>("RateLimits:TvPairingStart:PermitLimit") ?? 20;
var tvPairingStartWindowSeconds = builder.Configuration.GetValue<int?>("RateLimits:TvPairingStart:WindowSeconds") ?? 60;
// TV Personal Area PIN unlock (and owner-side PIN creation) is a secret check →
// brute-forceable; per-IP cap like the vault unlock. The per-SESSION progressive
// cooldown lives in TvPersonalAreaService on top of this.
var tvPersonalUnlockPermitLimit = builder.Configuration.GetValue<int?>("RateLimits:TvPersonalUnlock:PermitLimit") ?? 10;
var tvPersonalUnlockWindowSeconds = builder.Configuration.GetValue<int?>("RateLimits:TvPersonalUnlock:WindowSeconds") ?? 60;
// Public party JSON/actions stay comparatively tight; bulk thumbnail/preview
// rendering has its own media policy below. Public download intentionally stays
// on this stricter party policy.
var partyPublicPermitLimit = builder.Configuration.GetValue<int?>("RateLimits:Party:PermitLimit") ?? 300;
var partyPublicWindowSeconds = builder.Configuration.GetValue<int?>("RateLimits:Party:WindowSeconds") ?? 60;
var partyPublicQueueLimit = builder.Configuration.GetValue<int?>("RateLimits:Party:QueueLimit") ?? 0;
// Public party pages can render hundreds of derived thumbnails/previews from a
// single album view. This high policy is only for token-scoped derived media,
// never original/download/upload/face-search routes.
var partyPublicMediaPermitLimit = builder.Configuration.GetValue<int?>("RateLimits:PartyMedia:PermitLimit") ?? 3000;
var partyPublicMediaWindowSeconds = builder.Configuration.GetValue<int?>("RateLimits:PartyMedia:WindowSeconds") ?? 60;
var partyPublicMediaQueueLimit = builder.Configuration.GetValue<int?>("RateLimits:PartyMedia:QueueLimit") ?? 0;
// Anonymous party UPLOAD is far more expensive/abusable than viewing, so it gets
// a tighter per-IP window than the view/download policy.
var partyUploadPermitLimit = builder.Configuration.GetValue<int?>("RateLimits:PartyUpload:PermitLimit") ?? 30;
var partyUploadWindowSeconds = builder.Configuration.GetValue<int?>("RateLimits:PartyUpload:WindowSeconds") ?? 60;
// Beauty Lab QR mobile-upload: a token-bearing phone posting photos. Same
// tight per-IP window as the party upload path.
var beautyLabUploadPermitLimit = builder.Configuration.GetValue<int?>("RateLimits:BeautyLabUpload:PermitLimit") ?? 30;
var beautyLabUploadWindowSeconds = builder.Configuration.GetValue<int?>("RateLimits:BeautyLabUpload:WindowSeconds") ?? 60;
// Anonymous party MESSAGES cost almost nothing to serve, which is exactly why
// they need their own limit rather than none: a 120-character insert is the
// cheapest way to fill a party's TV with noise. Tighter than viewing, looser
// than uploading — a guest writes a handful of greetings an evening, not one a
// second. Party-public surfaces all partition per IP, so a coach party behind
// one NAT shares a bucket; the limit is set high enough for that.
var partyMessagePermitLimit = builder.Configuration.GetValue<int?>("RateLimits:PartyMessage:PermitLimit") ?? 20;
var partyMessageWindowSeconds = builder.Configuration.GetValue<int?>("RateLimits:PartyMessage:WindowSeconds") ?? 60;
// Anonymous party FACE SEARCH runs face detection + embedding per request, the
// most expensive public party operation, so it gets the tightest per-IP window.
var partyFaceSearchPermitLimit = builder.Configuration.GetValue<int?>("RateLimits:PartyFaceSearch:PermitLimit") ?? 15;
var partyFaceSearchWindowSeconds = builder.Configuration.GetValue<int?>("RateLimits:PartyFaceSearch:WindowSeconds") ?? 60;
// Authenticated semantic search runs the large text tower; bound bursts so one
// client cannot starve normal API traffic. ONNX concurrency remains the second
// line of defence.
// Password recovery is an unauthenticated endpoint that sends mail, so it gets
// a conservative per-IP window suited to a self-hosted installation: a person
// retrying is fine, a script is not. No CAPTCHA — this plus the per-email
// throttle is the whole defence.
var passwordRecoveryPermitLimit = builder.Configuration.GetValue<int?>("RateLimits:PasswordRecovery:PermitLimit") ?? 5;
var passwordRecoveryWindowSeconds = builder.Configuration.GetValue<int?>("RateLimits:PasswordRecovery:WindowSeconds") ?? 300;
var semanticSearchPermitLimit = builder.Configuration.GetValue<int?>("RateLimits:SemanticSearch:PermitLimit") ?? 30;
var semanticSearchWindowSeconds = builder.Configuration.GetValue<int?>("RateLimits:SemanticSearch:WindowSeconds") ?? 60;
// Natural-language interpret is bounded (a local model queue must never be
// flooded): conservative default 15/min per TV session IP, small queue.
// Creating a Cast grant mints a bearer capability, so it is capped per USER
// rather than per IP: several people behind one home NAT must not share a
// budget. HLS segment fetches are deliberately NOT on this policy — one film is
// hundreds of them — and need no separate one, because the bearer URL is already
// scoped to a single video with an expiry.
var castGrantCreatePermitLimit = builder.Configuration.GetValue<int?>("RateLimits:CastGrantCreate:PermitLimit") ?? 20;
var castGrantCreateWindowSeconds = builder.Configuration.GetValue<int?>("RateLimits:CastGrantCreate:WindowSeconds") ?? 60;
var tvPersonalInterpretPermitLimit = builder.Configuration.GetValue<int?>("RateLimits:TvPersonalInterpret:PermitLimit") ?? 15;
var tvPersonalInterpretWindowSeconds = builder.Configuration.GetValue<int?>("RateLimits:TvPersonalInterpret:WindowSeconds") ?? 60;
var tvPersonalInterpretQueueLimit = builder.Configuration.GetValue<int?>("RateLimits:TvPersonalInterpret:QueueLimit") ?? 2;

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = (context, _) =>
    {
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            context.HttpContext.Response.Headers.RetryAfter =
                ((int)retryAfter.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        return ValueTask.CompletedTask;
    };

    options.AddPolicy(LoginRateLimitPolicy, httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = loginPermitLimit,
                Window = TimeSpan.FromSeconds(loginWindowSeconds),
                QueueLimit = 0,
                AutoReplenishment = true,
            }));

    options.AddPolicy(SharePublicRateLimitPolicy, httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = sharePermitLimit,
                Window = TimeSpan.FromSeconds(shareWindowSeconds),
                QueueLimit = 0,
                AutoReplenishment = true,
            }));

    options.AddPolicy(ExportCreateRateLimitPolicy, httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = exportCreatePermitLimit,
                Window = TimeSpan.FromSeconds(exportCreateWindowSeconds),
                QueueLimit = 0,
                AutoReplenishment = true,
            }));

    // Optional Help: a per-USER budget, not per-IP. Every request costs an
    // outbound call — to a paid third party, or to an endpoint with finite GPU —
    // and the callers here are authenticated, so the account is the meaningful
    // subject.
    options.AddPolicy(NubArca.Api.Endpoints.HelpEndpoints.HelpChatRateLimitPolicy, httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.User?.Identity?.Name
                ?? httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 20,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            }));

    // Private documents: a per-USER budget, like Help, and a SEPARATE bucket.
    // These answers run local inference on the operator's own hardware — a
    // different cost with a different shape — and one bucket would let a busy
    // Help user starve somebody asking about their own manual, or the reverse.
    options.AddPolicy(
        NubArca.Api.Endpoints.PrivateDocumentAssistantEndpoints.PrivateChatRateLimitPolicy,
        httpContext =>
            RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: httpContext.User?.Identity?.Name
                    ?? httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 20,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true,
                }));

    options.AddPolicy(VaultUnlockRateLimitPolicy, httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = vaultUnlockPermitLimit,
                Window = TimeSpan.FromSeconds(vaultUnlockWindowSeconds),
                QueueLimit = 0,
                AutoReplenishment = true,
            }));

    options.AddPolicy(TvPairingStartRateLimitPolicy, httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = tvPairingStartPermitLimit,
                Window = TimeSpan.FromSeconds(tvPairingStartWindowSeconds),
                QueueLimit = 0,
                AutoReplenishment = true,
            }));

    options.AddPolicy(TvPersonalUnlockRateLimitPolicy, httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = tvPersonalUnlockPermitLimit,
                Window = TimeSpan.FromSeconds(tvPersonalUnlockWindowSeconds),
                QueueLimit = 0,
                AutoReplenishment = true,
            }));

    options.AddPolicy(PartyPublicRateLimitPolicy, httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = partyPublicPermitLimit,
                Window = TimeSpan.FromSeconds(partyPublicWindowSeconds),
                QueueLimit = partyPublicQueueLimit,
                AutoReplenishment = true,
            }));

    options.AddPolicy(PartyPublicMediaRateLimitPolicy, httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = partyPublicMediaPermitLimit,
                Window = TimeSpan.FromSeconds(partyPublicMediaWindowSeconds),
                QueueLimit = partyPublicMediaQueueLimit,
                AutoReplenishment = true,
            }));

    options.AddPolicy(PartyUploadRateLimitPolicy, httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = partyUploadPermitLimit,
                Window = TimeSpan.FromSeconds(partyUploadWindowSeconds),
                QueueLimit = 0,
                AutoReplenishment = true,
            }));

    options.AddPolicy(BeautyLabUploadRateLimitPolicy, httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = beautyLabUploadPermitLimit,
                Window = TimeSpan.FromSeconds(beautyLabUploadWindowSeconds),
                QueueLimit = 0,
                AutoReplenishment = true,
            }));

    options.AddPolicy(PartyMessageRateLimitPolicy, httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = partyMessagePermitLimit,
                Window = TimeSpan.FromSeconds(partyMessageWindowSeconds),
                QueueLimit = 0,
                AutoReplenishment = true,
            }));

    options.AddPolicy(PartyFaceSearchRateLimitPolicy, httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = partyFaceSearchPermitLimit,
                Window = TimeSpan.FromSeconds(partyFaceSearchWindowSeconds),
                QueueLimit = 0,
                AutoReplenishment = true,
            }));

    options.AddPolicy(PasswordRecoveryRateLimitPolicy, httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = passwordRecoveryPermitLimit,
                Window = TimeSpan.FromSeconds(passwordRecoveryWindowSeconds),
                QueueLimit = 0,
                AutoReplenishment = true,
            }));

    options.AddPolicy(SemanticSearchRateLimitPolicy, httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = semanticSearchPermitLimit,
                Window = TimeSpan.FromSeconds(semanticSearchWindowSeconds),
                QueueLimit = 0,
                AutoReplenishment = true,
            }));

    // Partitioned by USER (the endpoint is authenticated), so a household
    // sharing one public address does not share one budget. An unauthenticated
    // request cannot reach the endpoint at all; the IP fallback exists only so
    // the partition key is never empty.
    options.AddPolicy(CastEndpoints.GrantCreateRateLimitPolicy, httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.GetCurrentUserId()?.ToString()
                ?? httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = castGrantCreatePermitLimit,
                Window = TimeSpan.FromSeconds(castGrantCreateWindowSeconds),
                QueueLimit = 0,
                AutoReplenishment = true,
            }));

    options.AddPolicy(TvPersonalInterpretRateLimitPolicy, httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = tvPersonalInterpretPermitLimit,
                Window = TimeSpan.FromSeconds(tvPersonalInterpretWindowSeconds),
                QueueLimit = tvPersonalInterpretQueueLimit,
                AutoReplenishment = true,
            }));
});

// Reverse-proxy / forwarded-headers support. Off by default so local-dev
// behaviour stays identical. When enabled the middleware runs first in the
// pipeline so that HttpContext.Connection.RemoteIpAddress and
// HttpContext.Request.Scheme are rewritten before authentication, the rate
// limiter, the audit logger, or any endpoint inspects them.
//
// Trust model:
//   * X-Forwarded-* headers are NEVER honoured by default.
//   * Even when Enabled=true, only loopback proxies are honoured unless the
//     operator declares explicit `KnownProxies` and/or `KnownNetworks`.
//   * Setting `TrustAny=true` (also requires Enabled=true) accepts headers
//     from any source — only safe when the host is reachable solely through
//     a controlled private network.
var fwdEnabled = builder.Configuration.GetValue<bool>("ForwardedHeaders:Enabled");
if (fwdEnabled)
{
    var forwardLimit = builder.Configuration.GetValue<int?>("ForwardedHeaders:ForwardLimit") ?? 1;
    var trustAny = builder.Configuration.GetValue<bool>("ForwardedHeaders:TrustAny");
    var configuredProxies = builder.Configuration
        .GetSection("ForwardedHeaders:KnownProxies").Get<string[]>() ?? Array.Empty<string>();
    var configuredNetworks = builder.Configuration
        .GetSection("ForwardedHeaders:KnownNetworks").Get<string[]>() ?? Array.Empty<string>();

    builder.Services.Configure<ForwardedHeadersOptions>(opts =>
    {
        opts.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        opts.ForwardLimit = forwardLimit;

        foreach (var raw in configuredProxies)
        {
            if (IPAddress.TryParse(raw, out var addr))
            {
                opts.KnownProxies.Add(addr);
            }
        }

        foreach (var cidr in configuredNetworks)
        {
            var parts = cidr.Split('/');
            if (parts.Length == 2
                && IPAddress.TryParse(parts[0], out var prefix)
                && int.TryParse(parts[1], out var bits))
            {
                opts.KnownIPNetworks.Add(new System.Net.IPNetwork(prefix, bits));
            }
        }

        if (trustAny)
        {
            // Last-resort opt-in. Equivalent to "any source is a trusted proxy".
            opts.KnownIPNetworks.Add(new System.Net.IPNetwork(IPAddress.Any, 0));
            opts.KnownIPNetworks.Add(new System.Net.IPNetwork(IPAddress.IPv6Any, 0));
        }
    });
}

var connectionString = builder.Configuration.GetConnectionString("Postgres");

if (!string.IsNullOrWhiteSpace(connectionString))
{
    builder.Services.AddDbContext<AppDbContext>(options =>
        options.UseNpgsql(connectionString));

    // Application services that depend on AppDbContext are only registered when Postgres is configured.
    builder.Services.AddScoped<IBlobService, BlobService>();
    builder.Services.AddScoped<IUserService, UserService>();
    // Identity & Access: the role catalogue, effective-permission resolution
    // (a user's role, and nothing else) and the password-recovery flow. Scoped,
    // because they read current database state per request — that is what makes
    // a role edit or a credential change take effect on the next request, with
    // no re-login and no cached authority to invalidate.
    builder.Services.AddScoped<IRoleService, RoleService>();
    builder.Services.AddScoped<IUserPermissionService, UserPermissionService>();
    builder.Services.AddScoped<IPasswordRecoveryService, PasswordRecoveryService>();
    // Slice 94: media-library rules + eligibility (single source of truth for
    // gallery/map/batch-media membership). FolderService and FileItemService
    // both consume it; it depends only on AppDbContext.
    builder.Services.AddScoped<IMediaLibraryService, MediaLibraryService>();
    builder.Services.AddScoped<IMediaLibraryExclusionService, MediaLibraryExclusionService>();
    // Slice 77: FolderService now optionally depends on IFileItemService for
    // recursive soft-delete (preserving per-file blob/audit semantics). We
    // register with a factory so both are resolved from the same scope.
    builder.Services.AddScoped<IFolderService>(sp =>
        new FolderService(
            sp.GetRequiredService<AppDbContext>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<IFileItemService>(),
            sp.GetRequiredService<IMediaLibraryService>()));
    builder.Services.AddScoped<IFileItemService, FileItemService>();
    // mobile-sync-v1: replay-safe owner uploads. Backed by AppDbContext so
    // idempotency state survives restarts; scoped to share the request context.
    builder.Services.AddScoped<NubArca.Api.Uploads.IUploadIdempotencyService,
        NubArca.Api.Uploads.UploadIdempotencyService>();
    // /api/albums/{albumId}/media (library + album sources, one contract).
    builder.Services.AddScoped<
        NubArca.Api.Media.IMediaCollectionQueryService,
        NubArca.Api.Media.MediaCollectionQueryService>();
    // deleted-content-import-skip: per-owner deleted-content ledger + the import
    // skip evaluator that reads it (both scoped: share the request DbContext).
    builder.Services.AddScoped<IDeletedContentTombstoneService, DeletedContentTombstoneService>();
    builder.Services.AddScoped<IImportSkipEvaluator, ImportSkipEvaluator>();
    // Slice 100: pluggable image-derivative backends (libvips fast path with
    // ImageSharp fallback). Stateless + thread-safe → singletons. The renderer
    // selects + falls back; FileThumbnailService injects it optionally.
    builder.Services.Configure<MediaDerivativesOptions>(
        builder.Configuration.GetSection(MediaDerivativesOptions.SectionName));
    builder.Services.AddSingleton<VipsRuntime>();
    builder.Services.AddSingleton<ImageSharpDerivativeBackend>();
    builder.Services.AddSingleton<VipsDerivativeBackend>();
    builder.Services.AddSingleton<IImageDerivativeBackend>(
        sp => sp.GetRequiredService<VipsDerivativeBackend>());
    builder.Services.AddSingleton<ImageDerivativeRenderer>();
    builder.Services.AddScoped<IFileThumbnailService, FileThumbnailService>();
    builder.Services.AddScoped<IAuthService, AuthService>();
    builder.Services.AddScoped<IAdminUserService, AdminUserService>();
    builder.Services.AddScoped<ITvPairingService, TvPairingService>();
    builder.Services.AddScoped<ITvMediaService, TvMediaService>();
    builder.Services.AddScoped<ITvPersonalAreaService, TvPersonalAreaService>();
    builder.Services.AddScoped<ITvPersonalGalleryService, TvPersonalGalleryService>();
    builder.Services.AddScoped<ITvPersonalMediaService, TvPersonalMediaService>();
    builder.Services.AddScoped<IShareLinkService, ShareLinkService>();
    builder.Services.AddScoped<IAuditLogger, AuditLogger>();
    // Slice 84: short-lived Storage Stats cache (singleton; survives across the
    // scoped stats service so repeated admin loads don't re-run the heavy scan).
    builder.Services.AddSingleton<StorageStatsCache>();
    // Slice 78: inject IBlobStorage + IDerivedBlobStorage so StorageStatsService
    // can compute physical-blob cross-check counts.
    builder.Services.AddScoped<IStorageStatsService>(sp =>
        new StorageStatsService(
            sp.GetRequiredService<AppDbContext>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<IOptionsMonitor<FileItemSweeperOptions>>(),
            sp.GetRequiredService<IOptionsMonitor<BlobJanitorOptions>>(),
            sp.GetRequiredService<IOptionsMonitor<BlobStorageOptions>>(),
            sp.GetRequiredService<IBlobStorage>(),
            sp.GetService<IDerivedBlobStorage>(),
            sp.GetService<ILogger<StorageStatsService>>(),
            sp.GetRequiredService<StorageStatsCache>(),
            sp.GetRequiredService<BlobReferenceAuditService>(),
            sp.GetRequiredService<DerivativeDiagnosticsService>()));
    builder.Services.AddScoped<IStorageAccountingService, StorageAccountingService>();
    builder.Services.AddScoped<IMetadataService, MetadataService>();
    builder.Services.AddScoped<IAlbumService, AlbumService>();
    // SHARE-ALBUM-01: live album sharing. The resolver is the single gate for
    // "may this authenticated user act on this album"; the sharing service owns
    // the invitation lifecycle and the recipient read model. Neither widens the
    // owner-only /api/files/* endpoints — see Endpoints/AlbumSharingEndpoints.cs.
    builder.Services.AddScoped<
        NubArca.Api.Albums.Sharing.IAlbumAccessResolver,
        NubArca.Api.Albums.Sharing.AlbumAccessResolver>();
    builder.Services.AddScoped<
        NubArca.Api.Albums.Sharing.IAlbumSharingService,
        NubArca.Api.Albums.Sharing.AlbumSharingService>();
    // SHARE-ALBUM-03: the collaborative editing surface. One implementation for
    // Owner and Editor, so neither can drift from the other's authorization,
    // concurrency or audit.
    builder.Services.AddScoped<
        NubArca.Api.Albums.Sharing.IAlbumEditingService,
        NubArca.Api.Albums.Sharing.AlbumEditingService>();
    // SHARE-COPY-01: one-time DETACHED album copies. Deliberately separate from
    // the membership services above and sharing no state with them — a copy is
    // not a share, and an accepted copy is the recipient's outright.
    builder.Services.AddScoped<
        NubArca.Api.Albums.Sharing.IAlbumTransferService,
        NubArca.Api.Albums.Sharing.AlbumTransferService>();
    // Public read-only party album links (owner lifecycle + public validation)
    // and party-scoped media surfacing.
    builder.Services.AddScoped<NubArca.Api.Party.IPartyLinkService, NubArca.Api.Party.PartyLinkService>();
    builder.Services.AddScoped<NubArca.Api.Party.IPartyMediaService, NubArca.Api.Party.PartyMediaService>();
    builder.Services.AddScoped<NubArca.Api.Party.IPartyParticipantService, NubArca.Api.Party.PartyParticipantService>();
    builder.Services.AddScoped<NubArca.Api.Party.IPartyUploadService, NubArca.Api.Party.PartyUploadService>();
    builder.Services.AddScoped<NubArca.Api.Party.IPartyModerationService, NubArca.Api.Party.PartyModerationService>();
    builder.Services.AddScoped<NubArca.Api.Party.IPartyFaceSearchService, NubArca.Api.Party.PartyFaceSearchService>();
    // Guest party MESSAGES: a text-only domain beside the media pipeline, with
    // its own narrow owner-or-delegate authorization gate.
    builder.Services.AddScoped<NubArca.Api.Party.IPartyMessageAccessResolver, NubArca.Api.Party.PartyMessageAccessResolver>();
    builder.Services.AddScoped<NubArca.Api.Party.IPartyMessageService, NubArca.Api.Party.PartyMessageService>();

    // Slice 70: background jobs. The operations the handlers drive
    // (metadata / media-derivatives backfill, storage reconcile) are
    // registered here so the in-process worker can run them; the same
    // services back the CLI commands.
    builder.Services.AddScoped<MetadataBackfillService>();
    builder.Services.AddScoped<VideoMetadataBackfillService>();
    // Slice 99: durable derivative diagnostics — registered before the backfill
    // (which depends on it) and consumed by the Storage Stats service + the
    // `media derivatives failures` CLI.
    builder.Services.AddScoped<DerivativeDiagnosticsService>();
    builder.Services.AddScoped<MediaDerivativesBackfillService>();
    builder.Services.AddScoped<GalleryDerivativesRegenerationService>();
    builder.Services.AddScoped<MediumPreviewRegenerationService>();
    // Slice 95: operator poster regeneration (media posters regenerate).
    builder.Services.AddScoped<PosterRegenerationService>();
    // Video-hls slices 1–2: generation + serving + lazy-enqueue seam. These
    // depend on AppDbContext / IJobQueue, so they live INSIDE the DB-configured
    // block — a DB-less host (health/rate-limit test factories, misconfigured
    // deploys) must still pass ValidateOnBuild. The DB-free pieces of the HLS
    // graph (process runner, ladder store, transcoder) are registered
    // unconditionally next to the other media providers below.
    builder.Services.AddScoped<VideoHlsGenerationService>();
    builder.Services.AddScoped<VideoHlsBackfillService>();
    builder.Services.AddScoped<IJobQueueAccessor, NubArca.Api.Jobs.VideoHlsJobQueueAccessor>();
    builder.Services.AddScoped<VideoHlsServingService>();
    // NUBARCA-GOOGLE-CAST-01: delegated external playback. Depends on the DB,
    // the permission resolver and the HLS serving service, so it belongs in
    // this block; CastOptions itself is bound unconditionally below.
    builder.Services.AddScoped<NubArca.Api.Cast.CastGrantService>();
    // Admin console: dynamic command catalog (profile options, availability,
    // pending counts). Needs the DB + AI registry, so it lives in this block.
    builder.Services.AddScoped<NubArca.Api.Admin.AdminJobCatalogService>();
    // Slice 96: derived-bytes placement audit/repair (media derivatives
    // verify-bytes / repair-bytes).
    builder.Services.AddScoped<MediaDerivativeBytesService>();
    // Slice 100: backend benchmark (media derivatives benchmark).
    builder.Services.AddScoped<DerivativeBenchmarkService>();
    builder.Services.AddScoped<StorageReconciliationService>();
    // Slice 97: blob reference-count audit/repair (storage blobs
    // audit-references / repair-references) + the stats integrity section.
    builder.Services.AddScoped<BlobReferenceAuditService>();
    builder.Services.AddScoped<IJobQueue, JobQueue>();
    builder.Services.AddScoped<JobProcessor>();

    // Slice 81: admin-only server-side directory import. Reuses the file +
    // folder pipelines; runs as an `admin.import` background job.
    builder.Services.AddScoped<IAdminImportService, AdminImportService>();

    // Phase 2: owner-scoped "Organize photos by date". DB-only logical moves on
    // a cooperative `photo.organizer.datetaken` background job.
    builder.Services.AddScoped<PhotoDateTakenOrganizerService>();

    // Owner-scoped exact image/video duplicate cleanup. Uses canonical
    // BlobObject SHA-256 identity and the normal FileItem Trash lifecycle.
    builder.Services.AddScoped<ExactMediaDuplicateCleanupService>();

    // Photo archive export: read-only snapshot/manifest built by a cooperative
    // `photo.export.build` background job; streamed per-file downloads.
    builder.Services.AddScoped<PhotoExportService>();

    // Private Vault (v0): exclusion-first, password-unlocked owner-private area.
    builder.Services.AddScoped<IPrivateVaultService, PrivateVaultService>();

    // Plates (Targhe): owner-private, segregated image surface + ALPR pipeline.
    // Standalone entity (never a FileItem); reuses the shared blob store +
    // on-demand derivative rendering, but never enters Files/Gallery/People/
    // Party/TV/Private Vault. The ALPR analysis runs on the worker (Compute band)
    // via a model profile/config separate from the AI face substrate.
    builder.Services.AddNubArcaPlates();

    // Aesthetics Lab (Laboratorio estetico): owner-private, opt-in, EXPERIMENTAL
    // isolated space for local HumanAesExpert analysis. Standalone entity (never
    // a FileItem); reuses the shared blob store + on-demand derivative rendering,
    // but never enters Files/Gallery/People/Party/TV/Private Vault/shares. The
    // analysis runs on the worker (Compute band) via the internal HumanAesExpert
    // sidecar. Disabled by default (HumanAesExpert:Enabled=false).
    builder.Services.AddNubArcaAesthetics();

    // Post-ingestion media pipeline: after a normal-library upload, enqueue the
    // bounded, idempotent background work (metadata / derivatives / AI embedding)
    // so medium preview + AI indexing happen without opening the file.
    builder.Services.AddScoped<IPostIngestionMediaPipelineService, PostIngestionMediaPipelineService>();

    // Background-job handlers — the SHARED list both this web host and the CLI
    // `jobs worker` host register (see JobHandlerRegistration). Register the
    // dependent services above before this call.
    builder.Services.AddNubArcaJobHandlers();

    // AI substrate (Phase 0B): service abstractions + provider resolution +
    // deterministic dev/test backend. Inert by default (Ai:Enabled=false,
    // provider "none"); no real inference, ONNX, external calls, pgvector, or
    // jobs. AiOptions is bound above (outside this block) so it always resolves.
    builder.Services.AddAiSubstrate();

    // RAG persistence: the indexed corpus, canonical chunk embeddings, the
    // pgvector accelerator and the indexer. Inert until an operator runs
    // `rag index`; semantic retrieval stays off until `Rag:SemanticEnabled`.
    builder.Services.AddRagDatabase();

    // Slice 93: web remote-staging upload (resumable browser chunks into
    // temporary staging, then handoff to the admin-import pipeline). The
    // cleanup sweeper is registered always but self-disables unless
    // Staging:CleanupEnabled = true.
    builder.Services.AddScoped<IStagingUploadService, StagingUploadService>();
    builder.Services.AddSingleton<StagingCleanupService>();
    builder.Services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<StagingCleanupService>());

    // TV "Beauty Lab" QR upload-session sweeper — reclaims expired/revoked rows
    // (self-disables unless HumanAesExpert:UploadSessionCleanupEnabled = true).
    builder.Services.AddSingleton<NubArca.Api.Aesthetics.AestheticUploadSessionCleanupService>();
    builder.Services.AddSingleton<IHostedService>(sp =>
        sp.GetRequiredService<NubArca.Api.Aesthetics.AestheticUploadSessionCleanupService>());

    builder.Services.AddSingleton<BlobJanitor>();
    builder.Services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<BlobJanitor>());

    builder.Services.AddSingleton<FileItemSweeper>();
    builder.Services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<FileItemSweeper>());

    // SHARE-COPY-01: releases the blob references held by pending transfers
    // that can never be accepted (window elapsed, or sender disabled). Without
    // it those bytes would stay pinned forever — the janitor only reclaims
    // zero-reference blobs and would never see them.
    builder.Services.AddSingleton<NubArca.Api.Albums.Sharing.AlbumTransferCleanupService>();
    builder.Services.AddSingleton<IHostedService>(sp =>
        sp.GetRequiredService<NubArca.Api.Albums.Sharing.AlbumTransferCleanupService>());

    // In-process job worker is OFF by default. Only registered as a hosted
    // service when Jobs:WorkerEnabled = true — NubArca never processes jobs
    // automatically otherwise. The `jobs run-once` / `jobs worker` CLI
    // commands remain the out-of-band path.
    if (builder.Configuration.GetValue<bool>("Jobs:WorkerEnabled"))
    {
        builder.Services.AddHostedService<JobWorker>();
    }
}

builder.Services.Configure<BlobJanitorOptions>(
    builder.Configuration.GetSection(BlobJanitorOptions.SectionName));
builder.Services.Configure<FileItemSweeperOptions>(
    builder.Configuration.GetSection(FileItemSweeperOptions.SectionName));
builder.Services.Configure<NubArca.Api.Albums.Sharing.AlbumTransferCleanupOptions>(
    builder.Configuration.GetSection(
        NubArca.Api.Albums.Sharing.AlbumTransferCleanupOptions.SectionName));
builder.Services.Configure<JobsOptions>(
    builder.Configuration.GetSection(JobsOptions.SectionName));
// Slice 81: admin server-side import config (opt-in; OFF by default). Bound
// outside the Postgres conditional so the options always resolve.
builder.Services.Configure<AdminImportOptions>(
    builder.Configuration.GetSection(AdminImportOptions.SectionName));
// Slice 93: web remote-staging upload config (opt-in; OFF by default).
builder.Services.Configure<StagingOptions>(
    builder.Configuration.GetSection(StagingOptions.SectionName));
// deleted-content-import-skip: fingerprint pepper for the deleted-content
// ledger. Bound unconditionally so the fingerprint is stable everywhere.
builder.Services.Configure<DeletedContentOptions>(
    builder.Configuration.GetSection(DeletedContentOptions.SectionName));
builder.Services.Configure<TvSessionOptions>(
    builder.Configuration.GetSection(TvSessionOptions.SectionName));
builder.Services.Configure<TvUpdateOptions>(
    builder.Configuration.GetSection(TvUpdateOptions.SectionName));
builder.Services.AddSingleton<TvUpdateStore>();
// Plates (Targhe): logical-container-key pepper + upload cap. Bound
// unconditionally so the owner-scoped container key is stable everywhere.
builder.Services.Configure<NubArca.Api.Plates.PlatesOptions>(
    builder.Configuration.GetSection(NubArca.Api.Plates.PlatesOptions.SectionName));
// Plates ALPR pipeline config (separate from Ai:Face*). Disabled by default.
builder.Services.Configure<NubArca.Api.Plates.PlatesAlprOptions>(
    builder.Configuration.GetSection(NubArca.Api.Plates.PlatesAlprOptions.SectionName));
builder.Services.Configure<NubArca.Api.Plates.PlatesFaceRedactionOptions>(
    builder.Configuration.GetSection(NubArca.Api.Plates.PlatesFaceRedactionOptions.SectionName));
// Aesthetics Lab + HumanAesExpert sidecar config. Bound unconditionally so the
// owner-scoped container key is stable everywhere; DISABLED by default
// (HumanAesExpert:Enabled=false, empty SidecarBaseUrl).
builder.Services.Configure<NubArca.Api.Aesthetics.AestheticsOptions>(
    builder.Configuration.GetSection(NubArca.Api.Aesthetics.AestheticsOptions.SectionName));

// AI substrate (Phase 0A): options only, bound outside the Postgres conditional
// so they always resolve. AI is disabled by default; Phase 0A wires no AI
// services, jobs, or backends — only this configuration surface + schema.
builder.Services.Configure<AiOptions>(
    builder.Configuration.GetSection(AiOptions.SectionName));

// The Assistant substrate and the optional Help feature. Disabled by default;
// the registration is unconditional so the status endpoint can answer
// "not enabled" without the container failing to build a service.
//
// Nothing registered here can read library content — see HelpAssistantService's
// constructor. That holds for a LocalTrusted model too: trust decides what a
// model is ELIGIBLE for, and Help's own operation policy is public product
// knowledge, whichever side of the boundary the endpoint is on.
builder.Services.Configure<NubArca.Api.Assistant.AssistantOptions>(
    builder.Configuration.GetSection(NubArca.Api.Assistant.AssistantOptions.SectionName));
// Legacy `ExternalHelp__*`, adapted into one External profile when the
// `Assistant` section is absent. A deprecation path, not the configuration
// model — see ExternalHelpOptions.
builder.Services.Configure<NubArca.Api.Help.ExternalHelpOptions>(
    builder.Configuration.GetSection(NubArca.Api.Help.ExternalHelpOptions.SectionName));
builder.Services.AddSingleton<NubArca.Api.Assistant.AssistantModelResolver>();
builder.Services.AddHttpClient<NubArca.Api.Assistant.IAssistantTextModel,
    NubArca.Api.Assistant.OpenAiCompatibleTextModel>();

// The RAG substrate: named domains, their code-defined privacy policy, and the
// generic retriever every consumer goes through. `product-help` is one domain
// and Help is one consumer — the platform is not Help-shaped, so the semantic
// layer landed BEHIND IRagRetriever rather than beside it.
//
// Registered unconditionally: the Product Help corpus ships in the image, so an
// installation with no database still answers product questions lexically. The
// indexed corpus and the vector table are added inside the Postgres block.
builder.Services.Configure<NubArca.Api.Rag.RagOptions>(
    builder.Configuration.GetSection(NubArca.Api.Rag.RagOptions.SectionName));
builder.Services.AddRagSubstrate();
builder.Services.AddScoped<NubArca.Api.Help.HelpAssistantService>();

// Owner-private documents. Bounds for local text extraction, and the one
// service that answers "from MY documents" — LocalTrusted only, owner derived
// server-side. Registered unconditionally like Help: with no configured private
// model it resolves as disabled and presents no surface implying otherwise.
builder.Services.Configure<NubArca.Api.Ai.Documents.DocumentExtractionOptions>(
    builder.Configuration.GetSection(
        NubArca.Api.Ai.Documents.DocumentExtractionOptions.SectionName));

// Visual document retrieval. Bounds, profile identity and the two feature
// switches — all off by default, because enabling this re-renders and re-embeds
// every document in every library and that is the operator's decision to make.
builder.Services.Configure<NubArca.Api.Ai.DocumentVisual.DocumentVisualOptions>(
    builder.Configuration.GetSection(
        NubArca.Api.Ai.DocumentVisual.DocumentVisualOptions.SectionName));

// An EXPLICIT factory, because the private corpus source exists only when a
// database does and the built-in container has no notion of an optional
// constructor parameter — it validates the whole graph at startup, so a plain
// `AddScoped<T>()` here stopped a connection-string-less host from booting at
// all. A host with no database is a supported configuration; it simply has no
// private knowledge, and the feature says so.
builder.Services.AddScoped(sp => new NubArca.Api.Ai.Documents.PrivateDocumentAssistantService(
    sp.GetRequiredService<NubArca.Api.Assistant.IAssistantTextModel>(),
    sp.GetRequiredService<NubArca.Api.Rag.IRagRetriever>(),
    sp.GetRequiredService<NubArca.Api.Assistant.AssistantModelResolver>(),
    sp.GetService<NubArca.Api.Rag.Retrieval.OwnerDocumentCorpusSource>(),
    sp.GetRequiredService<NubArca.Api.Rag.IRagSemanticProfileResolver>(),
    // Optional for the same reason the corpus source is: no database, no visual
    // retriever — and an installation that never enables visual retrieval simply
    // answers from text, which is what it did before this existed.
    sp.GetService<NubArca.Api.Ai.DocumentVisual.IOwnerDocumentVisualRetriever>(),
    sp.GetRequiredService<ILogger<NubArca.Api.Ai.Documents.PrivateDocumentAssistantService>>()));

// VSEM-01: canonical video temporal substrate (scene segments + sample
// timestamps). Bound alongside AiOptions; disabled by default. The CLI/worker
// host binds the same section (parity — see CliEntryPoint).
// SEARCH-SEM-01: result-selection policy and the short-lived ranking cache.
// Both ship with safe defaults; thresholds stay DISABLED until calibrated
// against the real profile, so behaviour is unchanged out of the box.
builder.Services.Configure<NubArca.Api.Media.Semantic.SemanticResultPolicyOptions>(
    builder.Configuration.GetSection(
        NubArca.Api.Media.Semantic.SemanticResultPolicyOptions.SectionName));
builder.Services.Configure<NubArca.Api.Media.Semantic.SemanticRankingCacheOptions>(
    builder.Configuration.GetSection(
        NubArca.Api.Media.Semantic.SemanticRankingCacheOptions.SectionName));
builder.Services.Configure<NubArca.Api.Ai.Video.VideoSemanticSegmentationOptions>(
    builder.Configuration.GetSection(
        NubArca.Api.Ai.Video.VideoSemanticSegmentationOptions.SectionName));

// VSEM-02: canonical video sample embeddings. Same parity rule as VSEM-01 —
// the CLI/worker host binds the identical section; disabled by default.
builder.Services.Configure<NubArca.Api.Ai.Video.VideoVisualEmbeddingOptions>(
    builder.Configuration.GetSection(
        NubArca.Api.Ai.Video.VideoVisualEmbeddingOptions.SectionName));

// VFACE-01: canonical video face tracks. Same parity rule — the CLI/worker host
// binds the identical section; disabled by default.
builder.Services.Configure<NubArca.Api.Ai.Video.Faces.VideoFaceAnalysisOptions>(
    builder.Configuration.GetSection(
        NubArca.Api.Ai.Video.Faces.VideoFaceAnalysisOptions.SectionName));

builder.Services.Configure<BlobStorageOptions>(
    builder.Configuration.GetSection(BlobStorageOptions.SectionName));
builder.Services.Configure<ImageProcessingOptions>(
    builder.Configuration.GetSection(ImageProcessingOptions.SectionName));
builder.Services.AddSingleton(TimeProvider.System);
// Password-recovery email. Bound outside the Postgres-conditional block so the
// public status endpoint answers even on a host without a database, and so a
// misconfigured mailer presents as "recovery unavailable" rather than a
// missing service. Credentials live only in the operator's .env.
builder.Services.Configure<MailOptions>(
    builder.Configuration.GetSection(MailOptions.SectionName));
builder.Services.AddSingleton<IEmailSender, SmtpEmailSender>();
builder.Services.AddSingleton<PasswordRecoveryThrottle>();
builder.Services.AddSingleton<IBlobStorage, LocalFileSystemBlobStorage>();
// Slice 72: derived media store (thumbnails / previews / posters). Rooted at
// Storage:DerivedRootPath when set, else Storage:RootPath (single-root
// default — byte-for-byte the pre-slice-72 behaviour). BlobService picks this
// up via its optional IDerivedBlobStorage parameter.
builder.Services.AddSingleton<IDerivedBlobStorage>(sp =>
{
    var o = sp.GetRequiredService<IOptions<BlobStorageOptions>>().Value;
    return new DerivedFsBlobStorage(o.EffectiveDerivedRootPath, o.MaxUploadBytes);
});
// Stateless, dependency-free embedded-image-metadata extractor (slice 54).
builder.Services.AddSingleton<IEmbeddedMetadataExtractor, EmbeddedImageMetadataExtractor>();
// Strong metadata mutation: image re-encoder that drops embedded metadata
// profiles (slice 58). Depends on ImageProcessingOptions and so is bound
// after that section is configured above.
builder.Services.AddSingleton<IImageMetadataStripper, ImageSharpMetadataStripper>();
// Slice 66: DateTaken writeback (EXIF), JPEG-only. Same options dependency.
builder.Services.AddSingleton<IImageMetadataWriter, ImageSharpMetadataWriter>();
// Slice 62: stateless header-only video signature detector (no native deps).
builder.Services.AddSingleton<IVideoSignatureDetector, VideoSignatureDetector>();

// Slice 68: video poster providers.
// The synthetic provider is always registered (it is used as fallback by the
// FFmpeg provider too, and is the default when no FFmpeg is configured).
builder.Services.Configure<MediaOptions>(builder.Configuration.GetSection("Media"));
builder.Services.AddSingleton<IProcessRunner, SystemProcessRunner>();
builder.Services.AddSingleton<SyntheticVideoPosterProvider>();
var posterProvider = builder.Configuration["Media:VideoPosterProvider"] ?? "synthetic";
if (string.Equals(posterProvider, "ffmpeg", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<IVideoPosterProvider, FfmpegVideoPosterProvider>();
}
else
{
    builder.Services.AddSingleton<IVideoPosterProvider>(
        sp => sp.GetRequiredService<SyntheticVideoPosterProvider>());
}

// Video metadata probe provider (ffprobe). Opt-in: "none" (default) registers
// a no-op extractor and the video-metadata backfill / post-ingest enqueue do
// no work; "ffprobe" wires the real external-process probe.
var videoMetaProvider = builder.Configuration["Media:VideoMetadataProvider"] ?? "none";
if (string.Equals(videoMetaProvider, "ffprobe", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<IVideoMetadataExtractor, FfprobeVideoMetadataExtractor>();
}
else
{
    builder.Services.AddSingleton<IVideoMetadataExtractor, NoopVideoMetadataExtractor>();
}

// Video-hls slice 1: HLS playback ladder generation. Opt-in via
// Media:VideoHlsProvider=ffmpeg (default "none" → no-op transcoder, and the
// generation service refuses work). The directory-run process seam and the
// hash-sharded ladder store are always registered — inert while disabled.
builder.Services.AddSingleton<IDirectoryProcessRunner, SystemProcessRunner>();
builder.Services.AddSingleton<HlsDerivativeStorage>();
var videoHlsProvider = builder.Configuration["Media:VideoHlsProvider"] ?? "none";
if (string.Equals(videoHlsProvider, "ffmpeg", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<IVideoHlsTranscoder, FfmpegVideoHlsTranscoder>();
}
else
{
    builder.Services.AddSingleton<IVideoHlsTranscoder, NoopVideoHlsTranscoder>();
}
// The DB-dependent HLS services (generation, serving, enqueue seam) are
// registered in the connection-string-configured block above.

var app = builder.Build();

// Opt-in startup migrations (slice 48). Off by default so operators retain
// explicit control over when schema changes hit a populated database — the
// `db migrate` CLI remains the recommended manual path. When the flag is
// true the API host applies pending EF Core migrations before serving any
// request and fails fast if migration throws, so a misconfigured deploy
// never silently serves a half-migrated schema.
if (builder.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
{
    await ApplyStartupMigrationsAsync(app);
}

// The three built-in roles. The migration seeds them, so this is a self-heal
// rather than the primary path — and it is the ONE thing that keeps an
// Administrator's authority complete across a release that ADDS a permission
// key. Fail-soft on purpose: an installation whose schema has not been migrated
// yet must still boot far enough to serve /health and be diagnosed.
await EnsureAccessRolesAsync(app);

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    await TrySeedDevUserAsync(app);
}

// ForwardedHeaders MUST run before authentication, authorization, rate
// limiter, and any endpoint that reads RemoteIpAddress / Request.Scheme.
if (fwdEnabled)
{
    app.UseForwardedHeaders();
}

// Security hardening (slice 54.2). Runs after ForwardedHeaders so the request
// scheme/host reflect the reverse proxy, and before auth so cross-origin
// writes are rejected cheaply.
//   1. X-Content-Type-Options: nosniff on every response.
//   2. CSRF same-origin Origin/Referer validation for unsafe /api methods.
app.Use(static async (context, next) =>
{
    context.Response.Headers[SafeContentType.NoSniffHeader] = SafeContentType.NoSniffValue;
    await next(context);
});
app.Use(CsrfOriginValidation.InvokeAsync);

// CORS runs before authentication so a preflight is answered without one. It
// grants NOTHING by itself: only endpoints that opt in with
// .RequireCors(CastEndpoints.MediaCorsPolicy) are affected, which in this
// application is the grant-scoped Cast media family and nothing else.
app.UseCors();

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }))
    .WithName("Health");
// Face AI milestone: readiness is DISTINCT from liveness. Liveness (/health) is
// green as soon as the process is up — including while direct-mode face models
// compile. Readiness is green only once the direct face pipeline has compiled and
// synthetic-validated (or is not applicable for the configured provider). The
// container health check for openvino-direct points here. The body is sanitized:
// only stable state/failure tokens, never paths, native text, tensors or vectors.
// The preload state is resolved OPTIONALLY from RequestServices, not as a
// handler parameter: the AI substrate (which registers IOnnxFacePreloadState)
// only exists when a database is configured, and an un-attributed interface
// parameter on a host without the registration is inferred as a BODY param —
// making route validation throw at STARTUP ("Body was inferred but the method
// does not allow inferred body parameters"). That startup crash broke every
// DB-less host (HealthEndpointTests + RateLimitTests) since this endpoint
// shipped. Without the substrate there is no face pipeline to wait for, so
// readiness is trivially green (same as the no-op non-direct providers).
app.MapGet("/health/ready", (HttpContext httpContext) =>
{
    var preload = httpContext.RequestServices
        .GetService<NubArca.Api.Ai.Onnx.Face.IOnnxFacePreloadState>();
    if (preload is null)
    {
        return Results.Ok(new { status = "ready", state = "not-applicable" });
    }
    var s = preload.Current;
    return s.IsReady
        ? Results.Ok(new { status = "ready", state = s.State })
        : Results.Json(
            new { status = "not-ready", state = s.State, code = s.FailureCode },
            statusCode: StatusCodes.Status503ServiceUnavailable);
})
    .WithName("Readiness");
app.MapTvUpdateEndpoints();

// Auth endpoints (login/logout/me/language/password) live in
// Endpoints/AuthEndpoints.cs — extracted as part of the modular-monolith
// cleanup. Same routes, same behavior; see that file for the implementation.
app.MapAuthEndpoints();

// TV feature endpoints (pairing, paired-TV session, owner-side TV device
// management, TV Personal Area, and TV Party-album browsing/media delivery)
// live in Endpoints/TvEndpoints.cs — extracted as part of the
// modular-monolith cleanup. Same routes, same session/PIN/auth behavior;
// see that file for the implementation.
app.MapTvEndpoints();

// Slice 65: the caller's own logical storage usage + quota. Owner-scoped:
// returns ONLY the authenticated user's figures, never anyone else's. No
// ids, names, paths, or storage internals — just byte/count aggregates.
app.MapGet("/api/storage/me", async (
    HttpContext httpContext,
    [FromServices] IStorageAccountingService accounting,
    CancellationToken cancellationToken) =>
{
    var ownerUserId = CurrentUserId(httpContext)!.Value;
    var usage = await accounting.GetForUserAsync(ownerUserId, cancellationToken);
    return Results.Ok(usage);
}).WithName("StorageMe").RequireAuthorization();

// Deployment-wide aggregate counters — the admin dashboard's data. Requires
// `admin.dashboard`: a user without it gets 403, and holding a different
// administrative permission does not open this one. Unauthenticated callers get
// 401 from the cookie middleware before the policy runs.
app.MapGet("/api/admin/storage-stats", async (
    [FromQuery] bool? refresh,
    [FromQuery] bool? physical,
    [FromServices] IStorageStatsService stats,
    CancellationToken cancellationToken) =>
{
    // `physical` defaults to true (back-compat / API consumers); the admin UI
    // passes physical=false for fast loads and physical=true on demand.
    var snapshot = await stats.GetAsync(refresh ?? false, physical ?? true, cancellationToken);
    return Results.Ok(snapshot);
}).WithName("StorageStats").RequirePermission(Permissions.AdminDashboard);

// Admin-only medium-preview rebuild status/trigger and AI substrate
// status/diagnostics/face-settings endpoints live in
// Endpoints/AdminAiEndpoints.cs — extracted as part of the
// modular-monolith cleanup. Same routes, same admin-only behavior; see
// that file for the implementation.
app.MapAdminAiEndpoints();

// People / Face owner endpoints live in Endpoints/PeopleEndpoints.cs —
// extracted as part of the modular-monolith cleanup. Same routes, same
// owner-private/Vault-exclusion behavior; see that file for the
// implementation.
app.MapPeopleEndpoints();
app.MapHelpEndpoints();
app.MapPrivateDocumentAssistantEndpoints();

// Media Library (gallery membership rules + per-file exclusion) and Photo
// Organizer (owner-scoped date-taken reorganization) endpoints live in
// Endpoints/PhotoOrganizerEndpoints.cs — extracted as part of the
// modular-monolith cleanup. Same routes, same owner-scoped behavior; see
// that file for the implementation.
app.MapPhotoOrganizerEndpoints();
app.MapExactMediaDuplicateEndpoints();

// Admin user management endpoints live in Endpoints/AdminUserEndpoints.cs —
// extracted as part of the modular-monolith cleanup. Same routes, same
// authorization, same last-admin/self-demotion/self-disable safety behavior;
// see that file for the implementation.
app.MapAdminUserEndpoints();

// Roles as first-class objects. Reads are open to either administrative editor;
// every mutation requires admin.roles.manage, which is Administrator-only.
app.MapAdminRoleEndpoints();

// Admin server-side directory import endpoints live in
// Endpoints/AdminImportEndpoints.cs — extracted as part of the
// modular-monolith cleanup. Same routes, same behavior; see that file for
// the implementation.
app.MapAdminImportEndpoints();

// Web remote-staging upload endpoints live in
// Endpoints/StagingUploadEndpoints.cs — extracted as part of the
// modular-monolith cleanup. Same routes, same behavior; see that file for
// the implementation.
app.MapStagingUploadEndpoints();

// Admin background-jobs dashboard endpoints live in
// Endpoints/AdminJobsEndpoints.cs — extracted as part of the
// modular-monolith cleanup. Same routes, same behavior; see that file for
// the implementation.
app.MapAdminJobsEndpoints();

// FileItem-scoped media delivery + file lifecycle (upload/rename/move/
// delete/restore) live in Endpoints/FileEndpoints.cs; folder listing,
// Trash, and folder lifecycle live in Endpoints/FolderTrashEndpoints.cs —
// both extracted as part of the modular-monolith cleanup. Same routes,
// same owner-scoped behavior; see those files for the implementation.
// (The remaining chunk of file/folder routes below — interrupted here by
// /api/search and the gallery-media module call — is also mapped by these
// same two calls; the route templates don't overlap so registration order
// doesn't matter.)
app.MapFileEndpoints();
app.MapFolderTrashEndpoints();

// NUBARCA-GOOGLE-CAST-01: Google Cast grant lifecycle (cookie-authenticated,
// cast.access) plus the grant-scoped media family a Default Media Receiver
// fetches. The owner's own /api/files/{id}/video contract above is untouched
// and stays cookie-only. See Endpoints/CastEndpoints.cs.
app.MapCastEndpoints();

app.MapGet("/api/search", async (
    [FromQuery] string? q,
    HttpContext httpContext,
    [FromServices] IFileItemService files,
    CancellationToken cancellationToken) =>
{
    var ownerUserId = CurrentUserId(httpContext)!.Value;

    if (string.IsNullOrWhiteSpace(q))
    {
        return Results.BadRequest(new { error = "Missing or empty 'q' parameter." });
    }

    var results = await files.SearchAsync(ownerUserId, q, cancellationToken);
    return Results.Ok(results);
}).WithName("SearchFiles").RequireAuthorization();

// Unified gallery/media query surface (legacy /api/images, /api/videos,
// and the newer unified /api/media + /api/albums/{albumId}/media) lives
// in Endpoints/GalleryMediaEndpoints.cs — extracted as part of the
// modular-monolith cleanup. Same routes, same owner-scoped behavior; see
// that file for the implementation. (Media DELIVERY — thumbnail/preview/
// content/video streaming — lives in Endpoints/FileEndpoints.cs under
// /api/files/{id}/....)
app.MapGalleryMediaEndpoints();

// File rename/move/delete/restore and folder rename/move/delete-preview/
// delete/restore live in Endpoints/FileEndpoints.cs and
// Endpoints/FolderTrashEndpoints.cs (see app.MapFileEndpoints() and
// app.MapFolderTrashEndpoints() above). Same routes, same owner-scoped
// behavior; see those files for the implementation.

// Share Links — owner-side create/list/revoke plus the public,
// anonymous, rate-limited short-URL download — live in
// Endpoints/ShareLinkEndpoints.cs, extracted as part of the
// modular-monolith cleanup. Same routes, same owner-scoped/
// token-scoped behavior; see that file for the implementation.
app.MapShareLinkEndpoints();

// Party public read/media/upload + face-search + owner-side album Party
// settings/moderation endpoints live in Endpoints/PartyEndpoints.cs —
// extracted as part of the modular-monolith cleanup. Same routes, same
// token-scoped/owner-scoped behavior; see that file for the implementation.
app.MapPartyEndpoints();

// Aesthetics Lab / Beauty Lab endpoints — the public TV "Beauty Lab" QR
// mobile upload below, plus the owner-facing lab surface further down —
// live in Endpoints/AestheticsEndpoints.cs — extracted as part of the
// modular-monolith cleanup. Same routes, same owner-private/token-scoped
// behavior; see that file for the implementation.
app.MapAestheticsEndpoints();

// Party face-search endpoints are also mapped by
// Endpoints/PartyEndpoints.cs (see app.MapPartyEndpoints() above).

// Photo archive export (Cloud Functions) endpoints live in
// Endpoints/PhotoExportEndpoints.cs (see app.MapPhotoExportEndpoints()
// below) — extracted as part of the modular-monolith cleanup. Same
// routes, same owner-private/token-scoped behavior; see that file for the
// implementation.

// Private Vault (v0) endpoints live in Endpoints/PrivateVaultEndpoints.cs
// — extracted as part of the modular-monolith cleanup. Same routes, same
// owner-private lock/unlock/session behavior; see that file for the
// implementation.
app.MapPrivateVaultEndpoints();

// Plates (Targhe) endpoints live in Endpoints/PlatesEndpoints.cs — extracted
// as part of the modular-monolith cleanup. Same routes, same owner-private
// behavior; see that file for the implementation.
app.MapPlatesEndpoints();

// Aesthetics Lab (Laboratorio estetico) owner-facing endpoints live in
// Endpoints/AestheticsEndpoints.cs — extracted as part of the
// modular-monolith cleanup (see app.MapAestheticsEndpoints() above). Same
// routes, same owner-private behavior; see that file for the implementation.

app.MapPhotoExportEndpoints();

// Album CRUD/membership/TV-visibility endpoints live in
// Endpoints/AlbumEndpoints.cs — extracted as part of the modular-monolith
// cleanup. Same routes, same owner-scoped behavior; see that file for the
// implementation. The album-nested PARTY routes just below
// (party-settings, party-uploads) are deliberately NOT part of that module
// — they stay here as Party feature endpoints.
app.MapAlbumEndpoints();

// SHARE-ALBUM-01: owner-side member management under /api/albums/{id}/members
// and the recipient's /api/shared-albums/* family. Registration order relative
// to MapAlbumEndpoints does not affect matching — no album template above
// overlaps "/api/albums/{id}/members...", and /api/shared-albums is a distinct
// prefix.
app.MapAlbumSharingEndpoints();
app.MapAlbumTransferEndpoints();

// Album-nested Party settings/moderation endpoints and album item/membership
// + bulk endpoints are also mapped by Endpoints/PartyEndpoints.cs and
// Endpoints/AlbumEndpoints.cs respectively (see app.MapPartyEndpoints() and
// app.MapAlbumEndpoints() above).

await app.RunAsync();
return 0;

static Guid? CurrentUserId(HttpContext httpContext)
{
    var claim = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
    return Guid.TryParse(claim, out var id) ? id : null;
}

static async Task ApplyStartupMigrationsAsync(WebApplication app)
{
    using var scope = app.Services.CreateScope();
    var ctx = scope.ServiceProvider.GetService<AppDbContext>();
    if (ctx is null)
    {
        // Flag is on but no Postgres connection string was configured (e.g.
        // exploratory `dotnet run` with no DB). Nothing to migrate — log and
        // continue so the host still boots for non-DB endpoints like /health.
        app.Logger.LogWarning(
            "Database:MigrateOnStartup is true but ConnectionStrings:Postgres is not set; skipping startup migration.");
        return;
    }

    app.Logger.LogInformation("Startup migration: checking for pending EF Core migrations.");
    try
    {
        var pending = (await ctx.Database.GetPendingMigrationsAsync()).ToList();
        if (pending.Count == 0)
        {
            app.Logger.LogInformation("Startup migration: no pending migrations.");
            return;
        }

        app.Logger.LogInformation(
            "Startup migration: applying {Count} migration(s).", pending.Count);
        foreach (var name in pending)
        {
            app.Logger.LogInformation("Startup migration:   + {Migration}", name);
        }
        await ctx.Database.MigrateAsync();
        app.Logger.LogInformation("Startup migration: completed.");
    }
    catch (Exception ex)
    {
        app.Logger.LogCritical(ex, "Startup migration failed; aborting host start.");
        throw;
    }
}

static async Task EnsureAccessRolesAsync(WebApplication app)
{
    using var scope = app.Services.CreateScope();
    var roles = scope.ServiceProvider.GetService<IRoleService>();
    if (roles is null)
    {
        // No database configured (a host started for /health only).
        return;
    }

    try
    {
        await roles.EnsureBuiltInRolesAsync();
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(
            ex, "Could not ensure the built-in access roles; the schema may not be migrated yet.");
    }
}

static async Task TrySeedDevUserAsync(WebApplication app)
{
    var email = app.Configuration["Seed:User:Email"];
    var displayName = app.Configuration["Seed:User:DisplayName"];
    var password = app.Configuration["Seed:User:Password"];

    if (string.IsNullOrWhiteSpace(email)
        || string.IsNullOrWhiteSpace(displayName)
        || string.IsNullOrWhiteSpace(password))
    {
        return;
    }

    using var scope = app.Services.CreateScope();
    var users = scope.ServiceProvider.GetService<IUserService>();
    var auth = scope.ServiceProvider.GetService<IAuthService>();
    if (users is null || auth is null)
    {
        return;
    }

    var existing = await users.GetByEmailAsync(email);
    if (existing is null)
    {
        var created = await users.CreateAsync(email, displayName);
        await auth.SetPasswordAsync(created.Id, password);
    }
}

// (Album request bodies moved to Endpoints/AlbumEndpoints.cs;
// SetAlbumPartyModeRequest moved to Endpoints/PartyEndpoints.cs;
// PlateAddFromGalleryRequest moved to Endpoints/PlatesEndpoints.cs;
// AestheticAddFromGalleryRequest/AestheticAnalyzeRequest moved to
// Endpoints/AestheticsEndpoints.cs; FaceSettingsUpdateRequest moved to
// Endpoints/AdminAiEndpoints.cs.)

public partial class Program;
