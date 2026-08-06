using NubArca.Api.Ai.NaturalGallery;
using Xunit.Abstractions;

namespace NubArca.Api.Tests.Ai.NaturalGallery;

// Pure, DB-free tests of the deterministic IT/EN grammar interpreter + the
// evaluation harness. The harness test reports REAL structured-command accuracy
// on the versioned synthetic corpus (no cloud, no weights, no private names).
public sealed class GalleryCommandInterpreterTests
{
    private readonly ITestOutputHelper _output;
    public GalleryCommandInterpreterTests(ITestOutputHelper output) => _output = output;

    private static readonly TimeZoneInfo Rome = ResolveRome();
    private static TimeZoneInfo ResolveRome()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/Rome"); }
        catch { return TimeZoneInfo.Utc; }
    }

    private static RawGalleryCommand Run(string command, string locale = "it-IT")
        => DeterministicGalleryCommandInterpreter.Interpret(new GalleryCommandContext(
            command, locale, Rome, GalleryCommandBenchmark.ReferenceNow, new CurrentFilterStateDto()));

    [Fact]
    public void People_And_Is_All()
    {
        var r = Run("Anna e Marco insieme al tramonto");
        Assert.Equal("all", r.PeopleMatch);
        Assert.Contains(r.People, p => p.Text.Equals("Anna", StringComparison.OrdinalIgnoreCase) && p.Mode == PeopleTermModes.Include);
        Assert.Contains(r.People, p => p.Text.Equals("Marco", StringComparison.OrdinalIgnoreCase) && p.Mode == PeopleTermModes.Include);
        Assert.False(string.IsNullOrWhiteSpace(r.SemanticQuery));
    }

    [Fact]
    public void People_Or_Is_Any()
    {
        var r = Run("Anna o Marco");
        Assert.Equal("any", r.PeopleMatch);
        Assert.Equal(2, r.People.Count);
    }

    [Fact]
    public void People_Exclude_With_Senza()
    {
        var r = Run("Foto preferite di Giulia senza Paolo");
        Assert.Contains(r.People, p => p.Text.Equals("Giulia", StringComparison.OrdinalIgnoreCase) && p.Mode == PeopleTermModes.Include);
        Assert.Contains(r.People, p => p.Text.Equals("Paolo", StringComparison.OrdinalIgnoreCase) && p.Mode == PeopleTermModes.Exclude);
        Assert.True(r.Favorite);
    }

    [Fact]
    public void Clear_Operation()
    {
        Assert.Equal(GalleryCommandOperations.Clear, Run("Azzera tutti i filtri").Operation);
        Assert.Equal(GalleryCommandOperations.Clear, Run("Clear all filters", "en-US").Operation);
    }

    [Fact]
    public void Refine_Add_And_Favorite()
    {
        var r = Run("Aggiungi anche Marco e mostrami solo le preferite");
        Assert.Equal(GalleryCommandOperations.Refine, r.Operation);
        Assert.Contains(r.People, p => p.Text.Equals("Marco", StringComparison.OrdinalIgnoreCase));
        Assert.True(r.Favorite);
    }

    [Fact]
    public void Refine_Remove_Gps_And_Person()
    {
        var gps = Run("Togli il filtro GPS e cerca foto di notte");
        Assert.Equal(GalleryCommandOperations.Refine, gps.Operation);
        Assert.True(gps.RemoveHasGps);
        Assert.False(string.IsNullOrWhiteSpace(gps.SemanticQuery));

        var person = Run("Togli Marco");
        Assert.Contains(person.People, p => p.Mode == PeopleTermModes.Remove && p.Text.Equals("Marco", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Rating_And_Gps_And_Sort()
    {
        Assert.Equal(4, Run("Foto con almeno 4 stelle").MinRating);
        Assert.True(Run("Foto con GPS").HasGps);
        Assert.False(Run("Foto senza posizione").HasGps!.Value);
        var sort = Run("Le foto più recenti");
        Assert.Equal("created", sort.Sort);
        Assert.Equal("desc", sort.SortDirection);
    }

    [Fact]
    public void Metadata_Vs_Semantic_Separation()
    {
        var meta = Run("Foto con titolo vacanza");
        Assert.False(string.IsNullOrWhiteSpace(meta.MetadataSearch));
        Assert.True(string.IsNullOrWhiteSpace(meta.SemanticQuery));

        var sem = Run("Foto al mare al tramonto");
        Assert.True(string.IsNullOrWhiteSpace(sem.MetadataSearch));
        Assert.False(string.IsNullOrWhiteSpace(sem.SemanticQuery));

        var file = Run("File IMG_2024");
        Assert.False(string.IsNullOrWhiteSpace(file.MetadataSearch));
    }

    [Fact]
    public void Dates_Season_Month_Range()
    {
        var estate = Run("Foto dell'estate 2024");
        Assert.Equal(new DateTime(2024, 6, 1), estate.DateTakenFrom!.Value.Date);
        Assert.Equal(new DateTime(2024, 8, 31), estate.DateTakenTo!.Value.Date);

        var range = Run("Dal 3 al 10 giugno");
        Assert.Equal(new DateTime(2026, 6, 3), range.DateTakenFrom!.Value.Date);
        Assert.Equal(new DateTime(2026, 6, 10), range.DateTakenTo!.Value.Date);

        var before = Run("Foto prima del 2020");
        Assert.Null(before.DateTakenFrom);
        Assert.Equal(new DateTime(2019, 12, 31), before.DateTakenTo!.Value.Date);
    }

    [Fact]
    public void Christmas_Relative_Uses_Prior_Year()
    {
        var r = Run("Le foto con neve dello scorso Natale");
        Assert.Equal(new DateTime(2025, 12, 24), r.DateTakenFrom!.Value.Date);
        Assert.Equal(new DateTime(2025, 12, 26), r.DateTakenTo!.Value.Date);
        Assert.False(string.IsNullOrWhiteSpace(r.SemanticQuery));
    }

    // ---- The evaluation harness: REAL numbers on the synthetic corpus --------

    [Fact]
    public async Task Benchmark_Corpus_Reports_Real_Accuracy()
    {
        var path = CorpusPath();
        var corpus = GalleryCommandBenchmark.LoadCorpus(path);
        Assert.True(corpus.Cases.Count >= 40, $"corpus too small: {corpus.Cases.Count}");

        var report = await GalleryCommandBenchmark.RunAsync(
            new DeterministicGalleryCommandInterpreter(), corpus);

        _output.WriteLine(GalleryCommandBenchmark.FormatReport(report));

        // The deterministic grammar must always emit a valid structured draft.
        Assert.Equal(1.0, report.ValidOutputRate, 3);
        // Guardrails on the measured accuracy (kept below observed so a small
        // corpus edit doesn't flake; the printed report is the real number).
        Assert.True(report.ExactMatchRate >= 0.75, $"exact match too low: {report.ExactMatchRate:P1}");
        Assert.True(report.OperationAccuracy >= 0.95, $"operation acc: {report.OperationAccuracy:P1}");
        Assert.True(report.PersonSpanF1 >= 0.85, $"person span F1: {report.PersonSpanF1:P1}");
        Assert.True(report.DateAccuracy >= 0.85, $"date acc: {report.DateAccuracy:P1}");
        Assert.True(report.MetadataVsSemanticAccuracy >= 0.85, $"meta/semantic acc: {report.MetadataVsSemanticAccuracy:P1}");
    }

    // Measures the DETERMINISTIC interpreter on the adversarial holdout (same
    // corpus the ONNX candidate is scored on) so the two are directly comparable
    // and we can see exactly where the grammar fails.
    [Fact]
    public async Task Benchmark_Holdout_Deterministic_Baseline()
    {
        var corpus = GalleryCommandBenchmark.LoadCorpus(CorpusPath("nl-gallery-corpus.holdout.json"));
        var report = await GalleryCommandBenchmark.RunAsync(new DeterministicGalleryCommandInterpreter(), corpus);
        _output.WriteLine(GalleryCommandBenchmark.FormatReport(report));
        Assert.Equal(1.0, report.ValidOutputRate, 3); // grammar always emits a valid draft
    }

    internal static string CorpusPath(string fileName = "nl-gallery-corpus.v1.json")
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 12 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "docs", "model-deployment", fileName);
            if (File.Exists(candidate)) return candidate;
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new FileNotFoundException($"Could not locate {fileName} by walking up from " + AppContext.BaseDirectory);
    }
}
