using NubArca.Api.Ai.Jobs;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Files;
using NubArca.Api.Jobs;

namespace NubArca.Api.Admin;

// Admin console: the single source of truth for the background jobs an admin
// can launch from the UI, WITH their parameters. Mirrors the semantics of the
// `jobs enqueue` CLI (CliEntryPoint) — same job types, same payload shapes,
// same idempotency keys — so the console and the CLI can never diverge.
//
// Server-driven by design: the frontend renders a form per command purely from
// the descriptors here, so adding a command (or a parameter) is a one-line
// change to this catalog with NO new UI code. Descriptors are safe-only: they
// carry no storage keys, paths, payloads, or secrets.
//
// Deliberately EXCLUDED (matching the CLI): admin.import, photo.organizer,
// photo.export, plates.analyze, aesthetics.analyze — those run off dedicated
// run-rows created by their own owner-scoped flows, not flag-only enqueues.
public static class AdminJobCommands
{
    public const string CategoryMetadata = "metadata";
    public const string CategoryMedia = "media";
    public const string CategoryStorage = "storage";
    public const string CategoryAi = "ai";

    // Parameter name constants (also the JSON keys the UI submits).
    public const string PLimit = "limit";
    public const string PFailedOnly = "failedOnly";
    public const string PRetryFailed = "retryFailed";
    public const string PDryRun = "dryRun";
    public const string PForce = "force";
    public const string PDeleteOrphans = "deleteOrphans";
    public const string PBlobId = "blobId";
    public const string PProfileKey = "profileKey";

    // Built lazily on first access so it never depends on static-field
    // initialization ORDER: Build() reads the shared AdminJobParam fields
    // declared further down, which are still null during this type's field
    // initializer if Catalog were eagerly assigned here.
    private static IReadOnlyList<AdminJobCommand>? _catalog;
    private static IReadOnlyList<AdminJobCommand> Catalog => _catalog ??= Build();

    public static IReadOnlyList<AdminJobCommand> All => Catalog;

    public static AdminJobCommand? Find(string? key)
        => key is null ? null : Catalog.FirstOrDefault(c => c.Key == key);

    // NOTE: the catalog RESPONSE is built by AdminJobCatalogService — it needs
    // runtime state (registered AI profiles, configured defaults, feature
    // flags) that a static projection cannot see.

    private static readonly AdminJobParam Limit =
        new(PLimit, AdminJobParamKind.Int, Min: 1, Max: 100_000);
    private static readonly AdminJobParam FailedOnly = new(PFailedOnly, AdminJobParamKind.Bool);
    private static readonly AdminJobParam RetryFailed = new(PRetryFailed, AdminJobParamKind.Bool);
    private static readonly AdminJobParam DryRunDefaultOn =
        new(PDryRun, AdminJobParamKind.Bool, DefaultBool: true);
    private static readonly AdminJobParam Force = new(PForce, AdminJobParamKind.Bool, Danger: true);
    // Rendered as a select of the capability's registered profiles, with the
    // configured production model preselected (never a free-text box).
    private static readonly AdminJobParam ProfileKey = new(PProfileKey, AdminJobParamKind.Choice);

