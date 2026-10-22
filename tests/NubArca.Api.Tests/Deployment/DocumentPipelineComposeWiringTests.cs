using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NubArca.Api.Ai;
using NubArca.Api.Ai.Documents;
using NubArca.Api.Assistant;
using NubArca.Api.Help;
using NubArca.Api.Rag;
using NubArca.Api.Rag.Domains;

namespace NubArca.Api.Tests.Deployment;

/// <summary>
/// The owner-document pipeline's keys reach the containers that need them, and
/// with an empty .env they bind to exactly the "off" state.
///
/// A key that is not listed in docker-compose.prod.yml never reaches the
/// container, whatever .env says (`--env-file` feeds substitution, not the
/// process environment). That is how the document keys were unreachable in
/// production before. And a default written into Compose that the binder reads
/// differently from the code default would turn something on, or crash the
/// boot, on an installation that set nothing. This reads the tracked file and
/// binds what it would produce through the real option classes.
/// </summary>
public sealed class DocumentPipelineComposeWiringTests
{
    // What the `ai.documents.index` job (worker) and the documents CLI / query
    // embedding (api) both read.
    private static readonly string[] SharedKeys =
    [
        "Ai__Enabled",
        "Ai__DocumentExtractionEnabled",
        "Ai__DocumentEmbeddingsEnabled",
        "Ai__DocumentExtraction__OcrEnabled",
        "Ai__DocumentExtraction__OcrLanguages",
        "Ai__DocumentExtraction__OcrPageTimeoutSeconds",
        "Ai__DocumentExtraction__MaxConcurrentOcrPages",
        "Ai__DocumentExtraction__ParseTimeoutSeconds",
        "Ai__DocumentIndexing__MaxConcurrentJobs",
        "Ai__DocumentIndexing__GateWaitSeconds",
        "Ai__DocumentIndexing__MaxAttemptsPerSource",
        "Ai__DocumentIndexing__ParallelTextAndVisualModels",
        "Ai__DocumentVisual__Enabled",
        "Rag__Domains__user-documents__SemanticEnabled",
        "Rag__Domains__user-documents__TextEmbeddingProfileKey",
        "Ai__Onnx__OpenVino__RagTextDevice",
    ];

    // The private chat endpoint lives in the api only; the worker has no reason
    // to hold a model endpoint or its key.
    private static readonly string[] ApiOnlyKeys =
    [
        "Assistant__PrivateKnowledgeModel",
        "Assistant__Models__documents-local__Trust",
        "Assistant__Models__documents-local__BaseUrl",
        "Assistant__Models__documents-local__Model",
    ];

    [Fact]
    public void Document_Pipeline_Keys_Reach_The_Services_That_Read_Them()
    {
        var services = ReadEnvironmentBlocks();

        foreach (var key in SharedKeys)
        {
            Assert.True(services["api"].ContainsKey(key), $"api does not receive {key}");
            Assert.True(services["worker"].ContainsKey(key), $"worker does not receive {key}");
        }
        foreach (var key in ApiOnlyKeys)
        {
            Assert.True(services["api"].ContainsKey(key), $"api does not receive {key}");
            Assert.False(services["worker"].ContainsKey(key), $"worker must not receive {key}");
        }
    }

    [Theory]
    [InlineData("api")]
    [InlineData("worker")]
    public void An_Empty_Env_File_Binds_To_The_Code_Defaults_And_Everything_Off(string service)
    {
        var configuration = Bind(ReadEnvironmentBlocks()[service], new Dictionary<string, string>());

        var ai = configuration.GetSection(AiOptions.SectionName).Get<AiOptions>()!;
        Assert.False(ai.Enabled);
        Assert.False(ai.DocumentExtractionEnabled);
        Assert.False(ai.DocumentEmbeddingsEnabled);
        var indexingDefaults = new AiDocumentIndexingOptions();
        Assert.Equal(indexingDefaults.MaxConcurrentJobs, ai.DocumentIndexing.MaxConcurrentJobs);
        Assert.Equal(indexingDefaults.GateWaitSeconds, ai.DocumentIndexing.GateWaitSeconds);
        Assert.Equal(indexingDefaults.MaxAttemptsPerSource, ai.DocumentIndexing.MaxAttemptsPerSource);
        Assert.Equal(indexingDefaults.ParallelTextAndVisualModels, ai.DocumentIndexing.ParallelTextAndVisualModels);
        Assert.Equal(new AiOnnxOpenVinoOptions().RagTextDevice, ai.Onnx.OpenVino.RagTextDevice);

        var extraction = configuration.GetSection(DocumentExtractionOptions.SectionName)
            .Get<DocumentExtractionOptions>()!;
        var extractionDefaults = new DocumentExtractionOptions();
        Assert.False(extraction.OcrEnabled);
        Assert.Equal(extractionDefaults.OcrLanguages, extraction.OcrLanguages);
        Assert.Equal(extractionDefaults.OcrPageTimeoutSeconds, extraction.OcrPageTimeoutSeconds);
        Assert.Equal(extractionDefaults.MaxConcurrentOcrPages, extraction.MaxConcurrentOcrPages);
        Assert.Equal(extractionDefaults.ParseTimeoutSeconds, extraction.ParseTimeoutSeconds);

        Assert.Equal(RagSemanticSettings.Disabled, ResolveUserDocuments(configuration));

        if (service == "api")
        {
            Assert.False(ResolvePrivateModel(configuration).IsUsable);
        }
    }

