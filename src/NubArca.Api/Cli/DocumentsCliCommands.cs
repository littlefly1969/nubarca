using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NubArca.Api.Ai;
using NubArca.Api.Ai.DocumentVisual;
using NubArca.Api.Ai.Documents;
using NubArca.Api.Data;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Rag;
using NubArca.Api.Rag.Domains;
using NubArca.Api.Rag.Retrieval;

namespace NubArca.Api.Cli;

/// Operator commands for the owner-private document corpus.
///
/// SEPARATE FROM `rag`, and that separation is the point. `rag index --domain
/// user-documents` does not exist and must not: the `rag` verbs operate on
/// installation-wide knowledge with no owner, and giving them an `--owner` flag
/// would make "which corpus does this command touch" a question about arguments
/// rather than about which command was typed.
///
/// Every verb here REQUIRES `--owner`. There is no "all owners" mode, not
/// because it would be hard, but because an operator command that walks every
/// person's documents is a capability worth not having: the one legitimate use
/// (backfill after enabling the feature) is served by running it per owner,
/// which is also the form that can be stopped halfway without ambiguity.
///
/// Nothing here prints a document name, a heading, an excerpt or a storage key.
/// An operator diagnosing an indexing problem needs counts and reason tokens,
/// and a terminal that echoed somebody's filenames would put them in a scrollback
/// buffer, a screenshot and a support ticket.
internal static class DocumentsCliCommands
{
    public static Task<int> RunAsync(
        string[] args, IServiceProvider sp, TextWriter stdout, TextWriter stderr)
        => (args.Length == 0 ? "" : args[0]) switch
        {
            "index" => IndexAsync(args, sp, stdout, stderr),
            "status" => StatusAsync(args, sp, stdout, stderr),
            "visual-seed-profiles" => VisualSeedAsync(sp, stdout),
            "visual-index" => VisualIndexAsync(args, sp, stdout, stderr),
            "visual-status" => VisualStatusAsync(args, sp, stdout, stderr),
            "visual-evaluate" => VisualEvaluateAsync(args, sp, stdout, stderr),
            _ => Usage(stderr),
        };

    private static Task<int> Usage(TextWriter stderr)
    {
        stderr.WriteLine(
            "usage: documents <index|status|visual-index|visual-status> --owner <user-id> "
            + "[--limit N] [--embed]");
        stderr.WriteLine("       documents visual-seed-profiles");
        stderr.WriteLine(
            "       documents visual-evaluate --owner <user-id> --queries <file> [--expect <file>]");
        return Task.FromResult(2);
    }

    // ---- documents index ----------------------------------------------------

    private static async Task<int> IndexAsync(
        string[] args, IServiceProvider sp, TextWriter stdout, TextWriter stderr)
    {
        if (!TryOwner(args, stderr, out var owner)) return 2;

        var limit = TryInt(Arg(args, "--limit"));
        var embed = args.Contains("--embed", StringComparer.Ordinal);

        var outcome = await sp.GetRequiredService<OwnerDocumentIndexer>()
            .IndexOwnerAsync(owner, limit, embed);

        stdout.WriteLine($"files_seen={outcome.FilesSeen}");
        stdout.WriteLine($"extracted={outcome.Extracted}");
        stdout.WriteLine($"unchanged={outcome.Unchanged}");
        stdout.WriteLine($"chunks_created={outcome.ChunksCreated}");
        stdout.WriteLine($"chunks_removed={outcome.ChunksRemoved}");
        stdout.WriteLine($"embeddings_created={outcome.EmbeddingsCreated}");
        stdout.WriteLine($"embeddings_removed={outcome.EmbeddingsRemoved}");
        stdout.WriteLine($"skipped={outcome.Skipped}");
        // Reason TOKENS and counts. `binary=3` tells an operator what happened;
        // naming the three files would tell them rather more than that.
        foreach (var (reason, count) in outcome.SkipReasons.OrderBy(r => r.Key, StringComparer.Ordinal))
        {
            stdout.WriteLine($"  skipped_{reason}={count}");
        }
        stdout.WriteLine($"partial={(outcome.Partial ? "true" : "false")}");
        stdout.WriteLine($"embedding_profile={outcome.EmbeddingProfileKey ?? "(none)"}");
        if (outcome.EmbeddingReason is not null)
        {
            stdout.WriteLine($"embedding_reason={outcome.EmbeddingReason}");
        }

        // Within this process the corpus just changed under a cache built from
        // the old signature. Private indexes are not cached, but the system ones
        // share the process — clearing is cheap and keeps one rule.
        sp.GetRequiredService<RagLexicalIndexCache>().Clear();
        return 0;
    }

