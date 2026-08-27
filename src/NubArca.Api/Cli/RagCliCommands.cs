using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NubArca.Api.Ai;
using NubArca.Api.Ai.TextEmbeddings;
using NubArca.Api.Rag;
using NubArca.Api.Rag.Domains;
using NubArca.Api.Rag.Evaluation;
using NubArca.Api.Rag.Indexing;
using NubArca.Api.Rag.Retrieval;
using NubArca.Api.Rag.Sources;
using NubArca.Api.Rag.Storage;

namespace NubArca.Api.Cli;

/// Operator/developer diagnostics for the RAG substrate.
///
/// `rag query` is DIAGNOSTIC and never calls a generative model. That separation
/// is the point of the command: when Help gives a bad answer, the two candidate
/// causes are "retrieval found the wrong thing" and "the model wrote something
/// wrong about the right thing", and they are fixed in completely different
/// places. A CLI that also generated would tell you which answer you got and not
/// which of those happened.
///
/// Nothing here prints a vector, a secret, a physical path, a provider key or a
/// connection string. Source keys are repository-relative and are the same
/// strings a citation shows.
internal static class RagCliCommands
{
    internal static Task<int> RunAsync(
        string sub, string[] args, IServiceProvider sp, TextWriter stdout, TextWriter stderr)
        => sub switch
        {
            "domains" => DomainsAsync(sp, stdout),
            "status" => StatusAsync(args, sp, stdout, stderr),
            "index" => IndexAsync(args, sp, stdout, stderr),
            "coverage" => CoverageAsync(args, sp, stdout, stderr),
            "query" => QueryAsync(args, sp, stdout, stderr),
            "evaluate" => EvaluateAsync(args, sp, stdout, stderr),
            "seed-profiles" => SeedProfilesAsync(sp, stdout),
            "validate-model" => ValidateModelAsync(args, sp, stdout, stderr),
            _ => Usage(stderr),
        };

    private static Task<int> Usage(TextWriter stderr)
    {
        stderr.WriteLine("usage: rag <domains|status|index|coverage|query|evaluate|seed-profiles|validate-model>");
        return Task.FromResult(2);
    }

    // ---- rag domains --------------------------------------------------------

    private static Task<int> DomainsAsync(IServiceProvider sp, TextWriter stdout)
    {
        var registry = sp.GetRequiredService<IRagDomainRegistry>();
        foreach (var domain in registry.List())
        {
            stdout.WriteLine($"domain={domain.Key}");
            stdout.WriteLine($"  scope={domain.Scope}");
            stdout.WriteLine($"  privacy={domain.PrivacyClass}");
            stdout.WriteLine($"  requires_owner={Bool(domain.RequiresOwner)}");
            stdout.WriteLine($"  external_generation_allowed={Bool(domain.ExternalGenerationAllowed)}");
        }
        return Task.FromResult(0);
    }

    // ---- rag status ---------------------------------------------------------

    private static async Task<int> StatusAsync(
        string[] args, IServiceProvider sp, TextWriter stdout, TextWriter stderr)
    {
        if (!TryDomain(args, sp, stderr, out var domain)) return 2;

        var status = await sp.GetRequiredService<IRagRetriever>()
            .GetStatusAsync(domain.DomainKey);

        stdout.WriteLine($"domain={status.Domain}");
        stdout.WriteLine($"available={Bool(status.IsAvailable)}");
        stdout.WriteLine($"revision={status.Revision ?? "(none)"}");
        stdout.WriteLine($"sources={status.Sources}");
        stdout.WriteLine($"chunks={status.Chunks}");
        stdout.WriteLine($"embedding_profile={status.EmbeddingProfileKey ?? "(none)"}");
        stdout.WriteLine($"vectors={status.Vectors}");
        stdout.WriteLine($"semantic={Bool(status.SemanticAvailable)}");
        if (status.Reason is not null) stdout.WriteLine($"reason={status.Reason}");
        return status.IsAvailable ? 0 : 1;
    }

