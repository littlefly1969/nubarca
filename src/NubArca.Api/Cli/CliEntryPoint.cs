using System.Globalization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NubArca.Api.Admin;
using NubArca.Api.Aesthetics;
using NubArca.Api.Ai;
using NubArca.Api.Ai.Backends;
using NubArca.Api.Ai.Faces;
using NubArca.Api.Ai.Jobs;
using NubArca.Api.Ai.Onnx;
using NubArca.Api.Ai.Onnx.Face;
using NubArca.Api.Ai.Photos;
using NubArca.Api.Ai.Resolution;
using NubArca.Api.Ai.Video;
using NubArca.Api.Ai.Video.Faces;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Auth;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Folders;
using NubArca.Api.Audit;
using NubArca.Api.Jobs;
using NubArca.Api.Jobs.Handlers;
using NubArca.Api.Plates;
using NubArca.Api.MediaLibrary;
using NubArca.Api.Organizer;
using NubArca.Api.PhotoExport;
using NubArca.Api.Metadata;
using NubArca.Api.Storage;
using NubArca.Api.Uploads;
using NubArca.Api.Users;

namespace NubArca.Api.Cli;

// NubArca operator CLI for one-shot maintenance tasks. Invoked by passing a
// subcommand as the first program argument — e.g.
//
//   dotnet NubArca.Api.dll users ensure
//   dotnet NubArca.Api.dll db migrate
//
// `NubArca.Api.dll` is the produced assembly, and therefore the container
// ENTRYPOINT and the exact string every runbook command contains. Keep usage
// examples byte-identical to what an operator actually types.
//
// When `IsCliInvocation` returns true the host MUST NOT start Kestrel; see
// the early-return branch in Program.cs. The CLI builds its own
// `HostApplicationBuilder` so background services / web pipeline never spin
// up.
public static class CliEntryPoint
{
    // Subcommand keywords + their short aliases. Matched case-sensitively
    // against argv[0] (and argv[1] for the verb form).
    private static readonly HashSet<string> KnownEntryArgs =
        new(StringComparer.Ordinal)
        {
            "users", "ensure-user",
            "grant-admin", "revoke-admin",
            "db", "db-migrate",
            "metadata",
            "media",
            "storage",
            "jobs",
            "ai",
            "plates",
            "--help", "-h", "help",
        };

    public static bool IsCliInvocation(string[] args)
        => args.Length > 0 && KnownEntryArgs.Contains(args[0]);

    public static Task<int> RunAsync(string[] args)
        => RunAsync(args, Console.Out, Console.Error);

    // Test-friendly entry point: lets tests supply their own writers AND an
    // override `serviceProvider` so they don't need to spin a real Postgres.
    // Production callers always go through the public `RunAsync(string[])`
    // overload above.
    internal static async Task<int> RunAsync(
        string[] args,
        TextWriter stdout,
        TextWriter stderr,
        Func<IServiceProvider>? serviceProviderFactory = null)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
        {
            WriteHelp(stdout);
            return 0;
        }

        var (verb, sub, rest) = ParseVerbSubcommand(args);

