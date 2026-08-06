using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace NubArca.Api.Ai.NaturalGallery;

// Built-in LOCAL grammar interpreter for IT + EN gallery commands. It is fully
// deterministic, warm, bounded, needs no weights, and doubles as the dev/test +
// offline default and the fallback for the ONNX decoder sidecar. It produces a
// RAW draft (person text spans, normalised dates, semantic-vs-metadata split,
// operation); the server then validates + resolves. It NEVER emits ids/SQL/URLs.
//
// The grammar is intentionally conservative: recognised structural tokens
// (operation verbs, favourites/rating/GPS/sort/duplicate keywords, dates, person
// name spans, metadata markers) are extracted and stripped; the descriptive
// leftover becomes the visual semantic query. Anything it cannot classify stays
// in the semantic residual rather than being dropped.
public sealed class DeterministicGalleryCommandInterpreter : INaturalGalleryCommandInterpreter
{
    public string Key => "deterministic";

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

    public Task<RawGalleryCommand> InterpretAsync(
        GalleryCommandContext context, CancellationToken cancellationToken = default)
        => Task.FromResult(Interpret(context));

    public static RawGalleryCommand Interpret(GalleryCommandContext context)
    {
        var draft = new RawGalleryCommand();
        var original = context.Command.Trim();
        var work = original; // progressively stripped working copy

        // 1. Operation ---------------------------------------------------------
        if (IsClear(work))
        {
            draft.Operation = GalleryCommandOperations.Clear;
            return draft; // clear-all is deterministic; ignore everything else
        }
        draft.Operation = IsRefine(work) ? GalleryCommandOperations.Refine : GalleryCommandOperations.Replace;

        // 2. Dates (strip matched spans so they don't leak into the residual) --
        var date = GalleryDateResolver.Resolve(work, context);
        if (date.HasMatch)
        {
            draft.DateTakenFrom = date.From;
            draft.DateTakenTo = date.To;
            foreach (var span in date.MatchedSpans) work = RemoveSpan(work, span);
            draft.Warnings.AddRange(date.Warnings);
        }

        // 3. Metadata search markers (title / filename / tag) ------------------
        var metadata = ExtractMetadata(ref work);
        if (metadata is not null) draft.MetadataSearch = metadata;

        // 4. Boolean / scalar filters -----------------------------------------
        // GPS: removal (refine) beats set; "senza" beats "con".
        if (StripAny(ref work, GpsRemove)) draft.RemoveHasGps = true; // explicit remove in refine
        else if (StripAny(ref work, GpsFalse)) draft.HasGps = false;
        else if (StripAny(ref work, GpsTrue)) draft.HasGps = true;

        if (StripAny(ref work, FavoriteWords)) draft.Favorite = true;

        if (StripAny(ref work, CollapseWords)) draft.CollapseDuplicates = true;

        var rating = ExtractRating(ref work);
        if (rating is int r) draft.MinRating = r;

        // 5. Sort --------------------------------------------------------------
        ExtractSort(ref work, draft);

        // 6. People ------------------------------------------------------------
        ExtractPeople(original, draft);
        // Strip person spans from the residual too.
        foreach (var term in draft.People) work = RemoveWord(work, term.Text);

        // 7. Semantic residual -------------------------------------------------
        var residual = CleanResidual(work, context);
        if (residual.Length > 0) draft.SemanticQuery = residual;

        return draft;
    }

    // ---- operation ----------------------------------------------------------

    private static bool IsClear(string t) => Regex.IsMatch(t,
        @"\b(azzera(\s+(tutti\s+i|i|il|le)?\s*filtr\w*)?|cancella\s+(tutti\s+)?i?\s*filtr\w*|rimuovi\s+tutti\s+i\s+filtr\w*|reset(\s+(all\s+)?filters?)?|clear\s+(all|the\s+filters?|filters?))\b",
        RegexOptions.IgnoreCase);

    private static bool IsRefine(string t) => Regex.IsMatch(t,
        @"\b(aggiungi|anche|togli|rimuovi|escludi|also|add|remove|as\s+well|inoltre)\b",
        RegexOptions.IgnoreCase);

    // ---- metadata -----------------------------------------------------------

