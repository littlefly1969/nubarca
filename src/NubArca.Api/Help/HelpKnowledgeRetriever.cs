using System.Text.Json;
using Microsoft.Extensions.Options;

namespace NubArca.Api.Help;

public sealed record HelpExcerpt(string SourceId, string Title, string Path, string Text);

/// Local retrieval over the approved public corpus.
///
/// The MODEL does not retrieve. It is handed a bounded set of excerpts that this
/// service selected, and it has no way to ask for more — no tool, no callback,
/// no second round trip. That is what keeps "what may leave NubArca" a finite,
/// reviewable set rather than whatever a model decides to look up.
public interface IHelpKnowledgeRetriever
{
    /// The corpus revision, or null when no usable corpus is loaded.
    string? Revision { get; }

    bool IsAvailable { get; }

    IReadOnlyList<HelpExcerpt> Retrieve(string question, int maxExcerpts, int maxCharacters);
}

/// Loads the pre-built corpus from disk and ranks it with a small BM25.
///
/// Deliberately no vector database and no cloud embeddings: this has to explain
/// a product from a few dozen documents, and sending text to an embedding
/// service to decide what to send to a chat service would widen exactly the
/// boundary this feature exists to keep narrow.
///
/// REVISION GATE: a corpus whose revision differs from the running build is
/// refused outright. Help that answers from a newer `main` would tell an
/// operator to use a screen their installation does not have.
public sealed class FileHelpKnowledgeRetriever : IHelpKnowledgeRetriever
{
    private readonly HelpCorpus _corpus;
    private readonly Dictionary<string, double> _idf;
    private readonly double _averageLength;

    public FileHelpKnowledgeRetriever(
        IOptions<ExternalHelpOptions> options,
        ILogger<FileHelpKnowledgeRetriever> log)
        : this(LoadCorpus(options.Value, RunningRevision, log))
    {
    }

    public FileHelpKnowledgeRetriever(HelpCorpus corpus)
    {
        _corpus = corpus;
        var docs = corpus.Documents;
        _averageLength = docs.Count == 0 ? 1 : docs.Average(d => (double)Tokenize(d.Text).Count);
        _idf = BuildIdf(docs);
    }

    /// The revision this process was built from. Same environment variable the
    /// deploy gates read, so "help knowledge revision == running revision" is a
    /// comparison against the SAME provenance the rest of the system uses.
    public static string RunningRevision
        => Environment.GetEnvironmentVariable("NUBARCA_GIT_SHA") ?? string.Empty;

    public string? Revision => string.IsNullOrEmpty(_corpus.Revision) ? null : _corpus.Revision;

    public bool IsAvailable => _corpus.Documents.Count > 0;

    public IReadOnlyList<HelpExcerpt> Retrieve(string question, int maxExcerpts, int maxCharacters)
    {
        if (!IsAvailable || maxExcerpts <= 0 || maxCharacters <= 0)
        {
            return Array.Empty<HelpExcerpt>();
        }
        var terms = Tokenize(question);
        if (terms.Count == 0) return Array.Empty<HelpExcerpt>();

        var scored = _corpus.Documents
            .Select(d => (Doc: d, Score: Bm25(d, terms)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Doc.Id, StringComparer.Ordinal)
            .Take(maxExcerpts)
            .ToList();

        var excerpts = new List<HelpExcerpt>();
        var budget = maxCharacters;
        foreach (var (doc, _) in scored)
        {
            if (budget <= 0) break;
            var text = doc.Text.Length <= budget ? doc.Text : doc.Text[..budget];
            budget -= text.Length;
            excerpts.Add(new HelpExcerpt(doc.Id, doc.Title, doc.Path, text));
        }
        return excerpts;
    }

    private static HelpCorpus LoadCorpus(
        ExternalHelpOptions options, string runningRevision, ILogger log)
    {
        var path = options.CorpusPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            log.LogInformation("external help: no knowledge corpus at the configured path");
            return HelpCorpus.Empty;
        }
        HelpCorpus? corpus;
        try
        {
            corpus = JsonSerializer.Deserialize<HelpCorpus>(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            log.LogWarning("external help: knowledge corpus could not be read");
            return HelpCorpus.Empty;
        }
        if (corpus is null || corpus.Documents.Count == 0) return HelpCorpus.Empty;

        // An unknown running revision (a dev run outside the image) cannot be
        // compared, so the corpus is accepted; a KNOWN one that disagrees is
        // refused. Failing closed here is the point of the gate.
        if (!string.IsNullOrEmpty(runningRevision)
            && !string.Equals(corpus.Revision, runningRevision, StringComparison.Ordinal))
        {
            log.LogWarning(
                "external help: knowledge corpus revision does not match the running build; help knowledge disabled");
            return HelpCorpus.Empty;
        }
        return corpus;
    }

    // ---- small BM25 -------------------------------------------------------

    private const double K1 = 1.2;
    private const double B = 0.75;

    private double Bm25(HelpCorpusDocument doc, IReadOnlyList<string> terms)
    {
        var tokens = Tokenize(doc.Text);
        if (tokens.Count == 0) return 0;
        var frequencies = tokens.GroupBy(t => t).ToDictionary(g => g.Key, g => g.Count());
        double score = 0;
        foreach (var term in terms.Distinct())
        {
            if (!frequencies.TryGetValue(term, out var tf)) continue;
            if (!_idf.TryGetValue(term, out var idf)) continue;
            var norm = tf * (K1 + 1)
                / (tf + K1 * (1 - B + B * tokens.Count / _averageLength));
            score += idf * norm;
        }
        return score;
    }

    private static Dictionary<string, double> BuildIdf(IReadOnlyList<HelpCorpusDocument> docs)
    {
        var containing = new Dictionary<string, int>();
        foreach (var doc in docs)
        {
            foreach (var term in Tokenize(doc.Text).Distinct())
            {
                containing[term] = containing.GetValueOrDefault(term) + 1;
            }
        }
        var n = docs.Count;
        return containing.ToDictionary(
            kv => kv.Key,
            kv => Math.Log(1 + (n - kv.Value + 0.5) / (kv.Value + 0.5)));
    }

    private static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch)) current.Append(char.ToLowerInvariant(ch));
            else if (current.Length > 0) { Flush(tokens, current); }
        }
        if (current.Length > 0) Flush(tokens, current);
        return tokens;

        static void Flush(List<string> into, System.Text.StringBuilder buffer)
        {
            if (buffer.Length > 1) into.Add(buffer.ToString());
            buffer.Clear();
        }
    }
}