    [Fact]
    public void Operator_Values_Reach_The_Real_Domain_And_Model_Keys()
    {
        // The Compose VARIABLE spells the domain with an underscore (a hyphen
        // cannot appear in an interpolation name); the container KEY must still
        // be the real domain name, or the resolver would never see it.
        var env = new Dictionary<string, string>
        {
            ["Rag__Domains__user_documents__SemanticEnabled"] = "true",
            ["Rag__Domains__user_documents__TextEmbeddingProfileKey"] = "rag-text-multilingual-e5-small-v1",
            ["Assistant__Enabled"] = "true",
            ["Assistant__PrivateKnowledgeModel"] = "documents-local",
            ["Assistant__Models__documents_local__Trust"] = "LocalTrusted",
            ["Assistant__Models__documents_local__BaseUrl"] = "http://local-model:8080",
            ["Assistant__Models__documents_local__Model"] = "local",
            ["Ai__DocumentExtraction__OcrEnabled"] = "true",
            ["Ai__DocumentExtraction__OcrLanguages"] = "eng+ita",
        };
        var configuration = Bind(ReadEnvironmentBlocks()["api"], env);

        Assert.Equal(
            new RagSemanticSettings(true, "rag-text-multilingual-e5-small-v1"),
            ResolveUserDocuments(configuration));

        var model = ResolvePrivateModel(configuration);
        Assert.True(model.IsUsable);
        Assert.Equal(AssistantModelTrust.LocalTrusted, model.Profile!.Trust);

        var extraction = configuration.GetSection(DocumentExtractionOptions.SectionName)
            .Get<DocumentExtractionOptions>()!;
        Assert.True(extraction.OcrEnabled);
        Assert.Equal("eng+ita", extraction.OcrLanguages);
    }

    private static RagSemanticSettings ResolveUserDocuments(IConfiguration configuration)
    {
        var rag = configuration.GetSection(RagOptions.SectionName).Get<RagOptions>() ?? new RagOptions();
        return new RagSemanticProfileResolver(RagDomainRegistry.Instance, Options.Create(rag))
            .Resolve(RagDomainKey.UserDocuments);
    }

    private static AssistantModelResolution ResolvePrivateModel(IConfiguration configuration)
    {
        var assistant = configuration.GetSection(AssistantOptions.SectionName).Get<AssistantOptions>()
                        ?? new AssistantOptions();
        return new AssistantModelResolver(
                Options.Create(assistant),
                Options.Create(new ExternalHelpOptions()),
                NullLogger<AssistantModelResolver>.Instance)
            .PrivateKnowledgeModel;
    }

    /// Substitute `.env` values into one service's environment block, as
    /// Compose does, and load the result the way the environment-variable
    /// provider would (`__` → `:`).
    private static IConfiguration Bind(
        IReadOnlyDictionary<string, string> block, IReadOnlyDictionary<string, string> env)
    {
        var values = new Dictionary<string, string?>();
        foreach (var (key, raw) in block)
        {
            values[key.Replace("__", ":", StringComparison.Ordinal)] = Interpolate(raw, env);
        }
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static readonly Regex Substitution = new(@"\$\{([A-Za-z0-9_]+)(?::([-?])([^}]*))?\}");

    private static string Interpolate(string raw, IReadOnlyDictionary<string, string> env)
    {
        var value = raw.Trim();
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') value = value[1..^1];
        return Substitution.Replace(value, m =>
        {
            env.TryGetValue(m.Groups[1].Value, out var set);
            if (!string.IsNullOrEmpty(set)) return set;
            // `:?` is a required value; this test supplies a placeholder.
            return m.Groups[2].Value == "-" ? m.Groups[3].Value : "required";
        });
    }

    /// The `environment:` maps of the api and worker services, read line by
    /// line. The file's own style (two-space services, six-space keys) is
    /// stable and asserted non-empty below, so a reformat fails loudly here.
    private static Dictionary<string, Dictionary<string, string>> ReadEnvironmentBlocks()
    {
        var lines = File.ReadAllLines(Path.Combine(FindRepositoryRoot(), "docker-compose.prod.yml"));
        var services = new Dictionary<string, Dictionary<string, string>>();
        string? service = null;
        var inEnvironment = false;
        var inServices = false;

        foreach (var line in lines)
        {
            if (Regex.IsMatch(line, @"^[^\s#]")) { inServices = line.StartsWith("services:", StringComparison.Ordinal); service = null; continue; }
            if (!inServices) continue;

            var serviceMatch = Regex.Match(line, @"^  ([A-Za-z0-9_-]+):\s*$");
            if (serviceMatch.Success)
            {
                service = serviceMatch.Groups[1].Value;
                services[service] = new Dictionary<string, string>();
                inEnvironment = false;
                continue;
            }
            if (service is null) continue;

            if (Regex.IsMatch(line, @"^    [A-Za-z_]+:"))
            {
                inEnvironment = line.TrimStart().StartsWith("environment:", StringComparison.Ordinal);
                continue;
            }
            if (!inEnvironment) continue;

            var entry = Regex.Match(line, @"^      ([A-Za-z0-9_\-]+):\s*(.*)$");
            if (entry.Success) services[service][entry.Groups[1].Value] = entry.Groups[2].Value;
        }

        Assert.NotEmpty(services["api"]);
        Assert.NotEmpty(services["worker"]);
        return services;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "NubArca.sln")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new InvalidOperationException("could not locate the repository root (NubArca.sln)");
    }
}
