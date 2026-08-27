using System.Text;
using NubArca.Api.Rag.Text;

namespace NubArca.Api.Rag.Retrieval;

/// One lexical candidate: the chunk, its score, and the evidence about WHY it
/// scored — which the fusion layer and the evidence gate both read.
public sealed record RagLexicalHit(
    RagIndexedChunk Chunk,
    double Score,
    int MatchedAny,
    int MatchedLiteral,
    bool MatchedHighField,
    int Rank);

/// Field-weighted BM25 (BM25F) over one domain's corpus.
///
/// This is Slice 1's Product Help retriever, generalized: the scoring is
/// unchanged and the domain-specific judgement moved out into
/// RagRankingProfile. Semantic retrieval is added BESIDE it, never instead of
/// it — an exact identifier, a configuration key or a file name is a permanent
/// use case that vectors are worse at, and deleting the lexical path once
/// embeddings work would be trading a capability for a smaller diff.
///
/// What lexical retrieval had to stop doing, and must not start again:
///
///  - matching Italian `come` against English "come";
///  - scoring only body text, so the longest document won;
///  - treating `Score > 0` as evidence, which meant one accidental shared word
///    bought an outbound provider call and an improvised answer;
///  - truncating a chunk from character zero, so the sentence that matched was
///    frequently not in the excerpt.
///
/// The index is IMMUTABLE once built, so one instance serves every query for a
/// snapshot without a lock and without rebuilding per request.
public sealed class RagLexicalIndex
{
    private readonly RagRankingProfile _profile;
    private readonly IReadOnlyList<Indexed> _documents;
    private readonly Dictionary<string, double> _idf;
    private readonly double _averageBodyLength;
    private readonly Dictionary<Guid, RagIndexedChunk> _byChunkId;

    public RagLexicalIndex(RagCorpus corpus, RagRankingProfile profile)
    {
        Corpus = corpus;
        _profile = profile;
        _documents = corpus.Chunks.Select(Index).ToList();
        _averageBodyLength = _documents.Count == 0
            ? 1
            : Math.Max(1, _documents.Average(d => (double)d.BodyLength));
        _idf = BuildIdf(_documents);
        // The vector path returns chunk ids; evidence needs text, path, section
        // and the domain metadata. Resolving that here rather than with a second
        // database read keeps one query on the interactive path.
        _byChunkId = corpus.Chunks
            .Where(c => c.ChunkId != Guid.Empty)
            .GroupBy(c => c.ChunkId)
            .ToDictionary(g => g.Key, g => g.First());
    }

    /// Resolve a chunk the vector index found. False for a chunk that is not in
    /// this domain's corpus — which is how a vector row that outlived its
    /// membership fails to become evidence.
    public bool TryGetByChunkId(Guid chunkId, out RagIndexedChunk chunk)
        => _byChunkId.TryGetValue(chunkId, out chunk!);

    public RagCorpus Corpus { get; }

    public RagDomainKey Domain => Corpus.Domain;

    public int ChunkCount => _documents.Count;

    public bool IsEmpty => _documents.Count == 0;

