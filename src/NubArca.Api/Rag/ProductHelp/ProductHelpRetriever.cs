using System.Text;

namespace NubArca.Api.Rag.ProductHelp;

/// Lexical retrieval over the approved public product corpus.
///
/// Deliberately no vector database and no cloud embeddings in this slice: this
/// has to explain a product from a few hundred chunks, and sending text to an
/// embedding service to decide what to send to a chat service would widen
/// exactly the boundary the feature exists to keep narrow. Semantic retrieval is
/// the next slice, and it lands BEHIND IRagRetriever rather than beside it.
///
/// What lexical retrieval had to stop doing:
///
///  - matching Italian `come` against English "come";
///  - scoring only body text, so the longest document won;
///  - treating `Score > 0` as evidence, which meant one accidental shared word
///    bought an outbound provider call and an improvised answer;
///  - truncating a 4,000-character chunk from character zero, so the sentence
///    that matched was frequently not in the excerpt.
public sealed class ProductHelpRetriever : IRagRetriever
{
    // ---- field weights ----------------------------------------------------
    //
    // Metadata over prose. A document's feature name and aliases say what it is
    // ABOUT; its body says everything it happens to mention. For "how do I use
    // faces?", the first is the question and the second is why an operations
    // runbook used to win.
    private const double FeatureWeight = 3.0;
    private const double SectionWeight = 2.5;
    private const double TitleWeight = 2.0;
    private const double BodyWeight = 1.0;

    /// An alias-expanded term is a guess about what the person meant, so it
    /// contributes less than a word they actually typed.
    private const double ExpandedTermWeight = 0.45;

    private const double K1 = 1.2;
    private const double B = 0.75;

    // ---- the evidence gate ------------------------------------------------
    //
    // `Score > 0` is not evidence. Below these, the honest answer is that the
    // documentation does not cover the question — which costs nothing, where
    // sending a third party the question plus three irrelevant paragraphs and
    // asking it to improvise costs a boundary crossing and produces the answer
    // most likely to be wrong.
    private const double MinimumScore = 0.35;

    /// Everything below this share of the best hit is noise trailing the answer.
    private const double RelativeFloor = 0.25;

    // ---- intent shaping ---------------------------------------------------
    private const double HowToIntentBoost = 1.4;
    private const double UserGuideBoost = 1.3;
    private const double TechnicalReferencePenalty = 0.6;
    private const double TechnicalAudiencePenalty = 0.8;

    private readonly ProductHelpCorpus _corpus;
    private readonly IReadOnlyList<Indexed> _documents;
    private readonly Dictionary<string, double> _idf;
    private readonly double _averageBodyLength;

    public ProductHelpRetriever(ProductHelpCorpus corpus)
    {
        _corpus = corpus;
        _documents = corpus.Documents.Select(Index).ToList();
        _averageBodyLength = _documents.Count == 0
            ? 1
            : Math.Max(1, _documents.Average(d => (double)d.BodyLength));
        _idf = BuildIdf(_documents);
    }

    public RagDomainKey Domain => RagDomainKey.ProductHelp;

    public bool IsAvailable => _documents.Count > 0;

    public string? Revision => string.IsNullOrEmpty(_corpus.Revision) ? null : _corpus.Revision;

    public RagResult Retrieve(RagQuery query)
    {
        // A caller asking a domain for another domain's knowledge is a bug, not
        // a fallback: answer nothing rather than quietly serving product-help
        // to something that thinks it is reading a private index.
        if (query.Domain != Domain) return RagResult.Unavailable;
        if (!IsAvailable) return RagResult.Unavailable;
        if (query.MaxEvidence <= 0 || query.MaxCharacters <= 0) return RagResult.None;

        var content = ProductHelpText.ContentTokens(query.Text);
        if (content.Count == 0) return RagResult.None;

        var (literal, expanded) = ProductHelpAliases.Expand(content);
        var howTo = ProductHelpText.LooksLikeHowTo(query.Text);

        var scored = new List<(Indexed Doc, double Score, int MatchedAny, int MatchedLiteral, bool HighField)>();
        foreach (var doc in _documents)
        {
            var assessment = Score(doc, literal, expanded, howTo);
            if (assessment.Score <= 0) continue;
            scored.Add((doc, assessment.Score, assessment.MatchedAny,
                assessment.MatchedLiteral, assessment.HighField));
        }
        if (scored.Count == 0) return RagResult.None;

        // At least a third of what the person typed has to be found, and a
        // single body-only hit on one common word is never enough on its own.
        var required = literal.Count <= 1
            ? 1
            : Math.Clamp((int)Math.Ceiling(literal.Count * 0.4), 1, 3);

        var accepted = scored
            .Where(x => x.Score >= MinimumScore
                        && x.MatchedAny >= required
                        && (x.HighField || x.MatchedLiteral >= 2))
            .ToList();
        if (accepted.Count == 0) return RagResult.None;

        var best = accepted.Max(x => x.Score);
        // Ordinal id as the tie-break: two chunks that score identically must
        // come back in the same order on every machine, or a golden test is
        // testing the sort's mood.
        var ranked = accepted
            .Where(x => x.Score >= best * RelativeFloor)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Doc.Document.Id, StringComparer.Ordinal)
            .Take(query.MaxEvidence)
            .ToList();