        switch ((verb, sub))
        {
            case ("users", "ensure"):
                return await DispatchAsync(
                    serviceProviderFactory,
                    sp => EnsureUserAsync(rest, sp, stdout, stderr),
                    stderr);

            case ("users", "grant-admin"):
                return await DispatchAsync(
                    serviceProviderFactory,
                    sp => SetAdminAsync(rest, sp, stdout, stderr, isAdmin: true),
                    stderr);

            case ("users", "revoke-admin"):
                return await DispatchAsync(
                    serviceProviderFactory,
                    sp => SetAdminAsync(rest, sp, stdout, stderr, isAdmin: false),
                    stderr);

            case ("db", "migrate"):
                return await DispatchAsync(
                    serviceProviderFactory,
                    sp => DbMigrateAsync(sp, stdout, stderr),
                    stderr);

            case ("metadata", "backfill"):
                return await DispatchAsync(
                    serviceProviderFactory,
                    sp => MetadataBackfillAsync(rest, sp, stdout, stderr),
                    stderr);

            case ("metadata", "video-backfill"):
                return await DispatchAsync(
                    serviceProviderFactory,
                    sp => VideoMetadataBackfillAsync(rest, sp, stdout, stderr),
                    stderr);

            case ("metadata", "recompute-effective-dates"):
                return await DispatchAsync(
                    serviceProviderFactory,
                    sp => RecomputeEffectiveDatesAsync(sp, stdout, stderr),
                    stderr);

            case ("media", "derivatives") when rest.Length > 0 && rest[0] == "backfill":
                return await DispatchAsync(
                    serviceProviderFactory,
                    sp => MediaDerivativesBackfillAsync(rest[1..], sp, stdout, stderr),
                    stderr);

            case ("media", "derivatives") when rest.Length > 0 && rest[0] == "failures":
                return await DispatchAsync(
                    serviceProviderFactory,
                    sp => MediaDerivativesFailuresAsync(sp, stdout, stderr),
                    stderr);

            case ("media", "derivatives") when rest.Length > 0 && rest[0] == "benchmark":
                return await DispatchAsync(
                    serviceProviderFactory,
                    sp => MediaDerivativesBenchmarkAsync(rest[1..], sp, stdout, stderr),
                    stderr);

            case ("media", "derivatives") when rest.Length > 0 && rest[0] == "verify-bytes":
                return await DispatchAsync(
                    serviceProviderFactory,
                    sp => MediaDerivativesVerifyBytesAsync(rest[1..], sp, stdout, stderr),
                    stderr);

            case ("media", "derivatives") when rest.Length > 0 && rest[0] == "repair-bytes":
                return await DispatchAsync(
                    serviceProviderFactory,
                    sp => MediaDerivativesRepairBytesAsync(rest[1..], sp, stdout, stderr),
                    stderr);

            case ("media", "posters") when rest.Length > 0 && rest[0] == "regenerate":
                return await DispatchAsync(
                    serviceProviderFactory,
                    sp => MediaPostersRegenerateAsync(rest[1..], sp, stdout, stderr),
                    stderr);

            case ("media", "file-status"):
                return await DispatchAsync(
                    serviceProviderFactory,
                    sp => MediaFileStatusAsync(rest, sp, stdout, stderr),
                    stderr);

            case ("storage", "reconcile"):
                return await DispatchAsync(
                    serviceProviderFactory,
                    sp => StorageReconcileAsync(rest, sp, stdout, stderr),
                    stderr);

            case ("storage", "blobs") when rest.Length > 0 && rest[0] == "audit-references":
                return await DispatchAsync(
                    serviceProviderFactory,
                    sp => StorageBlobsAuditReferencesAsync(sp, stdout, stderr),
                    stderr);

            case ("storage", "blobs") when rest.Length > 0 && rest[0] == "repair-references":
                return await DispatchAsync(
                    serviceProviderFactory,
                    sp => StorageBlobsRepairReferencesAsync(rest[1..], sp, stdout, stderr),
                    stderr);

            case ("jobs", "enqueue"):
                return await DispatchAsync(
                    serviceProviderFactory,
                    sp => JobsEnqueueAsync(rest, sp, stdout, stderr),
                    stderr);

            case ("jobs", "list"):
                return await DispatchAsync(
                    serviceProviderFactory,
                    sp => JobsListAsync(rest, sp, stdout, stderr),
                    stderr);

            case ("jobs", "run-once"):
                return await DispatchAsync(
                    serviceProviderFactory,
                    sp => JobsRunOnceAsync(rest, sp, stdout, stderr),
                    stderr);

            case ("jobs", "worker"):
                return await DispatchAsync(
                    serviceProviderFactory,
                    sp => JobsWorkerAsync(rest, sp, stdout, stderr),
                    stderr);

            case ("ai", "status"):
                return await DispatchAsync(
                    serviceProviderFactory,
                    sp => AiStatusAsync(rest, sp, stdout, stderr),
                    stderr);

            case ("ai", "models"):
                return await DispatchAsync(
                    serviceProviderFactory,
                    sp => AiModelsAsync(rest, sp, stdout, stderr),
                    stderr);

            case ("ai", "profiles"):
                return await DispatchAsync(
                    serviceProviderFactory,
                    sp => AiProfilesAsync(rest, sp, stdout, stderr),
                    stderr);

            case ("ai", "diagnostics"):
                return await DispatchAsync(
                    serviceProviderFactory,
                    sp => AiDiagnosticsAsync(rest, sp, stdout, stderr),
                    stderr);

            case ("ai", "seed"):
                return await DispatchAsync(
                    serviceProviderFactory,
                    sp => AiSeedAsync(rest, sp, stdout, stderr),
                    stderr);

            case ("ai", "photos") when rest.Length > 0 && rest[0] == "similar":
                return await DispatchAsync(
                    serviceProviderFactory,
                    sp => AiPhotosSimilarAsync(rest[1..], sp, stdout, stderr),
                    stderr);

            case ("ai", "photos") when rest.Length > 0 && rest[0] == "embeddings":
                return await DispatchAsync(
                    serviceProviderFactory,
                    sp => AiPhotosEmbeddingsAsync(rest[1..], sp, stdout, stderr),
                    stderr);

            case ("ai", "onnx") when rest.Length > 0 && rest[0] == "image":
                return await DispatchAsync(
                    serviceProviderFactory,
                    sp => AiOnnxImageAsync(rest[1..], sp, stdout, stderr),
                    stderr);

            case ("ai", "onnx") when rest.Length > 0 && rest[0] == "runtime-info":
                return await DispatchAsync(
                    serviceProviderFactory,
                    sp => AiOnnxRuntimeInfoAsync(rest[1..], sp, stdout, stderr),
                    stderr);

            case ("ai", "onnx") when rest.Length > 0 && rest[0] == "face-embed":
                return await DispatchAsync(
                    serviceProviderFactory,
                    sp => AiOnnxFaceEmbedAsync(rest[1..], sp, stdout, stderr),
                    stderr);

            case ("ai", "onnx") when rest.Length > 0 && rest[0] == "image-embed":
                return await DispatchAsync(
                    serviceProviderFactory,
                    sp => AiOnnxImageEmbedAsync(rest[1..], sp, stdout, stderr),
                    stderr);

            case ("ai", "onnx") when rest.Length > 0 && rest[0] == "text-embed":
                return await DispatchAsync(
                    serviceProviderFactory,
                    sp => AiOnnxTextEmbedAsync(rest[1..], sp, stdout, stderr),
                    stderr);

            case ("ai", "face"):
                return await DispatchAsync(
                    serviceProviderFactory,
                    sp => AiFaceAsync(rest, sp, stdout, stderr),
                    stderr);

            case ("ai", "faces"):
                return await DispatchAsync(
                    serviceProviderFactory,
                    sp => AiFacesAsync(rest, sp, stdout, stderr),
                    stderr);

            case ("ai", "video") when rest.Length > 0 && rest[0] == "semantic":
                return await DispatchAsync(
                    serviceProviderFactory,
                    sp => AiVideoSemanticAsync(rest[1..], sp, stdout, stderr),
                    stderr);

            case ("plates", "models") when rest.Length > 0 && rest[0] == "validate":
                return await DispatchAsync(
                    serviceProviderFactory,
                    sp => PlatesModelsValidateAsync(rest[1..], sp, stdout, stderr),
                    stderr);

            case ("plates", "benchmark"):
                return await DispatchAsync(
                    serviceProviderFactory,
                    sp => PlatesBenchmarkAsync(rest, sp, stdout, stderr),
                    stderr);

            default:
                stderr.WriteLine($"Unknown command: {string.Join(' ', args)}");
                stderr.WriteLine("Run with --help to see available commands.");
                return 64; // EX_USAGE
        }
    }

    // ---- subcommand: users ensure -----------------------------------------

    internal static async Task<int> EnsureUserAsync(
        string[] args,
        IServiceProvider services,
        TextWriter stdout,
        TextWriter stderr)
    {
        var email = ReadOption(args, "--email")
            ?? ReadEnv("NUBARCA_ADMIN_EMAIL");
        var displayName = ReadOption(args, "--display-name")
            ?? ReadEnv("NUBARCA_ADMIN_DISPLAY_NAME");
        var password = ReadOption(args, "--password")
            ?? ReadEnv("NUBARCA_ADMIN_PASSWORD");
        var updatePassword = HasFlag(args, "--update-password")
            || string.Equals(ReadEnv("NUBARCA_ADMIN_UPDATE_PASSWORD"), "true",
                StringComparison.OrdinalIgnoreCase);
        var makeAdmin = HasFlag(args, "--admin")
            || string.Equals(ReadEnv("NUBARCA_ADMIN_IS_ADMIN"), "true",
                StringComparison.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(email))
        {
            stderr.WriteLine("users ensure: --email or NUBARCA_ADMIN_EMAIL is required.");
            return 64;
        }
        if (string.IsNullOrWhiteSpace(displayName))
        {
            stderr.WriteLine("users ensure: --display-name or NUBARCA_ADMIN_DISPLAY_NAME is required.");
            return 64;
        }
        if (string.IsNullOrWhiteSpace(password))
        {
            stderr.WriteLine("users ensure: --password or NUBARCA_ADMIN_PASSWORD is required.");
            return 64;
        }

        // Modest sanity check. Operators with stronger policies should pre-
        // validate; the goal here is to refuse trivial fat-finger mistakes
        // (e.g. forgetting the variable and ending up with `--password=`),
        // not to enforce any particular complexity rule.
        if (password.Length < 8)
        {
            stderr.WriteLine("users ensure: password must be at least 8 characters.");
            return 64;
        }

        var users = services.GetService<IUserService>();
        var auth = services.GetService<IAuthService>();
        if (users is null || auth is null)
        {
            stderr.WriteLine("users ensure: database is not configured. Set ConnectionStrings__Postgres and retry.");
            return 78; // EX_CONFIG
        }

        var existing = await users.GetByEmailAsync(email);
        if (existing is null)
        {
            var created = await users.CreateAsync(email, displayName);
            await auth.SetPasswordAsync(created.Id, password);
            if (makeAdmin)
            {
                await users.SetAdminAsync(created.Id, true);
            }
            stdout.WriteLine(
                $"users ensure: created user {created.Email} ({created.Id:N})"
                + (makeAdmin ? " as admin." : "."));
            return 0;
        }

        if (updatePassword)
        {
            await auth.SetPasswordAsync(existing.Id, password);
            stdout.WriteLine($"users ensure: updated password for {existing.Email} ({existing.Id:N}).");
        }
        else
        {
            stdout.WriteLine($"users ensure: user {existing.Email} already exists; password unchanged.");
            stdout.WriteLine("              Pass --update-password (or set NUBARCA_ADMIN_UPDATE_PASSWORD=true) to overwrite.");
        }

        // Admin flag is its own toggle: --admin on an existing non-admin
        // user is a deliberate upgrade. The CLI does NOT silently downgrade
        // someone — use `users revoke-admin` for that.
        if (makeAdmin && !existing.IsAdmin)
        {
            await users.SetAdminAsync(existing.Id, true);
            stdout.WriteLine($"users ensure: granted admin to {existing.Email}.");
        }

        return 0;
    }

    internal static async Task<int> SetAdminAsync(
        string[] args,
        IServiceProvider services,
        TextWriter stdout,
        TextWriter stderr,
        bool isAdmin)
    {
        var verb = isAdmin ? "grant-admin" : "revoke-admin";
        var email = ReadOption(args, "--email")
            ?? ReadEnv("NUBARCA_ADMIN_EMAIL");
        if (string.IsNullOrWhiteSpace(email))
        {
            stderr.WriteLine($"users {verb}: --email or NUBARCA_ADMIN_EMAIL is required.");
            return 64;
        }

        var users = services.GetService<IUserService>();
        if (users is null)
        {
            stderr.WriteLine($"users {verb}: database is not configured. Set ConnectionStrings__Postgres and retry.");
            return 78;
        }

        var existing = await users.GetByEmailAsync(email);
        if (existing is null)
        {
            stderr.WriteLine($"users {verb}: no user with email {email}.");
            return 64;
        }

        if (existing.IsAdmin == isAdmin)
        {
            stdout.WriteLine(
                $"users {verb}: {existing.Email} is already "
                + (isAdmin ? "admin." : "not admin.")
                + " No change.");
            return 0;
        }

        var ok = await users.SetAdminAsync(existing.Id, isAdmin);
        if (!ok)
        {
            stderr.WriteLine($"users {verb}: update did not match a row (concurrent delete?).");
            return 1;
        }

        stdout.WriteLine(
            $"users {verb}: {existing.Email} is now "
            + (isAdmin ? "admin." : "not admin."));
        return 0;
    }

    // ---- subcommand: db migrate -------------------------------------------

    internal static async Task<int> DbMigrateAsync(
        IServiceProvider services,
        TextWriter stdout,
        TextWriter stderr)
    {
        var ctx = services.GetService<AppDbContext>();
        if (ctx is null)
        {
            stderr.WriteLine("db migrate: ConnectionStrings:Postgres is not set; cannot run migrations.");
            return 78;
        }

        try
        {
            var pending = (await ctx.Database.GetPendingMigrationsAsync()).ToList();
            if (pending.Count == 0)
            {
                stdout.WriteLine("db migrate: no pending migrations.");
                return 0;
            }
            stdout.WriteLine($"db migrate: applying {pending.Count} migration(s):");
            foreach (var name in pending)
            {
                stdout.WriteLine($"  + {name}");
            }
            await ctx.Database.MigrateAsync();
            stdout.WriteLine("db migrate: completed.");
            return 0;
        }
        catch (Exception ex)
        {
            // Bubble the exception message but NEVER any environment / config
            // value (the operator would have those handy already; we don't
            // want stray secrets in stderr).
            stderr.WriteLine($"db migrate: failed: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    // ---- subcommand: metadata backfill ------------------------------------

    internal static async Task<int> MetadataBackfillAsync(
        string[] args,
        IServiceProvider services,
        TextWriter stdout,
        TextWriter stderr)
    {
        var backfill = services.GetService<MetadataBackfillService>();
        if (backfill is null)
        {
            stderr.WriteLine("metadata backfill: database is not configured. Set ConnectionStrings__Postgres and retry.");
            return 78; // EX_CONFIG
        }

        int? limit = null;
        var limitRaw = ReadOption(args, "--limit");
        if (limitRaw is not null)
        {
            if (!int.TryParse(limitRaw, out var n) || n <= 0)
            {
                stderr.WriteLine("metadata backfill: --limit must be a positive integer.");
                return 64;
            }
            limit = n;
        }

        var options = new MetadataBackfillOptions
        {
            Limit = limit,
            FailedOnly = HasFlag(args, "--failed-only"),
            DryRun = HasFlag(args, "--dry-run"),
        };

        try
        {
            // The service logs concise, numbers-only progress — never raw
            // metadata. We forward those lines straight to stdout.
            var result = await backfill.RunAsync(options, line => stdout.WriteLine(line));
            return 0;
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"metadata backfill: failed: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    // ---- subcommand: metadata video-backfill -------------------------------

    // Probes existing video blobs for container/stream metadata via ffprobe.
    // Refuses to run when no provider is configured (Media:VideoMetadataProvider
    // = "none") so it never marks every video "skipped" by accident.
    internal static async Task<int> VideoMetadataBackfillAsync(
        string[] args,
        IServiceProvider services,
        TextWriter stdout,
        TextWriter stderr)
    {
        var backfill = services.GetService<VideoMetadataBackfillService>();
        if (backfill is null)
        {
            stderr.WriteLine("metadata video-backfill: database is not configured. Set ConnectionStrings__Postgres and retry.");
            return 78; // EX_CONFIG
        }

        var mediaOptions = services.GetService<Microsoft.Extensions.Options.IOptions<MediaOptions>>()?.Value;
        if (mediaOptions is null || !mediaOptions.VideoMetadataProbeEnabled)
        {
            stderr.WriteLine("metadata video-backfill: no video-metadata provider is enabled. "
                + "Set Media__VideoMetadataProvider=ffprobe (and ensure ffprobe is installed) and retry.");
            return 78; // EX_CONFIG
        }

        int? limit = null;
        var limitRaw = ReadOption(args, "--limit");
        if (limitRaw is not null)
        {
            if (!int.TryParse(limitRaw, out var n) || n <= 0)
            {
                stderr.WriteLine("metadata video-backfill: --limit must be a positive integer.");
                return 64;
            }
            limit = n;
        }

        var options = new MetadataBackfillOptions
        {
            Limit = limit,
            FailedOnly = HasFlag(args, "--failed-only"),
            DryRun = HasFlag(args, "--dry-run"),
        };

        try
        {
            await backfill.RunAsync(options, line => stdout.WriteLine(line));
            return 0;
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"metadata video-backfill: failed: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    // ---- subcommand: metadata recompute-effective-dates --------------------

    // Repair command: rebuild FileItem.EffectiveDateTaken for every file from
    // the layered sources of truth. Set-based, no byte reads, no storage
    // internals in output (a single updated-count line only).
    internal static async Task<int> RecomputeEffectiveDatesAsync(
        IServiceProvider services,
        TextWriter stdout,
        TextWriter stderr)
    {
        var backfill = services.GetService<MetadataBackfillService>();
        if (backfill is null)
        {
            stderr.WriteLine("metadata recompute-effective-dates: database is not configured. Set ConnectionStrings__Postgres and retry.");
            return 78; // EX_CONFIG
        }

        try
        {
            await backfill.RecomputeEffectiveDatesAsync(line => stdout.WriteLine(line));
            return 0;
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"metadata recompute-effective-dates: failed: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    // ---- subcommand: media derivatives backfill ----------------------------

    internal static async Task<int> MediaDerivativesBackfillAsync(
        string[] args,
        IServiceProvider services,
        TextWriter stdout,
        TextWriter stderr)
    {
        var backfill = services.GetService<MediaDerivativesBackfillService>();
        if (backfill is null)
        {
            stderr.WriteLine("media derivatives backfill: database is not configured. Set ConnectionStrings__Postgres and retry.");
            return 78; // EX_CONFIG
        }

        int? limit = null;
        var limitRaw = ReadOption(args, "--limit");
        if (limitRaw is not null)
        {
            if (!int.TryParse(limitRaw, out var n) || n <= 0)
            {
                stderr.WriteLine("media derivatives backfill: --limit must be a positive integer.");
                return 64;
            }
            limit = n;
        }

        // Slice 99: --retry-failed (alias --force-failed, and the legacy
        // --failed-only) re-attempts derivatives blocked by a prior diagnostic.
        var retryFailed = HasFlag(args, "--retry-failed")
            || HasFlag(args, "--force-failed")
            || HasFlag(args, "--failed-only");
        var options = new MediaDerivativesBackfillOptions
        {
            Limit = limit,
            MissingOnly = !retryFailed,
            FailedOnly = HasFlag(args, "--failed-only"),
            DryRun = HasFlag(args, "--dry-run"),
            RetryFailed = retryFailed,
        };

        try
        {
            if (retryFailed)
            {
                stdout.WriteLine("media derivatives backfill: retry-failed — re-attempting previously-failed derivatives.");
            }
            // Numbers-only progress lines — never file names / paths / metadata.
            await backfill.RunAsync(options, line => stdout.WriteLine(line));
            return 0;
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"media derivatives backfill: failed: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    // ---- subcommand: media derivatives failures -----------------------------

    // Slice 99: aggregate, sanitized view of WHY derivatives are missing. Counts
    // only — never a file name, path, storage key, id, or raw metadata.
    internal static async Task<int> MediaDerivativesFailuresAsync(
        IServiceProvider services,
        TextWriter stdout,
        TextWriter stderr)
    {
        var service = services.GetService<DerivativeDiagnosticsService>();
        if (service is null)
        {
            stderr.WriteLine("media derivatives failures: database is not configured. Set ConnectionStrings__Postgres and retry.");
            return 78; // EX_CONFIG
        }

        try
        {
            // Supersede any diagnostics whose derivative now exists, then report.
            await service.PruneResolvedAsync();
            var summary = await service.SummariseAsync();
            if (summary.Sizes.Count == 0)
            {
                stdout.WriteLine("media derivatives failures: no recorded diagnostics. "
                    + "Missing derivatives (if any) have not been attempted by a backfill yet.");
                return 0;
            }

            stdout.WriteLine("media derivatives failures: by size / status / code (counts only).");
            foreach (var s in summary.Sizes)
            {
                stdout.WriteLine(
                    $"  {s.Size}: total={s.Total} failed_permanent={s.FailedPermanent} "
                    + $"failed_transient={s.FailedTransient} not_eligible={s.NotEligible} "
                    + $"skipped={s.Skipped} pending={s.Pending} retryable_now={s.RetryableNow}"
                    + (s.LastFailureAt is { } at ? $" last_failure={at:O}" : ""));
                foreach (var c in s.ByErrorCode)
                {
                    stdout.WriteLine($"    code {c.ErrorCode}={c.Count}");
                }
                foreach (var f in s.TopFormats)
                {
                    stdout.WriteLine($"    format {f.DetectedContentType}={f.Count}");
                }
            }
            stdout.WriteLine(
                "media derivatives failures: retry transient/permanent with "
                + "`media derivatives backfill --retry-failed`.");
            return 0;
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"media derivatives failures: failed: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    // ---- subcommand: media derivatives benchmark ----------------------------

    // Slice 100: read-only backend comparison on real library images. Renders
    // small+medium with each available backend in memory (nothing stored) and
    // reports aggregate timings + the vips/ImageSharp speedup. Counts/ms only.
    internal static async Task<int> MediaDerivativesBenchmarkAsync(
        string[] args,
        IServiceProvider services,
        TextWriter stdout,
        TextWriter stderr)
    {
        var service = services.GetService<DerivativeBenchmarkService>();
        if (service is null)
        {
            stderr.WriteLine("media derivatives benchmark: database is not configured. Set ConnectionStrings__Postgres and retry.");
            return 78; // EX_CONFIG
        }

        var limit = 50;
        var limitRaw = ReadOption(args, "--limit");
        if (limitRaw is not null)
        {
            if (!int.TryParse(limitRaw, out var n) || n <= 0)
            {
                stderr.WriteLine("media derivatives benchmark: --limit must be a positive integer.");
                return 64;
            }
            limit = n;
        }

        try
        {
            var result = await service.RunAsync(limit, line => stdout.WriteLine(line));
            if (result.SampledImages == 0)
            {
                stdout.WriteLine("media derivatives benchmark: no readable image sources sampled; nothing to compare.");
                return 0;
            }

            WriteBackendBenchmark(stdout, result.ImageSharp);
            if (result.Vips is { } vips)
            {
                WriteBackendBenchmark(stdout, vips);
                if (result.Speedup is { } s)
                {
                    stdout.WriteLine(
                        $"media derivatives benchmark: vips speedup = {s:0.00}x vs imagesharp "
                        + "(per-image average, small+medium).");
                }
            }
            else
            {
                stdout.WriteLine($"media derivatives benchmark: vips unavailable ({result.VipsUnavailableReason}); imagesharp only.");
            }
            return 0;
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"media derivatives benchmark: failed: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static void WriteBackendBenchmark(TextWriter stdout, BackendBenchmark b)
    {
        stdout.WriteLine(
            $"  {b.Name}: images={b.Images} failed={b.Failed} total_ms={b.TotalMillis} "
            + $"avg_ms={b.AvgMillis:0.0} output_bytes={b.TotalOutputBytes}");
    }

    // ---- subcommand: media derivatives verify-bytes / repair-bytes ----------

    // Slice 96: physical-placement audit for derived artifacts. The union-based
    // integrity scan treats bytes in EITHER root as present; the serving
    // endpoints read derived bytes ONLY from the derived root. verify-bytes
    // reports the gap; repair-bytes closes it by streaming bytes across roots
    // (no decode, no DB writes). Numbers-only output.
    internal static async Task<int> MediaDerivativesVerifyBytesAsync(
        string[] args,
        IServiceProvider services,
        TextWriter stdout,
        TextWriter stderr)
    {
        var service = services.GetService<MediaDerivativeBytesService>();
        if (service is null)
        {
            stderr.WriteLine("media derivatives verify-bytes: database is not configured. Set ConnectionStrings__Postgres and retry.");
            return 78; // EX_CONFIG
        }

        if (ParseBytesOptions("media derivatives verify-bytes", args, stderr) is not { } options)
        {
            return 64;
        }

        try
        {
            var result = await service.VerifyAsync(options, line => stdout.WriteLine(line));
            stdout.WriteLine(
                $"media derivatives verify-bytes: checked={result.Checked} "
                + $"present_in_derived_root={result.PresentInDerivedRoot} "
                + $"only_in_original_root={result.OnlyInOriginalRoot} "
                + $"missing_from_both={result.MissingFromBoth} "
                + $"bytes_copyable={result.BytesCopyable} elapsed_ms={result.ElapsedMillis}");
            WriteSizeCounts(stdout, "small", result.Small);
            WriteSizeCounts(stdout, "medium", result.Medium);
            WriteSizeCounts(stdout, "poster", result.Poster);
            if (result.OnlyInOriginalRoot > 0)
            {
                stdout.WriteLine(
                    "media derivatives verify-bytes: artifacts found only in the original root — "
                    + "run `media derivatives repair-bytes` to copy them into the derived root (no decode).");
            }
            return 0;
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"media derivatives verify-bytes: failed: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    internal static async Task<int> MediaDerivativesRepairBytesAsync(
        string[] args,
        IServiceProvider services,
        TextWriter stdout,
        TextWriter stderr)
    {
        var service = services.GetService<MediaDerivativeBytesService>();
        if (service is null)
        {
            stderr.WriteLine("media derivatives repair-bytes: database is not configured. Set ConnectionStrings__Postgres and retry.");
            return 78; // EX_CONFIG
        }

        if (ParseBytesOptions("media derivatives repair-bytes", args, stderr) is not { } parsed)
        {
            return 64;
        }
        var options = parsed with { RegenerateMissing = HasFlag(args, "--regenerate-missing") };

        try
        {
            if (options.DryRun)
            {
                stdout.WriteLine("media derivatives repair-bytes: dry-run — counts show what WOULD happen; nothing is written.");
            }
            if (options.RegenerateMissing && options.DryRun)
            {
                stdout.WriteLine("media derivatives repair-bytes: --regenerate-missing is ignored in a dry run.");
            }
            var result = await service.RepairAsync(options, line => stdout.WriteLine(line));
            stdout.WriteLine(
                $"media derivatives repair-bytes: checked={result.Checked} "
                + $"skipped_present_in_derived_root={result.SkippedPresentInDerivedRoot} "
                + $"copied_from_original_root={result.CopiedFromOriginalRoot} "
                + $"missing_from_both={result.MissingFromBoth} "
                + $"regenerated={result.Regenerated} failed={result.Failed} "
                + $"bytes_copied={result.BytesCopied} elapsed_ms={result.ElapsedMillis}"
                + (result.DryRun ? " (dry-run)" : ""));
            WriteSizeCounts(stdout, "small", result.Small);
            WriteSizeCounts(stdout, "medium", result.Medium);
            WriteSizeCounts(stdout, "poster", result.Poster);
            if (result.MissingFromBoth > 0 && !options.RegenerateMissing)
            {
                stdout.WriteLine(
                    "media derivatives repair-bytes: artifacts missing from both roots were left "
                    + "unchanged — re-run with --regenerate-missing to rebuild them.");
            }
            return 0;
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"media derivatives repair-bytes: failed: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static void WriteSizeCounts(TextWriter stdout, string label, DerivativeBytesSizeCounts counts)
    {
        stdout.WriteLine(
            $"  {label}: checked={counts.Checked} present={counts.PresentInDerivedRoot} "
            + $"only_original={counts.OnlyInOriginalRoot} missing={counts.MissingFromBoth}");
    }

    // Shared --size/--limit/--dry-run parsing for the two bytes subcommands.
    // Returns null (after printing a usage error) when the input is invalid.
    private static MediaDerivativeBytesOptions? ParseBytesOptions(
        string command, string[] args, TextWriter stderr)
    {
        int? limit = null;
        var limitRaw = ReadOption(args, "--limit");
        if (limitRaw is not null)
        {
            if (!int.TryParse(limitRaw, out var n) || n <= 0)
            {
                stderr.WriteLine($"{command}: --limit must be a positive integer.");
                return null;
            }
            limit = n;
        }

        string? size = ReadOption(args, "--size");
        if (size is not null && !ThumbnailSizes.IsKnown(size))
        {
            stderr.WriteLine($"{command}: --size must be one of: small, medium, poster.");
            return null;
        }

        return new MediaDerivativeBytesOptions
        {
            Size = size,
            Limit = limit,
            DryRun = HasFlag(args, "--dry-run"),
        };
    }

    // ---- subcommand: media posters regenerate -------------------------------

    // Slice 95: replaces existing poster rows so a later-enabled real provider
    // (Media__VideoPosterProvider=ffmpeg) can supersede synthetic placeholders.
    // Default scope: only posters recorded as synthetic; --force redoes all
    // posters (including pre-provenance rows). Numbers-only output.
    internal static async Task<int> MediaPostersRegenerateAsync(
        string[] args,
        IServiceProvider services,
        TextWriter stdout,
        TextWriter stderr)
    {
        var regeneration = services.GetService<PosterRegenerationService>();
        if (regeneration is null)
        {
            stderr.WriteLine("media posters regenerate: database is not configured. Set ConnectionStrings__Postgres and retry.");
            return 78; // EX_CONFIG
        }

        int? limit = null;
        var limitRaw = ReadOption(args, "--limit");
        if (limitRaw is not null)
        {
            if (!int.TryParse(limitRaw, out var n) || n <= 0)
            {
                stderr.WriteLine("media posters regenerate: --limit must be a positive integer.");
                return 64;
            }
            limit = n;
        }

        var force = HasFlag(args, "--force");
        if (force && HasFlag(args, "--only-synthetic"))
        {
            stderr.WriteLine("media posters regenerate: --force and --only-synthetic are mutually exclusive.");
            return 64;
        }

        var options = new PosterRegenerationOptions
        {
            Force = force,
            DryRun = HasFlag(args, "--dry-run"),
            Limit = limit,
        };

        try
        {
            if (!force)
            {
                stdout.WriteLine("media posters regenerate: scope = synthetic posters only (use --force for all).");
            }
            await regeneration.RunAsync(options, line => stdout.WriteLine(line));
            return 0;
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"media posters regenerate: failed: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    // ---- subcommand: media file-status --------------------------------------

    // Owner-derived, single-file post-ingestion verification: metadata status,
    // derivative presence (small/medium/poster), and AI embedding presence for
    // the active profile. Counts/flags + the (non-secret) profile key only —
    // NEVER SHA / BlobObjectId / StorageKey / path / raw vector / raw metadata.
    internal static async Task<int> MediaFileStatusAsync(
        string[] args,
        IServiceProvider services,
        TextWriter stdout,
        TextWriter stderr)
    {
        var fileRaw = ReadOption(args, "--file");
        if (string.IsNullOrWhiteSpace(fileRaw) || !Guid.TryParse(fileRaw, out var fileId))
        {
            stderr.WriteLine("media file-status: --file <file-id> (a GUID) is required.");
            return 64;
        }

        var db = services.GetService<AppDbContext>();
        if (db is null)
        {
            stderr.WriteLine("media file-status: database is not configured. Set ConnectionStrings__Postgres and retry.");
            return 78;
        }

        // Default query filter excludes Private Vault content: a vaulted (or
        // deleted / non-existent) file resolves to null — no leak, no diagnosis.
        var file = await db.FileItems.AsNoTracking()
            .Where(f => f.Id == fileId && f.DeletedAt == null)
            .Select(f => new { f.BlobObjectId })
            .FirstOrDefaultAsync();
        if (file is null)
        {
            stdout.WriteLine("media file-status: file not found in the normal library (missing, deleted, or Private Vault).");
            return 0;
        }

        var blobId = file.BlobObjectId;
        var meta = await db.BlobMetadata.AsNoTracking()
            .Where(m => m.BlobObjectId == blobId)
            .Select(m => new { m.MediaCategory, m.ExtractionStatus })
            .FirstOrDefaultAsync();

        var category = meta?.MediaCategory ?? "unknown";
        var isVideo = category == MediaCategories.Video;

        bool HasSize(string size) => db.FileThumbnails.AsNoTracking()
            .Any(t => t.FileItemId == fileId && t.Size == size);
        var small = HasSize(ThumbnailSizes.Small);
        var medium = HasSize(ThumbnailSizes.Medium);
        var poster = isVideo ? (bool?)HasSize(ThumbnailSizes.Poster) : null;

        // AI: resolve the active photo profile (stable key only) and report
        // whether a canonical embedding row exists for it.
        string profileLabel = "<none>";
        var usable = false;
        bool? embeddingPresent = null;
        var profileService = services.GetService<PhotoEmbeddingProfileService>();
        if (profileService is not null)
        {
            var resolution = await profileService.ResolveActiveProfileAsync(null, default);
            usable = resolution.Usable;
            if (resolution.Profile is { } profile)
            {
                profileLabel = profile.Key;
                if (category == MediaCategories.Image)
                {
                    embeddingPresent = await db.BlobEmbeddings.AsNoTracking()
                        .AnyAsync(e => e.BlobObjectId == blobId && e.ProfileId == profile.Id);
                }
            }
        }

        stdout.WriteLine("media file-status:");
        stdout.WriteLine($"  media_category      = {category}");
        stdout.WriteLine($"  metadata_extraction = {meta?.ExtractionStatus ?? "missing"}");
        stdout.WriteLine($"  thumbnail_small     = {(small ? "present" : "absent")}");
        stdout.WriteLine($"  preview_medium      = {(medium ? "present" : "absent")}");
        stdout.WriteLine($"  poster              = {(poster is null ? "n/a" : poster.Value ? "present" : "absent")}");
        stdout.WriteLine($"  ai_profile          = {profileLabel} (usable={usable.ToString().ToLowerInvariant()})");
        stdout.WriteLine($"  ai_embedding        = {(embeddingPresent is null ? "n/a" : embeddingPresent.Value ? "present" : "absent")}");
        return 0;
    }

    // ---- subcommand: storage blobs audit/repair-references ------------------

    // Slice 97: logical reference-count integrity (BlobObject.ReferenceCount
    // vs the actual owner rows). Counts only — never keys, ids, or paths.
    internal static async Task<int> StorageBlobsAuditReferencesAsync(
        IServiceProvider services,
        TextWriter stdout,
        TextWriter stderr)
    {
        var service = services.GetService<BlobReferenceAuditService>();
        if (service is null)
        {
            stderr.WriteLine("storage blobs audit-references: database is not configured. Set ConnectionStrings__Postgres and retry.");
            return 78; // EX_CONFIG
        }

        try
        {
            var report = await service.AuditAsync();
            stdout.WriteLine(
                $"storage blobs audit-references: total_blobs={report.TotalBlobs} "
                + $"matched_reference_count={report.MatchedReferenceCount} "
                + $"db_refcount_too_high={report.DbRefcountTooHigh} "
                + $"db_refcount_too_low={report.DbRefcountTooLow} "
                + $"orphaned_nonzero_refcount={report.OrphanedNonzeroRefcount} "
                + $"zero_ref_with_real_references={report.ZeroRefWithRealReferences} "
                + $"total_db_references={report.TotalDbReferences} "
                + $"total_computed_references={report.TotalComputedReferences}");
            if (report.DbRefcountTooHigh + report.DbRefcountTooLow > 0)
            {
                stdout.WriteLine(
                    "storage blobs audit-references: mismatches found — run "
                    + "`storage blobs repair-references --dry-run` then without --dry-run to fix.");
            }
            return 0;
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"storage blobs audit-references: failed: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    internal static async Task<int> StorageBlobsRepairReferencesAsync(
        string[] args,
        IServiceProvider services,
        TextWriter stdout,
        TextWriter stderr)
    {
        var service = services.GetService<BlobReferenceAuditService>();
        if (service is null)
        {
            stderr.WriteLine("storage blobs repair-references: database is not configured. Set ConnectionStrings__Postgres and retry.");
            return 78; // EX_CONFIG
        }

        var dryRun = HasFlag(args, "--dry-run");
        try
        {
            var result = await service.RepairAsync(dryRun);
            stdout.WriteLine(
                $"storage blobs repair-references: total_blobs={result.TotalBlobs} "
                + $"mismatched={result.Mismatched} repaired={result.Repaired} "
                + $"skipped_concurrent_change={result.SkippedConcurrentChange}"
                + (result.DryRun ? " (dry-run)" : ""));
            if (!dryRun && result.Repaired > 0)
            {
                stdout.WriteLine(
                    "storage blobs repair-references: corrected blobs now at ReferenceCount=0 "
                    + "are reclaimed by the blob janitor under its normal grace rules — "
                    + "no physical bytes were touched by this command.");
            }
            return 0;
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"storage blobs repair-references: failed: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    // ---- subcommand: storage reconcile ------------------------------------

    internal static async Task<int> StorageReconcileAsync(
        string[] args,
        IServiceProvider services,
        TextWriter stdout,
        TextWriter stderr)
    {
        var reconcile = services.GetService<StorageReconciliationService>();
        if (reconcile is null)
        {
            stderr.WriteLine("storage reconcile: database is not configured. Set ConnectionStrings__Postgres and retry.");
            return 78; // EX_CONFIG
        }

        int? limit = null;
        var limitRaw = ReadOption(args, "--limit");
        if (limitRaw is not null)
        {
            if (!int.TryParse(limitRaw, out var n) || n <= 0)
            {
                stderr.WriteLine("storage reconcile: --limit must be a positive integer.");
                return 64;
            }
            limit = n;
        }

        var deleteOrphans = HasFlag(args, "--delete-orphans");
        // Dry-run is the default. Destructive deletion needs an explicit
        // opt-out of dry-run via --delete-orphans (which implies "do it").
        var dryRun = !deleteOrphans || HasFlag(args, "--dry-run");

        var options = new StorageReconciliationOptions
        {
            DryRun = dryRun,
            DeleteOrphans = deleteOrphans,
            Limit = limit,
        };

        try
        {
            // The service logs counts only — never a storage key or a path.
            await reconcile.RunAsync(options, line => stdout.WriteLine(line));
            return 0;
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"storage reconcile: failed: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    // ---- subcommand: jobs -------------------------------------------------

    internal static async Task<int> JobsEnqueueAsync(
        string[] args,
        IServiceProvider services,
        TextWriter stdout,
        TextWriter stderr)
    {
        var queue = services.GetService<IJobQueue>();
        if (queue is null)
        {
            stderr.WriteLine("jobs enqueue: database is not configured. Set ConnectionStrings__Postgres and retry.");
            return 78;
        }

        if (args.Length == 0)
        {
            stderr.WriteLine("jobs enqueue: missing job name. One of: metadata-backfill, metadata-video-backfill, media-derivatives-backfill, media-gallery-derivatives-regenerate, media-posters-regenerate, media-video-hls-generate, media-video-hls-backfill, storage-reconcile, ai-photos-embeddings-backfill, ai-documents-extract-backfill, ai-documents-embeddings-backfill, ai-faces-detect-backfill, ai-faces-embeddings-backfill, ai-faces-cluster-backfill, ai-tags-generate-backfill.");
            return 64;
        }

        var jobName = args[0];
        var rest = args[1..];

        int? limit = null;
        var limitRaw = ReadOption(rest, "--limit");
        if (limitRaw is not null)
        {
            if (!int.TryParse(limitRaw, out var n) || n <= 0)
            {
                stderr.WriteLine("jobs enqueue: --limit must be a positive integer.");
                return 64;
            }
            limit = n;
        }

        // Optional profile stable key for AI backfills (never a GUID/path).
        var profileKey = ReadOption(rest, "--profile");

        // AI skeleton backfills (Phase 0C) all share one flag-only payload and an
        // idempotency key (job type + profile) so duplicate pending enqueues
        // collapse. Map the kebab CLI name to its dotted job type.
        var aiJobTypes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ai-photos-embeddings-backfill"] = JobTypes.AiPhotosEmbeddingsBackfill,
            ["ai-documents-extract-backfill"] = JobTypes.AiDocumentsExtractBackfill,
            ["ai-documents-embeddings-backfill"] = JobTypes.AiDocumentsEmbeddingsBackfill,
            ["ai-faces-detect-backfill"] = JobTypes.AiFacesDetectBackfill,
            ["ai-faces-embeddings-backfill"] = JobTypes.AiFacesEmbeddingsBackfill,
            ["ai-faces-cluster-backfill"] = JobTypes.AiFacesClusterBackfill,
            ["ai-tags-generate-backfill"] = JobTypes.AiTagsGenerateBackfill,
        };

        try
        {
            if (aiJobTypes.TryGetValue(jobName, out var aiJobType))
            {
                var payload = new AiBackfillJobPayload(
                    ProfileKey: profileKey,
                    Limit: limit,
                    DryRun: HasFlag(rest, "--dry-run"));
                var job = await queue.EnqueueAsync(
                    aiJobType, payload,
                    idempotencyKey: $"{aiJobType}:{profileKey ?? "default"}");
                stdout.WriteLine($"jobs enqueue: queued {aiJobType} ({job.Id:N}).");
                return 0;
            }

            switch (jobName)
            {
                case "metadata-backfill":
                {
                    var payload = new MetadataBackfillJobPayload(
                        Limit: limit,
                        FailedOnly: HasFlag(rest, "--failed-only"),
                        DryRun: HasFlag(rest, "--dry-run"));
                    var job = await queue.EnqueueAsync(
                        JobTypes.MetadataEmbeddedBackfill, payload,
                        idempotencyKey: JobTypes.MetadataEmbeddedBackfill);
                    stdout.WriteLine($"jobs enqueue: queued {JobTypes.MetadataEmbeddedBackfill} ({job.Id:N}).");
                    return 0;
                }
                case "media-derivatives-backfill":
                {
                    var retryFailed = HasFlag(rest, "--retry-failed")
                        || HasFlag(rest, "--force-failed")
                        || HasFlag(rest, "--failed-only");
                    var payload = new MediaDerivativesBackfillJobPayload(
                        Limit: limit,
                        MissingOnly: !retryFailed,
                        FailedOnly: HasFlag(rest, "--failed-only"),
                        DryRun: HasFlag(rest, "--dry-run"),
                        RetryFailed: retryFailed);
                    var job = await queue.EnqueueAsync(
                        JobTypes.MediaDerivativesBackfill, payload,
                        idempotencyKey: JobTypes.MediaDerivativesBackfill);
                    stdout.WriteLine($"jobs enqueue: queued {JobTypes.MediaDerivativesBackfill} ({job.Id:N}).");
                    return 0;
                }
                case "media-gallery-derivatives-regenerate":
                {
                    int? batchSize = null;
                    var batchRaw = ReadOption(rest, "--batch-size");
                    if (batchRaw is not null
                        && (!int.TryParse(batchRaw, out var parsedBatch) || parsedBatch <= 0))
                    {
                        stderr.WriteLine("jobs enqueue: --batch-size must be a positive integer.");
                        return 64;
                    }
                    if (batchRaw is not null)
                    {
                        batchSize = int.Parse(batchRaw);
                    }

                    var sizesRaw = ReadOption(rest, "--sizes");
                    var sizes = sizesRaw is null
                        ? new[]
                        {
                            ThumbnailSizes.Small,
                            ThumbnailSizes.Poster,
                            ThumbnailSizes.VideoPreviewStrip,
                        }
                        : sizesRaw.Split(',', StringSplitOptions.RemoveEmptyEntries
                            | StringSplitOptions.TrimEntries);
                    var payload = new GalleryDerivativesRegenerationJobPayload(
                        Sizes: sizes,
                        Force: HasFlag(rest, "--force"),
                        DryRun: HasFlag(rest, "--dry-run"),
                        Limit: limit,
                        BatchSize: batchSize);
                    var job = await queue.EnqueueAsync(
                        JobTypes.MediaGalleryDerivativesRegenerate,
                        payload,
                        idempotencyKey: JobTypes.MediaGalleryDerivativesRegenerate);
                    stdout.WriteLine(
                        $"jobs enqueue: queued {JobTypes.MediaGalleryDerivativesRegenerate} ({job.Id:N}).");
                    return 0;
                }
                case "metadata-video-backfill":
                {
                    var payload = new VideoMetadataBackfillJobPayload(
                        Limit: limit,
                        FailedOnly: HasFlag(rest, "--failed-only"),
                        DryRun: HasFlag(rest, "--dry-run"));
                    var job = await queue.EnqueueAsync(
                        JobTypes.MetadataVideoBackfill, payload,
                        idempotencyKey: JobTypes.MetadataVideoBackfill);
                    stdout.WriteLine($"jobs enqueue: queued {JobTypes.MetadataVideoBackfill} ({job.Id:N}).");
                    return 0;
                }
                case "media-posters-regenerate":
                {
                    var payload = new PosterRegenerationJobPayload(
                        Force: HasFlag(rest, "--force"),
                        Limit: limit,
                        DryRun: HasFlag(rest, "--dry-run"));
                    var job = await queue.EnqueueAsync(
                        JobTypes.MediaPostersRegenerate, payload,
                        idempotencyKey: JobTypes.MediaPostersRegenerate);
                    stdout.WriteLine($"jobs enqueue: queued {JobTypes.MediaPostersRegenerate} ({job.Id:N}).");
                    return 0;
                }
                case "media-video-hls-generate":
                {
                    // Video-hls slice 1: single-blob point work (manual
                    // pre-warming; the lazy playback path enqueues these too).
                    var blobRaw = ReadOption(rest, "--blob");
                    if (blobRaw is null || !Guid.TryParse(blobRaw, out var blobId))
                    {
                        stderr.WriteLine("jobs enqueue: media-video-hls-generate requires --blob <blob-object-id>.");
                        return 64;
                    }
                    var payload = new VideoHlsGenerateJobPayload(
                        BlobObjectId: blobId,
                        Force: HasFlag(rest, "--force"));
                    // Idempotency key collapses duplicate pending enqueues for
                    // the same blob (endpoint retries, repeated CLI calls).
                    var job = await queue.EnqueueAsync(
                        JobTypes.MediaVideoHlsGenerate, payload,
                        idempotencyKey: $"{JobTypes.MediaVideoHlsGenerate}:{blobId:N}");
                    stdout.WriteLine($"jobs enqueue: queued {JobTypes.MediaVideoHlsGenerate} ({job.Id:N}).");
                    return 0;
                }
                case "media-video-hls-backfill":
                {
                    // Admin console: bulk HLS pre-warm across eligible videos.
                    var retryFailed = HasFlag(rest, "--retry-failed") || HasFlag(rest, "--failed-only");
                    var payload = new VideoHlsBackfillJobPayload(
                        Limit: limit,
                        RetryFailed: retryFailed,
                        Force: HasFlag(rest, "--force"),
                        DryRun: HasFlag(rest, "--dry-run"));
                    var job = await queue.EnqueueAsync(
                        JobTypes.MediaVideoHlsBackfill, payload,
                        idempotencyKey: JobTypes.MediaVideoHlsBackfill);
                    stdout.WriteLine($"jobs enqueue: queued {JobTypes.MediaVideoHlsBackfill} ({job.Id:N}).");
                    return 0;
                }
                case "storage-reconcile":
                {
                    var deleteOrphans = HasFlag(rest, "--delete-orphans");
                    var payload = new StorageReconcileJobPayload(
                        Limit: limit,
                        DeleteOrphans: deleteOrphans,
                        DryRun: !deleteOrphans || HasFlag(rest, "--dry-run"));
                    var job = await queue.EnqueueAsync(
                        JobTypes.StorageReconcile, payload,
                        idempotencyKey: JobTypes.StorageReconcile);
                    stdout.WriteLine($"jobs enqueue: queued {JobTypes.StorageReconcile} ({job.Id:N}).");
                    return 0;
                }
                default:
                    stderr.WriteLine($"jobs enqueue: unknown job '{jobName}'. One of: metadata-backfill, metadata-video-backfill, media-derivatives-backfill, media-posters-regenerate, media-video-hls-generate, media-video-hls-backfill, storage-reconcile, ai-photos-embeddings-backfill, ai-documents-extract-backfill, ai-documents-embeddings-backfill, ai-faces-detect-backfill, ai-faces-embeddings-backfill, ai-faces-cluster-backfill, ai-tags-generate-backfill.");
                    return 64;
            }
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"jobs enqueue: failed: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    internal static async Task<int> JobsListAsync(
        string[] args,
        IServiceProvider services,
        TextWriter stdout,
        TextWriter stderr)
    {
        var queue = services.GetService<IJobQueue>();
        if (queue is null)
        {
            stderr.WriteLine("jobs list: database is not configured. Set ConnectionStrings__Postgres and retry.");
            return 78;
        }

        var snapshot = await queue.GetSnapshotAsync();
        stdout.WriteLine(
            $"jobs: queued={snapshot.Queued} running={snapshot.Running} "
            + $"succeeded={snapshot.Succeeded} failed={snapshot.Failed} cancelled={snapshot.Cancelled}");

        if (snapshot.Recent.Count == 0)
        {
            stdout.WriteLine("(no jobs)");
            return 0;
        }

        stdout.WriteLine("recent:");
        foreach (var j in snapshot.Recent)
        {
            // Never print PayloadJson. Id + type + status + attempts + error code only.
            var err = j.LastErrorCode is null ? "" : $" err={j.LastErrorCode}";
            stdout.WriteLine(
                $"  {j.Id:N}  {j.Type,-28}  {j.Status,-10}  {j.Attempts}/{j.MaxAttempts}{err}");
        }
        return 0;
    }

    internal static async Task<int> JobsRunOnceAsync(
        string[] args,
        IServiceProvider services,
        TextWriter stdout,
        TextWriter stderr)
    {
        var processor = services.GetService<JobProcessor>();
        if (processor is null)
        {
            stderr.WriteLine("jobs run-once: database is not configured. Set ConnectionStrings__Postgres and retry.");
            return 78;
        }

        var max = 10;
        var maxRaw = ReadOption(args, "--max");
        if (maxRaw is not null)
        {
            if (!int.TryParse(maxRaw, out var n) || n <= 0)
            {
                stderr.WriteLine("jobs run-once: --max must be a positive integer.");
                return 64;
            }
            max = n;
        }

        try
        {
            var processed = await processor.ProcessAvailableAsync(max, line => stdout.WriteLine(line));
            stdout.WriteLine($"jobs run-once: processed {processed} job(s).");
            return 0;
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"jobs run-once: failed: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    internal static async Task<int> JobsWorkerAsync(
        string[] args,
        IServiceProvider services,
        TextWriter stdout,
        TextWriter stderr)
    {
        var scopeFactory = services.GetService<IServiceScopeFactory>();
        if (scopeFactory is null || services.GetService<JobProcessor>() is null)
        {
            stderr.WriteLine("jobs worker: database is not configured. Set ConnectionStrings__Postgres and retry.");
            return 78;
        }

        var pollSeconds = 10;
        var pollRaw = ReadOption(args, "--poll-interval-seconds");
        if (pollRaw is not null)
        {
            if (!int.TryParse(pollRaw, out var n) || n <= 0)
            {
                stderr.WriteLine("jobs worker: --poll-interval-seconds must be a positive integer.");
                return 64;
            }
            pollSeconds = n;
        }

        // Ctrl+C stops the loop gracefully.
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        var jobsOptions = services.GetService<IOptions<JobsOptions>>()?.Value ?? new JobsOptions();
        var slots = Math.Clamp(jobsOptions.MaxConcurrentJobs, 1, 8);
        stdout.WriteLine(
            $"jobs worker: polling every {pollSeconds}s with {slots} slot(s). Press Ctrl+C to stop.");
        // Slice 97: configuration visibility for the classic field failure (a
        // worker container missing an options binding makes staging imports
        // fail with "Staging storage is not configured" despite a correct
        // environment). Flags only — never a path or secret.
        var stagingOpts = services.GetService<IOptions<StagingOptions>>()?.Value;
        var storageOpts = services.GetService<IOptions<BlobStorageOptions>>()?.Value;
        var stagingConfigured = stagingOpts is { Enabled: true }
            && !string.IsNullOrWhiteSpace(stagingOpts.RootPath);
        var derivedSplit = storageOpts is not null && !string.Equals(
            storageOpts.EffectiveDerivedRootPath, storageOpts.RootPath, StringComparison.Ordinal);
        stdout.WriteLine(
            $"jobs worker: staging_configured={stagingConfigured.ToString().ToLowerInvariant()} "
            + $"derived_root_split={derivedSplit.ToString().ToLowerInvariant()}");

        // SigLIP direct milestone: the worker hosts the SigLIP2 IMAGE tower, so in
        // openvino-direct mode compile + synthetic-validate it BEFORE accepting
        // jobs (the factory cache keeps it warm for the first real job). Failure is
        // logged with a sanitized code and does NOT stop the worker: AI jobs no-op
        // via the embedder's compile-backed readiness (provider unavailable is an
        // environment state, never a content failure), while non-AI jobs
        // (imports, derivatives, …) keep running.
        PreloadDirectPhotoImage(services, stdout, stderr);

        var outputGate = new object();
        void WriteLineSafe(TextWriter writer, string line)
        {
            lock (outputGate) writer.WriteLine(line);
        }

        try
        {
            await JobWorker.RunWorkerSlotsAsync(slots, RunSlotAsync, cts.Token);

            async Task RunSlotAsync(int slot, CancellationToken cancellationToken)
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        // A fresh scope per pass keeps AppDbContext and every
                        // handler isolated between slots and bounds tracking in
                        // this long-running CLI process.
                        using var scope = scopeFactory.CreateScope();
                        var processor = scope.ServiceProvider.GetRequiredService<JobProcessor>();
                        var processed = await processor.ProcessAvailableAsync(
                            10, line => WriteLineSafe(stdout, line), cancellationToken);
                        if (processed == 0)
                        {
                            await Task.Delay(TimeSpan.FromSeconds(pollSeconds), cancellationToken);
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        WriteLineSafe(stderr,
                            $"jobs worker: slot {slot} failed: {ex.GetType().Name}.");
                        await Task.Delay(TimeSpan.FromSeconds(pollSeconds), cancellationToken);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // graceful shutdown
        }
        stdout.WriteLine("jobs worker: stopped.");
        return 0;
    }

    // ---- helpers ----------------------------------------------------------

    // Worker-host preload of the SigLIP2 IMAGE tower (openvino-direct only).
    // Bounded synthetic integrity check through the SAME factory cache the
    // backfill jobs use; sanitized codes only, never fatal for the worker.
    internal static void PreloadDirectPhotoImage(
        IServiceProvider services, TextWriter stdout, TextWriter stderr)
    {
        var options = services.GetService<IOptions<AiOptions>>()?.Value;
        var factory = services.GetService<NubArca.Api.Ai.Onnx.IOnnxInferenceSessionFactory>();
        if (options is null || factory is null) return;
        if (OnnxExecutionProviders.Normalize(options.Onnx.ExecutionProvider)
            != OnnxExecutionProviders.OpenVinoDirect)
        {
            return;
        }
        if (!options.Enabled || !options.ImageEmbeddingsEnabled) return;
        var profileKey = options.PhotoSimilarityProfileKey;
        var config = string.IsNullOrWhiteSpace(profileKey)
            ? null
            : OnnxImageModels.ResolveConfig(configHash: null, profileKey: profileKey!);
        if (config is null) return;

        var modelDir = options.Onnx.ModelDir;
        var modelPath = string.IsNullOrWhiteSpace(modelDir)
            ? null
            : Path.Combine(modelDir, config.ModelSubdir, config.ModelFile);
        if (modelPath is null || !File.Exists(modelPath))
        {
            stderr.WriteLine(
                $"jobs worker: photo-image preload FAILED code={FacePreloadFailureCodes.PhotoImageModelMissing}. "
                + "AI photo jobs will no-op until resolved.");
            return;
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            factory.EnsureNativeProviderInitialized();
            using var lease = factory.Acquire(new NubArca.Api.Ai.Onnx.OnnxModelSpec(
                NubArca.Api.Ai.Onnx.OnnxModel.PhotoImage, modelPath));
            var session = lease.Session;
            var chw = OnnxFacePreloadService.BuildSyntheticChw(config.InputSize);
            var tensor = new Microsoft.ML.OnnxRuntime.Tensors.DenseTensor<float>(
                chw, new[] { 1, 3, config.InputSize, config.InputSize });
            var outputs = session.Run(new[]
            {
                Microsoft.ML.OnnxRuntime.NamedOnnxValue.CreateFromTensor(config.InputTensor, tensor),
            });
            if (!OnnxFacePreloadService.ValidatePhotoImage(outputs, config, out var reason))
            {
                stderr.WriteLine(
                    $"jobs worker: photo-image preload FAILED code={FacePreloadFailureCodes.PhotoImageValidationFailed} "
                    + $"({reason}). AI photo jobs will no-op until resolved.");
                return;
            }

            stdout.WriteLine(
                $"jobs worker: photo-image preload READY in {sw.ElapsedMilliseconds}ms "
                + $"(device={options.Onnx.OpenVino.PhotoImageDevice.Trim().ToUpperInvariant()}).");
        }
        catch (NubArca.Api.Ai.Onnx.OnnxSessionUnavailableException ex)
        {
            stderr.WriteLine(
                $"jobs worker: photo-image preload FAILED code={FacePreloadFailureCodes.PhotoImageCompileFailed} "
                + $"({ex.ReasonCode}). AI photo jobs will no-op until resolved.");
        }
        catch (Exception ex)
        {
            stderr.WriteLine(
                $"jobs worker: photo-image preload FAILED code={FacePreloadFailureCodes.PhotoImageCompileFailed} "
                + $"({ex.GetType().Name}). AI photo jobs will no-op until resolved.");
        }
    }

    // ---- subcommand: plates models validate -------------------------------
    // Sanitized, DB-free validation of the Plates model providers/config. Prints
    // provider, profile key, model KINDs, input sizes, model BASENAMES (never
    // absolute paths), presence booleans, and a ready/unavailable verdict with a
    // safe reason code. No inference, no persistence, no path/secret leakage.
    internal static Task<int> PlatesModelsValidateAsync(
        string[] args, IServiceProvider services, TextWriter stdout, TextWriter stderr)
    {
        var target = args.Length > 0 && !args[0].StartsWith('-') ? args[0].ToLowerInvariant() : "all";
        if (target is not ("all" or "alpr" or "face-redaction"))
        {
            stderr.WriteLine("usage: plates models validate [alpr|face-redaction]");
            return Task.FromResult(64);
        }

        var alpr = services.GetRequiredService<IOptions<NubArca.Api.Plates.PlatesAlprOptions>>().Value;
        var face = services.GetRequiredService<IOptions<NubArca.Api.Plates.PlatesFaceRedactionOptions>>().Value;

        if (target is "all" or "alpr")
        {
            ValidateAlpr(alpr, stdout);
        }
        if (target is "all" or "face-redaction")
        {
            ValidateFaceRedaction(face, stdout);
        }
        return Task.FromResult(0);
    }

    private static void ValidateAlpr(NubArca.Api.Plates.PlatesAlprOptions o, TextWriter stdout)
    {
        var provider = o.ResolveProvider();
        stdout.WriteLine("ALPR:");
        stdout.WriteLine($"  provider: {provider}");
        stdout.WriteLine($"  profileKey: {o.ProfileKey}");
        switch (provider)
        {
            case NubArca.Api.Plates.PlateAlprProvider.Disabled:
                stdout.WriteLine("  status: disabled");
                break;
            case NubArca.Api.Plates.PlateAlprProvider.DeterministicDev:
                stdout.WriteLine("  status: ready (deterministic dev/test — non-semantic, not for production)");
                break;
            case NubArca.Api.Plates.PlateAlprProvider.Onnx:
                var detPresent = !string.IsNullOrWhiteSpace(o.DetectorModelPath) && File.Exists(o.DetectorModelPath);
                var ocrPresent = !string.IsNullOrWhiteSpace(o.OcrModelPath) && File.Exists(o.OcrModelPath);
                stdout.WriteLine($"  detector: kind={o.DetectorModelKind} input={o.DetectorInputWidth}x{o.DetectorInputHeight} model={Basename(o.DetectorModelPath)} present={detPresent}");
                stdout.WriteLine($"  ocr: kind={o.OcrModelKind} input={o.OcrInputWidth}x{o.OcrInputHeight} alphabetLen={o.OcrAlphabet.Length} model={Basename(o.OcrModelPath)} present={ocrPresent}");
                stdout.WriteLine(
                    !detPresent ? $"  status: unavailable ({PlateAnalysisErrorCodes.DetectorModelMissing})"
                    : !ocrPresent ? $"  status: unavailable ({PlateAnalysisErrorCodes.OcrModelMissing})"
                    : "  status: ready");
                break;
        }
    }

    private static void ValidateFaceRedaction(
        NubArca.Api.Plates.PlatesFaceRedactionOptions o, TextWriter stdout)
    {
        var provider = o.ResolveProvider();
        stdout.WriteLine("FaceRedaction:");
        stdout.WriteLine($"  enabled: {o.Enabled}");
        stdout.WriteLine($"  provider: {provider}");
        stdout.WriteLine($"  profileKey: {o.ProfileKey}");
        switch (provider)
        {
            case NubArca.Api.Plates.PlateFaceRedactionProvider.Disabled:
                stdout.WriteLine("  status: disabled");
                break;
            case NubArca.Api.Plates.PlateFaceRedactionProvider.DeterministicDev:
                stdout.WriteLine("  status: ready (deterministic dev/test — non-semantic, not for production)");
                break;
            case NubArca.Api.Plates.PlateFaceRedactionProvider.ExistingNubArcaFaceDetector:
                var key = string.IsNullOrWhiteSpace(o.ExistingDetectorProfileKey)
                    ? "(capability-default)" : o.ExistingDetectorProfileKey;
                stdout.WriteLine($"  existingDetectorProfileKey: {key}");
                stdout.WriteLine("  status: configured (reuses the AI face-box detector — boxes only; verify the face model with `ai diagnostics`)");
                break;
            case NubArca.Api.Plates.PlateFaceRedactionProvider.OnnxDedicatedFaceDetector:
                stdout.WriteLine($"  detector: model={Basename(o.DetectorModelPath)} present={(!string.IsNullOrWhiteSpace(o.DetectorModelPath) && File.Exists(o.DetectorModelPath))}");
                stdout.WriteLine("  status: not implemented in this build (dedicated ONNX face detector is a future provider)");
                break;
        }
        if (!o.Enabled)
        {
            stdout.WriteLine($"  note: master switch Enabled=false — blurFaces=true returns {NubArca.Api.Plates.Redaction.PlateFaceRedactionUnavailableException.Code}");
        }
    }

    // ---- subcommand: plates benchmark --------------------------------------
    // Runs the configured ALPR pipeline or face-box detector on a LOCAL image
    // file, reporting sanitized timings + counts. It never creates a PlateImage,
    // FileItem, or any DB record — it operates on the file bytes in memory only.
    internal static async Task<int> PlatesBenchmarkAsync(
        string[] args, IServiceProvider services, TextWriter stdout, TextWriter stderr)
    {
        var which = args.Length > 0 && !args[0].StartsWith('-') ? args[0].ToLowerInvariant() : "alpr";
        if (which is not ("alpr" or "face-redaction"))
        {
            stderr.WriteLine("usage: plates benchmark <alpr|face-redaction> --image <path> [--runs N]");
            return 64;
        }
        var imagePath = ReadOption(args, "--image");
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
        {
            stderr.WriteLine("plates benchmark: --image <path> is required and must exist");
            return 64;
        }
        var runs = int.TryParse(ReadOption(args, "--runs"), out var r) && r > 0 ? r : 3;
        var bytes = await File.ReadAllBytesAsync(imagePath);

        if (which == "alpr")
        {
            return await BenchmarkAlprAsync(bytes, runs, services, stdout, stderr);
        }
        return await BenchmarkFaceRedactionAsync(bytes, runs, services, stdout, stderr);
    }

    private static async Task<int> BenchmarkAlprAsync(
        byte[] bytes, int runs, IServiceProvider services, TextWriter stdout, TextWriter stderr)
    {
        var pipeline = services.GetRequiredService<NubArca.Api.Plates.Alpr.IPlateAnalysisPipeline>();
        if (!pipeline.IsAvailable)
        {
            stdout.WriteLine($"ALPR benchmark: unavailable ({pipeline.UnavailableReason ?? PlateAnalysisErrorCodes.ModelNotConfigured})");
            return 0;
        }

        // Use the stored/decoded dimensions from a header identify (no persistence).
        int width, height;
        try
        {
            var info = SixLabors.ImageSharp.Image.Identify(bytes);
            width = info.Width;
            height = info.Height;
        }
        catch
        {
            stderr.WriteLine("ALPR benchmark: the image could not be decoded");
            return 1;
        }

        var input = new NubArca.Api.Plates.Alpr.PlateImageInput(bytes, width, height);
        double total = 0;
        var lastCount = 0;
        for (var i = 0; i < runs; i++)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var result = await pipeline.AnalyzeAsync(input, CancellationToken.None);
                lastCount = result.Detections.Count;
            }
            catch (NubArca.Api.Plates.Alpr.PlateAnalysisModelException ex)
            {
                stdout.WriteLine($"ALPR benchmark: failed ({ex.SafeCode})");
                return 0;
            }
            sw.Stop();
            total += sw.Elapsed.TotalMilliseconds;
        }
        stdout.WriteLine("ALPR benchmark:");
        stdout.WriteLine($"  runs: {runs}");
        stdout.WriteLine($"  avgMs: {total / runs:F1}");
        stdout.WriteLine($"  detections: {lastCount}");
        return 0;
    }

    private static async Task<int> BenchmarkFaceRedactionAsync(
        byte[] bytes, int runs, IServiceProvider services, TextWriter stdout, TextWriter stderr)
    {
        var detector = services.GetRequiredService<NubArca.Api.Plates.Redaction.IPlateFaceRedactionDetector>();
        if (!detector.IsAvailable)
        {
            stdout.WriteLine($"FaceRedaction benchmark: unavailable ({NubArca.Api.Plates.Redaction.PlateFaceRedactionUnavailableException.Code})");
            return 0;
        }

        int width, height;
        try
        {
            var info = SixLabors.ImageSharp.Image.Identify(bytes);
            width = info.Width;
            height = info.Height;
        }
        catch
        {
            stderr.WriteLine("FaceRedaction benchmark: the image could not be decoded");
            return 1;
        }

        var input = new NubArca.Api.Plates.Redaction.PlateRedactionImageInput(bytes, width, height);
        double total = 0;
        var lastFaces = 0;
        for (var i = 0; i < runs; i++)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var boxes = await detector.DetectAsync(input, CancellationToken.None);
                lastFaces = boxes.Count;
            }
            catch (NubArca.Api.Plates.Redaction.PlateFaceRedactionUnavailableException)
            {
                stdout.WriteLine($"FaceRedaction benchmark: unavailable ({NubArca.Api.Plates.Redaction.PlateFaceRedactionUnavailableException.Code})");
                return 0;
            }
            sw.Stop();
            total += sw.Elapsed.TotalMilliseconds;
        }
        stdout.WriteLine("FaceRedaction benchmark:");
        stdout.WriteLine($"  runs: {runs}");
        stdout.WriteLine($"  avgMs: {total / runs:F1}");
        stdout.WriteLine($"  faces: {lastFaces}");
        return 0;
    }

    // Model file BASENAME (never an absolute path) for sanitized diagnostics.
    private static string Basename(string? path) =>
        string.IsNullOrWhiteSpace(path) ? "(unset)" : Path.GetFileName(path);

    private static async Task<int> DispatchAsync(
        Func<IServiceProvider>? factory,
        Func<IServiceProvider, Task<int>> body,
        TextWriter stderr)
    {
        if (factory is not null)
        {
            return await body(factory());
        }

        IHost? host = null;
        try
        {
            host = BuildDefaultHost();
            using var scope = host.Services.CreateScope();
            return await body(scope.ServiceProvider);
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"cli: failed to initialise: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
        finally
        {
            host?.Dispose();
        }
    }

    private static IHost BuildDefaultHost()
    {
        var builder = Host.CreateApplicationBuilder();
        ConfigureCliServices(builder.Services, builder.Configuration);
        return builder.Build();
    }

    // The CLI/worker host's service graph. Extracted (slice 97) so a test can
    // assert registration parity with the web host using an in-memory
    // configuration — the field bug this guards against: `jobs worker` ran
    // admin imports whose staging-sourced runs failed with "Staging storage is
    // not configured" because the WEB host bound StagingOptions but the CLI
    // host did not, even though the worker container had Staging__RootPath.
    internal static void ConfigureCliServices(IServiceCollection services, IConfiguration configuration)
    {
        var cs = configuration.GetConnectionString("Postgres");

        if (!string.IsNullOrWhiteSpace(cs))
        {
            services.AddDbContext<AppDbContext>(o => o.UseNpgsql(cs));
            services.AddScoped<IUserService, UserService>();
            services.AddScoped<IAuthService, AuthService>();

            // Graph needed by `metadata backfill` (slice 55): the backfill
            // service drives FileItemService.ReExtractEmbeddedMetadataAsync,
            // which reads blob bytes and re-runs the embedded extractor.
            services.Configure<BlobStorageOptions>(
                configuration.GetSection(BlobStorageOptions.SectionName));
            services.Configure<ImageProcessingOptions>(
                configuration.GetSection(ImageProcessingOptions.SectionName));
            services.AddSingleton<IBlobStorage, LocalFileSystemBlobStorage>();
            // Slice 97: derived-store parity with the web host. Without this
            // registration BlobService fell back to the ORIGINAL root inside
            // the worker, so worker-generated derivatives landed in the wrong
            // root on split-root deployments (the displacement slice 96 made
            // visible/repairable).
            services.AddSingleton<IDerivedBlobStorage>(sp =>
            {
                var o = sp.GetRequiredService<IOptions<BlobStorageOptions>>().Value;
                return new DerivedFsBlobStorage(o.EffectiveDerivedRootPath, o.MaxUploadBytes);
            });
            services.AddSingleton<IEmbeddedMetadataExtractor, EmbeddedImageMetadataExtractor>();
            services.AddScoped<IBlobService, BlobService>();
            // FileThumbnailService requires a video poster provider. Mirror the
            // web host's synthetic-or-ffmpeg selection so any handler that pulls
            // in the thumbnail service (media-derivatives backfill, admin import)
            // can be constructed under `jobs run-once` / `jobs worker`.
            services.Configure<MediaOptions>(configuration.GetSection("Media"));
            // Slice 100: image-derivative backends (libvips fast path with
            // ImageSharp fallback) — same graph as the web host so worker-side
            // backfills get the optimized path too.
            services.Configure<MediaDerivativesOptions>(
                configuration.GetSection(MediaDerivativesOptions.SectionName));
            services.AddSingleton<VipsRuntime>();
            services.AddSingleton<ImageSharpDerivativeBackend>();
            services.AddSingleton<VipsDerivativeBackend>();
            services.AddSingleton<IImageDerivativeBackend>(
                sp => sp.GetRequiredService<VipsDerivativeBackend>());
            services.AddSingleton<ImageDerivativeRenderer>();
            services.AddSingleton<IProcessRunner, SystemProcessRunner>();
            // Slice 98: web-host parity — without this the worker silently
            // skipped video signature detection (no DetectedContentType, so
            // playback gates never opened for worker-imported videos).
            services.AddSingleton<IVideoSignatureDetector, VideoSignatureDetector>();
            services.AddSingleton<SyntheticVideoPosterProvider>();
            var cliPosterProvider = configuration["Media:VideoPosterProvider"] ?? "synthetic";
            if (string.Equals(cliPosterProvider, "ffmpeg", StringComparison.OrdinalIgnoreCase))
            {
                services.AddSingleton<IVideoPosterProvider, FfmpegVideoPosterProvider>();
            }
            else
            {
                services.AddSingleton<IVideoPosterProvider>(
                    sp => sp.GetRequiredService<SyntheticVideoPosterProvider>());
            }
            // Video metadata probe provider (ffprobe). Config-driven exactly as
            // the web host — without this the CLI/worker FileItemService would
            // fall back to the no-op extractor and mark every video "skipped"
            // even when ffprobe is configured.
            var cliVideoMetaProvider = configuration["Media:VideoMetadataProvider"] ?? "none";
            if (string.Equals(cliVideoMetaProvider, "ffprobe", StringComparison.OrdinalIgnoreCase))
            {
                services.AddSingleton<IVideoMetadataExtractor, FfprobeVideoMetadataExtractor>();
            }
            else
            {
                services.AddSingleton<IVideoMetadataExtractor, NoopVideoMetadataExtractor>();
            }
            // Video-hls slice 1: web-host parity — the worker runs the
            // media.video.hls.generate job, so the transcoder graph must
            // resolve here too (config-driven exactly as Program.cs).
            services.AddSingleton<IDirectoryProcessRunner, SystemProcessRunner>();
            services.AddSingleton<HlsDerivativeStorage>();
            var cliVideoHlsProvider = configuration["Media:VideoHlsProvider"] ?? "none";
            if (string.Equals(cliVideoHlsProvider, "ffmpeg", StringComparison.OrdinalIgnoreCase))
            {
                services.AddSingleton<IVideoHlsTranscoder, FfmpegVideoHlsTranscoder>();
            }
            else
            {
                services.AddSingleton<IVideoHlsTranscoder, NoopVideoHlsTranscoder>();
            }
            services.AddScoped<VideoHlsGenerationService>();
            services.AddScoped<VideoHlsBackfillService>();
            services.AddScoped<IFileThumbnailService, FileThumbnailService>();
            services.AddScoped<IFileItemService, FileItemService>();
            // deleted-content-import-skip: the worker runs imports + organizer,
            // so the ledger service + skip evaluator must resolve here too.
            services.AddScoped<IDeletedContentTombstoneService, DeletedContentTombstoneService>();
            services.AddScoped<IImportSkipEvaluator, ImportSkipEvaluator>();
            services.Configure<DeletedContentOptions>(
                configuration.GetSection(DeletedContentOptions.SectionName));
            // Slice 97: media-library parity with the web host — worker-side
            // folder creation must maintain the denormalized exclusion flags,
            // and batch media jobs must honour the same visibility rules.
            services.AddScoped<IMediaLibraryService, MediaLibraryService>();
            // Slice 81: admin server-side import handler runs under the CLI /
            // worker host too, so it needs the folder service + its options.
            services.AddScoped<IFolderService>(sp =>
                new FolderService(
                    sp.GetRequiredService<AppDbContext>(),
                    sp.GetRequiredService<TimeProvider>(),
                    sp.GetRequiredService<IFileItemService>(),
                    sp.GetRequiredService<IMediaLibraryService>()));
            services.Configure<AdminImportOptions>(
                configuration.GetSection(AdminImportOptions.SectionName));
            // Slice 97 (bug 1): staging options were bound by the web host only,
            // so a staging-sourced import executed by `jobs worker` saw an empty
            // RootPath and failed validation. Bind them exactly like Program.cs.
            services.Configure<StagingOptions>(
                configuration.GetSection(StagingOptions.SectionName));
            services.AddScoped<IAdminImportService, AdminImportService>();
            services.AddScoped<MetadataBackfillService>();
            services.AddScoped<VideoMetadataBackfillService>();
            // Slice 99: durable derivative diagnostics (failure recording, retry
            // gating, aggregates for `media derivatives failures`). Must be
            // registered BEFORE the backfill so DI injects it.
            services.AddScoped<DerivativeDiagnosticsService>();
            // Slice 63: media-derivatives prewarm. Depends on
            // IFileThumbnailService + DerivativeDiagnosticsService (above).
            services.AddScoped<MediaDerivativesBackfillService>();
            services.AddScoped<GalleryDerivativesRegenerationService>();
            services.AddScoped<MediumPreviewRegenerationService>();
            // Slice 96: derived-bytes placement audit/repair (media
            // derivatives verify-bytes / repair-bytes).
            services.AddScoped<MediaDerivativeBytesService>();
            // Slice 100: backend benchmark (media derivatives benchmark).
            services.AddScoped<DerivativeBenchmarkService>();
            // Slice 95: poster regeneration (media posters regenerate).
            services.AddScoped<PosterRegenerationService>();
            // Slice 65: storage reconciliation. Depends on IBlobStorage +
            // AppDbContext (both registered above when a connection string
            // is present).
            services.AddScoped<StorageReconciliationService>();
            // Slice 97: blob reference-count audit/repair CLI.
            services.AddScoped<BlobReferenceAuditService>();

            // Slice 70: background jobs. Queue + processor + handlers that
            // reuse the backfill / reconciliation services above. The hosted
            // JobWorker is NOT registered for the CLI host — the operator
            // drives processing explicitly via `jobs run-once` / `jobs worker`.
            services.Configure<JobsOptions>(
                configuration.GetSection(JobsOptions.SectionName));
            services.AddScoped<IJobQueue, JobQueue>();
            services.AddScoped<JobProcessor>();
            services.AddScoped<IAuditLogger, AuditLogger>();
            services.AddScoped<PhotoDateTakenOrganizerService>();
            services.AddScoped<PhotoExportService>();
            // Background-job handlers — the SHARED list (see
            // JobHandlerRegistration), identical to the web host, so the worker
            // can never miss a handler the API enqueues (UnknownJobType). Keep
            // the dependent services (organizer/export/admin-import) registered
            // above this call.
            services.AddNubArcaJobHandlers();

            // AI substrate (Phase 0B): bind AiOptions and register the same
            // service graph as the web host so resolution/seeding behave
            // identically under the CLI/worker host (parity matters for the
            // Phase 0C status/seed commands). Inert by default.
            services.Configure<AiOptions>(
                configuration.GetSection(AiOptions.SectionName));
            // VSEM-01: same section the web host binds, so the worker that runs
            // ai.videos.segments.backfill sees the same enable flag, version and
            // caps (a divergence here would silently segment at the wrong
            // version, or not at all).
            services.Configure<NubArca.Api.Ai.Video.VideoSemanticSegmentationOptions>(
                configuration.GetSection(
                    NubArca.Api.Ai.Video.VideoSemanticSegmentationOptions.SectionName));
            // VSEM-02: same section the web host binds, so the worker that runs
            // ai.videos.embeddings.backfill sees the same enable flag and frame
            // extraction caps.
            services.Configure<NubArca.Api.Ai.Video.VideoVisualEmbeddingOptions>(
                configuration.GetSection(
                    NubArca.Api.Ai.Video.VideoVisualEmbeddingOptions.SectionName));
            // VFACE-01: same section the web host binds, so the worker that runs
            // ai.videos.faces.backfill sees the same enable flag, analysis
            // version and sampling/tracking caps (a divergence here would
            // silently analyse at the wrong version, or not at all).
            services.Configure<NubArca.Api.Ai.Video.Faces.VideoFaceAnalysisOptions>(
                configuration.GetSection(
                    NubArca.Api.Ai.Video.Faces.VideoFaceAnalysisOptions.SectionName));
            services.AddAiSubstrate();

            // Plates (Targhe): the worker runs the plates.analyze ALPR job, so the
            // Plates service graph + its config must be present under the CLI/worker
            // host too (parity with the web host).
            services.Configure<NubArca.Api.Plates.PlatesOptions>(
                configuration.GetSection(NubArca.Api.Plates.PlatesOptions.SectionName));
            services.Configure<NubArca.Api.Plates.PlatesAlprOptions>(
                configuration.GetSection(NubArca.Api.Plates.PlatesAlprOptions.SectionName));
            services.Configure<NubArca.Api.Plates.PlatesFaceRedactionOptions>(
                configuration.GetSection(NubArca.Api.Plates.PlatesFaceRedactionOptions.SectionName));
            services.AddNubArcaPlates();

            // Aesthetics Lab: the worker runs the ai.aesthetics.human-aesexpert.
            // analyze job, so the Aesthetics service graph (incl. the sidecar
            // HttpClient) + its config must be present under the CLI/worker host
            // too (parity with the web host).
            services.Configure<NubArca.Api.Aesthetics.AestheticsOptions>(
                configuration.GetSection(NubArca.Api.Aesthetics.AestheticsOptions.SectionName));
            services.AddNubArcaAesthetics();
        }

        services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();
        services.AddSingleton(TimeProvider.System);

        // Plates model options bind UNCONDITIONALLY (no DB needed) so
        // `plates models validate` works on a fresh checkout for pre-deploy
        // config validation even without a Postgres connection string.
        services.Configure<NubArca.Api.Plates.PlatesAlprOptions>(
            configuration.GetSection(NubArca.Api.Plates.PlatesAlprOptions.SectionName));
        services.Configure<NubArca.Api.Plates.PlatesFaceRedactionOptions>(
            configuration.GetSection(NubArca.Api.Plates.PlatesFaceRedactionOptions.SectionName));
    }

    // ---- subcommand: ai ----------------------------------------------------
    // All `ai` commands surface SAFE fields only: stable keys, providers,
    // capabilities, counts, dimensions, distance metrics, sanitized diagnostic
    // codes. They NEVER print GUIDs, raw vectors, blob SHA, storage keys,
    // physical paths, raw payloads, stack traces, or secrets.

    internal static async Task<int> AiStatusAsync(
        string[] args,
        IServiceProvider services,
        TextWriter stdout,
        TextWriter stderr)
    {
        var status = services.GetService<IAiStatusService>();
        if (status is null)
        {
            stderr.WriteLine("ai status: database is not configured. Set ConnectionStrings__Postgres and retry.");
            return 78;
        }

        var s = await status.GetStatusAsync();
        stdout.WriteLine(
            $"ai status: enabled={s.Enabled} provider={s.DefaultProvider} "
            + $"models={s.ModelCount} profiles={s.ProfileCount}");
        stdout.WriteLine("capabilities:");
        foreach (var c in s.Capabilities)
        {
            var state = c.Available ? "available" : (c.UnavailableReason ?? "unavailable");
            var profile = c.DefaultProfileKey is { } pk ? $" profile={pk}" : "";
            var dim = c.Dimension is int d ? $" dim={d}" : "";
            var metric = c.DistanceMetric is { } m ? $" metric={m}" : "";
            stdout.WriteLine($"  {c.Capability,-22} {state}{profile}{dim}{metric}");
        }

        // Aggregate diagnostics summary, when available.
        var aggregator = services.GetService<AiDiagnosticsAggregator>();
        if (aggregator is not null)
        {
            var diag = await aggregator.AggregateAsync();
            var last = diag.LastOccurredAt is { } ts ? $" last={ts:O}" : "";
            stdout.WriteLine($"diagnostics: total={diag.Total}{last}");
        }

        return 0;
    }

    internal static async Task<int> AiModelsAsync(
        string[] args,
        IServiceProvider services,
        TextWriter stdout,
        TextWriter stderr)
    {
        var registry = services.GetService<IAiProfileRegistry>();
        if (registry is null)
        {
            stderr.WriteLine("ai models: database is not configured. Set ConnectionStrings__Postgres and retry.");
            return 78;
        }

        var models = await registry.ListModelsAsync();
        if (models.Count == 0)
        {
            stdout.WriteLine("ai models: (none)");
            return 0;
        }

        stdout.WriteLine("ai models:");
        foreach (var m in models)
        {
            stdout.WriteLine(
                $"  {m.Key,-24} provider={m.Provider} capability={m.Capability} "
                + $"modality={m.Modality} v={m.Version} enabled={m.Enabled} "
                + $"dim={(m.Dimension?.ToString() ?? "-")} metric={(m.DistanceMetric ?? "-")}");
        }

        return 0;
    }

    internal static async Task<int> AiProfilesAsync(
        string[] args,
        IServiceProvider services,
        TextWriter stdout,
        TextWriter stderr)
    {
        var registry = services.GetService<IAiProfileRegistry>();
        if (registry is null)
        {
            stderr.WriteLine("ai profiles: database is not configured. Set ConnectionStrings__Postgres and retry.");
            return 78;
        }

        var models = await registry.ListModelsAsync();
        // Map model id → (stable key, provider) so profiles print the model's
        // stable key, never its GUID.
        var modelById = models.ToDictionary(m => m.Id, m => (m.Key, m.Provider));
        var profiles = await registry.ListProfilesAsync();
        if (profiles.Count == 0)
        {
            stdout.WriteLine("ai profiles: (none)");
            return 0;
        }

        stdout.WriteLine("ai profiles:");
        foreach (var p in profiles)
        {
            var (modelKey, provider) = modelById.TryGetValue(p.AiModelId, out var info)
                ? info
                : ("?", "?");
            stdout.WriteLine(
                $"  {p.Key,-26} model={modelKey} provider={provider} capability={p.Capability} "
                + $"modality={p.Modality} default={p.IsDefault} enabled={p.Enabled} "
                + $"dim={(p.Dimension?.ToString() ?? "-")} metric={(p.DistanceMetric ?? "-")}");
        }

        return 0;
    }

    internal static async Task<int> AiDiagnosticsAsync(
        string[] args,
        IServiceProvider services,
        TextWriter stdout,
        TextWriter stderr)
    {
        var aggregator = services.GetService<AiDiagnosticsAggregator>();
        if (aggregator is null)
        {
            stderr.WriteLine("ai diagnostics: database is not configured. Set ConnectionStrings__Postgres and retry.");
            return 78;
        }

        var diag = await aggregator.AggregateAsync();
        var last = diag.LastOccurredAt is { } ts ? $" last={ts:O}" : "";
        stdout.WriteLine($"ai diagnostics: total={diag.Total}{last} (aggregate-only)");
        if (diag.Groups.Count == 0)
        {
            stdout.WriteLine("(no diagnostics)");
            return 0;
        }

        foreach (var g in diag.Groups)
        {
            stdout.WriteLine(
                $"  {g.Capability}/{g.TargetKind} code={g.ErrorCode} permanent={g.IsPermanent} "
                + $"count={g.Count} profile={(g.ProfileKey ?? "-")} latest={g.LatestOccurredAt:O}");
        }

        return 0;
    }

    internal static async Task<int> AiSeedAsync(
        string[] args,
        IServiceProvider services,
        TextWriter stdout,
        TextWriter stderr)
    {
        var registry = services.GetService<IAiProfileRegistry>();
        if (registry is null)
        {
            stderr.WriteLine("ai seed: database is not configured. Set ConnectionStrings__Postgres and retry.");
            return 78;
        }

        var result = await registry.SeedDeterministicProfilesAsync();
        stdout.WriteLine(
            $"ai seed: deterministic DEV/TEST profiles ensured "
            + $"(models_created={result.ModelsCreated} profiles_created={result.ProfilesCreated}). "
            + "These are NOT real semantic AI and do not enable inference.");
        return 0;
    }

    // Operator test harness for owner-private photo similarity. Scoped to the
    // TARGET FILE'S OWNER (derived from the file itself) — never cross-owner.
    // Prints owner-visible file names + rounded scores only; no vectors/ids/SHA.
    internal static async Task<int> AiPhotosSimilarAsync(
        string[] args,
        IServiceProvider services,
        TextWriter stdout,
        TextWriter stderr)
    {
        // `ai photos similar histogram --file <id> [--profile <key>]` — operator
        // diagnostic comparing the exact score distribution against what the
        // pgvector ANN path surfaces per threshold.
        if (args.Length > 0 && args[0] == "histogram")
        {
            return await AiPhotosSimilarHistogramAsync(args[1..], services, stdout, stderr);
        }

        var fileRaw = ReadOption(args, "--file");
        if (string.IsNullOrWhiteSpace(fileRaw) || !Guid.TryParse(fileRaw, out var fileId))
        {
            stderr.WriteLine("ai photos similar: --file <file-id> (a GUID) is required.");
            return 64;
        }

        var limit = 10;
        var limitRaw = ReadOption(args, "--limit");
        if (limitRaw is not null)
        {
            if (!int.TryParse(limitRaw, out var n) || n <= 0)
            {
                stderr.WriteLine("ai photos similar: --limit must be a positive integer.");
                return 64;
            }
            limit = n;
        }

        // Optional operator override of the active profile (stable key, never a
        // GUID). When omitted, the configured active profile (or documented
        // default fallback) is used.
        var profileOverride = ReadOption(args, "--profile");
        var hasOverride = !string.IsNullOrWhiteSpace(profileOverride);

        var db = services.GetService<AppDbContext>();
        var similarity = services.GetService<PhotoSimilarityService>();
        if (db is null || similarity is null)
        {
            stderr.WriteLine("ai photos similar: database is not configured. Set ConnectionStrings__Postgres and retry.");
            return 78;
        }

        // Operator tool: derive the owner from the target file, then run the
        // owner-scoped lookup (results stay within that one owner).
        var ownerUserId = await db.FileItems.AsNoTracking()
            .Where(f => f.Id == fileId && f.DeletedAt == null)
            .Select(f => (Guid?)f.OwnerUserId)
            .FirstOrDefaultAsync();

        if (ownerUserId is null)
        {
            stderr.WriteLine("ai photos similar: file not found.");
            return 64;
        }

        var result = await similarity.FindSimilarAsync(
            ownerUserId.Value, fileId, limit, profileKeyOverride: profileOverride);
        if (result is null)
        {
            stderr.WriteLine("ai photos similar: file not found.");
            return 64;
        }

        if (!result.ProfileAvailable)
        {
            var reason = result.UnavailableReason ?? "no-active-profile";
            // An explicit --profile that can't be used is an operator mistake →
            // clear error on stderr. An unconfigured/unusable DEFAULT is just an
            // informational state on stdout.
            if (hasOverride)
            {
                stderr.WriteLine($"ai photos similar: requested profile is not usable ({reason}).");
                return 64;
            }
            stdout.WriteLine($"ai photos similar: no usable active image-embedding profile ({reason}).");
            return 0;
        }
        if (!result.QueryIndexed)
        {
            stdout.WriteLine("ai photos similar: query photo is not indexed yet (run ai.photos.embeddings.backfill).");
            return 0;
        }

        if (result.Items.Count == 0)
        {
            stdout.WriteLine("ai photos similar: no similar photos found.");
            return 0;
        }

        stdout.WriteLine($"ai photos similar: top {result.Items.Count} for {fileId:N}");
        foreach (var item in result.Items)
        {
            stdout.WriteLine($"  {item.Score:F6}  {item.Name}");
        }
        return 0;
    }

    // Operator diagnostic: similarity-score histogram + per-threshold comparison
    // of exact-scan vs pgvector exact-count vs pgvector ANN-returned count, so an
    // operator can see whether the explorer's ANN path surfaces all owner photos
    // above a threshold (the gap reveals HNSW recall limits). Owner derived from
    // the file. Counts + bucket ranges only — no ids/vectors/SHA/paths.
    internal static async Task<int> AiPhotosSimilarHistogramAsync(
        string[] args,
        IServiceProvider services,
        TextWriter stdout,
        TextWriter stderr)
    {
        var fileRaw = ReadOption(args, "--file");
        if (string.IsNullOrWhiteSpace(fileRaw) || !Guid.TryParse(fileRaw, out var fileId))
        {
            stderr.WriteLine("ai photos similar histogram: --file <file-id> (a GUID) is required.");
            return 64;
        }

        var profileOverride = ReadOption(args, "--profile");
        var hasOverride = !string.IsNullOrWhiteSpace(profileOverride);

        var db = services.GetService<AppDbContext>();
        var similarity = services.GetService<PhotoSimilarityService>();
        if (db is null || similarity is null)
        {
            stderr.WriteLine("ai photos similar histogram: database is not configured. Set ConnectionStrings__Postgres and retry.");
            return 78;
        }

        var ownerUserId = await db.FileItems.AsNoTracking()
            .Where(f => f.Id == fileId && f.DeletedAt == null)
            .Select(f => (Guid?)f.OwnerUserId)
            .FirstOrDefaultAsync();
        if (ownerUserId is null)
        {
            stderr.WriteLine("ai photos similar histogram: file not found.");
            return 64;
        }

        var hist = await similarity.ComputeHistogramAsync(ownerUserId.Value, fileId, profileOverride);
        if (hist is null)
        {
            stderr.WriteLine("ai photos similar histogram: file not found.");
            return 64;
        }

        if (!hist.ProfileAvailable)
        {
            var reason = hist.UnavailableReason ?? "no-active-profile";
            if (hasOverride)
            {
                stderr.WriteLine($"ai photos similar histogram: requested profile is not usable ({reason}).");
                return 64;
            }
            stdout.WriteLine($"ai photos similar histogram: no usable active image-embedding profile ({reason}).");
            return 0;
        }
        if (!hist.QueryIndexed)
        {
            stdout.WriteLine("ai photos similar histogram: query photo is not indexed yet (run ai.photos.embeddings.backfill).");
            return 0;
        }

        stdout.WriteLine($"ai photos similar histogram for {fileId:N}");
        stdout.WriteLine($"  candidates scanned: {hist.TotalCandidates}");
        stdout.WriteLine(hist.VectorBackendAvailable
            ? "  vector backend: pgvector (ANN) available"
            : "  vector backend: unavailable (exact-scan only)");

        stdout.WriteLine("  score distribution (0.05 buckets):");
        foreach (var b in hist.Buckets)
        {
            stdout.WriteLine($"    [{b.Min:F2}, {b.Max:F2})  {b.Count}");
        }

        // The comparison table. When pgvector is available, AnnReturned should
        // track min(exact, MaxExplorable); a large shortfall means HNSW recall
        // (ef_search) is the limiter, not the data.
        stdout.WriteLine("  threshold   exactScan   pgExact   annReturned");
        foreach (var r in hist.Thresholds)
        {
            var pg = r.PgExactCount?.ToString(CultureInfo.InvariantCulture) ?? "n/a";
            var ann = r.AnnReturnedCount?.ToString(CultureInfo.InvariantCulture) ?? "n/a";
            stdout.WriteLine($"    {r.Threshold:F2}        {r.ExactScanCount,9}   {pg,7}   {ann,11}");
        }
        return 0;
    }

    // ---- subcommand: ai photos embeddings (lifecycle inspection) ------------
    // Aggregate, sanitized operator tooling for the photo-embedding profile
    // lifecycle: per-profile coverage and the resolved active profile. Output is
    // counts/keys/dimensions/metrics/sanitized reasons only — never vectors,
    // BlobObjectId, SHA, StorageKey, or physical paths.
    internal static async Task<int> AiPhotosEmbeddingsAsync(
        string[] args,
        IServiceProvider services,
        TextWriter stdout,
        TextWriter stderr)
    {
        var sub = args.Length > 0 ? args[0] : string.Empty;
        var rest = args.Length > 0 ? args[1..] : args;

        var profiles = services.GetService<PhotoEmbeddingProfileService>();
        if (profiles is null)
        {
            stderr.WriteLine("ai photos embeddings: database is not configured. Set ConnectionStrings__Postgres and retry.");
            return 78;
        }

        switch (sub)
        {
            case "coverage":
            {
                var profileKey = ReadOption(rest, "--profile");
                if (string.IsNullOrWhiteSpace(profileKey))
                {
                    stderr.WriteLine("ai photos embeddings coverage: --profile <profile-key> is required.");
                    return 64;
                }

                var coverage = await profiles.GetCoverageAsync(profileKey);
                if (coverage is null)
                {
                    stderr.WriteLine($"ai photos embeddings coverage: profile '{profileKey}' not found.");
                    return 64;
                }

                stdout.WriteLine($"profile={coverage.ProfileKey}");
                stdout.WriteLine($"eligible_images={coverage.EligibleImages}");
                stdout.WriteLine($"embedded={coverage.Embedded}");
                stdout.WriteLine($"missing_embeddings={coverage.Missing}");
                stdout.WriteLine($"coverage_percent={coverage.CoveragePercent:0.##}");
                stdout.WriteLine($"dimension={(coverage.Dimension?.ToString() ?? "-")}");
                stdout.WriteLine($"distance_metric={(coverage.DistanceMetric ?? "-")}");
                stdout.WriteLine($"vector_supported={coverage.VectorSupported}");
                stdout.WriteLine($"vector_indexed={coverage.VectorIndexed}");
                stdout.WriteLine($"missing_vectors={coverage.MissingVectors}");
                stdout.WriteLine($"vector_coverage_percent={coverage.VectorCoveragePercent:0.##}");
                return 0;
            }

            case "vector-sync":
            {
                var profileKey = ReadOption(rest, "--profile");
                if (string.IsNullOrWhiteSpace(profileKey))
                {
                    stderr.WriteLine("ai photos embeddings vector-sync: --profile <profile-key> is required.");
                    return 64;
                }

                int? limit = null;
                var limitRaw = ReadOption(rest, "--limit");
                if (limitRaw is not null)
                {
                    if (!int.TryParse(limitRaw, out var n) || n <= 0)
                    {
                        stderr.WriteLine("ai photos embeddings vector-sync: --limit must be a positive integer.");
                        return 64;
                    }
                    limit = n;
                }

                var dryRun = HasFlag(rest, "--dry-run");

                var registry = services.GetService<IAiProfileRegistry>();
                var vectors = services.GetService<PhotoVectorIndexService>();
                if (registry is null || vectors is null)
                {
                    stderr.WriteLine("ai photos embeddings vector-sync: database is not configured. Set ConnectionStrings__Postgres and retry.");
                    return 78;
                }

                var profile = await registry.GetProfileByKeyAsync(profileKey);
                if (profile is null)
                {
                    stderr.WriteLine($"ai photos embeddings vector-sync: profile '{profileKey}' not found.");
                    return 64;
                }

                var outcome = await vectors.SyncProfileAsync(profile, limit, dryRun, log: msg => stderr.WriteLine(msg));

                stdout.WriteLine($"profile={outcome.ProfileKey}");
                stdout.WriteLine($"dimension={outcome.Dimension}");
                stdout.WriteLine($"eligible_embeddings={outcome.EligibleEmbeddings}");
                stdout.WriteLine($"vector_indexed={outcome.VectorIndexed}");
                stdout.WriteLine($"missing_vectors={outcome.MissingVectors}");
                stdout.WriteLine($"synced={outcome.Synced}");
                stdout.WriteLine($"skipped_dimension_mismatch={outcome.SkippedDimensionMismatch}");
                stdout.WriteLine($"failed={outcome.Failed}");
                stdout.WriteLine($"dry_run={(outcome.DryRun ? "true" : "false")}");
                // Sanitized availability note (vector path unavailable => the read
                // path uses exact-scan; nothing was written).
                stdout.WriteLine($"vector_backend={(outcome.Available ? "available" : (outcome.Reason ?? "unavailable"))}");
                return 0;
            }

            case "active-profile":
            {
                var r = await profiles.ResolveActiveProfileAsync(overrideKey: null);
                var sourceText = r.Source switch
                {
                    PhotoProfileSource.Configured => "config",
                    PhotoProfileSource.DefaultFallback => "default-fallback",
                    _ => "override",
                };

                stdout.WriteLine("ai photos embeddings active-profile:");
                stdout.WriteLine($"  config_key={(r.Source == PhotoProfileSource.Configured ? r.RequestedKey : "(unset)")}");
                stdout.WriteLine($"  source={sourceText}");
                stdout.WriteLine($"  profile={(r.Profile?.Key ?? r.RequestedKey ?? "-")}");
                stdout.WriteLine($"  usable={r.Usable}");
                stdout.WriteLine($"  capability={(r.Profile?.Capability ?? "-")}");
                stdout.WriteLine($"  dimension={(r.Profile?.Dimension?.ToString() ?? "-")}");
                stdout.WriteLine($"  distance_metric={(r.Profile?.DistanceMetric ?? "-")}");
                stdout.WriteLine($"  reason={(r.UnavailableReason ?? "-")}");
                return 0;
            }

            case "retire-legacy-768":
            {
                var execute = HasFlag(rest, "--execute");
                var result = await profiles.RetireLegacy768Async(execute);
                stdout.WriteLine($"ready={result.Ready}");
                stdout.WriteLine($"executed={result.Executed}");
                stdout.WriteLine($"legacy_profiles={result.LegacyProfiles}");
                stdout.WriteLine($"legacy_embeddings={result.LegacyEmbeddings}");
                stdout.WriteLine($"reason={result.Reason ?? "-"}");
                return result.Ready ? 0 : 75;
            }

            default:
                stderr.WriteLine($"ai photos embeddings: unknown subcommand '{sub}'. One of: coverage, active-profile, vector-sync, retire-legacy-768.");
                return 64;
        }
    }

    // ---- subcommand: ai onnx runtime-info (Gate 3C diagnostics) -------------
    // Stable, non-sensitive ONNX runtime diagnostics: managed/native ORT + OpenVINO
    // versions, available providers, configured provider, and ABI match. No paths,
    // secrets or native exception text. Installs the OpenVINO resolver first (in
    // openvino-direct mode) so the reported providers reflect the loaded core.
    internal static Task<int> AiOnnxRuntimeInfoAsync(
        string[] args,
        IServiceProvider services,
        TextWriter stdout,
        TextWriter stderr)
    {
        var init = services.GetService<NubArca.Api.Ai.Onnx.OnnxDirectRuntimeInitializer>();
        if (init is null)
        {
            stderr.WriteLine("ai onnx runtime-info: AI substrate is not registered.");
            return Task.FromResult(78);
        }

        services.GetService<NubArca.Api.Ai.Onnx.IOnnxInferenceSessionFactory>()?.EnsureNativeProviderInitialized();
        var info = init.GatherInfo();
        stdout.WriteLine($"commit={Environment.GetEnvironmentVariable("NUBARCA_GIT_SHA") ?? "unknown"}");
        stdout.WriteLine($"managedOrt={info.ManagedOrtVersion}");
        stdout.WriteLine($"nativeOrt={info.NativeCoreVersion ?? "-"}");
        stdout.WriteLine($"openvino={info.OpenVinoVersion ?? "-"}");
        stdout.WriteLine($"providers={string.Join(",", info.AvailableProviders)}");
        stdout.WriteLine($"configuredProvider={info.ConfiguredProvider}");
        stdout.WriteLine($"abiMatch={info.AbiMatches}");
        if (NubArca.Api.Ai.Onnx.OnnxDirectRuntimeInitializer.LastProvidersError is { } err)
        {
            stdout.WriteLine($"providersError={err}");
        }
        return Task.FromResult(0);
    }

    // ---- subcommand: ai video semantic (VSEM-04 operational diagnostics) ---
    // Safe, read-only status plus bounded backfill/retry controls over the
    // VSEM-01 (segmentation) and VSEM-02 (embedding) substrate. `--dry-run`
    // (and every scope preview below a real enqueue) is a pure COUNT query —
    // no FFmpeg, no inference, no writes, no job. A real run enqueues the SAME
    // existing job types the backfill services already use; nothing here
    // duplicates their pipeline logic. Output is counts/keys/flags only —
    // never filenames, storage keys, ids beyond the job id, or vectors.
    internal static async Task<int> AiVideoSemanticAsync(
        string[] args,
        IServiceProvider services,
        TextWriter stdout,
        TextWriter stderr)
    {
        if (args.Length == 0)
        {
            stderr.WriteLine(
                "ai video semantic: missing subcommand. One of: status, segments backfill, "
                + "embeddings backfill, faces backfill, retry-failed segments, "
                + "retry-failed embeddings, retry-failed faces.");
            return 64;
        }

        var sub = args[0];
        var rest = args[1..];
        switch (sub)
        {
            case "status":
                return await AiVideoSemanticStatusAsync(rest, services, stdout, stderr);
            case "segments" when rest.Length > 0 && rest[0] == "backfill":
                return await AiVideoSemanticSegmentsBackfillAsync(
                    rest[1..], services, stdout, stderr, forceFailedOnly: false);
            case "embeddings" when rest.Length > 0 && rest[0] == "backfill":
                return await AiVideoSemanticEmbeddingsBackfillAsync(
                    rest[1..], services, stdout, stderr, forceFailedOnly: false);
            case "retry-failed" when rest.Length > 0 && rest[0] == "segments":
                return await AiVideoSemanticSegmentsBackfillAsync(
                    rest[1..], services, stdout, stderr, forceFailedOnly: true);
            case "retry-failed" when rest.Length > 0 && rest[0] == "embeddings":
                return await AiVideoSemanticEmbeddingsBackfillAsync(
                    rest[1..], services, stdout, stderr, forceFailedOnly: true);
            case "faces" when rest.Length > 0 && rest[0] == "backfill":
                return await AiVideoFacesBackfillAsync(
                    rest[1..], services, stdout, stderr, forceFailedOnly: false);
            case "retry-failed" when rest.Length > 0 && rest[0] == "faces":
                return await AiVideoFacesBackfillAsync(
                    rest[1..], services, stdout, stderr, forceFailedOnly: true);
            default:
                stderr.WriteLine(
                    "ai video semantic: unknown subcommand. One of: status, segments backfill, "
                    + "embeddings backfill, faces backfill, retry-failed segments, "
                    + "retry-failed embeddings, retry-failed faces.");
                return 64;
        }
    }

    internal static async Task<int> AiVideoSemanticStatusAsync(
        string[] args,
        IServiceProvider services,
        TextWriter stdout,
        TextWriter stderr)
    {
        var diagnostics = services.GetService<VideoSemanticDiagnosticsService>();
        if (diagnostics is null)
        {
            stderr.WriteLine("ai video semantic status: database is not configured. Set ConnectionStrings__Postgres and retry.");
            return 78;
        }

        var s = await diagnostics.GetStatusAsync();

        stdout.WriteLine($"ai video semantic status: eligible_video_blobs={s.EligibleVideoBlobs}");
        stdout.WriteLine($"active_segmentation_version={s.ActiveSegmentationVersion}");
        stdout.WriteLine("segmentation:");
        stdout.WriteLine($"  not_processed={s.SegmentationNotProcessed}");
        stdout.WriteLine($"  completed={s.SegmentationCompleted}");
        stdout.WriteLine($"  failed={s.SegmentationFailed}");
        stdout.WriteLine($"  skipped={s.SegmentationSkipped}");
        stdout.WriteLine($"  segmentation_capacity_exceeded={s.SegmentationCapacityExceeded}");
        if (s.HistoricalVersions.Count == 0)
        {
            stdout.WriteLine("historical_segmentation_versions: (none)");
        }
        else
        {
            stdout.WriteLine("historical_segmentation_versions (never mixed into the active counts above):");
            foreach (var h in s.HistoricalVersions)
            {
                stdout.WriteLine(
                    $"  v{h.SegmentationVersion}: completed={h.Completed} failed={h.Failed} skipped={h.Skipped}");
            }
        }

        stdout.WriteLine(
            $"active_visual_embedding_profile={s.ActiveEmbeddingProfileKey ?? "<none>"} "
            + $"(available={s.ActiveEmbeddingProfileAvailable.ToString().ToLowerInvariant()}"
            + (s.ActiveEmbeddingProfileUnavailableReason is { } reason ? $" reason={reason}" : "") + ")");
        stdout.WriteLine("embedding_manifests:");
        stdout.WriteLine($"  pending={s.EmbeddingManifestsPending}");
        stdout.WriteLine($"  completed={s.EmbeddingManifestsCompleted}");
        stdout.WriteLine($"  partial={s.EmbeddingManifestsPartial}");
        stdout.WriteLine($"  failed={s.EmbeddingManifestsFailed}");
        stdout.WriteLine($"  skipped={s.EmbeddingManifestsSkipped}");

        var samplePercent = s.SamplesExpected == 0
            ? 0d : Math.Round(s.SamplesCanonicallyEmbedded * 100.0 / s.SamplesExpected, 1);
        stdout.WriteLine("samples:");
        stdout.WriteLine($"  expected={s.SamplesExpected}");
        stdout.WriteLine(
            $"  canonically_embedded={s.SamplesCanonicallyEmbedded} / {s.SamplesExpected} ({samplePercent}%)");
        stdout.WriteLine($"  failed_or_missing={s.SamplesFailedOrMissing}");

        stdout.WriteLine("vector_acceleration (profile-wide, all segmentation versions):");
        stdout.WriteLine($"  canonical_embeddings={s.CanonicalEmbeddingsProfileWide}");
        if (s.PgvectorBackendAvailable)
        {
            var pgPercent = s.CanonicalEmbeddingsProfileWide == 0
                ? 0d : Math.Round(s.PgvectorSynchronizedProfileWide * 100.0 / s.CanonicalEmbeddingsProfileWide, 1);
            stdout.WriteLine(
                $"  pgvector_synchronized={s.PgvectorSynchronizedProfileWide} / "
                + $"{s.CanonicalEmbeddingsProfileWide} ({pgPercent}%)");
            stdout.WriteLine($"  stale_or_missing_pgvector={s.PgvectorStaleOrMissingProfileWide}");
        }
        else
        {
            stdout.WriteLine(
                "  pgvector: unavailable for this profile/dimension — exact in-process scan only.");
        }

        stdout.WriteLine("feature_flags:");
        stdout.WriteLine($"  video_segmentation_enabled={s.SegmentationEnabled}");
        stdout.WriteLine($"  video_visual_embeddings_enabled={s.EmbeddingsEnabled}");

        stdout.WriteLine(
            "ranking_window: (VSEM-03 unified semantic retrieval — a ranked-result window, "
            + "not total semantic-match cardinality)");
        stdout.WriteLine($"  max_ranked_photo_candidates={s.MaxRankedPhotoCandidates}");
        stdout.WriteLine($"  max_ranked_video_candidates={s.MaxRankedVideoCandidates}");
        stdout.WriteLine($"  ranking_contract_version={s.RankingContractVersion}");

        return 0;
    }

    // Shared --limit/--segmentation-version/--blob-id/--failed-only/--dry-run
    // parsing for the video-semantic backfill/retry commands. Returns null
    // (after printing a usage error) on any invalid input. `forceFailedOnly`
    // is set by the `retry-failed` alias so it always targets failures
    // regardless of whether the operator also passed --failed-only.
    private static VideoSemanticCliOptions? ParseVideoSemanticOptions(
        string command, string[] args, TextWriter stderr, bool forceFailedOnly)
    {
        int? limit = null;
        var limitRaw = ReadOption(args, "--limit");
        if (limitRaw is not null)
        {
            if (!int.TryParse(limitRaw, out var n) || n <= 0)
            {
                stderr.WriteLine($"{command}: --limit must be a positive integer.");
                return null;
            }
            limit = n;
        }

        int? version = null;
        var versionRaw = ReadOption(args, "--segmentation-version");
        if (versionRaw is not null)
        {
            if (!int.TryParse(versionRaw, out var v) || v <= 0)
            {
                stderr.WriteLine($"{command}: --segmentation-version must be a positive integer.");
                return null;
            }
            version = v;
        }

        Guid? blobId = null;
        var blobRaw = ReadOption(args, "--blob-id");
        if (blobRaw is not null)
        {
            if (!Guid.TryParse(blobRaw, out var g))
            {
                stderr.WriteLine($"{command}: --blob-id must be a GUID.");
                return null;
            }
            blobId = g;
        }

        return new VideoSemanticCliOptions(
            limit, forceFailedOnly || HasFlag(args, "--failed-only"), HasFlag(args, "--dry-run"), version, blobId);
    }

    private sealed record VideoSemanticCliOptions(
        int? Limit, bool FailedOnly, bool DryRun, int? SegmentationVersion, Guid? BlobObjectId);

    // ---- subcommand: ai video semantic segments backfill / retry-failed ----
    internal static async Task<int> AiVideoSemanticSegmentsBackfillAsync(
        string[] args,
        IServiceProvider services,
        TextWriter stdout,
        TextWriter stderr,
        bool forceFailedOnly)
    {
        var command = forceFailedOnly
            ? "ai video semantic retry-failed segments"
            : "ai video semantic segments backfill";
        var parsed = ParseVideoSemanticOptions(command, args, stderr, forceFailedOnly);
        if (parsed is null)
        {
            return 64;
        }

        var backfill = services.GetService<VideoSemanticSegmentationBackfillService>();
        var queue = services.GetService<IJobQueue>();
        var db = services.GetService<AppDbContext>();
        if (backfill is null || queue is null || db is null)
        {
            stderr.WriteLine($"{command}: database is not configured. Set ConnectionStrings__Postgres and retry.");
            return 78;
        }

        // ALWAYS preview first (a pure count query, no writes) — it drives
        // both the --dry-run output and the "selected targets" line printed
        // before a real enqueue.
        var previewOptions = new VideoSemanticBackfillOptions
        {
            Limit = parsed.Limit,
            FailedOnly = parsed.FailedOnly,
            DryRun = true,
            TargetBlobObjectId = parsed.BlobObjectId,
            SegmentationVersion = parsed.SegmentationVersion,
        };
        var preview = await backfill.RunAsync(previewOptions);

        if (parsed.DryRun)
        {
            stdout.WriteLine(
                $"{command} (dry-run): {preview.Examined} video blob(s) would be selected. "
                + "No semantic processing, no writes, no job enqueued.");
            return 0;
        }

        if (preview.Examined == 0)
        {
            stdout.WriteLine($"{command}: no eligible work selected. No job enqueued.");
            return 0;
        }

        var payload = new VideoSemanticSegmentsJobPayload(
            BlobObjectId: parsed.BlobObjectId,
            Limit: parsed.Limit,
            FailedOnly: parsed.FailedOnly,
            DryRun: false,
            SegmentationVersion: parsed.SegmentationVersion);
        var idempotencyKey =
            $"{JobTypes.AiVideosSegmentsBackfill}:"
            + $"{parsed.SegmentationVersion?.ToString(CultureInfo.InvariantCulture) ?? "active"}:"
            + $"{parsed.FailedOnly}:{parsed.BlobObjectId?.ToString("N") ?? "all"}";

        var alreadyQueued = await db.BackgroundJobs.AsNoTracking().AnyAsync(j =>
            j.IdempotencyKey == idempotencyKey
            && (j.Status == JobStatuses.Queued || j.Status == JobStatuses.Running));
        var job = await queue.EnqueueAsync(
            JobTypes.AiVideosSegmentsBackfill, payload, idempotencyKey: idempotencyKey);

        stdout.WriteLine(
            $"{command}: {(alreadyQueued ? "matched existing" : "queued")} "
            + $"{JobTypes.AiVideosSegmentsBackfill} ({job.Id:N}) "
            + $"limit={(parsed.Limit?.ToString(CultureInfo.InvariantCulture) ?? "-")} "
            + $"segmentation_version={(parsed.SegmentationVersion?.ToString(CultureInfo.InvariantCulture) ?? "active")} "
            + $"failed_only={parsed.FailedOnly} selected_targets={preview.Examined}.");
        return 0;
    }

    // ---- subcommand: ai video semantic embeddings backfill / retry-failed --
    internal static async Task<int> AiVideoSemanticEmbeddingsBackfillAsync(
        string[] args,
        IServiceProvider services,
        TextWriter stdout,
        TextWriter stderr,
        bool forceFailedOnly)
    {
        var command = forceFailedOnly
            ? "ai video semantic retry-failed embeddings"
            : "ai video semantic embeddings backfill";
        var profileKeyRaw = ReadOption(args, "--profile");
        var parsed = ParseVideoSemanticOptions(command, args, stderr, forceFailedOnly);
        if (parsed is null)
        {
            return 64;
        }

        var backfill = services.GetService<VideoSemanticEmbeddingBackfillService>();
        var queue = services.GetService<IJobQueue>();
        var db = services.GetService<AppDbContext>();
        var registry = services.GetService<IAiProfileRegistry>();
        var resolver = services.GetService<IAiBackendResolver>();
        var aiOptions = services.GetService<IOptions<AiOptions>>();
        if (backfill is null || queue is null || db is null || registry is null
            || resolver is null || aiOptions is null)
        {
            stderr.WriteLine($"{command}: database is not configured. Set ConnectionStrings__Postgres and retry.");
            return 78;
        }

        var hasOverride = !string.IsNullOrWhiteSpace(profileKeyRaw);
        AiProfile? profile;
        if (hasOverride)
        {
            profile = await registry.GetProfileByKeyAsync(profileKeyRaw!);
            if (profile is null || profile.Capability != AiCapabilities.ImageEmbedding || !profile.Enabled)
            {
                stderr.WriteLine(
                    $"{command}: --profile '{profileKeyRaw}' is not a known, enabled image-embedding profile.");
                return 64;
            }
        }
        else
        {
            // Same resolution order as the job handler: configured
            // Ai:PhotoSimilarityProfileKey wins, else the capability default.
            var configuredKey = aiOptions.Value.PhotoSimilarityProfileKey;
            var effectiveKey = !string.IsNullOrWhiteSpace(configuredKey)
                ? configuredKey
                : (await resolver.GetCapabilityAvailabilityAsync(AiCapabilities.ImageEmbedding)).ProfileKey;
            profile = string.IsNullOrWhiteSpace(effectiveKey)
                ? null
                : await registry.GetProfileByKeyAsync(effectiveKey!);
            if (profile is null || !profile.Enabled)
            {
                stdout.WriteLine($"{command}: no usable active image-embedding profile. No job enqueued.");
                return 0;
            }
        }

        var previewOptions = new VideoSemanticEmbeddingBackfillOptions
        {
            Limit = parsed.Limit,
            FailedOnly = parsed.FailedOnly,
            DryRun = true,
            TargetBlobObjectId = parsed.BlobObjectId,
            SegmentationVersion = parsed.SegmentationVersion,
        };
        var preview = await backfill.RunAsync(embedder: null, profile, previewOptions);

        if (parsed.DryRun)
        {
            stdout.WriteLine(
                $"{command} (dry-run): profile={profile.Key} {preview.Examined} video blob(s) would be selected. "
                + "No FFmpeg extraction, no inference, no writes, no job enqueued.");
            return 0;
        }

        if (preview.Examined == 0)
        {
            stdout.WriteLine($"{command}: profile={profile.Key} no eligible work selected. No job enqueued.");
            return 0;
        }

        var payload = new VideoSemanticEmbeddingsJobPayload(
            ProfileKey: profile.Key,
            BlobObjectId: parsed.BlobObjectId,
            SegmentationVersion: parsed.SegmentationVersion,
            Limit: parsed.Limit,
            FailedOnly: parsed.FailedOnly,
            DryRun: false);
        var idempotencyKey =
            $"{JobTypes.AiVideosEmbeddingsBackfill}:{profile.Key}:"
            + $"{parsed.SegmentationVersion?.ToString(CultureInfo.InvariantCulture) ?? "active"}:"
            + $"{parsed.FailedOnly}:{parsed.BlobObjectId?.ToString("N") ?? "all"}";

        var alreadyQueued = await db.BackgroundJobs.AsNoTracking().AnyAsync(j =>
            j.IdempotencyKey == idempotencyKey
            && (j.Status == JobStatuses.Queued || j.Status == JobStatuses.Running));
        var job = await queue.EnqueueAsync(
            JobTypes.AiVideosEmbeddingsBackfill, payload, idempotencyKey: idempotencyKey);

        stdout.WriteLine(
            $"{command}: {(alreadyQueued ? "matched existing" : "queued")} "
            + $"{JobTypes.AiVideosEmbeddingsBackfill} ({job.Id:N}) "
            + $"profile={profile.Key} limit={(parsed.Limit?.ToString(CultureInfo.InvariantCulture) ?? "-")} "
            + $"segmentation_version={(parsed.SegmentationVersion?.ToString(CultureInfo.InvariantCulture) ?? "active")} "
            + $"failed_only={parsed.FailedOnly} selected_targets={preview.Examined}.");
        return 0;
    }

    // ---- subcommand: ai video semantic faces backfill / retry-failed -------
    // VFACE-01. Deliberately shares the video-semantic option parsing (and adds
    // --analysis-version), so the CLI, the worker payload and the scheduler all
    // express the SAME bounded scope. It enqueues a job; it never analyses
    // in-process and never touches People or any person identity.
    internal static async Task<int> AiVideoFacesBackfillAsync(
        string[] args,
        IServiceProvider services,
        TextWriter stdout,
        TextWriter stderr,
        bool forceFailedOnly)
    {
        var command = forceFailedOnly
            ? "ai video semantic retry-failed faces"
            : "ai video semantic faces backfill";
        var profileKeyRaw = ReadOption(args, "--profile");
        var parsed = ParseVideoSemanticOptions(command, args, stderr, forceFailedOnly);
        if (parsed is null)
        {
            return 64;
        }

        int? analysisVersion = null;
        var analysisVersionRaw = ReadOption(args, "--analysis-version");
        if (analysisVersionRaw is not null)
        {
            if (!int.TryParse(analysisVersionRaw, out var v) || v <= 0)
            {
                stderr.WriteLine($"{command}: --analysis-version must be a positive integer.");
                return 64;
            }

            analysisVersion = v;
        }

        var backfill = services.GetService<VideoFaceAnalysisBackfillService>();
        var registry = services.GetService<IAiProfileRegistry>();
        var resolver = services.GetService<IAiBackendResolver>();
        var aiOptions = services.GetService<IOptions<AiOptions>>();
        var queue = services.GetService<IJobQueue>();
        var db = services.GetService<AppDbContext>();
        if (backfill is null || registry is null || resolver is null || aiOptions is null
            || queue is null || db is null)
        {
            stderr.WriteLine($"{command}: database is not configured. Set ConnectionStrings__Postgres and retry.");
            return 78;
        }

        AiProfile? profile;
        if (!string.IsNullOrWhiteSpace(profileKeyRaw))
        {
            profile = await registry.GetProfileByKeyAsync(profileKeyRaw!);
            if (profile is null || profile.Capability != AiCapabilities.FaceEmbedding || !profile.Enabled)
            {
                stderr.WriteLine(
                    $"{command}: --profile '{profileKeyRaw}' is not a known, enabled face profile.");
                return 64;
            }
        }
        else
        {
            // Same resolution order as the job handler: configured
            // Ai:FaceProfileKey wins, else the capability default.
            var configuredKey = aiOptions.Value.FaceProfileKey;
            var effectiveKey = !string.IsNullOrWhiteSpace(configuredKey)
                ? configuredKey
                : (await resolver.GetCapabilityAvailabilityAsync(AiCapabilities.FaceEmbedding)).ProfileKey;
            profile = string.IsNullOrWhiteSpace(effectiveKey)
                ? null
                : await registry.GetProfileByKeyAsync(effectiveKey!);
            if (profile is null || !profile.Enabled)
            {
                stdout.WriteLine($"{command}: no usable active face profile. No job enqueued.");
                return 0;
            }
        }

        // ALWAYS preview first (a pure count query, no writes).
        var previewOptions = new VideoFaceAnalysisBackfillOptions
        {
            Limit = parsed.Limit,
            FailedOnly = parsed.FailedOnly,
            DryRun = true,
            TargetBlobObjectId = parsed.BlobObjectId,
            SegmentationVersion = parsed.SegmentationVersion,
            AnalysisVersion = analysisVersion,
        };
        var preview = await backfill.RunAsync(detector: null, embedder: null, profile, previewOptions);

        if (parsed.DryRun)
        {
            stdout.WriteLine(
                $"{command} (dry-run): profile={profile.Key} {preview.Examined} video blob(s) would be selected. "
                + "No FFmpeg extraction, no inference, no writes, no job enqueued.");
            return 0;
        }

        if (preview.Examined == 0)
        {
            stdout.WriteLine($"{command}: profile={profile.Key} no eligible work selected. No job enqueued.");
            return 0;
        }

        var payload = new VideoFaceAnalysisJobPayload(
            BlobObjectId: parsed.BlobObjectId,
            SegmentationVersion: parsed.SegmentationVersion,
            AnalysisVersion: analysisVersion,
            DetectionProfileKey: profile.Key,
            EmbeddingProfileKey: profile.Key,
            Limit: parsed.Limit,
            FailedOnly: parsed.FailedOnly,
            DryRun: false);
        var idempotencyKey =
            $"{JobTypes.AiVideosFacesBackfill}:{profile.Key}:"
            + $"{parsed.SegmentationVersion?.ToString(CultureInfo.InvariantCulture) ?? "active"}:"
            + $"{analysisVersion?.ToString(CultureInfo.InvariantCulture) ?? "active"}:"
            + $"{parsed.FailedOnly}:{parsed.BlobObjectId?.ToString("N") ?? "all"}";

        var alreadyQueued = await db.BackgroundJobs.AsNoTracking().AnyAsync(j =>
            j.IdempotencyKey == idempotencyKey
            && (j.Status == JobStatuses.Queued || j.Status == JobStatuses.Running));
        var job = await queue.EnqueueAsync(
            JobTypes.AiVideosFacesBackfill, payload, idempotencyKey: idempotencyKey);

        stdout.WriteLine(
            $"{command}: {(alreadyQueued ? "matched existing" : "queued")} "
            + $"{JobTypes.AiVideosFacesBackfill} ({job.Id:N}) "
            + $"profile={profile.Key} limit={(parsed.Limit?.ToString(CultureInfo.InvariantCulture) ?? "-")} "
            + $"segmentation_version={(parsed.SegmentationVersion?.ToString(CultureInfo.InvariantCulture) ?? "active")} "
            + $"analysis_version={(analysisVersion?.ToString(CultureInfo.InvariantCulture) ?? "active")} "
            + $"failed_only={parsed.FailedOnly} selected_targets={preview.Examined}.");
        return 0;
    }

    // ---- subcommand: ai onnx face-embed (Gate 3C canary harness) ------------
    // Runs the REAL OnnxFaceBackend recognizer path on a non-sensitive FIXTURE
    // image (aligned-crop embed) with an in-code antelopev2 profile — NO DB, NO
    // storage. Drives bounded concurrency and emits the 512-d vector so the four
    // provider paths can be compared offline. This is a diagnostic harness (raw
    // vectors are the whole point here); it is not a general product endpoint.
    internal static async Task<int> AiOnnxFaceEmbedAsync(
        string[] args,
        IServiceProvider services,
        TextWriter stdout,
        TextWriter stderr)
    {
        var backend = services.GetService<OnnxFaceBackend>();
        var factory = services.GetService<NubArca.Api.Ai.Onnx.IOnnxInferenceSessionFactory>();
        if (backend is null || factory is null)
        {
            stderr.WriteLine("ai onnx face-embed: AI substrate is not registered.");
            return 78;
        }

        var file = ReadOption(args, "--file");
        if (string.IsNullOrWhiteSpace(file) || !File.Exists(file))
        {
            stderr.WriteLine("ai onnx face-embed: --file <fixture-image-path> is required and must exist.");
            return 64;
        }
        if (!TryReadNonNegativeInt(args, "--concurrency", 1, stderr, "ai onnx face-embed", out var concurrency)
            || !TryReadNonNegativeInt(args, "--iterations", 1, stderr, "ai onnx face-embed", out var iterations)
            || !TryReadNonNegativeInt(args, "--timeout-seconds", 120, stderr, "ai onnx face-embed", out var timeoutSeconds))
        {
            return 64;
        }
        concurrency = Math.Max(1, concurrency);
        iterations = Math.Max(1, iterations);

        var bytes = await File.ReadAllBytesAsync(file);
        var profile = new AiProfile
        {
            Id = Guid.NewGuid(),
            Key = OnnxFaceModels.Antelopev2ProfileKey,
            ConfigHash = OnnxFaceModels.Antelopev2Key,
            Capability = AiCapabilities.FaceEmbedding,
            Modality = AiModalities.Face,
            Dimension = 512,
            DistanceMetric = AiDistanceMetrics.Cosine,
            Enabled = true,
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)));
        var results = new System.Collections.Concurrent.ConcurrentBag<float[]>();
        var errors = new System.Collections.Concurrent.ConcurrentBag<string>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long firstMs = -1;

        var workers = Enumerable.Range(0, concurrency).Select(_ => Task.Run(async () =>
        {
            for (var i = 0; i < iterations; i++)
            {
                try
                {
                    var r = await backend.EmbedFaceAsync(bytes, profile, cts.Token);
                    Interlocked.CompareExchange(ref firstMs, sw.ElapsedMilliseconds, -1);
                    results.Add(r.Vector);
                }
                catch (Exception ex) { errors.Add(ex.GetType().Name); }
            }
        })).ToArray();
        await Task.WhenAll(workers);
        sw.Stop();

        if (!errors.IsEmpty)
        {
            stderr.WriteLine($"ai onnx face-embed: {errors.Count} failure(s): {string.Join(",", errors.Distinct())}");
            return 1;
        }

        var all = results.ToArray();
        var reference = all[0];
        double maxDrift = 0;
        var bad = false;
        foreach (var v in all)
        {
            if (v.Length != reference.Length) { bad = true; continue; }
            double dot = 0, na = 0, nb = 0;
            for (var i = 0; i < v.Length; i++)
            {
                if (float.IsNaN(v[i]) || float.IsInfinity(v[i])) bad = true;
                dot += (double)reference[i] * v[i];
                na += (double)reference[i] * reference[i];
                nb += (double)v[i] * v[i];
            }
            var cos = dot / (Math.Sqrt(na) * Math.Sqrt(nb));
            maxDrift = Math.Max(maxDrift, Math.Abs(1.0 - cos));
        }
        var l2 = Math.Sqrt(reference.Sum(x => (double)x * x));

        stdout.WriteLine($"commit={Environment.GetEnvironmentVariable("NUBARCA_GIT_SHA") ?? "unknown"}");
        stdout.WriteLine(
            $"dim={reference.Length} finite={!bad} count={all.Length} concurrency={concurrency} "
            + $"l2={l2.ToString("F6", CultureInfo.InvariantCulture)} "
            + $"maxCosDrift={maxDrift.ToString("E3", CultureInfo.InvariantCulture)} "
            + $"firstMs={firstMs} totalMs={sw.ElapsedMilliseconds}");
        // --detect: also run the DETECTOR in the SAME process. Both the detector and
        // the recognizer now route through IOnnxInferenceSessionFactory, so direct
        // mode exercises the COMPLETE in-process pipeline (per-model device). Decoded
        // geometry (score + normalized box + landmarks) is emitted deterministically
        // so detection EQUIVALENCE across providers is comparable, not only the
        // recognizer vector. Fixture-only diagnostic output — no user data.
        if (args.Contains("--detect"))
        {
            try
            {
                var det = await backend.DetectFacesAsync(bytes, profile, cts.Token);
                stdout.WriteLine($"detectOk=true detectFaces={det.Faces.Count} nativeCore={factory.NativeCoreState}");
                var idx = 0;
                foreach (var f in det.Faces.OrderByDescending(f => f.Confidence ?? 0))
                {
                    var lm = f.Landmarks is null
                        ? "-"
                        : string.Join(";", f.Landmarks.Select(p =>
                            $"{p.X.ToString("F5", CultureInfo.InvariantCulture)},{p.Y.ToString("F5", CultureInfo.InvariantCulture)}"));
                    stdout.WriteLine(
                        $"face[{idx++}] score={(f.Confidence ?? 0).ToString("F6", CultureInfo.InvariantCulture)} "
                        + $"box={f.X.ToString("F5", CultureInfo.InvariantCulture)},{f.Y.ToString("F5", CultureInfo.InvariantCulture)},"
                        + $"{f.Width.ToString("F5", CultureInfo.InvariantCulture)},{f.Height.ToString("F5", CultureInfo.InvariantCulture)} "
                        + $"lm={lm}");
                }
            }
            catch (Exception ex)
            {
                stdout.WriteLine($"detectOk=false detectError={ex.GetType().Name}");
            }
        }

        stdout.WriteLine("vec=" + string.Join(",", reference.Select(x => x.ToString("R", CultureInfo.InvariantCulture))));
        return bad ? 1 : 0;
    }

    // ---- subcommand: ai onnx image-embed (SigLIP direct equivalence) --------
    // DB-free fixture harness mirroring `ai onnx face-embed`: runs the REAL
    // OnnxImageEmbedder pipeline (decode → preprocess → inference → finalize)
    // under the configured execution provider and emits deterministic
    // diagnostics + the reference vector for cross-provider comparison.
    // Fixture-only output — never user data, never persisted.
    internal static async Task<int> AiOnnxImageEmbedAsync(
        string[] args,
        IServiceProvider services,
        TextWriter stdout,
        TextWriter stderr)
    {
        var embedder = services.GetService<OnnxImageEmbedder>();
        var factory = services.GetService<NubArca.Api.Ai.Onnx.IOnnxInferenceSessionFactory>();
        if (embedder is null || factory is null)
        {
            stderr.WriteLine("ai onnx image-embed: AI substrate is not registered.");
            return 78;
        }

        var file = ReadOption(args, "--file");
        var dir = ReadOption(args, "--dir");
        if ((string.IsNullOrWhiteSpace(file) || !File.Exists(file))
            && (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)))
        {
            stderr.WriteLine("ai onnx image-embed: --file <fixture-image-path> or --dir <fixture-dir> is required and must exist.");
            return 64;
        }
        if (!TryReadNonNegativeInt(args, "--concurrency", 1, stderr, "ai onnx image-embed", out var concurrency)
            || !TryReadNonNegativeInt(args, "--iterations", 1, stderr, "ai onnx image-embed", out var iterations)
            || !TryReadNonNegativeInt(args, "--timeout-seconds", 300, stderr, "ai onnx image-embed", out var timeoutSeconds))
        {
            return 64;
        }
        concurrency = Math.Max(1, concurrency);
        iterations = Math.Max(1, iterations);

        var profile = BuildEphemeralPhotoProfile(
            ReadOption(args, "--profile") ?? OnnxImageModels.SiglipSo400mProfileKey, stderr, "ai onnx image-embed");
        if (profile is null) return 64;

        // Batch mode (--dir): one process embeds EVERY fixture in the directory —
        // per-fixture reference vectors in one model-load/compile, for the
        // cross-provider equivalence + ranking harness.
        if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
        {
            using var batchCts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)));
            var files = Directory.EnumerateFiles(dir)
                .Where(f => new[] { ".jpg", ".jpeg", ".png", ".webp" }
                    .Contains(Path.GetExtension(f).ToLowerInvariant()))
                .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal)
                .ToArray();
            if (files.Length == 0)
            {
                stderr.WriteLine("ai onnx image-embed: --dir contains no image fixtures.");
                return 64;
            }

            stdout.WriteLine($"commit={Environment.GetEnvironmentVariable("NUBARCA_GIT_SHA") ?? "unknown"}");
            var batchSw = System.Diagnostics.Stopwatch.StartNew();
            foreach (var path in files)
            {
                try
                {
                    var r = await embedder.EmbedImageAsync(await File.ReadAllBytesAsync(path, batchCts.Token), profile, batchCts.Token);
                    var name = Path.GetFileName(path);
                    var norm = Math.Sqrt(r.Vector.Sum(x => (double)x * x));
                    stdout.WriteLine(
                        $"img[{name}].meta=dim:{r.Dimension},l2:{norm.ToString("F6", CultureInfo.InvariantCulture)}");
                    stdout.WriteLine($"img[{name}].vec="
                        + string.Join(",", r.Vector.Select(x => x.ToString("R", CultureInfo.InvariantCulture))));
                }
                catch (Exception ex)
                {
                    stderr.WriteLine($"ai onnx image-embed: {Path.GetFileName(path)} failed: {ex.GetType().Name}");
                    return 1;
                }
            }
            stdout.WriteLine($"batchCount={files.Length} totalMs={batchSw.ElapsedMilliseconds} nativeCore={factory.NativeCoreState}");
            return 0;
        }

        var bytes = await File.ReadAllBytesAsync(file!);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)));
        var results = new System.Collections.Concurrent.ConcurrentBag<float[]>();
        var errors = new System.Collections.Concurrent.ConcurrentBag<string>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long firstMs = -1;

        var workers = Enumerable.Range(0, concurrency).Select(_ => Task.Run(async () =>
        {
            for (var i = 0; i < iterations; i++)
            {
                try
                {
                    var r = await embedder.EmbedImageAsync(bytes, profile, cts.Token);
                    Interlocked.CompareExchange(ref firstMs, sw.ElapsedMilliseconds, -1);
                    results.Add(r.Vector);
                }
                catch (Exception ex) { errors.Add(ex.GetType().Name); }
            }
        })).ToArray();
        await Task.WhenAll(workers);
        sw.Stop();

        if (!errors.IsEmpty)
        {
            stderr.WriteLine($"ai onnx image-embed: {errors.Count} failure(s): {string.Join(",", errors.Distinct())}");
            return 1;
        }

        return EmitEmbedDiagnostics(
            results.ToArray(), concurrency, firstMs, sw.ElapsedMilliseconds, factory, stdout);
    }

    // ---- subcommand: ai onnx text-embed (SigLIP direct equivalence) ---------
    // DB-free query harness: tokenizes with the REAL production tokenizer
    // (Tokenizers.HuggingFace over the exported tokenizer.json — ids emitted so
    // tokenizer equivalence vs. the Python reference is directly comparable),
    // then runs the REAL OnnxTextEmbedder pipeline under the configured
    // execution provider. Fixture-only output — never user data, never persisted.
    internal static async Task<int> AiOnnxTextEmbedAsync(
        string[] args,
        IServiceProvider services,
        TextWriter stdout,
        TextWriter stderr)
    {
        var embedder = services.GetService<OnnxTextEmbedder>();
        var factory = services.GetService<NubArca.Api.Ai.Onnx.IOnnxInferenceSessionFactory>();
        var options = services.GetService<IOptions<AiOptions>>();
        if (embedder is null || factory is null || options is null)
        {
            stderr.WriteLine("ai onnx text-embed: AI substrate is not registered.");
            return 78;
        }

        var text = ReadOption(args, "--text");
        var queriesFile = ReadOption(args, "--queries-file");
        if (string.IsNullOrWhiteSpace(text)
            && (string.IsNullOrWhiteSpace(queriesFile) || !File.Exists(queriesFile)))
        {
            stderr.WriteLine("ai onnx text-embed: --text <query> or --queries-file <path> is required.");
            return 64;
        }
        if (!TryReadNonNegativeInt(args, "--iterations", 1, stderr, "ai onnx text-embed", out var iterations)
            || !TryReadNonNegativeInt(args, "--timeout-seconds", 300, stderr, "ai onnx text-embed", out var timeoutSeconds))
        {
            return 64;
        }
        iterations = Math.Max(1, iterations);

        var profile = BuildEphemeralPhotoProfile(
            ReadOption(args, "--profile") ?? OnnxImageModels.SiglipSo400mProfileKey, stderr, "ai onnx text-embed");
        if (profile is null) return 64;
        var config = OnnxImageModels.ResolveConfig(profile.ConfigHash, profile.Key)!;

        // Token-level diagnostics with the SAME tokenizer asset + call the
        // embedder uses, so Python↔.NET tokenizer equivalence is checkable.
        var modelDir = options.Value.Onnx.ModelDir;
        Tokenizers.HuggingFace.Tokenizer.Tokenizer? diagTokenizer = null;
        if (!string.IsNullOrWhiteSpace(modelDir) && config.TokenizerFile is not null)
        {
            var tokenizerPath = Path.Combine(modelDir, config.ModelSubdir, config.TokenizerFile);
            if (File.Exists(tokenizerPath))
            {
                diagTokenizer = Tokenizers.HuggingFace.Tokenizer.Tokenizer.FromFile(tokenizerPath);
            }
        }
        using var _diagTokenizer = diagTokenizer;

        // Batch mode (--queries-file): one process embeds EVERY non-empty line —
        // per-query token ids + vectors in one model-load/compile, for the
        // cross-provider equivalence + ranking harness.
        if (!string.IsNullOrWhiteSpace(queriesFile) && File.Exists(queriesFile))
        {
            using var batchCts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)));
            var queries = (await File.ReadAllLinesAsync(queriesFile, batchCts.Token))
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToArray();
            if (queries.Length == 0)
            {
                stderr.WriteLine("ai onnx text-embed: --queries-file contains no queries.");
                return 64;
            }

            stdout.WriteLine($"commit={Environment.GetEnvironmentVariable("NUBARCA_GIT_SHA") ?? "unknown"}");
            var batchSw = System.Diagnostics.Stopwatch.StartNew();
            for (var i = 0; i < queries.Length; i++)
            {
                try
                {
                    if (diagTokenizer is not null)
                    {
                        var enc = diagTokenizer.Encode(queries[i], addSpecialTokens: true, includeAttentionMask: true).First();
                        stdout.WriteLine($"q[{i}].ids=" + string.Join(",", enc.Ids));
                    }
                    var r = await embedder.EmbedTextAsync(queries[i], profile, batchCts.Token);
                    var norm = Math.Sqrt(r.Vector.Sum(x => (double)x * x));
                    stdout.WriteLine(
                        $"q[{i}].meta=dim:{r.Dimension},l2:{norm.ToString("F6", CultureInfo.InvariantCulture)}");
                    stdout.WriteLine($"q[{i}].vec="
                        + string.Join(",", r.Vector.Select(x => x.ToString("R", CultureInfo.InvariantCulture))));
                }
                catch (Exception ex)
                {
                    stderr.WriteLine($"ai onnx text-embed: query {i} failed: {ex.GetType().Name}");
                    return 1;
                }
            }
            stdout.WriteLine($"batchCount={queries.Length} totalMs={batchSw.ElapsedMilliseconds} nativeCore={factory.NativeCoreState}");
            return 0;
        }

        if (diagTokenizer is not null)
        {
            var encoding = diagTokenizer.Encode(text!, addSpecialTokens: true, includeAttentionMask: true).First();
            stdout.WriteLine("ids=" + string.Join(",", encoding.Ids));
            stdout.WriteLine("tokMask=" + string.Join(",", encoding.AttentionMask));
            stdout.WriteLine("runMask=" + string.Join(",",
                OnnxTextEmbedder.BuildFixedPaddingAttentionMask(config.TextSequenceLength)));
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)));
        var results = new List<float[]>(iterations);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long firstMs = -1;
        try
        {
            for (var i = 0; i < iterations; i++)
            {
                var r = await embedder.EmbedTextAsync(text!, profile, cts.Token);
                if (firstMs < 0) firstMs = sw.ElapsedMilliseconds;
                results.Add(r.Vector);
            }
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"ai onnx text-embed: failed: {ex.GetType().Name}");
            return 1;
        }
        sw.Stop();

        return EmitEmbedDiagnostics(
            results.ToArray(), concurrency: 1, firstMs, sw.ElapsedMilliseconds, factory, stdout);
    }

    // Ephemeral in-memory photo profile for the DB-free harnesses (identical
    // contract to the seeded production profile; never persisted).
    private static AiProfile? BuildEphemeralPhotoProfile(string profileKey, TextWriter stderr, string verb)
    {
        var config = OnnxImageModels.ResolveConfig(configHash: null, profileKey: profileKey);
        if (config is null)
        {
            stderr.WriteLine($"{verb}: unknown ONNX photo profile '{profileKey}'.");
            return null;
        }

        return new AiProfile
        {
            Id = Guid.NewGuid(),
            Key = profileKey,
            ConfigHash = config.Key,
            Capability = AiCapabilities.ImageEmbedding,
            Modality = AiModalities.Image,
            Dimension = config.Dimension,
            DistanceMetric = AiDistanceMetrics.Cosine,
            Enabled = true,
        };
    }

    // Shared deterministic embed diagnostics + reference vector emission (same
    // fields the face harness emits, so compare tooling is reusable).
    private static int EmitEmbedDiagnostics(
        float[][] all, int concurrency, long firstMs, long totalMs,
        NubArca.Api.Ai.Onnx.IOnnxInferenceSessionFactory factory, TextWriter stdout)
    {
        var reference = all[0];
        double maxDrift = 0;
        var bad = false;
        foreach (var v in all)
        {
            if (v.Length != reference.Length) { bad = true; continue; }
            double dot = 0, na = 0, nb = 0;
            for (var i = 0; i < v.Length; i++)
            {
                if (float.IsNaN(v[i]) || float.IsInfinity(v[i])) bad = true;
                dot += (double)reference[i] * v[i];
                na += (double)reference[i] * reference[i];
                nb += (double)v[i] * v[i];
            }
            var cos = dot / (Math.Sqrt(na) * Math.Sqrt(nb));
            maxDrift = Math.Max(maxDrift, Math.Abs(1.0 - cos));
        }
        var l2 = Math.Sqrt(reference.Sum(x => (double)x * x));

        stdout.WriteLine($"commit={Environment.GetEnvironmentVariable("NUBARCA_GIT_SHA") ?? "unknown"}");
        stdout.WriteLine(
            $"dim={reference.Length} finite={!bad} count={all.Length} concurrency={concurrency} "
            + $"l2={l2.ToString("F6", CultureInfo.InvariantCulture)} "
            + $"maxCosDrift={maxDrift.ToString("E3", CultureInfo.InvariantCulture)} "
            + $"firstMs={firstMs} totalMs={totalMs} nativeCore={factory.NativeCoreState}");
        stdout.WriteLine("vec=" + string.Join(",", reference.Select(x => x.ToString("R", CultureInfo.InvariantCulture))));
        return bad ? 1 : 0;
    }

    // ---- subcommand: ai onnx image (Phase 2A evaluation harness) ------------
    // Read-only operator tooling: list candidate models, seed eval profiles,
    // benchmark, embed-test, and compare. Never writes embeddings/status rows
    // and never prints raw vectors or internal storage identifiers.
    internal static async Task<int> AiOnnxImageAsync(
        string[] args,
        IServiceProvider services,
        TextWriter stdout,
        TextWriter stderr)
    {
        var sub = args.Length > 0 ? args[0] : string.Empty;
        var rest = args.Length > 0 ? args[1..] : args;

        if (sub == "seed-profiles")
        {
            var registry = services.GetService<IAiProfileRegistry>();
            if (registry is null)
            {
                stderr.WriteLine("ai onnx image seed-profiles: database is not configured. Set ConnectionStrings__Postgres and retry.");
                return 78;
            }
            var seeded = await registry.SeedOnnxImageEvalProfilesAsync();
            stdout.WriteLine(
                $"ai onnx image seed-profiles: ensured ONNX eval models/profiles "
                + $"(models_created={seeded.ModelsCreated} profiles_created={seeded.ProfilesCreated}). "
                + "Evaluation only — NOT default, inert until model files exist under Ai__Onnx__ModelDir.");
            return 0;
        }

        var eval = services.GetService<OnnxImageEvaluationService>();
        if (eval is null)
        {
            stderr.WriteLine("ai onnx image: database is not configured. Set ConnectionStrings__Postgres and retry.");
            return 78;
        }

        switch (sub)
        {
            case "models":
            {
                var models = eval.ListModels();
                stdout.WriteLine("ai onnx image models:");
                foreach (var m in models)
                {
                    stdout.WriteLine(
                        $"  {m.ModelKey,-28} profile={m.ProfileKey} input={m.InputSize} resize={m.ResizeMode} "
                        + $"dim={m.Dimension} modeldir_configured={m.ModelDirConfigured} model_present={m.ModelPresent} "
                        + $"text_model_present={m.TextModelPresent} tokenizer_present={m.TokenizerPresent}");
                }
                return 0;
            }

            case "benchmark":
            {
                var profileKey = ReadOption(rest, "--profile");
                if (string.IsNullOrWhiteSpace(profileKey))
                {
                    stderr.WriteLine("ai onnx image benchmark: --profile <profile-key> is required.");
                    return 64;
                }
                if (!TryReadPositiveInt(rest, "--limit", 25, stderr, "ai onnx image benchmark", out var limit))
                {
                    return 64;
                }
                var r = await eval.BenchmarkAsync(profileKey, limit);
                if (!r.Available)
                {
                    stdout.WriteLine($"ai onnx image benchmark: unavailable ({r.UnavailableReason}) — nothing processed (dry-run).");
                    return 0;
                }
                stdout.WriteLine(
                    $"ai onnx image benchmark (dry-run, no writes): profile={r.ProfileKey} dim={(r.Dimension?.ToString() ?? "-")} "
                    + $"attempted={r.Attempted} succeeded={r.Succeeded} failed={r.Failed} "
                    + $"avg_ms={Fmt(r.AvgMs)} p50_ms={Fmt(r.P50Ms)} p95_ms={Fmt(r.P95Ms)}");
                return 0;
            }

            case "embed-test":
            {
                var profileKey = ReadOption(rest, "--profile");
                var fileRaw = ReadOption(rest, "--file");
                if (string.IsNullOrWhiteSpace(profileKey))
                {
                    stderr.WriteLine("ai onnx image embed-test: --profile <profile-key> is required.");
                    return 64;
                }
                if (string.IsNullOrWhiteSpace(fileRaw) || !Guid.TryParse(fileRaw, out var fileId))
                {
                    stderr.WriteLine("ai onnx image embed-test: --file <file-id> (a GUID) is required.");
                    return 64;
                }
                var r = await eval.EmbedTestAsync(fileId, profileKey);
                if (!r.Available)
                {
                    stdout.WriteLine($"ai onnx image embed-test: unavailable ({r.UnavailableReason}).");
                    return 0;
                }
                if (!r.Found)
                {
                    stdout.WriteLine("ai onnx image embed-test: file not found.");
                    return 0;
                }
                stdout.WriteLine(
                    $"ai onnx image embed-test: ok dim={(r.Dimension?.ToString() ?? "-")} "
                    + $"l2_norm={Fmt(r.L2Norm)} finite={r.Finite} ms={Fmt(r.Ms)}");
                return 0;
            }

            case "compare":
            {
                var profileKey = ReadOption(rest, "--profile");
                var fileRaw = ReadOption(rest, "--file");
                if (string.IsNullOrWhiteSpace(profileKey))
                {
                    stderr.WriteLine("ai onnx image compare: --profile <profile-key> is required.");
                    return 64;
                }
                if (string.IsNullOrWhiteSpace(fileRaw) || !Guid.TryParse(fileRaw, out var fileId))
                {
                    stderr.WriteLine("ai onnx image compare: --file <file-id> (a GUID) is required.");
                    return 64;
                }
                if (!TryReadPositiveInt(rest, "--limit", 10, stderr, "ai onnx image compare", out var limit)
                    || !TryReadPositiveInt(rest, "--candidate-limit", 50, stderr, "ai onnx image compare", out var candidateLimit))
                {
                    return 64;
                }
                var r = await eval.CompareAsync(fileId, profileKey, limit, candidateLimit);
                if (!r.Available)
                {
                    stdout.WriteLine($"ai onnx image compare: unavailable ({r.UnavailableReason}).");
                    return 0;
                }
                if (!r.Found)
                {
                    stdout.WriteLine("ai onnx image compare: file not found.");
                    return 0;
                }
                if (r.Items.Count == 0)
                {
                    stdout.WriteLine("ai onnx image compare: no comparable candidates.");
                    return 0;
                }
                stdout.WriteLine($"ai onnx image compare: top {r.Items.Count} for {fileId:N} (profile={profileKey})");
                foreach (var item in r.Items)
                {
                    stdout.WriteLine($"  {item.Score:F6}  {item.Name}");
                }
                return 0;
            }

            default:
                stderr.WriteLine($"ai onnx image: unknown subcommand '{sub}'. One of: models, seed-profiles, benchmark, embed-test, compare.");
                return 64;
        }
    }

    // ---- subcommand: ai face (face model evaluation harness) ----------------
    // Read-only operator tooling to EVALUATE local ONNX face-recognition models
    // (detector + ArcFace recognition) before any People/Face feature is built.
    // Never writes detections/embeddings/clusters, never names/identifies faces,
    // never prints raw vectors or internal storage identifiers, and never touches
    // Private Vault content. Face processing stays disabled by default.
    internal static async Task<int> AiFaceAsync(
        string[] args,
        IServiceProvider services,
        TextWriter stdout,
        TextWriter stderr)
    {
        var sub = args.Length > 0 ? args[0] : string.Empty;
        var rest = args.Length > 0 ? args[1..] : args;

        if (sub == "seed-profiles")
        {
            var registry = services.GetService<IAiProfileRegistry>();
            if (registry is null)
            {
                stderr.WriteLine("ai face seed-profiles: database is not configured. Set ConnectionStrings__Postgres and retry.");
                return 78;
            }
            var seeded = await registry.SeedOnnxFaceEvalProfilesAsync();
            stdout.WriteLine(
                $"ai face seed-profiles: ensured face eval models/profiles "
                + $"(models_created={seeded.ModelsCreated} profiles_created={seeded.ProfilesCreated}). "
                + "Evaluation only — NOT default, inert until model files exist under Ai__Onnx__ModelDir.");
            return 0;
        }

        var eval = services.GetService<OnnxFaceEvaluationService>();
        if (eval is null)
        {
            stderr.WriteLine("ai face: database is not configured. Set ConnectionStrings__Postgres and retry.");
            return 78;
        }

        // --profile is optional; falls back to Ai__FaceProfileKey when configured.
        string? ProfileOrDefault() => ReadOption(rest, "--profile") ?? eval.ConfiguredProfileKey;

        switch (sub)
        {
            case "models":
            {
                var models = eval.ListModels();
                stdout.WriteLine("ai face models:");
                foreach (var m in models)
                {
                    stdout.WriteLine(
                        $"  {m.ModelKey,-12} profile={m.ProfileKey} capability={m.Capability} "
                        + $"det_input={m.DetectorInputSize} rec_input={m.RecognitionInputSize} "
                        + $"landmarks={m.LandmarkCount} dim={m.Dimension} metric={m.DistanceMetric} "
                        + $"modeldir_configured={m.ModelDirConfigured} detector_present={m.DetectorPresent} "
                        + $"recognition_present={m.RecognitionPresent}");
                    stdout.WriteLine($"      license: {m.LicenseNote}");
                }
                return 0;
            }

            case "detect-test":
            {
                var profileKey = ProfileOrDefault();
                var fileRaw = ReadOption(rest, "--file");
                if (string.IsNullOrWhiteSpace(profileKey))
                {
                    stderr.WriteLine("ai face detect-test: --profile <profile-key> is required (or set Ai__FaceProfileKey).");
                    return 64;
                }
                if (string.IsNullOrWhiteSpace(fileRaw) || !Guid.TryParse(fileRaw, out var fileId))
                {
                    stderr.WriteLine("ai face detect-test: --file <file-id> (a GUID) is required.");
                    return 64;
                }
                var r = await eval.DetectTestAsync(fileId, profileKey);
                if (!r.Available)
                {
                    stdout.WriteLine($"ai face detect-test: unavailable ({r.UnavailableReason}).");
                    return 0;
                }
                if (!r.Found)
                {
                    stdout.WriteLine("ai face detect-test: file not found.");
                    return 0;
                }
                stdout.WriteLine(
                    $"ai face detect-test: faces={r.FaceCount} image={r.ImageWidth}x{r.ImageHeight}"
                    + (r.Diagnostic is { } d ? $" diagnostic={d}" : string.Empty));
                for (var i = 0; i < r.Faces.Count; i++)
                {
                    var f = r.Faces[i];
                    stdout.WriteLine(
                        $"  [{i}] score={Fmt4(f.Score)} box=({f.X:F4},{f.Y:F4},{f.Width:F4},{f.Height:F4}) "
                        + $"landmarks={f.HasLandmarks}");
                }
                return 0;
            }

            case "embed-test":
            {
                var profileKey = ProfileOrDefault();
                var fileRaw = ReadOption(rest, "--file");
                if (string.IsNullOrWhiteSpace(profileKey))
                {
                    stderr.WriteLine("ai face embed-test: --profile <profile-key> is required (or set Ai__FaceProfileKey).");
                    return 64;
                }
                if (string.IsNullOrWhiteSpace(fileRaw) || !Guid.TryParse(fileRaw, out var fileId))
                {
                    stderr.WriteLine("ai face embed-test: --file <file-id> (a GUID) is required.");
                    return 64;
                }
                if (!TryReadNonNegativeInt(rest, "--face-index", 0, stderr, "ai face embed-test", out var faceIndex))
                {
                    return 64;
                }
                var r = await eval.EmbedTestAsync(fileId, profileKey, faceIndex);
                if (!r.Available)
                {
                    stdout.WriteLine($"ai face embed-test: unavailable ({r.UnavailableReason}).");
                    return 0;
                }
                if (!r.Found)
                {
                    stdout.WriteLine("ai face embed-test: file not found.");
                    return 0;
                }
                if (r.Dimension is null)
                {
                    stdout.WriteLine(
                        $"ai face embed-test: no embedding (faces={r.FaceCount} face_index={faceIndex} "
                        + $"detect_ms={Fmt(r.DetectMs)}"
                        + (r.Diagnostic is { } d ? $" diagnostic={d}" : string.Empty) + ").");
                    return 0;
                }
                stdout.WriteLine(
                    $"ai face embed-test: ok faces={r.FaceCount} face_index={r.FaceIndex} dim={r.Dimension} "
                    + $"l2_norm={Fmt(r.L2Norm)} finite={r.Finite} detect_ms={Fmt(r.DetectMs)} embed_ms={Fmt(r.EmbedMs)}");
                return 0;
            }

            case "compare":
            {
                var profileKey = ProfileOrDefault();
                var rawA = ReadOption(rest, "--file-a");
                var rawB = ReadOption(rest, "--file-b");
                if (string.IsNullOrWhiteSpace(profileKey))
                {
                    stderr.WriteLine("ai face compare: --profile <profile-key> is required (or set Ai__FaceProfileKey).");
                    return 64;
                }
                if (string.IsNullOrWhiteSpace(rawA) || !Guid.TryParse(rawA, out var fileA)
                    || string.IsNullOrWhiteSpace(rawB) || !Guid.TryParse(rawB, out var fileB))
                {
                    stderr.WriteLine("ai face compare: --file-a <id> and --file-b <id> (GUIDs) are required.");
                    return 64;
                }
                if (!TryReadNonNegativeInt(rest, "--face-a", 0, stderr, "ai face compare", out var faceA)
                    || !TryReadNonNegativeInt(rest, "--face-b", 0, stderr, "ai face compare", out var faceB))
                {
                    return 64;
                }
                var r = await eval.CompareAsync(fileA, faceA, fileB, faceB, profileKey);
                if (!r.Available)
                {
                    stdout.WriteLine($"ai face compare: unavailable ({r.UnavailableReason}).");
                    return 0;
                }
                if (!r.FoundA || !r.FoundB)
                {
                    stdout.WriteLine($"ai face compare: file not found (a_found={r.FoundA} b_found={r.FoundB}).");
                    return 0;
                }
                if (!r.HasScore)
                {
                    stdout.WriteLine(
                        $"ai face compare: could not embed both faces "
                        + $"(faces_a={r.FaceCountA} faces_b={r.FaceCountB}).");
                    return 0;
                }
                stdout.WriteLine(
                    $"ai face compare: cosine={Fmt6(r.Cosine)} distance={Fmt6(r.Distance)} "
                    + $"(faces_a={r.FaceCountA} faces_b={r.FaceCountB})");
                return 0;
            }

            case "benchmark":
            {
                var profileKey = ProfileOrDefault();
                if (string.IsNullOrWhiteSpace(profileKey))
                {
                    stderr.WriteLine("ai face benchmark: --profile <profile-key> is required (or set Ai__FaceProfileKey).");
                    return 64;
                }
                if (!TryReadPositiveInt(rest, "--limit", 100, stderr, "ai face benchmark", out var limit))
                {
                    return 64;
                }
                var r = await eval.BenchmarkAsync(profileKey, limit);
                if (!r.Available)
                {
                    stdout.WriteLine($"ai face benchmark: unavailable ({r.UnavailableReason}) — nothing processed (dry-run).");
                    return 0;
                }
                stdout.WriteLine(
                    $"ai face benchmark (dry-run, no writes): profile={r.ProfileKey} dim={(r.Dimension?.ToString() ?? "-")} "
                    + $"images_attempted={r.ImagesAttempted} succeeded={r.ImagesSucceeded} failed={r.ImagesFailed}");
                stdout.WriteLine(
                    $"  faces_detected={r.FacesDetected} zero_face_images={r.ZeroFaceImages} "
                    + $"faces_embedded={r.FacesEmbedded} avg_faces_per_image={Fmt(r.AvgFacesPerImage)}");
                stdout.WriteLine(
                    $"  detect_ms avg={Fmt(r.DetectAvgMs)} p50={Fmt(r.DetectP50Ms)} p95={Fmt(r.DetectP95Ms)}");
                stdout.WriteLine(
                    $"  embed_ms  avg={Fmt(r.EmbedAvgMs)} p50={Fmt(r.EmbedP50Ms)} p95={Fmt(r.EmbedP95Ms)}");
                if (r.FailureReasons.Count > 0)
                {
                    var reasons = string.Join(" ", r.FailureReasons.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"));
                    stdout.WriteLine($"  failures: {reasons}");
                }
                return 0;
            }

            case "sample-pairs":
            {
                var profileKey = ProfileOrDefault();
                if (string.IsNullOrWhiteSpace(profileKey))
                {
                    stderr.WriteLine("ai face sample-pairs: --profile <profile-key> is required (or set Ai__FaceProfileKey).");
                    return 64;
                }
                if (!TryReadPositiveInt(rest, "--limit", 25, stderr, "ai face sample-pairs", out var limit))
                {
                    return 64;
                }
                var r = await eval.SamplePairsAsync(profileKey, limit);
                if (!r.Available)
                {
                    stdout.WriteLine($"ai face sample-pairs: unavailable ({r.UnavailableReason}).");
                    return 0;
                }
                if (r.Items.Count == 0)
                {
                    stdout.WriteLine("ai face sample-pairs: no images with detected faces in the sample.");
                    return 0;
                }
                stdout.WriteLine($"ai face sample-pairs: {r.Items.Count} image(s) with faces (pick pairs for `ai face compare`)");
                foreach (var item in r.Items)
                {
                    stdout.WriteLine($"  {item.FileItemId:N}  faces={item.FaceCount,-3} {item.Name}");
                }
                return 0;
            }

            default:
                stderr.WriteLine(
                    $"ai face: unknown subcommand '{sub}'. One of: models, seed-profiles, detect-test, "
                    + "embed-test, compare, benchmark, sample-pairs.");
                return 64;
        }
    }

    // ---- subcommand: ai faces (Face Substrate v0 operator tooling) ----------
    // Aggregate, sanitized, owner-safe: coverage, bounded detection/embedding
    // backfills, vector-sync repair, diagnostics, and an owner-scoped `similar`
    // lookup. Output is counts/keys/dimensions/scores/booleans only — never raw
    // vectors, BlobObjectId, SHA, StorageKey, physical paths, or model internals.
    // Face processing stays OFF by default; backfills refuse to run unless the
    // relevant Ai__Face*Enabled flag is true.
    internal static async Task<int> AiFacesAsync(
        string[] args, IServiceProvider services, TextWriter stdout, TextWriter stderr)
    {
        var sub = args.Length > 0 ? args[0] : string.Empty;
        var rest = args.Length > 0 ? args[1..] : args;

        var options = services.GetService<IOptions<AiOptions>>()?.Value;
        if (options is null)
        {
            stderr.WriteLine("ai faces: AI is not configured in this host.");
            return 78;
        }

        switch (sub)
        {
            case "coverage":
            {
                var profileKey = ReadOption(rest, "--profile") ?? options.FaceProfileKey;
                if (string.IsNullOrWhiteSpace(profileKey))
                {
                    stderr.WriteLine("ai faces coverage: --profile <profile-key> is required (or set Ai__FaceProfileKey).");
                    return 64;
                }

                var coverage = services.GetRequiredService<FaceCoverageService>();
                var c = await coverage.GetCoverageAsync(profileKey);
                if (c is null)
                {
                    stderr.WriteLine($"ai faces coverage: profile '{profileKey}' not found.");
                    return 64;
                }

                stdout.WriteLine($"profile={c.ProfileKey}");
                stdout.WriteLine($"dimension={(c.Dimension?.ToString() ?? "-")}");
                stdout.WriteLine($"distance_metric={(c.DistanceMetric ?? "-")}");
                stdout.WriteLine($"eligible_images={c.EligibleImages}");
                stdout.WriteLine($"detection_completed_blobs={c.DetectionCompletedBlobs}");
                stdout.WriteLine($"detection_missing_blobs={c.DetectionMissingBlobs}");
                stdout.WriteLine($"faces_detected={c.FacesDetected}");
                stdout.WriteLine($"embeddings_completed={c.EmbeddingsCompleted}");
                stdout.WriteLine($"embeddings_missing={c.EmbeddingsMissing}");
                stdout.WriteLine($"embeddings_failed={c.EmbeddingsFailed}");
                stdout.WriteLine($"embeddings_skipped={c.EmbeddingsSkipped}");
                stdout.WriteLine($"embedding_coverage_percent={c.EmbeddingCoveragePercent:0.##}");
                stdout.WriteLine($"vector_supported={c.VectorSupported}");
                stdout.WriteLine($"vector_indexed={c.VectorIndexed}");
                stdout.WriteLine($"missing_vectors={c.MissingVectors}");
                stdout.WriteLine($"vector_coverage_percent={c.VectorCoveragePercent:0.##}");
                return 0;
            }

            case "diagnostics":
            {
                var diag = await services.GetRequiredService<FaceDiagnosticsService>().GetAsync();
                stdout.WriteLine("ai faces diagnostics:");
                stdout.WriteLine($"  ai_enabled={diag.AiEnabled}");
                stdout.WriteLine($"  face_detection_enabled={diag.FaceDetectionEnabled}");
                stdout.WriteLine($"  face_embeddings_enabled={diag.FaceEmbeddingsEnabled}");
                stdout.WriteLine($"  face_clustering_enabled={diag.FaceClusteringEnabled}");
                stdout.WriteLine($"  active_face_profile={(diag.ActiveFaceProfileKey ?? "(unset)")}");
                stdout.WriteLine($"  model_dir_configured={diag.ModelDirConfigured}");
                stdout.WriteLine($"  onnx_intra_op_threads={(diag.OnnxIntraOpThreads?.ToString() ?? "(default)")}");
                stdout.WriteLine($"  max_concurrency={diag.MaxConcurrency}");
                var t = diag.Thresholds;
                stdout.WriteLine($"  cluster_similarity_threshold={t.ClusterSimilarityThreshold:0.###}");
                stdout.WriteLine($"  candidate_similarity_threshold={t.CandidateSimilarityThreshold:0.###}");
                stdout.WriteLine($"  search_default_similarity_threshold={t.SearchDefaultSimilarityThreshold:0.###}");
                stdout.WriteLine($"  search_min_similarity={t.SearchMinSimilarity:0.###}");
                stdout.WriteLine($"  search_max_similarity={t.SearchMaxSimilarity:0.###}");
                stdout.WriteLine($"  max_faces_per_image={t.MaxFacesPerImage}");
                var cl = diag.Clustering;
                stdout.WriteLine($"  clustering_mode={cl.Mode}");
                stdout.WriteLine($"  knn_neighbors={cl.KnnNeighbors}");
                stdout.WriteLine($"  knn_ef_search={cl.KnnEfSearch}");
                stdout.WriteLine($"  knn_min_similarity={cl.KnnMinSimilarity:0.###}");
                stdout.WriteLine($"  knn_candidate_similarity={cl.KnnCandidateSimilarity:0.###}");
                stdout.WriteLine($"  knn_max_eligible_faces_per_run={cl.KnnMaxEligibleFacesPerRun}");
                stdout.WriteLine($"  exact_max_faces_to_cluster={cl.ExactMaxFacesToCluster}");
                foreach (var m in diag.Models)
                {
                    stdout.WriteLine(
                        $"  model {m.ProfileKey}: dim={m.Dimension} detector_present={m.DetectorPresent} "
                        + $"recognition_present={m.RecognitionPresent}");
                }
                return 0;
            }

            case "backfill":
                return await AiFacesBackfillAsync(rest, services, options, stdout, stderr);

            case "vector-sync":
            {
                var profileKey = ReadOption(rest, "--profile") ?? options.FaceProfileKey;
                if (string.IsNullOrWhiteSpace(profileKey))
                {
                    stderr.WriteLine("ai faces vector-sync: --profile <profile-key> is required (or set Ai__FaceProfileKey).");
                    return 64;
                }
                if (!TryReadPositiveInt(rest, "--limit", int.MaxValue, stderr, "ai faces vector-sync", out var limit))
                {
                    return 64;
                }

                var registry = services.GetRequiredService<IAiProfileRegistry>();
                var vectors = services.GetRequiredService<FaceVectorIndexService>();
                var profile = await registry.GetProfileByKeyAsync(profileKey);
                if (profile is null)
                {
                    stderr.WriteLine($"ai faces vector-sync: profile '{profileKey}' not found.");
                    return 64;
                }

                var outcome = await vectors.SyncProfileAsync(
                    profile, limit == int.MaxValue ? null : limit, HasFlag(rest, "--dry-run"),
                    log: msg => stderr.WriteLine(msg));
                stdout.WriteLine($"profile={outcome.ProfileKey}");
                stdout.WriteLine($"dimension={outcome.Dimension}");
                stdout.WriteLine($"eligible_embeddings={outcome.EligibleEmbeddings}");
                stdout.WriteLine($"vector_indexed={outcome.VectorIndexed}");
                stdout.WriteLine($"missing_vectors={outcome.MissingVectors}");
                stdout.WriteLine($"synced={outcome.Synced}");
                stdout.WriteLine($"skipped_dimension_mismatch={outcome.SkippedDimensionMismatch}");
                stdout.WriteLine($"failed={outcome.Failed}");
                stdout.WriteLine($"dry_run={(outcome.DryRun ? "true" : "false")}");
                stdout.WriteLine($"vector_backend={(outcome.Available ? "available" : (outcome.Reason ?? "unavailable"))}");
                return 0;
            }

            case "similar":
                return await AiFacesSimilarAsync(rest, services, options, stdout, stderr);

            default:
                stderr.WriteLine(
                    $"ai faces: unknown subcommand '{sub}'. One of: coverage, diagnostics, "
                    + "backfill (detection|embeddings), vector-sync, similar.");
                return 64;
        }
    }

    private static async Task<int> AiFacesBackfillAsync(
        string[] args, IServiceProvider services, AiOptions options, TextWriter stdout, TextWriter stderr)
    {
        var kind = args.Length > 0 ? args[0] : string.Empty;
        var rest = args.Length > 0 ? args[1..] : args;
        if (kind is not ("detection" or "embeddings"))
        {
            stderr.WriteLine("ai faces backfill: specify 'detection' or 'embeddings'.");
            return 64;
        }

        var profileKey = ReadOption(rest, "--profile") ?? options.FaceProfileKey;
        if (string.IsNullOrWhiteSpace(profileKey))
        {
            stderr.WriteLine($"ai faces backfill {kind}: --profile <profile-key> is required (or set Ai__FaceProfileKey).");
            return 64;
        }
        if (!TryReadPositiveInt(rest, "--limit", int.MaxValue, stderr, $"ai faces backfill {kind}", out var limitRaw))
        {
            return 64;
        }
        int? limit = limitRaw == int.MaxValue ? null : limitRaw;
        var dryRun = HasFlag(rest, "--dry-run");

        if (!options.Enabled)
        {
            stderr.WriteLine("ai faces backfill: AI is disabled (set Ai__Enabled=true).");
            return 69;
        }

        // Face processing is OFF by default; the CLI refuses to run a real backfill
        // unless the matching capability flag is explicitly enabled.
        var flagOn = kind == "detection" ? options.FaceDetectionEnabled : options.FaceEmbeddingsEnabled;
        if (!flagOn)
        {
            var flag = kind == "detection" ? "Ai__FaceDetectionEnabled" : "Ai__FaceEmbeddingsEnabled";
            stderr.WriteLine($"ai faces backfill {kind}: capability disabled (set {flag}=true).");
            return 69;
        }

        var resolver = services.GetRequiredService<IAiBackendResolver>();
        var registry = services.GetRequiredService<IAiProfileRegistry>();
        var opts = new FaceBackfillOptions { Limit = limit, DryRun = dryRun };

        if (kind == "detection")
        {
            var resolution = await FaceProfileResolver.ResolveDetectorAsync(resolver, profileKey, options.FaceProfileKey);
            if (!resolution.IsAvailable || resolution.Backend is null)
            {
                stdout.WriteLine($"backend=unavailable reason={resolution.Resolution.UnavailableReason ?? "unknown"}");
                return 0;
            }
            var profile = await registry.GetProfileByKeyAsync(resolution.Resolution.ProfileKey!);
            if (profile is null)
            {
                stdout.WriteLine("backend=unavailable reason=profile-not-found");
                return 0;
            }
            var service = services.GetRequiredService<FaceDetectionBackfillService>();
            var r = await service.RunAsync(resolution.Backend, profile, opts, log: msg => stderr.WriteLine(msg));
            PrintFaceBackfillResult(stdout, "detection", profile.Key, r);
            return 0;
        }
        else
        {
            var resolution = await FaceProfileResolver.ResolveEmbedderAsync(resolver, profileKey, options.FaceProfileKey);
            if (!resolution.IsAvailable || resolution.Backend is null)
            {
                stdout.WriteLine($"backend=unavailable reason={resolution.Resolution.UnavailableReason ?? "unknown"}");
                return 0;
            }
            var profile = await registry.GetProfileByKeyAsync(resolution.Resolution.ProfileKey!);
            if (profile is null)
            {
                stdout.WriteLine("backend=unavailable reason=profile-not-found");
                return 0;
            }
            var service = services.GetRequiredService<FaceEmbeddingBackfillService>();
            var r = await service.RunAsync(resolution.Backend, profile, opts, log: msg => stderr.WriteLine(msg));
            PrintFaceBackfillResult(stdout, "embeddings", profile.Key, r);
            return 0;
        }
    }

    private static void PrintFaceBackfillResult(TextWriter stdout, string kind, string profileKey, FaceBackfillResult r)
    {
        stdout.WriteLine($"backfill={kind}");
        stdout.WriteLine($"profile={profileKey}");
        stdout.WriteLine($"dry_run={(r.DryRun ? "true" : "false")}");
        stdout.WriteLine($"processed={r.Processed}");
        stdout.WriteLine($"produced={r.Produced}");
        stdout.WriteLine($"skipped={r.Skipped}");
        stdout.WriteLine($"failed={r.Failed}");
        stdout.WriteLine($"more_work_remaining={r.MoreWorkRemaining}");
        if (kind == "embeddings")
        {
            stdout.WriteLine($"vectors_indexed={r.VectorIndexed}");
            stdout.WriteLine($"vectors_deferred={r.VectorDeferred}");
        }
    }

    private static async Task<int> AiFacesSimilarAsync(
        string[] args, IServiceProvider services, AiOptions options, TextWriter stdout, TextWriter stderr)
    {
        var faceRaw = ReadOption(args, "--face");
        if (!Guid.TryParse(faceRaw, out var faceDetectionId))
        {
            stderr.WriteLine("ai faces similar: --face <faceDetectionId> is required.");
            return 64;
        }
        // Owner-safe by construction: results are scoped to ONE owner's visible,
        // non-vault files. The owner is required (no cross-owner face search).
        var ownerRaw = ReadOption(args, "--owner");
        if (!Guid.TryParse(ownerRaw, out var ownerUserId))
        {
            stderr.WriteLine("ai faces similar: --owner <userId> is required (owner-scoped; no cross-owner search).");
            return 64;
        }

        var profileKey = ReadOption(args, "--profile") ?? options.FaceProfileKey;
        if (string.IsNullOrWhiteSpace(profileKey))
        {
            stderr.WriteLine("ai faces similar: --profile <profile-key> is required (or set Ai__FaceProfileKey).");
            return 64;
        }
        if (!TryReadPositiveInt(args, "--limit", 20, stderr, "ai faces similar", out var limit))
        {
            return 64;
        }

        var settings = await services.GetRequiredService<IFaceSettingsProvider>().GetAsync();
        var threshold = settings.SearchDefaultSimilarityThreshold;
        var thrRaw = ReadOption(args, "--threshold");
        if (thrRaw is not null)
        {
            if (!double.TryParse(thrRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out threshold))
            {
                stderr.WriteLine("ai faces similar: --threshold must be a number.");
                return 64;
            }
            threshold = settings.ClampSearchThreshold(threshold);
        }

        var registry = services.GetRequiredService<IAiProfileRegistry>();
        var profile = await registry.GetProfileByKeyAsync(profileKey);
        if (profile is null)
        {
            stderr.WriteLine($"ai faces similar: profile '{profileKey}' not found.");
            return 64;
        }

        var db = services.GetRequiredService<AppDbContext>();
        var source = await db.FaceEmbeddings.AsNoTracking()
            .FirstOrDefaultAsync(e => e.FaceDetectionId == faceDetectionId && e.ProfileId == profile.Id);
        if (source is null)
        {
            stdout.WriteLine("ai faces similar: no embedding for that face/profile (nothing to compare).");
            return 0;
        }

        var serializer = services.GetRequiredService<IAiVectorSerializer>();
        float[] vector;
        try
        {
            vector = serializer.Deserialize(source.EmbeddingBytes);
        }
        catch
        {
            stderr.WriteLine("ai faces similar: stored embedding could not be read.");
            return 70;
        }

        var vectors = services.GetRequiredService<FaceVectorIndexService>();
        var neighbors = await vectors.SearchAsync(
            profile.Id, vector, ownerUserId, faceDetectionId, threshold, limit);
        if (neighbors is null)
        {
            stdout.WriteLine("ai faces similar: vector_backend=unavailable (pgvector required for face search).");
            return 0;
        }

        stdout.WriteLine($"profile={profile.Key}");
        stdout.WriteLine($"threshold={threshold:0.###}");
        stdout.WriteLine($"results={neighbors.Count}");
        foreach (var n in neighbors)
        {
            stdout.WriteLine($"  score={n.Score:0.####} face={n.FaceDetectionId:N} {n.Name}");
        }
        return 0;
    }

    private static string Fmt(double? ms) => ms is { } v ? v.ToString("F1") : "-";

    private static string Fmt4(double? v) => v is { } x ? x.ToString("F4") : "-";

    private static string Fmt6(double? v) => v is { } x ? x.ToString("F6") : "-";

    private static bool TryReadPositiveInt(
        string[] args, string name, int fallback, TextWriter stderr, string cmd, out int value)
    {
        value = fallback;
        var raw = ReadOption(args, name);
        if (raw is null)
        {
            return true;
        }
        if (!int.TryParse(raw, out var n) || n <= 0)
        {
            stderr.WriteLine($"{cmd}: {name} must be a positive integer.");
            return false;
        }
        value = n;
        return true;
    }

    private static bool TryReadNonNegativeInt(
        string[] args, string name, int fallback, TextWriter stderr, string cmd, out int value)
    {
        value = fallback;
        var raw = ReadOption(args, name);
        if (raw is null)
        {
            return true;
        }
        if (!int.TryParse(raw, out var n) || n < 0)
        {
            stderr.WriteLine($"{cmd}: {name} must be a non-negative integer.");
            return false;
        }
        value = n;
        return true;
    }

    private static (string verb, string sub, string[] rest) ParseVerbSubcommand(string[] args)
    {
        if (args[0] == "ensure-user")
        {
            return ("users", "ensure", args[1..]);
        }
        if (args[0] == "db-migrate")
        {
            return ("db", "migrate", args[1..]);
        }
        // Bare aliases for the two slice-46 admin subcommands so an operator
        // can run e.g. `... grant-admin --email foo@bar` without typing
        // `users` first.
        if (args[0] == "grant-admin")
        {
            return ("users", "grant-admin", args[1..]);
        }
        if (args[0] == "revoke-admin")
        {
            return ("users", "revoke-admin", args[1..]);
        }
        if (args.Length >= 2)
        {
            return (args[0], args[1], args[2..]);
        }
        return (args[0], "", []);
    }

    private static string? ReadOption(string[] args, string name)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == name && i + 1 < args.Length)
            {
                return args[i + 1];
            }
            if (args[i].StartsWith(name + "=", StringComparison.Ordinal))
            {
                return args[i][(name.Length + 1)..];
            }
        }
        return null;
    }

    private static bool HasFlag(string[] args, string name)
        => Array.IndexOf(args, name) >= 0;

    private static string? ReadEnv(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }

    private static void WriteHelp(TextWriter stdout)
    {
        // Plain ASCII only — keep readable in any terminal locale.
        // The banner is the product name; the USAGE lines below deliberately
        // keep the literal `dotnet NubArca.Api.dll` entrypoint (retained
        // compatibility identifier — it is the real assembly on disk).
        stdout.WriteLine("NubArca operator CLI");
        stdout.WriteLine();
        stdout.WriteLine("USAGE");
        stdout.WriteLine("  dotnet NubArca.Api.dll users ensure        [options]");
        stdout.WriteLine("  dotnet NubArca.Api.dll users grant-admin   --email <addr>");
        stdout.WriteLine("  dotnet NubArca.Api.dll users revoke-admin  --email <addr>");
        stdout.WriteLine("  dotnet NubArca.Api.dll db migrate");
        stdout.WriteLine("  dotnet NubArca.Api.dll metadata backfill   [options]");
        stdout.WriteLine("  dotnet NubArca.Api.dll metadata recompute-effective-dates");
        stdout.WriteLine("  dotnet NubArca.Api.dll media derivatives backfill [options]");
        stdout.WriteLine("  dotnet NubArca.Api.dll media derivatives failures");
        stdout.WriteLine("  dotnet NubArca.Api.dll media derivatives benchmark [--limit N]");
        stdout.WriteLine("  dotnet NubArca.Api.dll storage reconcile          [options]");
        stdout.WriteLine("  dotnet NubArca.Api.dll jobs enqueue <job>         [options]");
        stdout.WriteLine("  dotnet NubArca.Api.dll jobs list");
        stdout.WriteLine("  dotnet NubArca.Api.dll jobs run-once              [--max N]");
        stdout.WriteLine("  dotnet NubArca.Api.dll jobs worker                [--poll-interval-seconds N]");
        stdout.WriteLine("  dotnet NubArca.Api.dll ai status");
        stdout.WriteLine("  dotnet NubArca.Api.dll ai models");
        stdout.WriteLine("  dotnet NubArca.Api.dll ai profiles");
        stdout.WriteLine("  dotnet NubArca.Api.dll ai diagnostics");
        stdout.WriteLine("  dotnet NubArca.Api.dll ai seed");
        stdout.WriteLine("  dotnet NubArca.Api.dll ai onnx image <models|seed-profiles|benchmark|embed-test|compare>");
        stdout.WriteLine("  dotnet NubArca.Api.dll ai face <models|seed-profiles|detect-test|embed-test|compare|benchmark|sample-pairs>");
        stdout.WriteLine("  dotnet NubArca.Api.dll plates models validate     [alpr|face-redaction]");
        stdout.WriteLine("  dotnet NubArca.Api.dll plates benchmark <alpr|face-redaction> --image <path> [--runs N]");
        stdout.WriteLine();
        stdout.WriteLine("users ensure");
        stdout.WriteLine("  Creates the user if missing. With an existing user, leaves the");
        stdout.WriteLine("  password unchanged unless --update-password is passed. Admin state");
        stdout.WriteLine("  is NEVER downgraded; pass --admin only when you want to grant.");
        stdout.WriteLine();
        stdout.WriteLine("  --email <addr>           or NUBARCA_ADMIN_EMAIL");
        stdout.WriteLine("  --display-name <name>    or NUBARCA_ADMIN_DISPLAY_NAME");
        stdout.WriteLine("  --password <secret>      or NUBARCA_ADMIN_PASSWORD");
        stdout.WriteLine("  --update-password        or NUBARCA_ADMIN_UPDATE_PASSWORD=true");
        stdout.WriteLine("  --admin                  or NUBARCA_ADMIN_IS_ADMIN=true");
        stdout.WriteLine();
        stdout.WriteLine("  Recommendation: pass the password via env var (or .env / docker");
        stdout.WriteLine("  compose run --env-file ...) rather than the command line, so it");
        stdout.WriteLine("  does not end up in shell history.");
        stdout.WriteLine();
        stdout.WriteLine("users grant-admin / users revoke-admin");
        stdout.WriteLine("  Toggles the admin marker on an existing user, identified by email.");
        stdout.WriteLine("  Idempotent: re-granting an already-admin user is a no-op. Missing");
        stdout.WriteLine("  users return exit code 64. Today the marker gates /api/admin/*.");
        stdout.WriteLine();
        stdout.WriteLine("db migrate");
        stdout.WriteLine("  Applies pending EF Core migrations against the configured");
        stdout.WriteLine("  ConnectionStrings:Postgres. Safe to re-run; a no-op once up to");
        stdout.WriteLine("  date. Back up your database before running on a populated DB.");
        stdout.WriteLine();
        stdout.WriteLine("metadata backfill");
        stdout.WriteLine("  Re-extracts embedded image metadata for existing blobs whose");
        stdout.WriteLine("  extraction is pending/failed or was produced by an older extractor.");
        stdout.WriteLine("  Idempotent; reads bytes only and never mutates files. Logs counts");
        stdout.WriteLine("  only (no raw metadata). Does NOT run on startup.");
        stdout.WriteLine();
        stdout.WriteLine("  --limit <N>       cap the number of blobs processed this run");
        stdout.WriteLine("  --failed-only     only re-extract rows whose status is 'failed'");
        stdout.WriteLine("  --dry-run         report how many blobs would be processed, change nothing");
        stdout.WriteLine();
        stdout.WriteLine("metadata recompute-effective-dates");
        stdout.WriteLine("  Rebuilds the denormalized FileItem.EffectiveDateTaken (gallery");
        stdout.WriteLine("  'Date taken' sort key) for every file from its sources of truth");
        stdout.WriteLine("  (user override -> embedded date -> upload time). Set-based, reads");
        stdout.WriteLine("  no bytes, mutates no sources, logs an updated count only. Safe to");
        stdout.WriteLine("  re-run; useful after a bulk import. Does NOT run on startup.");
        stdout.WriteLine();
        stdout.WriteLine("media derivatives backfill");
        stdout.WriteLine("  Generates missing derivative artefacts: video posters first, then");
        stdout.WriteLine("  image medium previews, then image small thumbnails. Idempotent; a");
        stdout.WriteLine("  finished run leaves every row present so a re-run is a no-op. Logs");
        stdout.WriteLine("  counts only (no file names / paths / metadata). Does NOT run on");
        stdout.WriteLine("  startup. Source blobs are never mutated.");
        stdout.WriteLine();
        stdout.WriteLine("  --limit <N>       cap the number of derivatives generated this run");
        stdout.WriteLine("  --missing-only    default: regenerate only missing derivatives");
        stdout.WriteLine("  --retry-failed    re-attempt derivatives blocked by a prior failure");
        stdout.WriteLine("                    diagnostic (alias: --force-failed, --failed-only)");
        stdout.WriteLine("  --dry-run         report how many derivatives would be generated");
        stdout.WriteLine();
        stdout.WriteLine("media derivatives failures");
        stdout.WriteLine("  Reports WHY derivatives are missing, aggregated by size / status /");
        stdout.WriteLine("  error code / detected format (e.g. 18 TIFF identify_failed, 67 JPEG");
        stdout.WriteLine("  decode_failed). Counts only — never a file name, path, key, or");
        stdout.WriteLine("  metadata. Populated by `media derivatives backfill` attempts;");
        stdout.WriteLine("  permanent failures are skipped by default and retried with");
        stdout.WriteLine("  --retry-failed. Read-only (prunes diagnostics already resolved).");
        stdout.WriteLine();
        stdout.WriteLine("media derivatives benchmark");
        stdout.WriteLine("  Compares the image backends (libvips vs ImageSharp) on real library");
        stdout.WriteLine("  images: renders small+medium in memory (nothing stored), reports per-");
        stdout.WriteLine("  backend timings + output bytes and the vips speedup. Read-only; counts");
        stdout.WriteLine("  and milliseconds only. --limit caps the sample (default 50).");
        stdout.WriteLine();
        stdout.WriteLine("media derivatives verify-bytes");
        stdout.WriteLine("  Audits the PHYSICAL placement of existing derivative rows: for each");
        stdout.WriteLine("  FileThumbnail row, reports whether its bytes are in the derived root");
        stdout.WriteLine("  (where the endpoints read), only in the original root (displaced by");
        stdout.WriteLine("  a Storage:DerivedRootPath change), or missing from both. Read-only.");
        stdout.WriteLine("  Counts only — never a storage key, id, name, or path.");
        stdout.WriteLine();
        stdout.WriteLine("  --size <s>        restrict to one size: small | medium | poster");
        stdout.WriteLine("  --limit <N>       cap the number of rows checked this run");
        stdout.WriteLine("  --dry-run         accepted for symmetry; verify never mutates");
        stdout.WriteLine();
        stdout.WriteLine("media derivatives repair-bytes");
        stdout.WriteLine("  Fixes the placement verify-bytes reports: copies derivative bytes");
        stdout.WriteLine("  from the original root into the derived root (streaming copy with");
        stdout.WriteLine("  re-hash + atomic rename — no image decode, no DB writes, originals");
        stdout.WriteLine("  never deleted). Rows missing from BOTH roots are left unchanged");
        stdout.WriteLine("  unless --regenerate-missing is given.");
        stdout.WriteLine();
        stdout.WriteLine("  --size <s>            restrict to one size: small | medium | poster");
        stdout.WriteLine("  --limit <N>           cap the number of rows checked this run");
        stdout.WriteLine("  --dry-run             report what would be copied, change nothing");
        stdout.WriteLine("  --regenerate-missing  rebuild rows missing from both roots via the");
        stdout.WriteLine("                        standard generation path (CPU-heavy)");
        stdout.WriteLine();
        stdout.WriteLine("storage reconcile");
        stdout.WriteLine("  Compares the physical blob store with the BlobObject table and");
        stdout.WriteLine("  reports COUNTS ONLY (never a storage key or path): on-disk objects");
        stdout.WriteLine("  with no DB row (orphans), and DB rows whose physical object is");
        stdout.WriteLine("  missing. Dry-run by default. --delete-orphans physically removes");
        stdout.WriteLine("  orphan objects (filesystem only; never touches the database).");
        stdout.WriteLine();
        stdout.WriteLine("  --dry-run          report only, change nothing (default)");
        stdout.WriteLine("  --delete-orphans   delete on-disk objects with no BlobObject row");
        stdout.WriteLine("  --limit <N>        cap the number of orphan deletions this run");
        stdout.WriteLine();
        stdout.WriteLine("storage blobs audit-references");
        stdout.WriteLine("  Compares BlobObject.ReferenceCount with the real owner rows");
        stdout.WriteLine("  (file_items + file_thumbnails). Detects leaked references (nonzero");
        stdout.WriteLine("  refcount, no owners — invisible to the janitor) and the dangerous");
        stdout.WriteLine("  inverse (zero refcount with live owners). Read-only; counts only.");
        stdout.WriteLine();
        stdout.WriteLine("storage blobs repair-references");
        stdout.WriteLine("  Sets each mismatched ReferenceCount to the value computed from the");
        stdout.WriteLine("  owner tables (guarded against concurrent changes). Never deletes");
        stdout.WriteLine("  physical bytes — corrected zero-ref blobs are reclaimed by the blob");
        stdout.WriteLine("  janitor under its normal grace rules.");
        stdout.WriteLine();
        stdout.WriteLine("  --dry-run          report how many rows would be corrected");
        stdout.WriteLine();
        stdout.WriteLine("jobs enqueue <job>");
        stdout.WriteLine("  Durably queues a background job in the database. <job> is one of:");
        stdout.WriteLine("    metadata-backfill            [--limit N] [--failed-only] [--dry-run]");
        stdout.WriteLine("    media-derivatives-backfill   [--limit N] [--retry-failed] [--dry-run]");
        stdout.WriteLine("    media-video-hls-generate     --blob <blob-object-id> [--force]");
        stdout.WriteLine("    media-video-hls-backfill     [--limit N] [--retry-failed] [--force] [--dry-run]");
        stdout.WriteLine("    storage-reconcile            [--limit N] [--delete-orphans] [--dry-run]");
        stdout.WriteLine("    ai-photos-embeddings-backfill      [--profile <key>] [--dry-run]");
        stdout.WriteLine("    ai-documents-extract-backfill      [--profile <key>] [--dry-run]");
        stdout.WriteLine("    ai-documents-embeddings-backfill   [--profile <key>] [--dry-run]");
        stdout.WriteLine("    ai-faces-detect-backfill           [--profile <key>] [--dry-run]");
        stdout.WriteLine("    ai-faces-embeddings-backfill       [--profile <key>] [--dry-run]");
        stdout.WriteLine("    ai-faces-cluster-backfill          [--profile <key>] [--dry-run]");
        stdout.WriteLine("    ai-tags-generate-backfill          [--profile <key>] [--dry-run]");
        stdout.WriteLine("  Payloads carry operation flags only — never paths, keys, or metadata.");
        stdout.WriteLine("  AI backfills are Phase 0C skeletons: they no-op (no inference) until");
        stdout.WriteLine("  later phases. They never mark blobs skipped/failed for a disabled");
        stdout.WriteLine("  flag or unavailable provider.");
        stdout.WriteLine();
        stdout.WriteLine("  NOTE: photo.organizer.datetaken jobs are owner-scoped and NOT");
        stdout.WriteLine("  enqueueable here. They are created via the REST API:");
        stdout.WriteLine("    POST /api/photo-organizer/date-taken/run  (authenticated owner)");
        stdout.WriteLine("  A failed run can be retried by the owner by starting a new run");
        stdout.WriteLine("  (the organizer is idempotent; already-moved files are skipped).");
        stdout.WriteLine();
        stdout.WriteLine("jobs list");
        stdout.WriteLine("  Prints status counts (queued/running/succeeded/failed/cancelled) and");
        stdout.WriteLine("  the most recent jobs (id, type, status, attempts, error code). Never");
        stdout.WriteLine("  prints job payloads.");
        stdout.WriteLine();
        stdout.WriteLine("jobs run-once [--max N]");
        stdout.WriteLine("  Claims and processes up to N available jobs (default 10), then exits.");
        stdout.WriteLine("  Idempotent handlers reuse the metadata/media/storage services.");
        stdout.WriteLine();
        stdout.WriteLine("jobs worker [--poll-interval-seconds N]");
        stdout.WriteLine("  Loops: processes available jobs, then sleeps N seconds (default 10).");
        stdout.WriteLine("  Ctrl+C stops gracefully. The in-process API worker is OFF unless");
        stdout.WriteLine("  Jobs:WorkerEnabled=true; this command is the out-of-band alternative.");
        stdout.WriteLine();
        stdout.WriteLine("ai status | ai models | ai profiles | ai diagnostics");
        stdout.WriteLine("  Inspect the AI substrate (read-only, aggregate/safe fields only):");
        stdout.WriteLine("  enabled flag + default provider + per-capability availability;");
        stdout.WriteLine("  model and profile registries by stable key; aggregate diagnostics");
        stdout.WriteLine("  (capability/target/code/count). Never prints GUIDs, raw vectors,");
        stdout.WriteLine("  blob SHA, storage keys, paths, payloads, or secrets.");
        stdout.WriteLine();
        stdout.WriteLine("ai seed");
        stdout.WriteLine("  Idempotently seeds DETERMINISTIC dev/test models + profiles only.");
        stdout.WriteLine("  Not real semantic AI; does NOT enable inference or AI globally.");
        stdout.WriteLine("  Never runs automatically on startup. Safe to run repeatedly.");
        stdout.WriteLine();
        stdout.WriteLine("ai photos similar --file <id> [--limit N] [--profile <key>]");
        stdout.WriteLine("  Owner-private similar-photo test harness (exact-scan, no pgvector).");
        stdout.WriteLine("  Scoped to the target file's owner; prints file names + scores only.");
        stdout.WriteLine("ai photos similar histogram --file <id> [--profile <key>]");
        stdout.WriteLine("  Diagnostic: score histogram + per-threshold exact-scan vs pgvector");
        stdout.WriteLine("  exact-count vs ANN-returned count (reveals HNSW recall limits). Counts only.");
        stdout.WriteLine("  Uses the active profile (Ai__PhotoSimilarityProfileKey or the default");
        stdout.WriteLine("  fallback); --profile overrides it. Index first with:");
        stdout.WriteLine("  jobs enqueue ai-photos-embeddings-backfill [--profile <key>].");
        stdout.WriteLine();
        stdout.WriteLine("ai photos embeddings <coverage|active-profile|vector-sync|retire-legacy-768>");
        stdout.WriteLine("  Lifecycle inspection (aggregate, sanitized — counts/keys/dims only).");
        stdout.WriteLine("    coverage --profile <key>   embedding + pgvector coverage (counts/percents)");
        stdout.WriteLine("    active-profile             resolved active profile + source + usability");
        stdout.WriteLine("    vector-sync --profile <key> [--limit N] [--dry-run]");
        stdout.WriteLine("                               index existing embeddings into pgvector (1152-dim);");
        stdout.WriteLine("                               idempotent, profile-keyed; no-op w/o pgvector");
        stdout.WriteLine("    retire-legacy-768 [--execute]");
        stdout.WriteLine("                               dry-run by default; after complete 1152 canonical+");
        stdout.WriteLine("                               vector coverage, disables legacy profiles and removes");
        stdout.WriteLine("                               their embeddings + obsolete pgvector table");
        stdout.WriteLine();
        stdout.WriteLine("ai video semantic <status|segments backfill|embeddings backfill|retry-failed>");
        stdout.WriteLine("  Operational status + bounded controls over VSEM-01/02 (segmentation +");
        stdout.WriteLine("  visual embeddings). Read-only status; --dry-run previews are pure count");
        stdout.WriteLine("  queries (no FFmpeg, no inference, no writes, no job enqueued).");
        stdout.WriteLine("    status                                    coverage, versions, pgvector sync");
        stdout.WriteLine("    segments backfill    [--limit N] [--failed-only] [--dry-run]");
        stdout.WriteLine("                         [--segmentation-version N] [--blob-id <id>]");
        stdout.WriteLine("    embeddings backfill  [--limit N] [--failed-only] [--dry-run] [--profile <key>]");
        stdout.WriteLine("                         [--segmentation-version N] [--blob-id <id>]");
        stdout.WriteLine("    retry-failed segments|embeddings   same flags; always scoped to failures");
        stdout.WriteLine("  Enqueues the SAME ai.videos.segments.backfill / ai.videos.embeddings.backfill");
        stdout.WriteLine("  jobs as `jobs enqueue`; idempotent per (version, profile, failed-only, blob).");
        stdout.WriteLine();
        stdout.WriteLine("ai onnx image <models|seed-profiles|benchmark|embed-test|compare>");
        stdout.WriteLine("  Phase 2A local-ONNX image-embedding EVALUATION harness (read-only,");
        stdout.WriteLine("  no writes). Stays unavailable until a model exists under");
        stdout.WriteLine("  Ai__Onnx__ModelDir; missing model => clean 'unavailable', never a");
        stdout.WriteLine("  failure. Prints counts/timings/dims + names + scores only — never");
        stdout.WriteLine("  raw vectors or storage identifiers.");
        stdout.WriteLine("    models                                         list candidates + presence");
        stdout.WriteLine("    seed-profiles                                  seed onnx eval profiles (not default)");
        stdout.WriteLine("    benchmark    --profile <key> [--limit N]       dry-run timings (ms, p50/p95, dim)");
        stdout.WriteLine("    embed-test   --profile <key> --file <id>       embed one file: dim/L2-norm/ms");
        stdout.WriteLine("    compare      --profile <key> --file <id> [--limit N] [--candidate-limit M=50]");
        stdout.WriteLine("                                                   owner-private top-k (~3.5 s/img on CPU)");
        stdout.WriteLine();
        stdout.WriteLine("ai face <models|seed-profiles|detect-test|embed-test|compare|benchmark|sample-pairs>");
        stdout.WriteLine("  Local-ONNX face-recognition model EVALUATION harness (read-only, no");
        stdout.WriteLine("  writes). Detector + ArcFace recognition; stays unavailable until both");
        stdout.WriteLine("  model files exist under Ai__Onnx__ModelDir. Evaluation-only: no People");
        stdout.WriteLine("  UI, no names, no clustering, no persistence; face processing OFF by");
        stdout.WriteLine("  default. Excludes Private Vault. Prints counts/timings/dims/scores +");
        stdout.WriteLine("  names only — never raw vectors, storage identifiers, or model internals.");
        stdout.WriteLine("    models                                         list packages + file presence + license");
        stdout.WriteLine("    seed-profiles                                  seed face eval profiles (not default)");
        stdout.WriteLine("    detect-test  --profile <key> --file <id>       face count, scores, boxes");
        stdout.WriteLine("    embed-test   --profile <key> --file <id> [--face-index N=0]");
        stdout.WriteLine("                                                   dim/L2-norm/finite/timings");
        stdout.WriteLine("    compare      --profile <key> --file-a <id> --face-a N --file-b <id> --face-b N");
        stdout.WriteLine("                                                   cosine similarity of two faces");
        stdout.WriteLine("    benchmark    --profile <key> [--limit N=100]   detect/embed timings + face stats");
        stdout.WriteLine("    sample-pairs --profile <key> [--limit N=25]    safe file refs + face counts");
        stdout.WriteLine("  --profile falls back to Ai__FaceProfileKey when set.");
        stdout.WriteLine();
        stdout.WriteLine("EXIT CODES");
        stdout.WriteLine("  0  success");
        stdout.WriteLine("  1  runtime failure (e.g. migration error)");
        stdout.WriteLine("  64 usage error (missing/invalid arguments)");
        stdout.WriteLine("  78 configuration error (ConnectionStrings:Postgres not set)");
    }
}