    // ---- rag index ----------------------------------------------------------

    private static async Task<int> IndexAsync(
        string[] args, IServiceProvider sp, TextWriter stdout, TextWriter stderr)
    {
        if (!TryDomain(args, sp, stderr, out var domain)) return 2;

        var root = Arg(args, "--source") ?? Directory.GetCurrentDirectory();
        if (!Directory.Exists(root))
        {
            stderr.WriteLine("rag index: source directory not found.");
            return 2;
        }

        // Resolved through Git to a FULL COMMIT SHA, so `--revision main` and a
        // bare `HEAD` both become a thing that cannot later mean something else.
        // An unresolvable commit-ish fails here, before any row is written.
        var reader = sp.GetRequiredService<IRepositorySnapshotReader>();
        string revision;
        try
        {
            var resolvedRoot = await reader.ResolveRootAsync(root);
            revision = await reader.ResolveRevisionAsync(resolvedRoot, Arg(args, "--revision"));
        }
        catch (RepositorySnapshotUnavailableException ex)
        {
            stderr.WriteLine($"rag index: {ex.Reason}"
                             + (ex.Revision is null ? string.Empty : $" ({ex.Revision})"));
            stderr.WriteLine("An index that cannot name the snapshot it describes is refused.");
            return 2;
        }

        var request = new RagIndexRequest(
            domain.Key, root, revision,
            EmbedPassages: Flag(args, "--embed"),
            Limit: Int(args, "--limit"),
            DryRun: Flag(args, "--dry-run"));

        RagIndexOutcome outcome;
        try
        {
            outcome = await sp.GetRequiredService<IRagIndexer>().IndexAsync(request);
        }
        catch (RepositorySnapshotUnavailableException ex)
        {
            stderr.WriteLine($"rag index: {ex.Reason}");
            return 1;
        }

        stdout.WriteLine($"domain={outcome.Domain}");
        stdout.WriteLine($"revision={outcome.Revision}");
        // A partial run saw only part of the snapshot and therefore concluded
        // nothing about what left it. Reported, because "why did my --limit run
        // not remove that stale source" has an answer and it is this line.
        stdout.WriteLine($"partial={Bool(outcome.Partial)}");
        stdout.WriteLine($"reconciliation_performed={Bool(outcome.ReconciliationPerformed)}");
        stdout.WriteLine($"sources_seen={outcome.SourcesSeen}");
        stdout.WriteLine(
            $"sources created={outcome.SourcesCreated} updated={outcome.SourcesUpdated} "
            + $"unchanged={outcome.SourcesUnchanged} removed={outcome.SourcesRemoved}");
        stdout.WriteLine(
            $"chunks created={outcome.ChunksCreated} updated={outcome.ChunksUpdated} "
            + $"unchanged={outcome.ChunksUnchanged} removed={outcome.ChunksRemoved}");
        stdout.WriteLine(
            $"embeddings created={outcome.EmbeddingsCreated} removed={outcome.EmbeddingsRemoved} "
            + $"vectors={outcome.VectorsIndexed}");
        if (outcome.EmbeddingProfileKey is not null)
        {
            stdout.WriteLine($"embedding_profile={outcome.EmbeddingProfileKey}");
        }
        if (outcome.EmbeddingReason is not null)
        {
            stdout.WriteLine($"embedding_reason={outcome.EmbeddingReason}");
        }

        // Skips grouped by category, so a run that indexed less than expected
        // says why without listing every path in the repository.
        foreach (var provider in sp.GetServices<IRagSourceProvider>())
        {
            if (provider is RepositorySnapshotSourceProvider repository
                && repository.Domain == domain.Key
                && repository.Tally.Tracked > 0)
            {
                stdout.WriteLine(
                    $"tracked={repository.Tally.Tracked} indexed={repository.Tally.Included} "
                    + $"skipped={repository.Tally.Skipped}");
                foreach (var (reason, count) in repository.Tally.SkipReasons.OrderBy(r => r.Key, StringComparer.Ordinal))
                {
                    stdout.WriteLine($"  skip {reason}={count}");
                }
            }
            if (provider is ProductHelpSourceProvider help && help.MissingSources.Count > 0)
            {
                // A manifest entry with no file is a rename nobody noticed: the
                // run still succeeds, and it says which knowledge silently
                // stopped being indexed.
                foreach (var missing in help.MissingSources)
                {
                    stderr.WriteLine($"rag index: approved source not found: {missing}");
                }
            }
        }

        // Within this process the corpus just changed under a cache that was
        // built from the old signature.
        sp.GetRequiredService<RagLexicalIndexCache>().Clear();
        return 0;
    }