    private static IReadOnlyList<AdminJobCommand> Build()
    {
        var list = new List<AdminJobCommand>();

        // ── metadata ──────────────────────────────────────────────────────
        list.Add(new AdminJobCommand(
            "metadata-backfill", CategoryMetadata, JobTypes.MetadataEmbeddedBackfill,
            [Limit, FailedOnly, DryRunDefaultOn],
            v => new(JobTypes.MetadataEmbeddedBackfill,
                new MetadataBackfillJobPayload(v.Int(PLimit), v.Bool(PFailedOnly), v.Bool(PDryRun)),
                JobTypes.MetadataEmbeddedBackfill)));

        list.Add(new AdminJobCommand(
            "metadata-video-backfill", CategoryMetadata, JobTypes.MetadataVideoBackfill,
            [Limit, FailedOnly, DryRunDefaultOn],
            v => new(JobTypes.MetadataVideoBackfill,
                new VideoMetadataBackfillJobPayload(v.Int(PLimit), v.Bool(PFailedOnly), v.Bool(PDryRun)),
                JobTypes.MetadataVideoBackfill))
        { Availability = c => c.VideoProbeEnabled ? null : AdminJobDisabledReasons.ProbeDisabled });

        // ── media ─────────────────────────────────────────────────────────
        list.Add(new AdminJobCommand(
            "media-derivatives-backfill", CategoryMedia, JobTypes.MediaDerivativesBackfill,
            [Limit, RetryFailed, DryRunDefaultOn],
            v =>
            {
                var retry = v.Bool(PRetryFailed);
                return new(JobTypes.MediaDerivativesBackfill,
                    new MediaDerivativesBackfillJobPayload(
                        Limit: v.Int(PLimit), MissingOnly: !retry, FailedOnly: retry,
                        DryRun: v.Bool(PDryRun), RetryFailed: retry),
                    JobTypes.MediaDerivativesBackfill);
            }));

        list.Add(new AdminJobCommand(
            "media-gallery-derivatives-regenerate",
            CategoryMedia,
            JobTypes.MediaGalleryDerivativesRegenerate,
            [Force, Limit, DryRunDefaultOn],
            v => new(
                JobTypes.MediaGalleryDerivativesRegenerate,
                new GalleryDerivativesRegenerationJobPayload(
                    Sizes:
                    [
                        ThumbnailSizes.Small,
                        ThumbnailSizes.Poster,
                        ThumbnailSizes.VideoPreviewStrip,
                    ],
                    Force: v.Bool(PForce),
                    DryRun: v.Bool(PDryRun),
                    Limit: v.Int(PLimit)),
                JobTypes.MediaGalleryDerivativesRegenerate))
        { Availability = c => c.FfmpegPosterEnabled ? null : AdminJobDisabledReasons.FfmpegDisabled });

        list.Add(new AdminJobCommand(
            "media-previews-medium-regenerate", CategoryMedia, JobTypes.MediumPreviewRegenerate,
            [Limit],
            v => new(JobTypes.MediumPreviewRegenerate,
                new MediumPreviewRegenerationJobPayload(v.Int(PLimit)),
                JobTypes.MediumPreviewRegenerate)));

        list.Add(new AdminJobCommand(
            "media-posters-regenerate", CategoryMedia, JobTypes.MediaPostersRegenerate,
            [Force, Limit, DryRunDefaultOn],
            v => new(JobTypes.MediaPostersRegenerate,
                new PosterRegenerationJobPayload(v.Bool(PForce), v.Int(PLimit), v.Bool(PDryRun)),
                JobTypes.MediaPostersRegenerate))
        { Availability = c => c.FfmpegPosterEnabled ? null : AdminJobDisabledReasons.FfmpegDisabled });

        list.Add(new AdminJobCommand(
            "media-video-hls-backfill", CategoryMedia, JobTypes.MediaVideoHlsBackfill,
            [Limit, RetryFailed, Force, DryRunDefaultOn],
            v => new(JobTypes.MediaVideoHlsBackfill,
                new VideoHlsBackfillJobPayload(
                    Limit: v.Int(PLimit), RetryFailed: v.Bool(PRetryFailed),
                    Force: v.Bool(PForce), DryRun: v.Bool(PDryRun)),
                JobTypes.MediaVideoHlsBackfill))
        { Availability = c => c.VideoHlsEnabled ? null : AdminJobDisabledReasons.HlsDisabled });

        list.Add(new AdminJobCommand(
            "media-video-hls-generate", CategoryMedia, JobTypes.MediaVideoHlsGenerate,
            [new AdminJobParam(PBlobId, AdminJobParamKind.Guid, Required: true), Force],
            v =>
            {
                var blobId = v.Guid(PBlobId)!.Value;
                return new(JobTypes.MediaVideoHlsGenerate,
                    new VideoHlsGenerateJobPayload(blobId, v.Bool(PForce)),
                    $"{JobTypes.MediaVideoHlsGenerate}:{blobId:N}");
            })
        { Availability = c => c.VideoHlsEnabled ? null : AdminJobDisabledReasons.HlsDisabled });

        // ── storage ───────────────────────────────────────────────────────
        list.Add(new AdminJobCommand(
            "storage-reconcile", CategoryStorage, JobTypes.StorageReconcile,
            [Limit, new AdminJobParam(PDeleteOrphans, AdminJobParamKind.Bool, Danger: true), DryRunDefaultOn],
            v =>
            {
                var deleteOrphans = v.Bool(PDeleteOrphans);
                // Mirror the CLI coupling: without delete-orphans the run is
                // ALWAYS dry (safe); with it, dry only if explicitly asked.
                var dryRun = !deleteOrphans || v.Bool(PDryRun);
                return new(JobTypes.StorageReconcile,
                    new StorageReconcileJobPayload(v.Int(PLimit), deleteOrphans, dryRun),
                    JobTypes.StorageReconcile);
            }));

        // ── ai (shared AiBackfillJobPayload; idempotency = type:profile) ────
        // Capability drives the profile select; each command also needs its own
        // feature flag, so a disabled capability is shown as such instead of
        // silently enqueueing a no-op.
        AddAi(list, "ai-photos-embeddings-backfill", JobTypes.AiPhotosEmbeddingsBackfill,
            AiCapabilities.ImageEmbedding, c => c.ImageEmbeddingsEnabled, hasLimit: true);
        AddAi(list, "ai-documents-extract-backfill", JobTypes.AiDocumentsExtractBackfill,
            AiCapabilities.DocumentExtraction, c => c.DocumentExtractionEnabled, hasLimit: true);
        AddAi(list, "ai-documents-embeddings-backfill", JobTypes.AiDocumentsEmbeddingsBackfill,
            AiCapabilities.DocumentEmbedding, c => c.DocumentEmbeddingsEnabled, hasLimit: true);
        AddAi(list, "ai-faces-detect-backfill", JobTypes.AiFacesDetectBackfill,
            AiCapabilities.FaceEmbedding, c => c.FaceDetectionEnabled, hasLimit: true);
        AddAi(list, "ai-faces-embeddings-backfill", JobTypes.AiFacesEmbeddingsBackfill,
            AiCapabilities.FaceEmbedding, c => c.FaceEmbeddingsEnabled, hasLimit: true);
        // Clustering is whole-library by design — no per-run limit.
        AddAi(list, "ai-faces-cluster-backfill", JobTypes.AiFacesClusterBackfill,
            AiCapabilities.FaceEmbedding, c => c.FaceClusteringEnabled, hasLimit: false);
        AddAi(list, "ai-tags-generate-backfill", JobTypes.AiTagsGenerateBackfill,
            AiCapabilities.Tagging, c => c.TagsEnabled, hasLimit: true);

        return list;
    }

