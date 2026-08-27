namespace NubArca.Api.Rag.Sources;

/// Why a repository file was or was not indexed.
public sealed record RepositoryEligibility(bool IsEligible, string Reason)
{
    public static RepositoryEligibility Eligible { get; } = new(true, "eligible");

    public static RepositoryEligibility No(string reason) => new(false, reason);
}

/// What may become `nubarca-repository` knowledge.
///
/// TRACKED IS THE FIRST GATE, NOT THE LAST ONE. `git ls-files` answers "is this
/// part of the project", which is a useful question and a different one from
/// "is this safe to put in a retrieval index and hand to a model". A tracked
/// file can be a committed example credential, a build artifact somebody added
/// by accident, a 40 MB fixture, or a binary. So eligibility is:
///
///     tracked  +  path policy  +  content check  =  indexable source
///
/// The path rules below are a DENYLIST, which is the one place in the RAG
/// substrate that is not an allowlist — and it is worth stating why, because
/// Product Help deliberately went the other way. Product Help's corpus is
/// curated: a few dozen documents somebody chose, where "a new document is out
/// until classified" is the correct default. The repository domain's whole
/// purpose is broad coverage of a codebase that gains files every day, and an
/// allowlist there would mean the index silently stops describing the code the
/// moment somebody forgets to update it. The compensating controls are that
/// this domain is `SystemInternal` and can never reach an External model, and
/// that the content checks below are conservative.
public static class RepositorySourcePolicy
{
    /// Files larger than this are not knowledge. A megabyte of tracked text is
    /// a data file, a lockfile or a generated bundle.
    public const int MaximumBytes = 512 * 1024;

    /// A file with fewer characters than this has nothing to retrieve.
    public const int MinimumCharacters = 40;

    /// Path segments that are never knowledge, matched as whole segments so
    /// `bin` matches `src/bin/` and not `binary-formats.md`.
    ///
    /// Every name here had to be checked against the repository rather than
    /// assumed: `data` and `storage` are NOT on this list, because
    /// `src/NubArca.Api/Data` and `src/NubArca.Api/Storage` are two of the most
    /// important directories in the product. The runtime directories those
    /// names also describe are untracked, so `git ls-files` never offers them.
    private static readonly string[] DeniedSegments =
    {
        ".git", "node_modules", "bin", "obj",
        "dist", "build", "coverage", "TestResults", ".vs", ".idea", ".vscode",
        "__pycache__", ".venv", "venv", "vendor", "Pods", ".gradle",
        "graphify-out", "backups", "secrets", "credentials", ".ssh", ".gnupg",
    };