        var evidence = new List<RagEvidence>();
        var budget = query.MaxCharacters;
        var terms = literal.Concat(expanded).ToList();
        foreach (var (doc, score, _, _, _) in ranked)
        {
            if (budget <= 0) break;
            var text = doc.Document.Text.Length <= budget
                ? doc.Document.Text
                : CenterOnMatch(doc.Document.Text, terms, budget);
            if (text.Length == 0) break;
            budget -= text.Length;
            evidence.Add(new RagEvidence(
                Id: doc.Document.Id,
                Domain: Domain,
                Path: doc.Document.Path,
                Title: doc.Document.Title,
                Section: doc.Document.Section,
                Text: text,
                Feature: doc.Document.Feature,
                SourceKind: doc.Document.SourceKind,
                Audience: doc.Document.Audience,
                Intent: doc.Document.Intent,
                Language: doc.Document.Language,
                Score: score));
        }

        return evidence.Count == 0
            ? RagResult.None
            : new RagResult(RagRetrievalOutcome.Strong, evidence);
    }

    // ---- scoring ----------------------------------------------------------

    private (double Score, int MatchedAny, int MatchedLiteral, bool HighField) Score(
        Indexed doc, IReadOnlyList<string> literal, IReadOnlyList<string> expanded, bool howTo)
    {
        double score = 0;
        var matchedAny = 0;
        var matchedLiteral = 0;
        var highField = false;

        foreach (var term in literal)
        {
            var (contribution, hit, high) = TermScore(doc, term, 1.0);
            score += contribution;
            if (!hit) continue;
            matchedAny++;
            matchedLiteral++;
            highField |= high;
        }
        foreach (var term in expanded)
        {
            var (contribution, hit, high) = TermScore(doc, term, ExpandedTermWeight);
            score += contribution;
            if (!hit) continue;
            matchedAny++;
            highField |= high;
        }
        if (score <= 0) return (0, 0, 0, false);

        // The manifest's editorial judgement, as a multiplier rather than a
        // replacement: a high-priority source still has to match.
        score *= 0.5 + doc.Document.Priority / 100.0;

        if (howTo)
        {
            if (doc.Document.Intent == ProductHelpVocabulary.Intent.HowTo) score *= HowToIntentBoost;
            if (doc.Document.SourceKind == ProductHelpVocabulary.SourceKind.UserGuide)
            {
                score *= UserGuideBoost;
            }
            if (doc.Document.SourceKind == ProductHelpVocabulary.SourceKind.TechnicalReference)
            {
                score *= TechnicalReferencePenalty;
            }
            if (doc.Document.Audience == ProductHelpVocabulary.Audience.Technical)
            {
                score *= TechnicalAudiencePenalty;
            }
        }
        return (score, matchedAny, matchedLiteral, highField);
    }

    /// BM25F: weighted term frequencies are summed ACROSS fields first, then
    /// saturated once. Saturating per field and adding would let a term repeated
    /// in a title beat a term present in every field, which is backwards.
    private (double Score, bool Hit, bool HighField) TermScore(
        Indexed doc, string term, double weight)
    {
        if (!_idf.TryGetValue(term, out var idf)) return (0, false, false);

        var feature = doc.Feature.GetValueOrDefault(term);
        var section = doc.Section.GetValueOrDefault(term);
        var title = doc.Title.GetValueOrDefault(term);
        var body = doc.Body.GetValueOrDefault(term);
        if (feature + section + title + body == 0) return (0, false, false);

        var normalization = 1 - B + B * doc.BodyLength / _averageBodyLength;
        var tf = FeatureWeight * feature
                 + SectionWeight * section
                 + TitleWeight * title
                 + BodyWeight * body / normalization;

        var saturated = tf * (K1 + 1) / (tf + K1);
        return (idf * saturated * weight, true, feature + section + title > 0);
    }

    private static Dictionary<string, double> BuildIdf(IReadOnlyList<Indexed> documents)
    {
        var containing = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var doc in documents)
        {
            foreach (var term in doc.AllTerms)
            {
                containing[term] = containing.GetValueOrDefault(term) + 1;
            }
        }
        var n = documents.Count;
        return containing.ToDictionary(
            kv => kv.Key,
            kv => Math.Max(0.01, Math.Log(1 + (n - kv.Value + 0.5) / (kv.Value + 0.5))),
            StringComparer.Ordinal);
    }

    // ---- excerpting -------------------------------------------------------

    /// Keep the region that MATCHED, plus enough around it to be understandable.
    ///
    /// The predecessor cut from character zero, so a question answered in the
    /// last paragraph of a chunk arrived at the model as three paragraphs about
    /// something else. Boundaries are moved to whitespace so the excerpt does
    /// not begin or end mid-word.
    private static string CenterOnMatch(string text, IReadOnlyList<string> terms, int budget)
    {
        if (budget <= 0) return string.Empty;
        if (text.Length <= budget) return text;

        var haystack = text.ToLowerInvariant();
        var match = -1;
        foreach (var term in terms)
        {
            var at = haystack.IndexOf(term, StringComparison.Ordinal);
            if (at >= 0 && (match < 0 || at < match)) match = at;
        }
        if (match < 0) return Trim(text, 0, budget);

        // A third of the window before the match, so the sentence it sits in has
        // its beginning.
        var start = Math.Max(0, match - budget / 3);
        if (start + budget > text.Length) start = Math.Max(0, text.Length - budget);
        return Trim(text, start, budget);
    }

    private static string Trim(string text, int start, int budget)
    {
        var end = Math.Min(text.Length, start + budget);
        // Move to word boundaries, but never so far that nothing is left.
        while (start > 0 && start < end && !char.IsWhiteSpace(text[start - 1])) start++;
        while (end > start && end < text.Length && !char.IsWhiteSpace(text[end])) end--;
        if (end <= start) { start = Math.Max(0, Math.Min(start, text.Length - 1)); end = Math.Min(text.Length, start + budget); }

        var slice = text[start..end].Trim();
        if (slice.Length == 0) return string.Empty;
        var builder = new StringBuilder();
        if (start > 0) builder.Append("… ");
        builder.Append(slice);
        if (end < text.Length) builder.Append(" …");
        var result = builder.ToString();
        return result.Length <= budget ? result : result[..budget];
    }

    // ---- the index --------------------------------------------------------

    private sealed record Indexed(
        ProductHelpDocument Document,
        Dictionary<string, int> Feature,
        Dictionary<string, int> Section,
        Dictionary<string, int> Title,
        Dictionary<string, int> Body,
        int BodyLength,
        IReadOnlyCollection<string> AllTerms);

    private static Indexed Index(ProductHelpDocument document)
    {
        // Feature and aliases share one field: they are the same claim about
        // what the document is about, written twice.
        var featureText = string.Join(' ', new[] { document.Feature }.Concat(document.Aliases));
        var feature = Count(ProductHelpText.ContentTokens(featureText));
        var section = Count(ProductHelpText.ContentTokens(document.Section));
        var title = Count(ProductHelpText.ContentTokens(document.Title));
        var bodyTokens = ProductHelpText.ContentTokens(document.Text);
        var body = Count(bodyTokens);

        var all = new HashSet<string>(StringComparer.Ordinal);
        all.UnionWith(feature.Keys);
        all.UnionWith(section.Keys);
        all.UnionWith(title.Keys);
        all.UnionWith(body.Keys);

        return new Indexed(document, feature, section, title, body, Math.Max(1, bodyTokens.Count), all);
    }

    private static Dictionary<string, int> Count(IEnumerable<string> tokens)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var token in tokens) counts[token] = counts.GetValueOrDefault(token) + 1;
        return counts;
    }
}
