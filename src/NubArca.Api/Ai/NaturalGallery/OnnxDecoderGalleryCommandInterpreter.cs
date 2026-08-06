using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NubArca.Api.Ai.NaturalGallery;

// Transport to the isolated, internal-only decoder-LLM sidecar. The sidecar runs
// a pinned instruct model under ONNX Runtime GenAI with concurrency 1, a bounded
// queue, greedy decoding, a strict output-token cap, and NO outbound network. It
// is reached only over the internal Docker network (never a public host) and
// returns STRICT JSON matching the RawGalleryCommand schema. See the deployment
// docs + docker-compose sidecar service.
public interface INaturalGalleryCommandModelClient
{
    string ModelKey { get; }
    Task<bool> IsReadyAsync(CancellationToken cancellationToken = default);

    // Returns the model's raw JSON completion for the given prompt. Implementations
    // throw InterpreterUnavailableException / InterpreterBusyException /
    // InterpreterTimeoutException on transport failures; the interpreter maps
    // JSON-shape failures to InterpreterMalformedException.
    Task<string> CompleteJsonAsync(
        string systemPrompt, string userPrompt, int maxOutputTokens,
        CancellationToken cancellationToken = default);
}

// Production interpreter: prompts the local decoder sidecar to emit the strict
// structured draft, parses + shape-validates it (one local repair attempt), and
// maps to a RawGalleryCommand. It NEVER trusts the model's free text — only the
// typed JSON fields — and the downstream validator re-checks everything.
public sealed class OnnxDecoderGalleryCommandInterpreter : INaturalGalleryCommandInterpreter
{
    private const int MaxOutputTokens = 200;

    private readonly INaturalGalleryCommandModelClient _client;

    public OnnxDecoderGalleryCommandInterpreter(INaturalGalleryCommandModelClient client) => _client = client;

    public string Key => $"onnx:{_client.ModelKey}";

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
        => _client.IsReadyAsync(cancellationToken);

    public async Task<RawGalleryCommand> InterpretAsync(
        GalleryCommandContext context, CancellationToken cancellationToken = default)
    {
        var system = BuildSystemPrompt();
        var user = BuildUserPrompt(context);

        var json = await _client.CompleteJsonAsync(system, user, MaxOutputTokens, cancellationToken);
        if (TryParse(json, out var raw)) return raw;

        // One local repair attempt: re-ask with an explicit "JSON only" reminder.
        var repair = await _client.CompleteJsonAsync(
            system, user + "\n\nRespond with ONLY the JSON object, no prose.", MaxOutputTokens, cancellationToken);
        if (TryParse(repair, out raw)) return raw;

        throw new InterpreterMalformedException();
    }

    private static string BuildSystemPrompt() =>
        "You convert a photo-gallery search command (Italian or English) into a strict JSON object. " +
        "Output ONLY JSON, no prose, no code fences, no reasoning. Never invent people ids. " +
        "Extract person NAMES as text spans. Classify the residual visual description as semanticQuery, " +
        "and explicit title/filename/tag references as metadataSearch. Schema: " +
        "{\"operation\":\"replace|refine|clear\",\"people\":[{\"text\":string,\"mode\":\"include|exclude|remove\"}]," +
        "\"peopleMatch\":\"all|any\",\"favorite\":bool|null,\"minRating\":int|null,\"hasGps\":bool|null," +
        "\"removeHasGps\":bool,\"dateFrom\":\"YYYY-MM-DDTHH:MM:SSZ\"|null,\"dateTo\":same|null," +
        "\"collapseDuplicates\":bool|null,\"sort\":\"created|name|size|datetaken\"|null," +
        "\"sortDirection\":\"asc|desc\"|null,\"metadataSearch\":string|null,\"semanticQuery\":string|null," +
        "\"semanticQueryEnglish\":string|null,\"needsClarification\":bool}.";

    private static string BuildUserPrompt(GalleryCommandContext ctx) =>
        $"locale={ctx.Locale}; now={ctx.Now.UtcDateTime:o}; tz={ctx.TimeZone.Id}\nCOMMAND: {ctx.Command}";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private static bool TryParse(string? json, out RawGalleryCommand raw)
    {
        raw = new RawGalleryCommand();
        if (string.IsNullOrWhiteSpace(json)) return false;

        // Extract the first balanced {...} object (models sometimes wrap prose).
        var start = json.IndexOf('{');
        var end = json.LastIndexOf('}');
        if (start < 0 || end <= start) return false;
        var slice = json.Substring(start, end - start + 1);

        ModelDraftJson? dto;
        try { dto = JsonSerializer.Deserialize<ModelDraftJson>(slice, JsonOptions); }
        catch { return false; }
        if (dto is null) return false;

        raw.Operation = GalleryCommandOperations.IsKnown(dto.Operation) ? dto.Operation! : GalleryCommandOperations.Replace;
        raw.PeopleMatch = string.Equals(dto.PeopleMatch, "any", StringComparison.OrdinalIgnoreCase) ? "any" : "all";
        raw.Favorite = dto.Favorite;
        raw.MinRating = dto.MinRating;
        raw.HasGps = dto.HasGps;
        raw.RemoveHasGps = dto.RemoveHasGps ?? false;
        raw.CollapseDuplicates = dto.CollapseDuplicates;
        raw.Sort = dto.Sort;
        raw.SortDirection = dto.SortDirection;
        raw.MetadataSearch = dto.MetadataSearch;
        raw.SemanticQuery = dto.SemanticQuery;
        raw.SemanticQueryEnglish = dto.SemanticQueryEnglish;
        raw.NeedsClarification = dto.NeedsClarification ?? false;
        raw.DateTakenFrom = ParseDate(dto.DateFrom);
        raw.DateTakenTo = ParseDate(dto.DateTo);

        if (dto.People is not null)
        {
            foreach (var p in dto.People)
            {
                if (string.IsNullOrWhiteSpace(p?.Text)) continue;
                var mode = p!.Mode is PeopleTermModes.Exclude or PeopleTermModes.Remove ? p.Mode! : PeopleTermModes.Include;
                raw.People.Add(new RawPersonTerm(p.Text!.Trim(), mode));
            }
        }
        return true;
    }

    private static DateTime? ParseDate(string? iso)
        => string.IsNullOrWhiteSpace(iso) ? null
            : DateTime.TryParse(iso, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt) ? dt : null;

    private sealed class ModelDraftJson
    {
        public string? Operation { get; set; }
        public List<ModelPerson>? People { get; set; }
        public string? PeopleMatch { get; set; }
        public bool? Favorite { get; set; }
        public int? MinRating { get; set; }
        public bool? HasGps { get; set; }
        public bool? RemoveHasGps { get; set; }
        public string? DateFrom { get; set; }
        public string? DateTo { get; set; }
        public bool? CollapseDuplicates { get; set; }
        public string? Sort { get; set; }
        public string? SortDirection { get; set; }
        public string? MetadataSearch { get; set; }
        public string? SemanticQuery { get; set; }
        public string? SemanticQueryEnglish { get; set; }
        public bool? NeedsClarification { get; set; }
    }

    private sealed class ModelPerson
    {
        public string? Text { get; set; }
        public string? Mode { get; set; }
    }
}