    // ---- rag coverage -------------------------------------------------------

    private static async Task<int> CoverageAsync(
        string[] args, IServiceProvider sp, TextWriter stdout, TextWriter stderr)
    {
        if (!TryDomain(args, sp, stderr, out var domain)) return 2;

        var retriever = sp.GetRequiredService<IRagRetriever>();
        var status = await retriever.GetStatusAsync(domain.DomainKey);

        stdout.WriteLine($"domain={status.Domain}");
        stdout.WriteLine($"revision={status.Revision ?? "(none)"}");
        stdout.WriteLine($"sources={status.Sources}");
        stdout.WriteLine($"chunks={status.Chunks}");

        if (status.EmbeddingProfileKey is null)
        {
            stdout.WriteLine("embeddings=(no text-embedding profile configured)");
            return 0;
        }

        stdout.WriteLine($"embedding_profile={status.EmbeddingProfileKey}");
        stdout.WriteLine($"embeddings={status.Embeddings}/{status.Chunks}");
        stdout.WriteLine($"vectors={status.Vectors}/{status.Embeddings}");
        stdout.WriteLine($"missing_embeddings={Math.Max(0, status.Chunks - status.Embeddings)}");
        // A gap here and not above means the canonical embeddings exist and the
        // accelerator is behind — repaired by re-running `rag index --embed`,
        // and never a reason to re-derive a vector that already exists.
        stdout.WriteLine($"missing_vectors={Math.Max(0, status.Embeddings - status.Vectors)}");
        return 0;
    }

    // ---- rag query ----------------------------------------------------------

    private static async Task<int> QueryAsync(
        string[] args, IServiceProvider sp, TextWriter stdout, TextWriter stderr)
    {
        if (!TryDomain(args, sp, stderr, out var domain)) return 2;

        var question = Positional(args);
        if (string.IsNullOrWhiteSpace(question))
        {
            stderr.WriteLine("rag query: a question is required.");
            return 2;
        }

        var max = Int(args, "--max") ?? 5;
        var result = await sp.GetRequiredService<IRagRetriever>()
            .RetrieveAsync(new RagQuery(domain.DomainKey, question, Math.Clamp(max, 1, 20), 12000));

        stdout.WriteLine($"domain={result.Domain}");
        stdout.WriteLine($"revision={result.Revision ?? "(none)"}");
        stdout.WriteLine($"mode={result.Mode}");
        stdout.WriteLine($"embedding_profile={result.EmbeddingProfileKey ?? "(none)"}");
        stdout.WriteLine($"strong_evidence={Bool(result.HasStrongEvidence)}");
        if (result.Reason is not null) stdout.WriteLine($"reason={result.Reason}");
        stdout.WriteLine();

        var rank = 0;
        foreach (var evidence in result.Evidence)
        {
            rank++;
            stdout.WriteLine($"{rank}  {evidence.Id}");
            if (!string.IsNullOrEmpty(evidence.Title)) stdout.WriteLine($"   title={evidence.Title}");
            if (!string.IsNullOrEmpty(evidence.Section)) stdout.WriteLine($"   section={evidence.Section}");
            stdout.WriteLine($"   kind={evidence.SourceKind}");
            stdout.WriteLine(
                $"   lexical_rank={Rank(evidence.LexicalRank)} vector_rank={Rank(evidence.VectorRank)} "
                + $"fusion_rank={evidence.FusionRank}");
        }
        return result.HasStrongEvidence ? 0 : 1;
    }