    // ---- documents status ---------------------------------------------------

    private static async Task<int> StatusAsync(
        string[] args, IServiceProvider sp, TextWriter stdout, TextWriter stderr)
    {
        if (!TryOwner(args, stderr, out var owner)) return 2;

        var semantic = sp.GetRequiredService<IRagSemanticProfileResolver>()
            .Resolve(RagDomainKey.UserDocuments);
        var corpus = sp.GetRequiredService<OwnerDocumentCorpusSource>();
        var stats = await corpus.GetStatsAsync(owner);

        stdout.WriteLine($"domain={RagDomains.UserDocuments}");
        stdout.WriteLine($"documents={stats.Documents}");
        stdout.WriteLine($"chunks={stats.Chunks}");
        stdout.WriteLine($"semantic_enabled={(semantic.Enabled ? "true" : "false")}");
        stdout.WriteLine($"embedding_profile={semantic.ProfileKey ?? "(none)"}");

        // PER-FORMAT COUNTS, AND NOTHING ELSE. An operator needs to know that
        // twelve PDFs were read and three refused; they do not need — and must
        // never be shown here — a filename, a sheet name, a slide title or an
        // excerpt. The counts come from the extraction PROFILE, which is the one
        // record of which parser produced what.
        var database = sp.GetRequiredService<AppDbContext>();
        var byProfile = await database.DocumentTexts.AsNoTracking()
            .Where(d => d.OwnerUserId == owner && d.IsCurrent)
            .Join(database.AiProfiles.AsNoTracking(), d => d.ProfileId, p => p.Id,
                (d, p) => new { p.Key, d.Status })
            .GroupBy(x => new { x.Key, x.Status })
            .Select(g => new { g.Key.Key, g.Key.Status, Count = g.Count() })
            .ToListAsync();

        int CountFor(string profileKey) => byProfile
            .Where(x => x.Key == profileKey && x.Status == AiArtifactStatuses.Completed)
            .Sum(x => x.Count);

        stdout.WriteLine($"native_documents={CountFor(DocumentTextSources.NativeProfileKey)}");
        stdout.WriteLine($"pdf_documents={CountFor(DocumentTextSources.PdfProfileKey)}");
        stdout.WriteLine($"docx_documents={CountFor(DocumentTextSources.WordProfileKey)}");
        stdout.WriteLine($"xlsx_documents={CountFor(DocumentTextSources.SpreadsheetProfileKey)}");
        stdout.WriteLine($"pptx_documents={CountFor(DocumentTextSources.PresentationProfileKey)}");
        stdout.WriteLine(
            $"skipped_documents={byProfile.Where(x => x.Status == AiArtifactStatuses.Skipped).Sum(x => x.Count)}");

        // OCR readiness, sanitized. Never the executable path, never the
        // tessdata directory — a diagnostic carrying a filesystem path is
        // exactly what the privacy rules forbid.
        var ocr = sp.GetService<IDocumentOcrProvider>();
        var readiness = ocr?.CheckReadiness();
        stdout.WriteLine($"ocr_provider={ocr?.Provider ?? "(none)"}");
        stdout.WriteLine($"ocr_ready={(readiness?.IsReady == true ? "true" : "false")}");
        stdout.WriteLine($"ocr_reason={readiness?.Reason ?? "(none)"}");

        if (semantic.Enabled && semantic.ProfileKey is not null)
        {
            var db = sp.GetRequiredService<AppDbContext>();
            var profileId = await db.AiProfiles.AsNoTracking()
                .Where(p => p.Key == semantic.ProfileKey)
                .Select(p => (Guid?)p.Id)
                .FirstOrDefaultAsync();

            if (profileId is { } id)
            {
                var embedded = await corpus.CountEmbeddedAsync(owner, id);
                stdout.WriteLine($"embeddings={embedded}/{stats.Chunks}");
                stdout.WriteLine($"missing_embeddings={Math.Max(0, stats.Chunks - embedded)}");
            }
            else
            {
                stdout.WriteLine("embeddings=(profile not installed)");
            }
        }

        return stats.Chunks > 0 ? 0 : 1;
    }


