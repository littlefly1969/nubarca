using System.Globalization;
using System.Text;

namespace NubArca.Api.Rag.ProductHelp;

/// Normalization shared by the corpus builder and the query path.
///
/// The two MUST agree. A document indexed with one tokenizer and queried with
/// another retrieves nothing, and the failure looks like a ranking problem
/// rather than a tokenizer problem — so there is exactly one of them, here.
public static class ProductHelpText
{
    /// Stopwords for BOTH interface languages, in one set.
    ///
    /// One set rather than a per-language set chosen by a language guess,
    /// because the guess is the bug. Italian `come` ("how") is also an English
    /// verb, so a language-switched list would keep it for an Italian question,
    /// and every English sentence containing "come" would score — which is
    /// exactly the false match this corpus produced before. A word that carries
    /// no meaning in EITHER language carries none here.
    private static readonly HashSet<string> Stopwords = new(StringComparer.Ordinal)
    {
        // Italian
        "il", "lo", "la", "le", "gli", "un", "uno", "una", "di", "del", "dello",
        "della", "dei", "degli", "delle", "da", "dal", "dalla", "in", "nel",
        "nella", "con", "su", "sul", "sulla", "per", "tra", "fra", "che", "chi",
        "cui", "non", "piu", "quale", "quali", "quando", "dove", "come", "cosa",
        "questo", "questa", "questi", "queste", "quello", "quella", "si", "no",
        "ci", "vi", "ne", "mi", "ti", "se", "sono", "sei", "essere", "stato",
        "ho", "hai", "ha", "abbiamo", "hanno", "avere", "faccio", "fai", "fa",
        "fare", "posso", "puoi", "puo", "possiamo", "devo", "deve", "voglio",
        "vuoi", "anche", "ancora", "sempre", "poi", "ed", "al", "allo", "alla",
        "ai", "agli", "alle", "una", "delle",
        // English
        "the", "a", "an", "and", "or", "but", "if", "of", "to", "in", "on",
        "at", "by", "for", "with", "from", "as", "is", "are", "was", "were",
        "be", "been", "being", "do", "does", "did", "doing", "have", "has",
        "had", "can", "could", "should", "would", "will", "shall", "may",
        "might", "must", "this", "that", "these", "those", "it", "its", "i",
        "you", "he", "she", "we", "they", "my", "your", "our", "their", "there",
        "here", "what", "which", "who", "whom", "when", "where", "why", "how",
        "not", "no", "yes", "so", "than", "then", "too", "very", "just", "also",
        "into", "about", "over", "any", "all", "some", "more", "most", "other",
    };

    /// Words that make a question a HOW-TO question, in either language.
    ///
    /// Detected on the raw token stream, BEFORE stopwords are removed, because
    /// most of them are stopwords: `come`, `how` and `faccio` carry no topical
    /// meaning and are the strongest available signal about what kind of answer
    /// the person wants.
    private static readonly HashSet<string> HowToMarkers = new(StringComparer.Ordinal)
    {
        "come", "faccio", "fare", "posso", "usare", "utilizzare", "usa", "uso",
        "attivare", "abilitare", "configurare", "impostare", "aggiungere",
        "creare", "assegnare", "gestire", "trovare",
        "how", "use", "using", "enable", "enabling", "configure", "setup",
        "set", "add", "create", "assign", "manage", "find", "start",
    };

    /// Lowercase, fold diacritics, drop everything that is not a letter or a
    /// digit.
    ///
    /// Folding matters in both directions: someone types `perche` for `perché`,
    /// and a document writes `più` where a question writes `piu`. Neither should
    /// be a miss.
    public static List<string> Tokenize(string? text)
    {
        var tokens = new List<string>();
        if (string.IsNullOrEmpty(text)) return tokens;

        var folded = Fold(text);
        var current = new StringBuilder();
        foreach (var ch in folded)
        {
            if (char.IsLetterOrDigit(ch))
            {
                current.Append(char.ToLowerInvariant(ch));
            }
            else if (current.Length > 0)
            {
                Flush(tokens, current);
            }
        }
        if (current.Length > 0) Flush(tokens, current);
        return tokens;

        static void Flush(List<string> into, StringBuilder buffer)
        {
            // Single characters are noise in both languages, and a bare digit
            // matches every version number in the corpus.
            if (buffer.Length > 1) into.Add(buffer.ToString());
            buffer.Clear();
        }
    }

    /// Tokens with no topical meaning removed. The order of what remains is
    /// preserved, so callers that care about adjacency still can.
    public static List<string> ContentTokens(string? text)
        => Tokenize(text).Where(t => !Stopwords.Contains(t)).ToList();

    public static bool IsStopword(string token) => Stopwords.Contains(token);

    public static bool LooksLikeHowTo(string? text)
        => Tokenize(text).Any(HowToMarkers.Contains);

    private static string Fold(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(ch);
            }
        }
        return builder.ToString().Normalize(NormalizationForm.FormC);
    }
}