    private static void AddAi(
        List<AdminJobCommand> list,
        string key,
        string jobType,
        string capability,
        Func<AdminJobAvailabilityContext, bool> featureEnabled,
        bool hasLimit)
    {
        var pars = hasLimit
            ? new List<AdminJobParam> { ProfileKey, Limit, DryRunDefaultOn }
            : [ProfileKey, DryRunDefaultOn];
        list.Add(new AdminJobCommand(key, CategoryAi, jobType, pars,
            v =>
            {
                // Empty selection → null payload key, which makes the handler
                // fall back to the CONFIGURED production profile
                // (Ai:PhotoSimilarityProfileKey / Ai:FaceProfileKey), not the
                // deterministic dev default.
                var profile = v.Text(PProfileKey);
                return new(jobType,
                    new AiBackfillJobPayload(
                        ProfileKey: profile,
                        Limit: hasLimit ? v.Int(PLimit) : null,
                        DryRun: v.Bool(PDryRun)),
                    $"{jobType}:{profile ?? "default"}");
            })
        {
            Capability = capability,
            Availability = c => !c.AiEnabled
                ? AdminJobDisabledReasons.AiDisabled
                : featureEnabled(c) ? null : AdminJobDisabledReasons.FeatureDisabled,
        });
    }
}

// Validates raw request params against a command descriptor and produces the
// bag the Build delegate consumes. Rejects (returns null + error) only on
// genuinely invalid input: a missing required param, an unparseable int/guid,
// or a text over a sane length. Ints are CLAMPED to [Min,Max] (never rejected);
// unknown params are ignored (forward-compatible with older UIs).
public static class AdminJobCommandBinder
{
    private const int MaxTextLength = 128;