    // ---- rag evaluate -------------------------------------------------------

    private static async Task<int> EvaluateAsync(
        string[] args, IServiceProvider sp, TextWriter stdout, TextWriter stderr)
    {
        if (!TryDomain(args, sp, stderr, out var domain)) return 2;

        var cases = RagGoldenSet.For(domain.Key);
        if (cases.Count == 0)
        {
            stderr.WriteLine($"rag evaluate: no golden set for domain '{domain.Key}'.");
            return 2;
        }

        var report = await new RagEvaluator(sp.GetRequiredService<IRagRetriever>())
            .EvaluateAsync(domain.Key, cases);

        foreach (var outcome in report.Outcomes.Where(o => !o.Passed))
        {
            // Only the failures are listed. A green run is three numbers; a red
            // one has to say which question and what led instead.
            stderr.WriteLine($"FAIL [{outcome.Case.Language}] {outcome.Case.Query}");
            stderr.WriteLine($"     expected={string.Join(", ", outcome.Case.ExpectedSourcePrefixes)}");
            stderr.WriteLine($"     got={(outcome.TopSources.Count == 0 ? "(nothing)" : string.Join(", ", outcome.TopSources))}");
        }

        stdout.WriteLine($"domain={report.Domain}");
        stdout.WriteLine($"revision={report.Revision ?? "(none)"}");
        stdout.WriteLine($"mode={report.Mode}");
        stdout.WriteLine($"profile={report.EmbeddingProfileKey ?? "(none)"}");
        stdout.WriteLine($"queries={report.Queries}");
        stdout.WriteLine($"recall_at_5={report.RecallAtFive:0.000}");
        stdout.WriteLine($"mrr={report.MeanReciprocalRank:0.000}");
        stdout.WriteLine($"top3_expected_source_pass={report.TopThreePassed}/{report.Queries}");
        return report.TopThreePassed == report.Queries ? 0 : 1;
    }

    // ---- rag seed-profiles --------------------------------------------------

    private static async Task<int> SeedProfilesAsync(IServiceProvider sp, TextWriter stdout)
    {
        var result = await sp.GetRequiredService<IAiProfileRegistry>()
            .SeedRagTextEmbeddingProfilesAsync();
        stdout.WriteLine($"models_created={result.ModelsCreated}");
        stdout.WriteLine($"profiles_created={result.ProfilesCreated}");
        stdout.WriteLine($"deterministic_profile={DeterministicTextEmbeddingProvider.ProfileKey}");
        stdout.WriteLine($"onnx_profile={RagTextEmbeddingModels.MultilingualE5SmallProfileKey}");
        return 0;
    }

    // ---- rag validate-model -------------------------------------------------