    private static string? ExtractMetadata(ref string work)
    {
        // "titolo X" / "con titolo X" / "title X" / "nome file X" / "file X" / "tag X"
        var m = Regex.Match(work,
            @"\b(?:con\s+)?(?:titolo|title|nome\s+del\s+file|nome\s+file|filename|file|tag|etichett\w*|descrizione|description)\s+[""']?([\p{L}\p{N}_\-\.]+)[""']?",
            RegexOptions.IgnoreCase);
        if (m.Success)
        {
            work = RemoveSpan(work, m.Value);
            return m.Groups[1].Value;
        }
        // Bare filename-like token (IMG_2024, DSC0001, foo.jpg).
        var f = Regex.Match(work, @"\b([A-Za-z]{2,}[_\-]?\d{2,}[\w\-]*|\w+\.(?:jpe?g|png|heic|mp4|mov))\b");
        if (f.Success)
        {
            work = RemoveSpan(work, f.Value);
            return f.Groups[1].Value;
        }
        return null;
    }

    // ---- rating -------------------------------------------------------------

    private static int? ExtractRating(ref string work)
    {
        var m = Regex.Match(work,
            @"\b(?:almeno\s+|at\s+least\s+|rating\s+|valutazione\s+|con\s+)?(\d)\s*(?:stell\w*|star\w*)\b",
            RegexOptions.IgnoreCase);
        if (!m.Success)
        {
            m = Regex.Match(work, @"\b(?:rating|valutazione)\s+(\d)\b", RegexOptions.IgnoreCase);
            if (!m.Success) return null;
        }
        work = RemoveSpan(work, m.Value);
        var value = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        return Math.Clamp(value, 0, 5);
    }

    // ---- sort ---------------------------------------------------------------

    private static void ExtractSort(ref string work, RawGalleryCommand draft)
    {
        foreach (var (pattern, field, dir) in SortRules)
        {
            var m = Regex.Match(work, pattern, RegexOptions.IgnoreCase);
            if (m.Success)
            {
                draft.Sort = field;
                draft.SortDirection = dir;
                work = RemoveSpan(work, m.Value);
                return;
            }
        }
    }

    // ---- people -------------------------------------------------------------

    private static void ExtractPeople(string original, RawGalleryCommand draft)
    {
        // Walk tokens; a capitalized token (not a keyword/month/filler) starts a
        // name span, extended over consecutive capitalized non-keyword tokens.
        // Exclusion markers (senza/without/tranne/eccetto/non/no) flip the next
        // span(s) to exclude; "o/oppure/or" sets peopleMatch = any; "e/and/,/
        // insieme/together" keeps all. "né X né Y" excludes both (with a warning).
        var tokens = Tokenize(original);
        var mode = PeopleTermModes.Include;
        var neither = false;
        var sawOr = false;
        var names = new List<RawPersonTerm>();

        for (var i = 0; i < tokens.Count; i++)
        {
            var tok = tokens[i];
            var lower = PersonNameResolver.Normalize(tok.Text);

            if (RemoveMarkers.Contains(lower)) { mode = PeopleTermModes.Remove; continue; }
            if (ExcludeMarkers.Contains(lower)) { mode = PeopleTermModes.Exclude; continue; }
            if (NeitherMarkers.Contains(lower)) { neither = true; mode = PeopleTermModes.Exclude; continue; }
            if (OrMarkers.Contains(lower)) { sawOr = true; continue; }
            if (AndMarkers.Contains(lower)) { continue; }

            if (IsNameToken(tok, i))
            {
                // Extend across consecutive capitalized name tokens.
                var sb = new StringBuilder(tok.Text);
                while (i + 1 < tokens.Count && IsNameToken(tokens[i + 1], i + 1)
                       && !ExcludeMarkers.Contains(PersonNameResolver.Normalize(tokens[i + 1].Text)))
                {
                    sb.Append(' ').Append(tokens[i + 1].Text);
                    i++;
                }
                names.Add(new RawPersonTerm(sb.ToString(), mode));
                // A single exclusion marker only applies to the immediate next
                // name unless it was "senza" (which scopes to the rest). Keep the
                // simple rule: exclusion persists until an AND/OR resets include.
            }
        }

        // Dedup by (normalized text, mode); include wins ties.
        foreach (var term in names)
        {
            if (!draft.People.Any(p =>
                PersonNameResolver.Normalize(p.Text) == PersonNameResolver.Normalize(term.Text)
                && p.Mode == term.Mode))
            {
                draft.People.Add(term);
            }
        }

        draft.PeopleMatch = sawOr ? "any" : "all";
        if (neither && draft.People.Count > 0)
        {
            draft.Warnings.Add("neither_interpreted_as_exclude");
        }
    }