    public static AdminJobParamValues? Bind(
        AdminJobCommand command,
        IReadOnlyDictionary<string, System.Text.Json.JsonElement>? raw,
        out string? error)
    {
        error = null;
        var bag = new Dictionary<string, object?>(StringComparer.Ordinal);
        raw ??= new Dictionary<string, System.Text.Json.JsonElement>();

        foreach (var p in command.Params)
        {
            var present = raw.TryGetValue(p.Name, out var el)
                && el.ValueKind is not System.Text.Json.JsonValueKind.Null
                and not System.Text.Json.JsonValueKind.Undefined;
            if (!present)
            {
                if (p.Required)
                {
                    error = $"Missing required parameter '{p.Name}'.";
                    return null;
                }
                bag[p.Name] = p.Kind switch
                {
                    AdminJobParamKind.Bool => p.DefaultBool,
                    AdminJobParamKind.Int => (object?)p.DefaultInt,
                    _ => null,
                };
                continue;
            }

            switch (p.Kind)
            {
                case AdminJobParamKind.Bool:
                    bag[p.Name] = el.ValueKind == System.Text.Json.JsonValueKind.True
                        || (el.ValueKind == System.Text.Json.JsonValueKind.String
                            && bool.TryParse(el.GetString(), out var pb) && pb);
                    break;
                case AdminJobParamKind.Int:
                    if (!TryReadInt(el, out var iv))
                    {
                        error = $"Parameter '{p.Name}' must be an integer.";
                        return null;
                    }
                    var min = p.Min ?? int.MinValue;
                    var max = p.Max ?? int.MaxValue;
                    bag[p.Name] = Math.Clamp(iv, min, max);
                    break;
                // Choice carries a plain string value (validated against the
                // offered options by AdminJobCatalogService), so it binds
                // exactly like Text — without this case the selection would be
                // silently dropped and the job would run with the default.
                case AdminJobParamKind.Text:
                case AdminJobParamKind.Choice:
                    var s = el.ValueKind == System.Text.Json.JsonValueKind.String ? el.GetString() : null;
                    s = string.IsNullOrWhiteSpace(s) ? null : s.Trim();
                    if (s is { Length: > MaxTextLength })
                    {
                        error = $"Parameter '{p.Name}' is too long.";
                        return null;
                    }
                    bag[p.Name] = s;
                    break;
                case AdminJobParamKind.Guid:
                    var gs = el.ValueKind == System.Text.Json.JsonValueKind.String ? el.GetString() : null;
                    if (!Guid.TryParse(gs, out var g))
                    {
                        error = $"Parameter '{p.Name}' must be a GUID.";
                        return null;
                    }
                    bag[p.Name] = g;
                    break;
            }
        }

        return new AdminJobParamValues(bag);
    }

    private static bool TryReadInt(System.Text.Json.JsonElement el, out int value)
    {
        value = 0;
        return el.ValueKind switch
        {
            System.Text.Json.JsonValueKind.Number => el.TryGetInt32(out value),
            System.Text.Json.JsonValueKind.String => int.TryParse(el.GetString(), out value),
            _ => false,
        };
    }
}

// Choice = a closed set of values resolved at request time (currently the AI
// profiles registered for the command's capability), so the UI can render a
// select with the state-of-the-art model preselected instead of a free-text box.
public enum AdminJobParamKind { Bool, Int, Text, Guid, Choice }

public sealed record AdminJobParam(
    string Name,
    AdminJobParamKind Kind,
    bool Required = false,
    int? Min = null,
    int? Max = null,
    bool DefaultBool = false,
    int? DefaultInt = null,
    bool Danger = false);