    /// Exact repository-relative paths that are never knowledge.
    private static readonly HashSet<string> DeniedPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "package-lock.json", "frontend/package-lock.json", "mobile/package-lock.json",
        "tv/package-lock.json", "yarn.lock", "pnpm-lock.yaml", "Cargo.lock",
    };

    /// THE EVALUATION SET IS NOT PART OF THE CORPUS IT MEASURES.
    ///
    /// `RagGoldenSet.cs` holds the golden queries as string literals, so once the
    /// repository indexed itself, the single best lexical match for a conceptual
    /// golden question became the file containing that exact sentence. It led
    /// three of four failures in the first real evaluation run and dropped MRR
    /// from 0.583 to 0.395 — a benchmark measuring its own question list, which
    /// is worth nothing.
    ///
    /// This comment deliberately does not quote the question either: a comment
    /// is indexed source, so explaining the problem by restating the prompt
    /// would recreate it. RagContaminationTests enforces that.
    ///
    /// This is a general rule rather than one file's exemption: a corpus that
    /// contains the questions cannot answer them, it can only find them.
    private const string EvaluationSetPrefix = "src/NubArca.Api/Rag/Evaluation/";

    /// File names that are never knowledge, whatever they contain. These are
    /// the shapes of key material itself.
    private static readonly string[] DeniedNameFragments =
    {
        ".env", "id_rsa", "id_ed25519", ".pem", ".key", ".pfx", ".p12",
        ".keystore", ".jks",
    };

    /// Words that make a CONFIGURATION file suspect and mean nothing in a source
    /// file name.
    ///
    /// This split is not fussiness. Applied to everything, "secret" excluded
    /// `20260809003319_AddTvPersonalSecretScheme.cs` — an EF migration
    /// describing a feature — and "password" would exclude
    /// `PasswordResetToken.cs` and `PasswordRecoveryService.cs`. Those files are
    /// the answer to "how does NubArca handle credentials", which is exactly the
    /// kind of question the repository domain exists for. A file named
    /// `secrets.json` is a different thing, and it is what this list is for.
    private static readonly string[] DeniedConfigurationNameFragments =
    {
        "secret", "credential", "password", "token",
    };

    /// Languages whose files are PROSE OR CODE — descriptions of the product
    /// rather than values it runs on. The suspect-word list above does not apply
    /// to them.
    private static readonly HashSet<string> DescriptiveLanguages = new(StringComparer.Ordinal)
    {
        RagCodeLanguages.CSharp, RagCodeLanguages.TypeScript, RagCodeLanguages.Tsx,
        RagCodeLanguages.JavaScript, RagCodeLanguages.Kotlin, RagCodeLanguages.Markdown,
        RagCodeLanguages.Sql, RagCodeLanguages.Css,
    };

    /// The ONE narrow exception, and it is an exception by NAME rather than by
    /// pattern. `.env.example` documents which variables exist, with placeholder
    /// values, and it is genuinely the answer to "what configuration does
    /// NubArca take". Everything else matching `.env` stays denied — including
    /// `.env.production.example`, which nobody has reviewed.
    private static readonly HashSet<string> SafeExamples = new(StringComparer.Ordinal)
    {
        ".env.example",
    };

    /// Extensions whose content is text NubArca is willing to index. An
    /// allowlist here rather than "anything that is not binary", because
    /// "probably text" is how a `.pem` or a `.csv` of real data gets in.
    private static readonly IReadOnlyDictionary<string, string> TextExtensions =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".cs"] = RagCodeLanguages.CSharp,
            [".ts"] = RagCodeLanguages.TypeScript,
            [".tsx"] = RagCodeLanguages.Tsx,
            [".js"] = RagCodeLanguages.JavaScript,
            [".mjs"] = RagCodeLanguages.JavaScript,
            [".cjs"] = RagCodeLanguages.JavaScript,
            [".jsx"] = RagCodeLanguages.JavaScript,
            [".kt"] = RagCodeLanguages.Kotlin,
            [".kts"] = RagCodeLanguages.Kotlin,
            [".md"] = RagCodeLanguages.Markdown,
            [".json"] = RagCodeLanguages.Json,
            [".yml"] = RagCodeLanguages.Yaml,
            [".yaml"] = RagCodeLanguages.Yaml,
            [".sql"] = RagCodeLanguages.Sql,
            [".sh"] = RagCodeLanguages.Shell,
            [".bash"] = RagCodeLanguages.Shell,
            [".zsh"] = RagCodeLanguages.Shell,
            [".css"] = RagCodeLanguages.Css,
            [".csproj"] = RagCodeLanguages.Xml,
            [".props"] = RagCodeLanguages.Xml,
            [".targets"] = RagCodeLanguages.Xml,
            [".xml"] = RagCodeLanguages.Xml,
            [".toml"] = RagCodeLanguages.Toml,
            [".sln"] = RagCodeLanguages.Text,
            [".txt"] = RagCodeLanguages.Text,
            [".gradle"] = RagCodeLanguages.Text,
        };

    /// Extension-less files that are still text worth indexing, by exact name.
    private static readonly IReadOnlyDictionary<string, string> TextFileNames =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Dockerfile"] = RagCodeLanguages.Shell,
            ["LICENSE"] = RagCodeLanguages.Text,
            ["Makefile"] = RagCodeLanguages.Shell,
        };

    /// Whether a Git tree entry's MODE may be treated as readable content.
    ///
    /// Stated as policy rather than left implicit in the reader, because the
    /// dangerous behaviour is a filesystem read that FOLLOWS a link. The
    /// predecessor read files through the working tree, so a tracked symlink
    /// pointing at `/etc/shadow` or at a sibling checkout was simply read. The
    /// Git-object reader cannot do that by construction — a symlink's blob is
    /// its target STRING — but a future implementation could regress to
    /// filesystem reads, and this is the assertion that would catch it.
    ///
    /// The target is never resolved, never normalized back into the repository,
    /// and never read to decide whether it is safe. A link is refused for being
    /// a link.
    public static RepositoryEligibility CheckGitMode(string? mode) => mode switch
    {
        RepositorySnapshotEntry.SymbolicLinkMode => RepositoryEligibility.No("symlink"),
        RepositorySnapshotEntry.SubmoduleMode => RepositoryEligibility.No("submodule"),
        null or "" => RepositoryEligibility.No("unknown-mode"),
        _ => RepositoryEligibility.Eligible,
    };

    /// Path-only eligibility. Content is checked separately, because a caller
    /// that has not read the file yet should still be able to skip it.
    public static RepositoryEligibility CheckPath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return RepositoryEligibility.No("empty-path");

        var normalized = relativePath.Replace('\\', '/').TrimStart('/');
        if (normalized.Contains("..", StringComparison.Ordinal))
        {
            return RepositoryEligibility.No("path-traversal");
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return RepositoryEligibility.No("empty-path");

        var name = segments[^1];

        if (DeniedPaths.Contains(normalized)) return RepositoryEligibility.No("denied-path");

        if (normalized.StartsWith(EvaluationSetPrefix, StringComparison.Ordinal))
        {
            return RepositoryEligibility.No("evaluation-set");
        }

        foreach (var segment in segments)
        {
            if (DeniedSegments.Contains(segment, StringComparer.OrdinalIgnoreCase))
            {
                return RepositoryEligibility.No("denied-directory");
            }
        }

        if (SafeExamples.Contains(name))
        {
            return RepositoryEligibility.Eligible;
        }

        foreach (var fragment in DeniedNameFragments)
        {
            if (name.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                return RepositoryEligibility.No("denied-secret-material");
            }
        }

        var language = LanguageOf(normalized);
        if (language is null || !DescriptiveLanguages.Contains(language))
        {
            foreach (var fragment in DeniedConfigurationNameFragments)
            {
                if (name.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                {
                    return RepositoryEligibility.No("denied-secret-material");
                }
            }
        }

        // A dot-directory is tooling state. A dot-FILE at the root is often
        // configuration worth reading (`.editorconfig`), but the ones that are
        // are also the ones with a denied fragment, so the simple rule is the
        // safe one.
        foreach (var segment in segments[..^1])
        {
            if (segment.StartsWith('.')) return RepositoryEligibility.No("hidden-directory");
        }

        return language is null
            ? RepositoryEligibility.No("unsupported-type")
            : RepositoryEligibility.Eligible;
    }

    /// Size eligibility from the TREE ENTRY, before any bytes exist.
    ///
    /// CheckContent below asks the same question of a byte array, which means
    /// the array had to be allocated to ask it — a report rather than a bound. A
    /// tracked multi-gigabyte blob is refused here, from the size `ls-tree -l`
    /// already printed, and the object store is never asked for it.
    ///
    /// An UNKNOWN size is not refused: `-l` prints `-` for a non-blob, and those
    /// are already excluded by mode. It falls through to the content check,
    /// which still has the last word — but with the low-level allocation ceiling
    /// in GitCatFileSession underneath it.
    public static RepositoryEligibility CheckSize(long size)
        => size > MaximumBytes ? RepositoryEligibility.No("too-large") : RepositoryEligibility.Eligible;

    /// Content eligibility: size, emptiness, and the one check an extension can
    /// never make — whether the bytes are actually text.
    public static RepositoryEligibility CheckContent(string relativePath, byte[] bytes)
    {
        if (bytes.Length == 0) return RepositoryEligibility.No("empty-file");
        if (bytes.Length > MaximumBytes) return RepositoryEligibility.No("too-large");
        if (LooksBinary(bytes)) return RepositoryEligibility.No("binary");
        return RepositoryEligibility.Eligible;
    }

    /// A NUL byte anywhere in the first block is the same test `git diff` uses,
    /// and it is the right one: no text encoding NubArca stores puts a NUL in
    /// the middle of a file, and every binary format does.
    public static bool LooksBinary(ReadOnlySpan<byte> bytes)
    {
        var window = bytes.Length < 8000 ? bytes : bytes[..8000];
        foreach (var b in window)
        {
            if (b == 0) return true;
        }
        return false;
    }

    /// The code language for a path, or null when the path is not a text type
    /// this policy indexes.
    public static string? LanguageOf(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        var name = normalized[(normalized.LastIndexOf('/') + 1)..];

        if (SafeExamples.Contains(name)) return RagCodeLanguages.Text;
        if (TextFileNames.TryGetValue(name, out var byName)) return byName;

        var dot = name.LastIndexOf('.');
        if (dot <= 0) return null;
        var extension = name[dot..];
        return TextExtensions.TryGetValue(extension, out var byExtension) ? byExtension : null;
    }

    /// What KIND of source a repository path is.
    ///
    /// Directory-driven, because in this repository directory IS the strongest
    /// available statement of intent: everything under `tests/` is a test,
    /// everything under `Migrations/` is a migration, and `docs/` is prose.
    public static string SourceKindOf(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/').TrimStart('/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var name = segments.Length > 0 ? segments[^1] : normalized;

        if (SafeExamples.Contains(name)) return RagSourceKinds.ExampleConfiguration;

        if (segments.Any(s => s.Equals("tests", StringComparison.OrdinalIgnoreCase))
            || name.EndsWith("Tests.cs", StringComparison.Ordinal)
            || name.EndsWith(".test.ts", StringComparison.Ordinal)
            || name.EndsWith(".test.tsx", StringComparison.Ordinal)
            || name.EndsWith(".spec.ts", StringComparison.Ordinal))
        {
            return RagSourceKinds.Test;
        }

        if (segments.Any(s => s.Equals("Migrations", StringComparison.Ordinal)))
        {
            return RagSourceKinds.Migration;
        }

        if (segments.Length > 0 && segments[0].Equals("scripts", StringComparison.OrdinalIgnoreCase))
        {
            return RagSourceKinds.Script;
        }

        return LanguageOf(normalized) switch
        {
            RagCodeLanguages.Markdown => RagSourceKinds.Documentation,
            RagCodeLanguages.Shell => RagSourceKinds.Script,
            RagCodeLanguages.Json or RagCodeLanguages.Yaml or RagCodeLanguages.Toml
                or RagCodeLanguages.Xml => RagSourceKinds.Configuration,
            RagCodeLanguages.Sql => RagSourceKinds.Migration,
            null => RagSourceKinds.Configuration,
            _ => RagSourceKinds.SourceCode,
        };
    }
}
