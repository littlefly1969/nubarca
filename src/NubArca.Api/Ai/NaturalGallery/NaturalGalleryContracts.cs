using System.Text.Json.Serialization;

namespace NubArca.Api.Ai.NaturalGallery;

// Strict, versioned contracts for the TV natural-language gallery command
// interpreter. Everything here is LOCAL: the command text is interpreted by a
// local model / local grammar, resolved deterministically on the server, and
// turned into a PROPOSED draft the user must explicitly apply. The model never
// emits SQL, URLs, database ids or executable expressions — only these typed
// fields, which are validated before anything touches the gallery.

// Operation the command proposes against the CURRENT filter state.
public static class GalleryCommandOperations
{
    public const string Replace = "replace"; // build a fresh target state
    public const string Refine = "refine";   // start from current, adjust
    public const string Clear = "clear";     // deterministic clear-all

    public static bool IsKnown(string? op) =>
        op is Replace or Refine or Clear;
}

public static class PeopleTermModes
{
    public const string Include = "include";
    public const string Exclude = "exclude";
    // Refine-only: drop this person from BOTH the current include and exclude
    // sets ("togli Marco" / "remove Marco"). Never appears in the final draft.
    public const string Remove = "remove";
}

// ---- Request (client → interpret endpoint) ---------------------------------

public sealed class InterpretCommandRequest
{
    public string? Command { get; set; }
    public string? Locale { get; set; }        // e.g. "it-IT", "en-US"
    public string? TimeZone { get; set; }       // IANA, e.g. "Europe/Rome"
    public string? CurrentDate { get; set; }    // client ISO instant (advisory)
    public CurrentFilterStateDto? CurrentFilters { get; set; }
}

// The complete current gallery filter state the client is viewing. People are
// already resolved to owner-private ids on the client. Sent so "refine" and
// "clear" commands can work from the live state (no server-side chat memory).
public sealed class CurrentFilterStateDto
{
    public List<Guid> PeopleInclude { get; set; } = new();
    public List<Guid> PeopleExclude { get; set; } = new();
    public string PeopleMatch { get; set; } = "all";
    public bool? Favorite { get; set; }
    public int? MinRating { get; set; }
    public bool? HasGps { get; set; }
    public DateTime? DateTakenFrom { get; set; }
    public DateTime? DateTakenTo { get; set; }
    public bool? CollapseDuplicates { get; set; }
    public string? Sort { get; set; }
    public string? SortDirection { get; set; }
    public string? MetadataSearch { get; set; }
    public string? SemanticQuery { get; set; }
}

// ---- Response (interpret endpoint → client) --------------------------------

// The validated draft = the complete PROPOSED target filter state. People are
// resolved owner-private ids (never invented). Ambiguous people are NOT placed
// here — they surface in Ambiguities and must be resolved before Apply.
public sealed class GalleryCommandDraftDto
{
    public int Version { get; set; } = 1;
    public string Operation { get; set; } = GalleryCommandOperations.Replace;

    public List<Guid> PeopleInclude { get; set; } = new();
    public List<Guid> PeopleExclude { get; set; } = new();
    public string PeopleMatch { get; set; } = "all";

    public bool? Favorite { get; set; }
    public int? MinRating { get; set; }
    public bool? HasGps { get; set; }
    public DateTime? DateTakenFrom { get; set; }
    public DateTime? DateTakenTo { get; set; }
    public bool? CollapseDuplicates { get; set; }
    public string? Sort { get; set; }
    public string? SortDirection { get; set; }
    public string? MetadataSearch { get; set; }
    public string? SemanticQuery { get; set; }
    public string? SemanticQueryEnglish { get; set; }
    public int SemanticTopK { get; set; }
}

public sealed class ResolvedPersonDto
{
    public string Text { get; set; } = "";
    public string Mode { get; set; } = PeopleTermModes.Include;
    public Guid PersonId { get; set; }
    public string? Name { get; set; }
}

