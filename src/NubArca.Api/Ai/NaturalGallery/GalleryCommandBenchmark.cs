using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NubArca.Api.Ai.NaturalGallery;

// LOCAL evaluation harness for the natural-language command interpreter. It runs
// a versioned, synthetic IT/EN corpus (no real names/data) through ANY
// interpreter backend and scores structured-command accuracy + latency. It never
// emits model reasoning and never calls a cloud service. Used by the CLI
// benchmark command and the xUnit accuracy test so reported numbers are REAL.
public static class GalleryCommandBenchmark
{
    // Reference "now" the corpus's relative dates ("oggi", "ieri", "last week")
    // were authored against. Fixed so results are reproducible.
    public static readonly DateTimeOffset ReferenceNow =
        new(2026, 7, 12, 12, 0, 0, TimeSpan.Zero);

    public sealed class Corpus
    {
        [JsonPropertyName("version")] public int Version { get; set; } = 1;
        [JsonPropertyName("cases")] public List<BenchmarkCase> Cases { get; set; } = new();
    }

    public sealed class BenchmarkCase
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("command")] public string Command { get; set; } = "";
        [JsonPropertyName("locale")] public string Locale { get; set; } = "it-IT";
        [JsonPropertyName("category")] public string? Category { get; set; }
        [JsonPropertyName("expect")] public ExpectedDraft Expect { get; set; } = new();
    }

    public sealed class ExpectedDraft
    {
        [JsonPropertyName("operation")] public string Operation { get; set; } = "replace";
        [JsonPropertyName("people")] public List<ExpectPerson> People { get; set; } = new();
        [JsonPropertyName("peopleMatch")] public string PeopleMatch { get; set; } = "all";
        [JsonPropertyName("favorite")] public bool? Favorite { get; set; }
        [JsonPropertyName("minRating")] public int? MinRating { get; set; }
        [JsonPropertyName("hasGps")] public bool? HasGps { get; set; }
        [JsonPropertyName("removeHasGps")] public bool RemoveHasGps { get; set; }
        [JsonPropertyName("hasDate")] public bool HasDate { get; set; }
        [JsonPropertyName("dateFrom")] public string? DateFrom { get; set; }
        [JsonPropertyName("dateTo")] public string? DateTo { get; set; }
        [JsonPropertyName("collapseDuplicates")] public bool? CollapseDuplicates { get; set; }
        [JsonPropertyName("sort")] public string? Sort { get; set; }
        [JsonPropertyName("sortDirection")] public string? SortDirection { get; set; }
        [JsonPropertyName("metadata")] public bool Metadata { get; set; }
        [JsonPropertyName("semantic")] public bool Semantic { get; set; }
    }

    public sealed class ExpectPerson
    {
        [JsonPropertyName("text")] public string Text { get; set; } = "";
        [JsonPropertyName("mode")] public string Mode { get; set; } = PeopleTermModes.Include;
    }

    public sealed record CaseResult(string Id, bool ValidOutput, bool ExactMatch, IReadOnlyList<string> FieldMisses, double Ms);

    public sealed record BenchmarkReport(
        string InterpreterKey,
        int Total,
        double ValidOutputRate,
        double ExactMatchRate,
        double OperationAccuracy,
        double PersonSpanPrecision,
        double PersonSpanRecall,
        double PersonSpanF1,
        double PeopleLogicAccuracy,
        double DateAccuracy,
        double MetadataVsSemanticAccuracy,
        double FavoriteAccuracy,
        double RatingAccuracy,
        double GpsAccuracy,
        double SortAccuracy,
        double P50Ms,
        double P95Ms,
        double ModelLoadMs,
        IReadOnlyList<CaseResult> Cases);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static Corpus LoadCorpus(string path)
        => JsonSerializer.Deserialize<Corpus>(File.ReadAllText(path), JsonOptions)
           ?? throw new InvalidOperationException("Corpus deserialised to null.");

    public static async Task<BenchmarkReport> RunAsync(
        INaturalGalleryCommandInterpreter interpreter,
        Corpus corpus,
        double modelLoadMs = 0,
        CancellationToken cancellationToken = default)
    {
        var tz = TryTz("Europe/Rome");
        var results = new List<CaseResult>(corpus.Cases.Count);
        var latencies = new List<double>(corpus.Cases.Count);

        // person-span aggregates
        int spanTp = 0, spanFp = 0, spanFn = 0;
        int peopleLogicApplicable = 0, peopleLogicOk = 0;
        int dateApplicable = 0, dateOk = 0;
        int metaSemOk = 0;
        int favApplicable = 0, favOk = 0, ratApplicable = 0, ratOk = 0, gpsApplicable = 0, gpsOk = 0;
        int sortApplicable = 0, sortOk = 0, opOk = 0, valid = 0, exact = 0;

        foreach (var c in corpus.Cases)
        {
            var ctx = new GalleryCommandContext(
                c.Command, c.Locale, tz, ReferenceNow, new CurrentFilterStateDto());

            RawGalleryCommand raw;
            var sw = Stopwatch.StartNew();
            try
            {
                raw = await interpreter.InterpretAsync(ctx, cancellationToken);
                sw.Stop();
            }
            catch
            {
                sw.Stop();
                results.Add(new CaseResult(c.Id, false, false, new[] { "no_output" }, sw.Elapsed.TotalMilliseconds));
                latencies.Add(sw.Elapsed.TotalMilliseconds);
                continue;
            }

            latencies.Add(sw.Elapsed.TotalMilliseconds);
            valid++;
            var e = c.Expect;
            var misses = new List<string>();

            if (string.Equals(raw.Operation, e.Operation, StringComparison.OrdinalIgnoreCase)) opOk++;
            else misses.Add("operation");

            // people spans (normalized text + mode set)
            var expSet = e.People
                .Select(p => (PersonNameResolver.Normalize(p.Text), Norm(p.Mode)))
                .ToHashSet();
            var gotSet = raw.People
                .Select(p => (PersonNameResolver.Normalize(p.Text), Norm(p.Mode)))
                .ToHashSet();
            spanTp += gotSet.Count(g => expSet.Contains(g));
            spanFp += gotSet.Count(g => !expSet.Contains(g));
            spanFn += expSet.Count(x => !gotSet.Contains(x));
            var peopleOk = expSet.SetEquals(gotSet);
            if (!peopleOk) misses.Add("people");

            if (e.People.Count > 0)
            {
                peopleLogicApplicable++;
                var matchOk = string.Equals(NormalizeMatch(raw.PeopleMatch), NormalizeMatch(e.PeopleMatch), StringComparison.Ordinal);
                if (peopleOk && matchOk) peopleLogicOk++;
                if (!matchOk) misses.Add("peopleMatch");
            }

            var favMatch = raw.Favorite == e.Favorite;
            if (e.Favorite is not null) { favApplicable++; if (favMatch) favOk++; }
            if (!favMatch) misses.Add("favorite");

            var ratMatch = raw.MinRating == e.MinRating;
            if (e.MinRating is not null) { ratApplicable++; if (ratMatch) ratOk++; }
            if (!ratMatch) misses.Add("minRating");

            var gpsMatch = raw.HasGps == e.HasGps && raw.RemoveHasGps == e.RemoveHasGps;
            if (e.HasGps is not null || e.RemoveHasGps) { gpsApplicable++; if (gpsMatch) gpsOk++; }
            if (!gpsMatch) misses.Add("gps");

            var gotHasDate = raw.DateTakenFrom is not null || raw.DateTakenTo is not null;
            var dateOkCase = gotHasDate == e.HasDate
                && DateMatches(raw.DateTakenFrom, e.DateFrom)
                && DateMatches(raw.DateTakenTo, e.DateTo);
            if (e.HasDate) { dateApplicable++; if (dateOkCase) dateOk++; }
            if (!dateOkCase) misses.Add("date");

            var gotMeta = !string.IsNullOrWhiteSpace(raw.MetadataSearch);
            var gotSem = !string.IsNullOrWhiteSpace(raw.SemanticQuery);
            var metaSemCase = gotMeta == e.Metadata && gotSem == e.Semantic;
            if (metaSemCase) metaSemOk++;
            else misses.Add("metadata_vs_semantic");

            var sortMatch = string.Equals(Nullify(raw.Sort), Nullify(e.Sort), StringComparison.OrdinalIgnoreCase);
            if (e.Sort is not null) { sortApplicable++; if (sortMatch) sortOk++; }
            if (!sortMatch) misses.Add("sort");

            var collapseMatch = raw.CollapseDuplicates == e.CollapseDuplicates;
            if (!collapseMatch) misses.Add("collapseDuplicates");

            var exactCase = misses.Count == 0;
            if (exactCase) exact++;
            results.Add(new CaseResult(c.Id, true, exactCase, misses, sw.Elapsed.TotalMilliseconds));
        }

        latencies.Sort();
        var n = Math.Max(1, corpus.Cases.Count);
        return new BenchmarkReport(
            interpreter.Key,
            corpus.Cases.Count,
            valid / (double)n,
            exact / (double)n,
            opOk / (double)n,
            Precision(spanTp, spanFp),
            Recall(spanTp, spanFn),
            F1(spanTp, spanFp, spanFn),
            Rate(peopleLogicOk, peopleLogicApplicable),
            Rate(dateOk, dateApplicable),
            metaSemOk / (double)n,
            Rate(favOk, favApplicable),
            Rate(ratOk, ratApplicable),
            Rate(gpsOk, gpsApplicable),
            Rate(sortOk, sortApplicable),
            Percentile(latencies, 50),
            Percentile(latencies, 95),
            modelLoadMs,
            results);
    }

    public static string FormatReport(BenchmarkReport r)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"NL gallery command benchmark — interpreter={r.InterpreterKey}, cases={r.Total}");
        sb.AppendLine($"  valid structured output : {r.ValidOutputRate:P1}");
        sb.AppendLine($"  EXACT structured match  : {r.ExactMatchRate:P1}");
        sb.AppendLine($"  operation accuracy      : {r.OperationAccuracy:P1}");
        sb.AppendLine($"  person span P/R/F1      : {r.PersonSpanPrecision:P1} / {r.PersonSpanRecall:P1} / {r.PersonSpanF1:P1}");
        sb.AppendLine($"  AND/OR/exclusion logic  : {r.PeopleLogicAccuracy:P1}");
        sb.AppendLine($"  date accuracy           : {r.DateAccuracy:P1}");
        sb.AppendLine($"  metadata-vs-semantic    : {r.MetadataVsSemanticAccuracy:P1}");
        sb.AppendLine($"  favorite / rating / gps : {r.FavoriteAccuracy:P1} / {r.RatingAccuracy:P1} / {r.GpsAccuracy:P1}");
        sb.AppendLine($"  sort accuracy           : {r.SortAccuracy:P1}");
        sb.AppendLine($"  latency p50 / p95 (ms)  : {r.P50Ms:F2} / {r.P95Ms:F2}");
        sb.AppendLine($"  model load (ms)         : {r.ModelLoadMs:F0}");
        var failing = r.Cases.Where(c => !c.ExactMatch).ToList();
        if (failing.Count > 0)
        {
            sb.AppendLine($"  --- {failing.Count} non-exact case(s) ---");
            foreach (var c in failing)
            {
                sb.AppendLine($"    {c.Id}: {string.Join(", ", c.FieldMisses)}");
            }
        }
        return sb.ToString();
    }

    private static string Norm(string mode) => mode switch
    {
        PeopleTermModes.Exclude => PeopleTermModes.Exclude,
        PeopleTermModes.Remove => PeopleTermModes.Remove,
        _ => PeopleTermModes.Include,
    };

    private static string NormalizeMatch(string? m) =>
        string.Equals(m, "any", StringComparison.OrdinalIgnoreCase) ? "any" : "all";

    private static string? Nullify(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim().ToLowerInvariant();

    private static bool DateMatches(DateTime? got, string? expected)
    {
        if (string.IsNullOrWhiteSpace(expected)) return true; // don't score exact when unspecified
        if (got is null) return false;
        return DateTime.TryParse(expected, CultureInfo.InvariantCulture,
                   DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var exp)
               && got.Value.ToUniversalTime().Date == exp.Date;
    }

    private static double Rate(int ok, int applicable) => applicable == 0 ? 1.0 : ok / (double)applicable;
    private static double Precision(int tp, int fp) => tp + fp == 0 ? 1.0 : tp / (double)(tp + fp);
    private static double Recall(int tp, int fn) => tp + fn == 0 ? 1.0 : tp / (double)(tp + fn);
    private static double F1(int tp, int fp, int fn)
    {
        var p = Precision(tp, fp); var r = Recall(tp, fn);
        return p + r == 0 ? 0 : 2 * p * r / (p + r);
    }

    private static double Percentile(List<double> sorted, int pct)
    {
        if (sorted.Count == 0) return 0;
        var rank = (int)Math.Ceiling(pct / 100.0 * sorted.Count) - 1;
        return sorted[Math.Clamp(rank, 0, sorted.Count - 1)];
    }

    private static TimeZoneInfo TryTz(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch { return TimeZoneInfo.Utc; }
    }
}