    private static bool IsNameToken(Token tok, int index)
    {
        if (!tok.IsCapitalized) return false;
        var norm = PersonNameResolver.Normalize(tok.Text);
        if (norm.Length < 2) return false;
        if (FillerWords.Contains(norm) || StopWords.Contains(norm)) return false;
        if (MonthAndSeason.Contains(norm)) return false;
        if (NonNameWords.Contains(norm)) return false; // command verbs / descriptors
        if (LeadingVerbs.Contains(norm)) return false; // "Mostrami", "Show", "Ordina"…
        if (Prepositions.Contains(norm)) return false;
        return true;
    }

    // ---- residual -----------------------------------------------------------

    private static string CleanResidual(string work, GalleryCommandContext ctx)
    {
        var text = work;
        // Remove leading command verbs / framing phrases.
        foreach (var phrase in ResidualStripPhrases)
        {
            text = Regex.Replace(text, @"\b" + Regex.Escape(phrase) + @"\b", " ", RegexOptions.IgnoreCase);
        }
        // Remove connective residue AND split elided forms (dell', l', un') on the
        // apostrophe so the article fragment ("dell", "l") separates from content.
        text = Regex.Replace(text, @"[^\p{L}\p{N}\s]", " ");
        // Keep only CONTENT tokens: drop structural noise (articles, prepositions,
        // connectors, command verbs, filter markers, elided fragments) and stray
        // single letters. Descriptive words (mare, tramonto, neve, sunset…) survive
        // and become the visual semantic query.
        var tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Where(w =>
            {
                var norm = PersonNameResolver.Normalize(w);
                return norm.Length >= 2 && !ResidualNoise.Contains(norm);
            })
            .ToList();
        if (tokens.Count == 0) return "";

        // Rebuild the residual as a natural phrase from the ORIGINAL work text
        // between the first and last surviving content token (keeps prepositions
        // like "al" that sit between content words: "mare al tramonto").
        var content = new HashSet<string>(tokens.Select(t => PersonNameResolver.Normalize(t)));
        return RebuildPhrase(work, content);
    }

    private static string RebuildPhrase(string work, HashSet<string> content)
    {
        var words = work.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        int first = -1, last = -1;
        for (var i = 0; i < words.Length; i++)
        {
            var clean = PersonNameResolver.Normalize(Regex.Replace(words[i], @"[^\p{L}\p{N}'’]", ""));
            if (content.Contains(clean)) { if (first < 0) first = i; last = i; }
        }
        if (first < 0) return "";
        var span = string.Join(' ', words.Skip(first).Take(last - first + 1));
        span = Regex.Replace(span, @"[^\p{L}\p{N}\s'’]", " ");
        span = Regex.Replace(span, @"\s+", " ").Trim();
        return span;
    }

    // ---- tokenisation -------------------------------------------------------

    private readonly record struct Token(string Text, bool IsCapitalized);

    private static List<Token> Tokenize(string text)
    {
        var matches = Regex.Matches(text, @"[\p{L}][\p{L}'’]*|\d+|[,]");
        var list = new List<Token>(matches.Count);
        foreach (Match m in matches)
        {
            var w = m.Value;
            var cap = w.Length > 0 && char.IsLetter(w[0]) && char.IsUpper(w[0]);
            list.Add(new Token(w, cap));
        }
        return list;
    }

    // ---- string helpers -----------------------------------------------------

    private static bool StripAny(ref string work, IReadOnlyList<string> phrases)
    {
        var hit = false;
        foreach (var p in phrases)
        {
            var pattern = @"\b" + p.Replace(" ", @"\s+") + @"\b";
            if (Regex.IsMatch(work, pattern, RegexOptions.IgnoreCase))
            {
                work = Regex.Replace(work, pattern, " ", RegexOptions.IgnoreCase);
                hit = true;
            }
        }
        return hit;
    }

    private static string RemoveSpan(string work, string span)
        => string.IsNullOrEmpty(span) ? work
            : Regex.Replace(work, Regex.Escape(span), " ", RegexOptions.IgnoreCase);