    /// Proves a locally installed embedding model actually works, before an
    /// operator turns semantic retrieval on. It asserts CONTRACT — the model
    /// loads, the tokenizer loads, both input kinds produce the profile's
    /// dimension, the vectors are finite and normalized, the same text is
    /// deterministic, and different texts are not identical. It deliberately
    /// asserts nothing about semantic quality: that is what `rag evaluate`
    /// measures, against real questions.
    private static async Task<int> ValidateModelAsync(
        string[] args, IServiceProvider sp, TextWriter stdout, TextWriter stderr)
    {
        var options = sp.GetRequiredService<IOptions<RagOptions>>().Value;
        var key = Arg(args, "--profile") ?? options.TextEmbeddingProfileKey;
        if (string.IsNullOrWhiteSpace(key))
        {
            stderr.WriteLine("rag validate-model: --profile (or Rag:TextEmbeddingProfileKey) is required.");
            return 2;
        }

        var resolution = await sp.GetRequiredService<TextEmbeddingResolver>().ResolveProfileAsync(key);
        stdout.WriteLine($"profile={key}");
        if (!resolution.IsAvailable)
        {
            stdout.WriteLine($"available=false reason={resolution.Reason}");
            return 1;
        }

        var profile = resolution.Profile!;
        var provider = resolution.Provider!;
        stdout.WriteLine($"available=true dimension={profile.Dimension}");

        var query = await provider.EmbedAsync(profile, "come uso i volti?", TextEmbeddingInputKind.Query);
        var passage = await provider.EmbedAsync(
            profile, "Apri Volti per assegnare un nome a un gruppo suggerito.",
            TextEmbeddingInputKind.Passage);
        var repeat = await provider.EmbedAsync(profile, "come uso i volti?", TextEmbeddingInputKind.Query);

        var ok = true;
        ok &= Check(stdout, "query_dimension", query.Dimension == profile.Dimension);
        ok &= Check(stdout, "passage_dimension", passage.Dimension == profile.Dimension);
        ok &= Check(stdout, "finite", query.Vector.All(float.IsFinite) && passage.Vector.All(float.IsFinite));
        ok &= Check(stdout, "normalized", Math.Abs(Norm(query.Vector) - 1.0) < 0.01);
        ok &= Check(stdout, "deterministic", query.Vector.SequenceEqual(repeat.Vector));
        ok &= Check(stdout, "distinct", !query.Vector.SequenceEqual(passage.Vector));
        ok &= Check(stdout, "vector_table",
            await sp.GetRequiredService<RagVectorIndexService>()
                .IsBackendAvailableAsync(profile.Dimension));

        return ok ? 0 : 1;
    }

    private static bool Check(TextWriter stdout, string name, bool ok)
    {
        stdout.WriteLine($"{name}={(ok ? "ok" : "FAILED")}");
        return ok;
    }

    private static double Norm(float[] vector)
    {
        double sum = 0;
        foreach (var value in vector) sum += (double)value * value;
        return Math.Sqrt(sum);
    }

    // ---- argument plumbing --------------------------------------------------

    private static bool TryDomain(
        string[] args, IServiceProvider sp, TextWriter stderr, out RagDomainDefinition domain)
    {
        var key = Arg(args, "--domain");
        if (string.IsNullOrWhiteSpace(key))
        {
            stderr.WriteLine("rag: --domain is required (see `rag domains`).");
            domain = default!;
            return false;
        }
        if (!sp.GetRequiredService<IRagDomainRegistry>().TryGet(key, out domain))
        {
            // Unknown fails rather than defaulting. A typo that resolved to
            // something would resolve to a domain the operator was not asking
            // about, and the output would look right.
            stderr.WriteLine($"rag: unknown domain '{key}' (see `rag domains`).");
            return false;
        }
        return true;
    }

    private static string Bool(bool value) => value ? "true" : "false";

    private static string Rank(int? rank) => rank?.ToString() ?? "-";

    private static string? Arg(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.Ordinal)) return args[i + 1];
        }
        return null;
    }

    private static bool Flag(string[] args, string name)
        => args.Any(a => string.Equals(a, name, StringComparison.Ordinal));

    private static int? Int(string[] args, string name)
        => int.TryParse(Arg(args, name), out var value) ? value : null;

    /// Options that TAKE a value, listed rather than guessed.
    ///
    /// "Skip the token after any `--flag`" would silently eat the question in
    /// `rag query --domain d --some-boolean "come uso i volti?"`, and the
    /// command would then complain that no question was given.
    private static readonly HashSet<string> ValueOptions = new(StringComparer.Ordinal)
    {
        "--domain", "--max", "--source", "--revision", "--limit", "--profile",
    };

    /// The first argument that is neither an option nor an option's value — the
    /// question, in `rag query --domain product-help "…"`.
    private static string? Positional(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                if (ValueOptions.Contains(args[i])) i++;
                continue;
            }
            return args[i];
        }
        return null;
    }
}