    // ---- documents visual-seed-profiles -------------------------------------

    /// Create the document-visual model/profile rows. Explicit, never on
    /// startup, and creating them enables nothing: the capability still needs
    /// `Ai__DocumentVisual__Enabled` and the SigLIP2 files on disk.
    private static async Task<int> VisualSeedAsync(IServiceProvider sp, TextWriter stdout)
    {
        var seeded = await sp.GetRequiredService<IAiProfileRegistry>()
            .SeedDocumentVisualProfilesAsync();

        stdout.WriteLine($"models_created={seeded.ModelsCreated}");
        stdout.WriteLine($"profiles_created={seeded.ProfilesCreated}");
        stdout.WriteLine($"profile={DocumentVisualProfiles.DenseSiglip2So400m}");
        return 0;
    }

    // ---- documents visual-index ---------------------------------------------

    private static async Task<int> VisualIndexAsync(
        string[] args, IServiceProvider sp, TextWriter stdout, TextWriter stderr)
    {
        if (!TryOwner(args, stderr, out var owner)) return 2;

        var limit = TryInt(Arg(args, "--limit"));
        var outcome = await sp.GetRequiredService<OwnerDocumentVisualIndexer>()
            .IndexOwnerAsync(owner, limit);

        stdout.WriteLine($"files_seen={outcome.FilesSeen}");
        stdout.WriteLine($"indexed={outcome.Indexed}");
        stdout.WriteLine($"unchanged={outcome.Unchanged}");
        stdout.WriteLine($"units_rendered={outcome.UnitsRendered}");
        stdout.WriteLine($"units_embedded={outcome.UnitsEmbedded}");
        stdout.WriteLine($"skipped={outcome.Skipped}");
        foreach (var (reason, count) in outcome.SkipReasons.OrderBy(r => r.Key, StringComparer.Ordinal))
        {
            stdout.WriteLine($"  skipped_{reason}={count}");
        }
        stdout.WriteLine($"partial={(outcome.Partial ? "true" : "false")}");
        stdout.WriteLine($"visual_profile={outcome.ProfileKey ?? "(none)"}");
        if (outcome.Reason is not null) stdout.WriteLine($"visual_reason={outcome.Reason}");

        // THE ACCELERATOR IS A MIRROR, refreshed after the canonical rows moved.
        // A gap between the two is invisible at query time — retrieval simply
        // falls back to the exact scan — so it is closed here rather than
        // discovered later as "visual search got slow".
        var resolution = await sp.GetRequiredService<DocumentVisualProfileResolver>().ResolveAsync();
        if (resolution.IsAvailable)
        {
            var synced = await sp.GetRequiredService<DocumentVisualVectorIndexService>()
                .SyncAsync(resolution.Profile!.Id);
            stdout.WriteLine($"accelerator_synced={synced}");
        }

        return outcome.Reason is null ? 0 : 1;
    }

    // ---- documents visual-status --------------------------------------------