// (JobType, Payload, optional idempotency key) produced by a command's builder.
public sealed record AdminJobEnqueueSpec(string JobType, object Payload, string? IdempotencyKey);

public sealed record AdminJobCommand(
    string Key,
    string Category,
    string JobType,
    IReadOnlyList<AdminJobParam> Params,
    Func<AdminJobParamValues, AdminJobEnqueueSpec> Build)
{
    // AI capability whose registered profiles populate this command's `choice`
    // parameter (null for non-AI commands). Also selects which configured key
    // is the recommended default.
    public string? Capability { get; init; }

    // Feature flags this command needs to do real work. Returns null when the
    // command is usable, or a stable reason CODE the UI translates (never a
    // user-facing sentence, never config values).
    public Func<AdminJobAvailabilityContext, string?>? Availability { get; init; }
}

// Everything the availability predicates may inspect. Flags only — no secrets,
// no paths.
public sealed record AdminJobAvailabilityContext(
    bool AiEnabled,
    bool ImageEmbeddingsEnabled,
    bool DocumentExtractionEnabled,
    bool DocumentEmbeddingsEnabled,
    bool FaceDetectionEnabled,
    bool FaceEmbeddingsEnabled,
    bool FaceClusteringEnabled,
    bool TagsEnabled,
    bool VideoHlsEnabled,
    bool VideoProbeEnabled,
    bool FfmpegPosterEnabled);

// Stable disabled-reason codes (translated by the UI).
public static class AdminJobDisabledReasons
{
    public const string AiDisabled = "ai-disabled";
    public const string FeatureDisabled = "feature-disabled";
    public const string HlsDisabled = "hls-disabled";
    public const string ProbeDisabled = "probe-disabled";
    public const string FfmpegDisabled = "ffmpeg-disabled";
}

// Validated parameter bag handed to a command's Build delegate. Only declared,
// type-checked, clamped values reach it (see AdminJobCommandBinder).
public sealed class AdminJobParamValues
{
    private readonly IReadOnlyDictionary<string, object?> _values;

    public AdminJobParamValues(IReadOnlyDictionary<string, object?> values) => _values = values;

    public bool Bool(string name) => _values.TryGetValue(name, out var o) && o is bool b && b;
    public int? Int(string name) => _values.TryGetValue(name, out var o) && o is int i ? i : null;
    public string? Text(string name) => _values.TryGetValue(name, out var o) ? o as string : null;
    public Guid? Guid(string name) => _values.TryGetValue(name, out var o) && o is Guid g ? g : null;
}

// ── DTOs for GET /api/admin/jobs/catalog ────────────────────────────────────
public sealed record AdminJobCatalogResponse(IReadOnlyList<AdminJobCommandDto> Commands);

public sealed record AdminJobCommandDto(
    string Key,
    string Category,
    string JobType,
    IReadOnlyList<AdminJobParamDto> Params,
    // False when the command's feature is switched off — the UI marks it and
    // blocks the run instead of silently queueing a no-op.
    bool Available = true,
    string? DisabledReason = null);

public sealed record AdminJobParamDto(
    string Name,
    string Kind,
    bool Required,
    int? Min,
    int? Max,
    bool DefaultBool,
    int? DefaultInt,
    bool Danger,
    // `choice` params only: the closed option set + the preselected value
    // (the configured production model).
    IReadOnlyList<AdminJobChoiceDto>? Options = null,
    string? DefaultText = null);

public sealed record AdminJobChoiceDto(string Value, string Label, bool Recommended);

// POST /api/admin/jobs/enqueue body. `Params` is the raw JSON object the UI
// submits; the binder validates it against the named command's descriptor.
public sealed record AdminJobEnqueueRequest(
    string? Command,
    IReadOnlyDictionary<string, System.Text.Json.JsonElement>? Params);

// AlreadyQueued: an identical run of this library-wide backfill was already
// queued/running, so the request collapsed onto it instead of piling up.
public sealed record AdminJobEnqueueResponse(Guid JobId, string JobType, bool AlreadyQueued = false);