    /// Ranked candidates that cleared the evidence gate, best first.
    ///
    /// `take` bounds the CANDIDATE set, not the evidence: fusion sees more than
    /// it returns, so a chunk that lexical ranked eighth can still become the
    /// answer when the vector path agrees with it.
    public IReadOnlyList<RagLexicalHit> Search(RagQueryShape shape, int take)
    {
        if (_documents.Count == 0 || take <= 0 || shape.Literal.Count == 0)
        {
            return Array.Empty<RagLexicalHit>();
        }

        var scored = new List<(Indexed Doc, double Score, int Any, int Literal, bool High)>();
        foreach (var doc in _documents)
        {
            var assessment = Score(doc, shape);
            if (assessment.Score <= 0) continue;
            scored.Add((doc, assessment.Score, assessment.MatchedAny,
                assessment.MatchedLiteral, assessment.HighField));
        }
        if (scored.Count == 0) return Array.Empty<RagLexicalHit>();

        // At least a share of what the person typed has to be found, and a
        // single body-only hit on one common word is never enough on its own.
        var required = shape.Literal.Count <= 1
            ? 1
            : Math.Clamp((int)Math.Ceiling(shape.Literal.Count * 0.4), 1, 3);

        var accepted = scored
            .Where(x => x.Score >= _profile.MinimumScore
                        && x.Any >= required
                        && (x.High || x.Literal >= 2))
            .ToList();
        if (accepted.Count == 0) return Array.Empty<RagLexicalHit>();

        var best = accepted.Max(x => x.Score);
        // Ordinal id as the tie-break: two chunks that score identically must
        // come back in the same order on every machine, or a golden test is
        // testing the sort's mood.
        return accepted
            .Where(x => x.Score >= best * _profile.RelativeFloor)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Doc.Chunk.Id, StringComparer.Ordinal)
            .Take(take)
            .Select((x, i) => new RagLexicalHit(x.Doc.Chunk, x.Score, x.Any, x.Literal, x.High, i + 1))
            .ToList();
    }

    // ---- scoring ----------------------------------------------------------

    private (double Score, int MatchedAny, int MatchedLiteral, bool HighField) Score(
        Indexed doc, RagQueryShape shape)
    {
        double score = 0;
        var matchedAny = 0;
        var matchedLiteral = 0;
        var highField = false;

        foreach (var term in shape.Literal)
        {
            var (contribution, hit, high) = TermScore(doc, term, 1.0);
            score += contribution;
            if (!hit) continue;
            matchedAny++;
            matchedLiteral++;
            highField |= high;
        }
        foreach (var term in shape.Expanded)
        {
            var (contribution, hit, high) = TermScore(doc, term, _profile.ExpandedTermWeight);
            score += contribution;
            if (!hit) continue;
            matchedAny++;
            highField |= high;
        }
        if (score <= 0) return (0, 0, 0, false);

        score *= _profile.Boost(doc.Chunk, shape);
        return (score, matchedAny, matchedLiteral, highField);
    }

    /// BM25F: weighted term frequencies are summed ACROSS fields first, then
    /// saturated once. Saturating per field and adding would let a term repeated
    /// in a title beat a term present in every field, which is backwards.
    private (double Score, bool Hit, bool HighField) TermScore(Indexed doc, string term, double weight)
    {
        if (!_idf.TryGetValue(term, out var idf)) return (0, false, false);

        var feature = doc.Feature.GetValueOrDefault(term);
        var section = doc.Section.GetValueOrDefault(term);
        var title = doc.Title.GetValueOrDefault(term);
        var body = doc.Body.GetValueOrDefault(term);
        if (feature + section + title + body == 0) return (0, false, false);

        var normalization = 1 - RagRankingProfile.B
                            + RagRankingProfile.B * doc.BodyLength / _averageBodyLength;
        var tf = _profile.FeatureWeight * feature
                 + _profile.SectionWeight * section
                 + _profile.TitleWeight * title
                 + _profile.BodyWeight * body / normalization;

        var saturated = tf * (RagRankingProfile.K1 + 1) / (tf + RagRankingProfile.K1);
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
    public static string CenterOnMatch(string text, IReadOnlyList<string> terms, int budget)
    {
        if (budget <= 0) return string.Empty;
        if (text.Length <= budget) return text;

        // Folded the SAME way the query terms were. `ToLowerInvariant()` alone
        // lowercases `perché` to `perché`, and the term it is being compared
        // against is `perche` — so an Italian question about anything accented
        // silently fell back to cutting from character zero, which is the exact
        // failure match-centering exists to prevent.
        var haystack = RagText.FoldForSearch(text);
        var match = -1;
        foreach (var term in terms)
        {
            var at = haystack.IndexOf(term, StringComparison.Ordinal);
            if (at >= 0 && (match < 0 || at < match)) match = at;
        }
        // Folding preserves offsets, so a position found in `haystack` is the
        // same position in `text` and the excerpt is cut from the ORIGINAL —
        // accents and all.
        if (match < 0 || haystack.Length != text.Length) return Trim(text, 0, budget);

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
        if (end <= start)
        {
            start = Math.Max(0, Math.Min(start, text.Length - 1));
            end = Math.Min(text.Length, start + budget);
        }

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
        RagIndexedChunk Chunk,
        Dictionary<string, int> Feature,
        Dictionary<string, int> Section,
        Dictionary<string, int> Title,
        Dictionary<string, int> Body,
        int BodyLength,
        IReadOnlyCollection<string> AllTerms);

    private static Indexed Index(RagIndexedChunk chunk)
    {
        // Feature and aliases share one field: they are the same claim about
        // what the document is about, written twice. For a repository source the
        // provider puts path segments and declared symbols here, which is the
        // same claim in that domain's vocabulary.
        var featureText = string.Join(' ', new[] { chunk.Feature }.Concat(chunk.Aliases));
        var feature = Count(RagText.ContentTokens(featureText));
        var section = Count(RagText.ContentTokens(chunk.Section));
        var title = Count(RagText.ContentTokens(chunk.Title));
        var bodyTokens = RagText.ContentTokens(chunk.Text);
        var body = Count(bodyTokens);

        var all = new HashSet<string>(StringComparer.Ordinal);
        all.UnionWith(feature.Keys);
        all.UnionWith(section.Keys);
        all.UnionWith(title.Keys);
        all.UnionWith(body.Keys);

        return new Indexed(chunk, feature, section, title, body, Math.Max(1, bodyTokens.Count), all);
    }

    private static Dictionary<string, int> Count(IEnumerable<string> tokens)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var token in tokens) counts[token] = counts.GetValueOrDefault(token) + 1;
        return counts;
    }
}