    private static string RemoveWord(string work, string word)
    {
        foreach (var part in word.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            work = Regex.Replace(work, @"\b" + Regex.Escape(part) + @"\b", " ", RegexOptions.IgnoreCase);
        }
        return work;
    }

    // ---- vocab --------------------------------------------------------------

    private static readonly string[] FavoriteWords =
        { "preferite", "preferiti", "preferita", "preferito", "favorite", "favorites", "favourites", "favourite", "con la stella", "starred", "star" };
    private static readonly string[] GpsTrue =
        { "con gps", "con posizione", "con geolocalizzazione", "geolocalizzate", "geolocalizzata", "con coordinate", "with gps", "with location", "geotagged", "with coordinates", "con la posizione" };
    private static readonly string[] GpsFalse =
        { "senza gps", "senza posizione", "without gps", "without location", "no gps", "senza coordinate" };
    private static readonly string[] GpsRemove =
        { "togli il filtro gps", "rimuovi il filtro gps", "remove the gps filter", "remove gps filter", "togli gps", "leva il filtro gps" };
    private static readonly string[] CollapseWords =
        { "senza duplicati", "nascondi i duplicati", "nascondi duplicati", "collassa i duplicati", "collassa duplicati", "hide duplicates", "collapse duplicates", "no duplicates", "without duplicates" };

    private static readonly (string Pattern, string Field, string Dir)[] SortRules =
    {
        (@"\b(le\s+)?(più|piu)\s+recenti\b|\bmost\s+recent\b|\bnewest\b|\blatest\b", "created", "desc"),
        (@"\b(le\s+)?(più|piu)\s+vecchie\b|\boldest\b", "created", "asc"),
        (@"\bper\s+nome\b|\bby\s+name\b|\balphabetical(ly)?\b", "name", "asc"),
        (@"\bper\s+data\s+di\s+scatto\b|\bby\s+date\s+taken\b|\bper\s+data\b|\bby\s+date\b", "datetaken", "desc"),
        (@"\bper\s+dimensione\b|\bby\s+size\b|\bpiù\s+grandi\b|\blargest\b", "size", "desc"),
    };

    private static readonly HashSet<string> RemoveMarkers = new(StringComparer.Ordinal)
        { "togli", "rimuovi", "remove", "leva" };
    private static readonly HashSet<string> ExcludeMarkers = new(StringComparer.Ordinal)
        { "senza", "without", "tranne", "eccetto", "escludi", "escludendo", "excluding", "except", "no", "non", "not", "meno" };
    private static readonly HashSet<string> NeitherMarkers = new(StringComparer.Ordinal)
        { "ne", "neither", "nor" }; // "né" normalises to "ne"
    private static readonly HashSet<string> OrMarkers = new(StringComparer.Ordinal)
        { "o", "oppure", "or" };
    private static readonly HashSet<string> AndMarkers = new(StringComparer.Ordinal)
        { "e", "ed", "and", "insieme", "together", "con" };

    private static readonly HashSet<string> LeadingVerbs = new(StringComparer.Ordinal)
    {
        "mostrami", "mostra", "fammi", "vedere", "cerca", "cercami", "trova", "trovami", "voglio",
        "show", "find", "search", "get", "give", "display", "aggiungi", "togli", "rimuovi",
        "ordina", "nascondi", "azzera", "cancella", "collassa", "leva", "escludi", "add", "remove",
        "hide", "clear", "reset", "sort", "collapse", "elimina", "delete", "share", "condividi",
        "rimuovere", "mostrare",
    };

    // Common IT/EN command/descriptor/framing words that are NOT person names.
    // Prevents a sentence-initial capitalised word ("Sunset", "Favorite", "File",
    // "GPS", "Photos") from being mis-extracted as a person. Real given names are
    // never in this set. Checked at every position.
    private static readonly HashSet<string> NonNameWords = new(StringComparer.Ordinal)
    {
        "file", "filename", "titolo", "title", "tag", "etichetta", "descrizione", "description",
        "gps", "img", "dsc", "only", "solo", "also", "from", "with", "senza", "without",
        "favorite", "favorites", "favourite", "favourites", "preferite", "preferiti", "preferita", "preferito",
        "photos", "photo", "pictures", "picture", "images", "image", "foto", "immagini", "immagine",
        "gallery", "galleria", "sunset", "sunrise", "beach", "sea", "snow", "mountain", "night", "day",
        "mare", "tramonto", "alba", "neve", "spiaggia", "montagna", "notte", "giorno", "sera", "mattina",
        "holiday", "vacanza", "vacanze", "duplicati", "duplicates", "star", "stars", "stelle", "rating",
        "after", "before", "dopo", "prima", "almeno", "least", "oldest", "newest", "latest", "first",
        "recenti", "vecchie", "valutazione",
    };