public sealed class PersonCandidateDto
{
    public Guid PersonId { get; set; }
    public string? Name { get; set; }
    public int FaceCount { get; set; }
}

// A person span the resolver could not resolve to exactly one owner person.
// The client must let the user pick (or cancel) before Apply.
public sealed class PersonAmbiguityDto
{
    public string Text { get; set; } = "";
    public string Mode { get; set; } = PeopleTermModes.Include;
    public List<PersonCandidateDto> Candidates { get; set; } = new();
}

public sealed class InterpretCommandResponse
{
    public GalleryCommandDraftDto Draft { get; set; } = new();
    public List<ResolvedPersonDto> ResolvedPeople { get; set; } = new();
    public List<PersonAmbiguityDto> Ambiguities { get; set; } = new();
    // Machine-stable warning codes (never raw command text). e.g.
    // "date_year_assumed", "candidates_truncated", "semantic_needs_indexing".
    public List<string> Warnings { get; set; } = new();
    public bool RequiresClarification { get; set; }
}

// ---- Interpreter-internal raw draft (model / grammar output) ---------------

// The raw, UN-resolved structured draft produced by an interpreter backend
// BEFORE deterministic validation + person/date resolution. People are TEXT
// spans (never ids). Dates are already normalised to whole-day UTC boundaries
// by the interpreter (deterministic) or parsed from the model's ISO strings.
public sealed class RawGalleryCommand
{
    public int Version { get; set; } = 1;
    public string Operation { get; set; } = GalleryCommandOperations.Replace;
    public List<RawPersonTerm> People { get; set; } = new();
    public string PeopleMatch { get; set; } = "all";
    public bool? Favorite { get; set; }
    public int? MinRating { get; set; }
    public bool? HasGps { get; set; }
    // Refine-only: command asked to REMOVE the GPS filter ("togli il filtro
    // GPS"). Distinct from HasGps=null "not mentioned".
    public bool RemoveHasGps { get; set; }
    public DateTime? DateTakenFrom { get; set; }
    public DateTime? DateTakenTo { get; set; }
    public bool? CollapseDuplicates { get; set; }
    public string? Sort { get; set; }
    public string? SortDirection { get; set; }
    public string? MetadataSearch { get; set; }
    public string? SemanticQuery { get; set; }
    public string? SemanticQueryEnglish { get; set; }
    // Non-fatal signals the validator turns into warning codes / clarifications.
    public List<string> Warnings { get; set; } = new();
    // The interpreter believes the command is genuinely ambiguous (e.g. a
    // dangerous ambiguous year, "né X né Y" it will not silently guess) and the
    // server should ask rather than apply.
    public bool NeedsClarification { get; set; }
}

public sealed class RawPersonTerm
{
    public RawPersonTerm() { }
    public RawPersonTerm(string text, string mode) { Text = text; Mode = mode; }
    public string Text { get; set; } = "";
    public string Mode { get; set; } = PeopleTermModes.Include;
}

// Context handed to every interpreter backend. `Now` is the server clock; the
// interpreter uses the client's time zone + locale for relative dates.
public sealed record GalleryCommandContext(
    string Command,
    string Locale,
    TimeZoneInfo TimeZone,
    DateTimeOffset Now,
    CurrentFilterStateDto CurrentFilters)
{
    public bool IsItalian =>
        Locale.StartsWith("it", StringComparison.OrdinalIgnoreCase);
}

// Distinct outcome of an interpret call so the endpoint can map each to the
// right user-facing message + HTTP status (never a raw exception / model dump).
public enum InterpretOutcomeKind
{
    Ok,
    ModelUnavailable,
    ModelBusy,
    Timeout,
    Malformed,
    Unsupported,
}

public sealed record InterpretOutcome(
    InterpretOutcomeKind Kind,
    InterpretCommandResponse? Response,
    string InterpreterKey)
{
    public bool Succeeded => Kind == InterpretOutcomeKind.Ok && Response is not null;
}