    /// AGGREGATES ONLY, like every other verb here. Counts, reason tokens,
    /// readiness booleans and profile keys — never a document name, never a
    /// page, never a socket path.
    private static async Task<int> VisualStatusAsync(
        string[] args, IServiceProvider sp, TextWriter stdout, TextWriter stderr)
    {
        if (!TryOwner(args, stderr, out var owner)) return 2;

        var options = sp.GetRequiredService<IOptions<DocumentVisualOptions>>().Value;
        var resolution = await sp.GetRequiredService<DocumentVisualProfileResolver>().ResolveAsync();
        var renderers = sp.GetRequiredService<DocumentVisualRenderers>();
        var db = sp.GetRequiredService<AppDbContext>();

        stdout.WriteLine($"visual_enabled={(options.Enabled ? "true" : "false")}");
        stdout.WriteLine($"dense_visual_ready={(resolution.IsAvailable ? "true" : "false")}");
        stdout.WriteLine($"visual_profile={resolution.Profile?.Key ?? options.DenseProfileKey}");
        if (resolution.Reason is not null) stdout.WriteLine($"visual_reason={resolution.Reason}");

        foreach (var format in Enum.GetValues<DocumentFormatKind>())
        {
            var renderer = renderers.For(format);
            var readiness = renderer?.CheckReadiness();
            var name = format.ToString().ToLowerInvariant();
            stdout.WriteLine(
                $"renderer_{name}={renderer?.RenderProfileKey ?? "(none)"} "
                + $"ready={(readiness?.Ready == true ? "true" : "false")} "
                + $"reason={readiness?.Reason ?? "(none)"}");
        }

        stdout.WriteLine(
            $"office_renderer_enabled={(options.RenderOfficeEnabled ? "true" : "false")}");
        stdout.WriteLine(
            $"late_interaction_ready={(options.LateInteractionEnabled
                && sp.GetService<IVisualLateInteractionProvider>() is not null ? "true" : "false")}");

        if (resolution.Profile is { } profile)
        {
            // COUNTED THROUGH THE LIVE ELIGIBILITY JOIN, so a document deleted
            // or vaulted since it was indexed stops counting the moment it is —
            // the same number retrieval would see, not the row count.
            var eligible = OwnerDocumentVisualEligibility.EligibleUnits(
                db.DocumentVisualUnits.AsNoTracking(),
                db.DocumentVisualIndexes.AsNoTracking(),
                db.DocumentTexts.AsNoTracking(),
                db.FileItems.AsNoTracking(),
                owner, profile.Id, renderers.ActiveRenderProfileKeys);

            var units = await eligible.CountAsync();
            var documents = await eligible.Select(r => r.Index.Id).Distinct().CountAsync();
            stdout.WriteLine($"visual_documents={documents}");
            stdout.WriteLine($"visual_units={units}");

            var accelerator = sp.GetRequiredService<DocumentVisualVectorIndexService>();
            stdout.WriteLine(
                $"accelerator_available="
                + $"{(await accelerator.IsBackendAvailableAsync(profile.Dimension) ? "true" : "false")}");
            stdout.WriteLine($"accelerator_vectors={await accelerator.CountIndexedAsync(profile.Id)}");

            // SKIPS BY REASON. What a skipped document is called stays private;
            // that four of them were too complex does not.
            var skipped = await db.DocumentVisualIndexes.AsNoTracking()
                .Where(i => i.OwnerUserId == owner
                            && i.EmbeddingProfileId == profile.Id
                            && i.Status == AiArtifactStatuses.Skipped)
                .GroupBy(i => i.ErrorCode)
                .Select(g => new { Reason = g.Key, Count = g.Count() })
                .ToListAsync();
            foreach (var row in skipped.OrderBy(r => r.Reason, StringComparer.Ordinal))
            {
                stdout.WriteLine($"  visual_skipped_{row.Reason ?? "unknown"}={row.Count}");
            }

            return units > 0 ? 0 : 1;
        }

