using Microsoft.EntityFrameworkCore;
using NubArca.Api.Ai.Documents;
using NubArca.Api.Data;
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
            _ => Usage(stderr),
        };

    private static Task<int> Usage(TextWriter stderr)
    {
        stderr.WriteLine("usage: documents <index|status> --owner <user-id> [--limit N] [--embed]");
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