    private static readonly HashSet<string> FillerWords = new(StringComparer.Ordinal)
    {
        "foto", "fotografie", "immagini", "immagine", "photo", "photos", "picture", "pictures", "pics",
        "gallery", "galleria", "solo", "soltanto", "only", "just", "tutte", "tutti", "all", "please",
        "per favore", "anche", "also", "adesso", "ora", "now", "quelle", "quelli",
    };

    private static readonly HashSet<string> Prepositions = new(StringComparer.Ordinal)
    {
        "al", "allo", "alla", "ai", "agli", "alle", "a", "di", "del", "dello", "della", "dei", "degli",
        "delle", "da", "dal", "dallo", "dalla", "dai", "dagli", "dalle", "in", "nel", "nello", "nella",
        "nei", "negli", "nelle", "con", "su", "sul", "sullo", "sulla", "per", "the", "of", "at", "on",
        "with", "to", "il", "lo", "la", "i", "gli", "le", "un", "uno", "una", "and", "e", "ed", "o", "or",
    };

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "le", "la", "il", "lo", "i", "gli", "un", "uno", "una", "the", "a", "an", "che",
        "durante", "during", "dello", "della", "scorso", "scorsa", "last", "questa", "questo",
        "this", "e", "ed", "o", "and", "or",
    };

    private static readonly HashSet<string> MonthAndSeason = new(StringComparer.Ordinal)
    {
        "gennaio","febbraio","marzo","aprile","maggio","giugno","luglio","agosto","settembre","ottobre","novembre","dicembre",
        "january","february","march","april","may","june","july","august","september","october","november","december",
        "estate","inverno","primavera","autunno","summer","winter","spring","autumn","fall","natale","christmas",
    };

    // Structural noise removed from the semantic residual (so only descriptive
    // CONTENT words remain). Union of every structural vocabulary + elided
    // article fragments + common possessives. Built after the other sets so the
    // unions see them initialised.
    private static readonly HashSet<string> ResidualNoise = BuildResidualNoise();

    private static HashSet<string> BuildResidualNoise()
    {
        var s = new HashSet<string>(StringComparer.Ordinal);
        foreach (var w in StopWords) s.Add(w);
        foreach (var w in FillerWords) s.Add(w);
        foreach (var w in Prepositions) s.Add(w);
        foreach (var w in LeadingVerbs) s.Add(w);
        foreach (var w in ExcludeMarkers) s.Add(w);
        foreach (var w in RemoveMarkers) s.Add(w);
        foreach (var w in OrMarkers) s.Add(w);
        foreach (var w in AndMarkers) s.Add(w);
        foreach (var w in NeitherMarkers) s.Add(w);
        foreach (var w in new[]
        {
            "dell", "dall", "nell", "sull", "all", "quest", "un", "uno", "una", "my", "me",
            "mie", "miei", "mia", "mio", "le", "la", "il", "lo", "i", "gli", "of", "from",
            "add", "also", "sort", "hide", "only", "just", "please", "per", "favore",
            "star", "stars", "stelle", "rating", "valutazione", "duplicati", "duplicates",
            "first", "last", "oldest", "newest", "latest", "recenti", "vecchie", "primo", "ultimo",
        }) s.Add(w);
        return s;
    }

    private static readonly string[] ResidualStripPhrases =
    {
        "mostrami", "mostra", "fammi vedere", "voglio vedere", "cercami", "cerca", "trovami", "trova",
        "show me", "show", "find me", "find", "search for", "search", "give me", "get me",
        "foto di", "foto con", "foto della", "foto del", "le foto", "photos of", "photos with",
        "pictures of", "immagini di", "foto", "photos", "pictures", "immagini",
        "aggiungi anche", "aggiungi", "togli", "rimuovi",
    };
}