        return 1;
    }


    // ---- documents visual-evaluate -------------------------------------------

    /// DOES THE VISUAL SIGNAL EARN ITS COST, on THIS installation's documents.
    ///
    /// The repository's golden set is synthetic, which is what makes it a
    /// committable regression gate and also what stops it from answering "is
    /// this worth enabling for us". This runs the same pipeline over a real
    /// owner's real library, in both modes, and prints the difference.
    ///
    /// THE QUERIES COME FROM A FILE THE OPERATOR WRITES, and the expected
    /// documents from another. Nothing about somebody's library is invented
    /// here, and — as everywhere in this command group — the OUTPUT is counts,
    /// ranks and the document names the operator themselves supplied. A query
    /// the operator wrote is echoed back; nothing else is.
    private static async Task<int> VisualEvaluateAsync(
        string[] args, IServiceProvider sp, TextWriter stdout, TextWriter stderr)
    {
        if (!TryOwner(args, stderr, out var owner)) return 2;

        var queriesPath = Arg(args, "--queries");
        if (string.IsNullOrWhiteSpace(queriesPath) || !File.Exists(queriesPath))
        {
            stderr.WriteLine(
                "documents visual-evaluate: --queries <file> is required. One case per line, "
                + "as `question<TAB>expected-document[,expected-document]`; an expected list "
                + "may be empty for a deliberately unanswerable case.");
            return 2;
        }

        var cases = new List<DocumentVisualGoldenCase>();
        foreach (var line in await File.ReadAllLinesAsync(queriesPath))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;

            var parts = trimmed.Split('\t');
            var expected = parts.Length > 1
                ? parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : Array.Empty<string>();
            // A third column marks the case as one the visual signal is SUPPOSED
            // to help with, so the report can separate those from the rest
            // instead of hiding a category behind one aggregate.
            var visual = parts.Length > 2
                && string.Equals(parts[2].Trim(), "visual", StringComparison.OrdinalIgnoreCase);

            cases.Add(new DocumentVisualGoldenCase(parts[0].Trim(), expected, visual));
        }

        if (cases.Count == 0)
        {
            stderr.WriteLine("documents visual-evaluate: the query file contained no cases.");
            return 2;
        }

        var comparison = await sp.GetRequiredService<DocumentVisualEvaluator>()
            .CompareAsync(owner, cases);

        Print(stdout, "text_only", comparison.Baseline);
        Print(stdout, "visual_expanded", comparison.Candidate);

        stdout.WriteLine($"recovered={comparison.Recovered.Count}");
        foreach (var query in comparison.Recovered) stdout.WriteLine($"  recovered_query={query}");
        stdout.WriteLine($"regressed={comparison.Regressed.Count}");
        foreach (var query in comparison.Regressed) stdout.WriteLine($"  regressed_query={query}");

        // THE PROMOTION SIGNAL, printed rather than decided. Whether a
        // late-interaction profile is worth enabling is an operator's call
        // against their own corpus, licence and hardware; this command's job is
        // to make the numbers visible, not to flip a switch.
        var delta = comparison.Candidate.VisualNdcgAtFive - comparison.Baseline.VisualNdcgAtFive;
        stdout.WriteLine($"visual_ndcg5_delta={delta:F4}");
        stdout.WriteLine(
            $"relative_visual_ndcg5_gain="
            + $"{(comparison.Baseline.VisualNdcgAtFive > 0
                ? delta / comparison.Baseline.VisualNdcgAtFive
                : 0):F4}");

        return 0;
    }

    private static void Print(TextWriter stdout, string label, DocumentVisualModeReport report)
    {
        stdout.WriteLine($"{label}_mode={report.Mode}");
        stdout.WriteLine($"{label}_queries={report.Queries}");
        stdout.WriteLine($"{label}_recall5={report.RecallAtFive:F4}");
        stdout.WriteLine($"{label}_mrr={report.MeanReciprocalRank:F4}");
        stdout.WriteLine($"{label}_top3={report.TopThreePassed}");
        stdout.WriteLine($"{label}_visual_ndcg5={report.VisualNdcgAtFive:F4}");
        stdout.WriteLine($"{label}_latency_p50_ms={report.MedianLatencyMs}");
        stdout.WriteLine($"{label}_latency_p95_ms={report.P95LatencyMs}");
    }

    // ---- arguments ----------------------------------------------------------

    /// `--owner` is REQUIRED and must parse. A command that defaulted to
    /// "everybody" or to "the first user" would be one typo away from walking
    /// somebody else's library.
    private static bool TryOwner(string[] args, TextWriter stderr, out Guid owner)
    {
        owner = Guid.Empty;
        var raw = Arg(args, "--owner");
        if (string.IsNullOrWhiteSpace(raw))
        {
            stderr.WriteLine("documents: --owner <user-id> is required.");
            return false;
        }
        if (!Guid.TryParse(raw, out owner) || owner == Guid.Empty)
        {
            stderr.WriteLine("documents: --owner must be a user id.");
            return false;
        }
        return true;
    }

    private static string? Arg(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.Ordinal)) return args[i + 1];
        }
        return null;
    }

    private static int? TryInt(string? value)
        => int.TryParse(value, out var parsed) && parsed >= 0 ? parsed : null;
}
