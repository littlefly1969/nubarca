using System.Text.Json;

namespace NubArca.Api.Rag.ProductHelp;

/// Loads the pre-built corpus from the image, and refuses one that does not
/// belong to the running release.
///
/// REVISION GATE. Help that answered from a newer `main` would tell an operator
/// to click something their installation does not have, which is worse than no
/// Help. An UNKNOWN running revision — a dev run outside the image — cannot be
/// compared, so the corpus is accepted; a KNOWN one that disagrees is refused.
public static class ProductHelpCorpusLoader
{
    /// The revision this process was built from. The same environment variable
    /// the deploy gates read, so "knowledge revision == running revision" is a
    /// comparison against the SAME provenance the rest of the system uses.
    public static string RunningRevision
        => Environment.GetEnvironmentVariable("NUBARCA_GIT_SHA") ?? string.Empty;

    public static ProductHelpCorpus Load(string? path, string runningRevision, ILogger log)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            // No path in the message: an operator reads their own configuration,
            // and a log line is not the place to publish a filesystem layout.
            log.LogInformation("product-help: no knowledge corpus at the configured path");
            return ProductHelpCorpus.Empty;
        }

        ProductHelpCorpus? corpus;
        try
        {
            corpus = JsonSerializer.Deserialize<ProductHelpCorpus>(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            log.LogWarning("product-help: knowledge corpus could not be read");
            return ProductHelpCorpus.Empty;
        }
        if (corpus is null || corpus.Documents.Count == 0) return ProductHelpCorpus.Empty;

        if (!string.IsNullOrEmpty(runningRevision)
            && !string.Equals(corpus.Revision, runningRevision, StringComparison.Ordinal))
        {
            log.LogWarning(
                "product-help: knowledge corpus revision does not match the running build; help knowledge disabled");
            return ProductHelpCorpus.Empty;
        }
        return corpus;
    }
}
