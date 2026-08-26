namespace NubArca.Api.Rag.ProductHelp;

/// The product feature catalogue: the words people actually use for a feature,
/// in both interface languages, grouped by the concept they name.
///
/// Retrieval needs this because the interface is Italian and much of the
/// documentation is English. "come faccio a utilizzare la funzione dei volti?"
/// shares almost no tokens with a paragraph about assigning a name to a
/// suggested face group — the concepts are identical and the strings are not.
///
/// Expansion is DETERMINISTIC, LOCAL and BOUNDED: a fixed table, no model, no
/// network, and a hard cap on how many terms one question can become. It is
/// also weaker than a literal hit — an expanded term contributes at a discount
/// (see ProductHelpRetriever), so a document that uses the person's own words
/// still wins.
public static class ProductHelpAliases
{
    /// Each group is one concept. Every token in a group expands to every other
    /// token in it.
    private static readonly string[][] Concepts =
    {
        // Faces / People — the first high-value feature, and the query that
        // motivated the catalogue.
        new[]
        {
            "volto", "volti", "faccia", "facce", "facciale", "riconoscimento",
            "persona", "persone", "face", "faces", "facial", "recognition",
            "people", "person", "cluster", "clusters", "gruppo", "gruppi",
        },
        new[] { "album", "albums", "raccolta", "raccolte", "collezione", "collection", "collections" },
        new[] { "ricerca", "ricerche", "cercare", "cerca", "search", "searching", "query" },
        new[] { "caricamento", "caricare", "carica", "upload", "uploads", "uploading", "import", "importazione" },
        new[] { "condivisione", "condividere", "condividi", "share", "shares", "sharing", "shared" },
        new[] { "video", "filmato", "filmati", "movie", "movies", "clip" },
        new[] { "foto", "fotografia", "fotografie", "immagine", "immagini", "photo", "photos", "picture", "pictures", "image", "images" },
        new[] { "cassaforte", "vault", "privato", "private" },
        new[] { "miniatura", "miniature", "anteprima", "anteprime", "thumbnail", "thumbnails", "preview", "previews", "poster" },
        new[] { "televisore", "television", "telecomando", "remote" },
    };

    /// A question expands to at most this many terms. A person cannot make
    /// retrieval expensive by typing a paragraph of feature nouns.
    public const int MaxExpandedTerms = 48;

    private static readonly Dictionary<string, string[]> ByToken = Build();

    private static Dictionary<string, string[]> Build()
    {
        var map = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var concept in Concepts)
        {
            foreach (var token in concept)
            {
                // A token in two concepts keeps the FIRST — the table is
                // ordered by how specific the concept is, and a silent merge
                // would quietly widen both.
                if (!map.ContainsKey(token)) map[token] = concept;
            }
        }
        return map;
    }

    /// The literal content terms, plus the concept terms they imply.
    ///
    /// Returned as two sets rather than one, because they are not worth the
    /// same: the caller scores `Literal` at full weight and `Expanded` at a
    /// discount.
    public static (IReadOnlyList<string> Literal, IReadOnlyList<string> Expanded) Expand(
        IReadOnlyList<string> contentTokens)
    {
        var literal = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in contentTokens)
        {
            if (seen.Add(token)) literal.Add(token);
        }

        var expanded = new List<string>();
        foreach (var token in literal)
        {
            if (!ByToken.TryGetValue(token, out var concept)) continue;
            foreach (var related in concept)
            {
                if (seen.Add(related)) expanded.Add(related);
                if (literal.Count + expanded.Count >= MaxExpandedTerms) return (literal, expanded);
            }
        }
        return (literal, expanded);
    }
}
