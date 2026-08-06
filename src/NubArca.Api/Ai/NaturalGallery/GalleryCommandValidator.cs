using NubArca.Api.Files;

namespace NubArca.Api.Ai.NaturalGallery;

// Deterministic validation + person/date resolution of a RAW interpreter draft
// into the safe, complete PROPOSED target state the user must explicitly apply.
//
// This is the trust boundary: whatever the interpreter (grammar OR decoder LLM)
// proposes, NOTHING becomes a query parameter until it survives here. Unknown
// operations/enums are rejected, oversized strings dropped, dates validated,
// Top-K clamped, and person text spans resolved STRICTLY within the owner's
// people (ambiguous → clarification, unknown → dropped with a warning). The
// model can never mutate server state or inject an id it invented.
public sealed class GalleryCommandValidator
{
    public const int MaxTextLength = 256;

    private readonly PersonNameResolver _people;
    private readonly AiNaturalGallerySearchOptions _options;

    public GalleryCommandValidator(PersonNameResolver people, AiNaturalGallerySearchOptions options)
    {
        _people = people;
        _options = options;
    }

    public async Task<InterpretCommandResponse> ValidateAsync(
        Guid ownerUserId,
        RawGalleryCommand raw,
        CurrentFilterStateDto current,
        CancellationToken cancellationToken = default)
    {
        var response = new InterpretCommandResponse();
        var draft = response.Draft;
        var warnings = new HashSet<string>(raw.Warnings);

        var operation = GalleryCommandOperations.IsKnown(raw.Operation)
            ? raw.Operation
            : GalleryCommandOperations.Replace;
        draft.Operation = operation;

        // Clear-all is deterministic and ignores everything else.
        if (operation == GalleryCommandOperations.Clear)
        {
            draft.SemanticTopK = 0;
            return response;
        }

        var refine = operation == GalleryCommandOperations.Refine;

        // Seed from current state on refine; empty on replace.
        var include = new List<Guid>(refine ? current.PeopleInclude.Distinct() : Enumerable.Empty<Guid>());
        var exclude = new List<Guid>(refine ? current.PeopleExclude.Distinct() : Enumerable.Empty<Guid>());
        draft.Favorite = refine ? current.Favorite : null;
        draft.MinRating = refine ? current.MinRating : null;
        draft.HasGps = refine ? current.HasGps : null;
        draft.DateTakenFrom = refine ? current.DateTakenFrom : null;
        draft.DateTakenTo = refine ? current.DateTakenTo : null;
        draft.CollapseDuplicates = refine ? current.CollapseDuplicates : null;
        draft.Sort = refine ? current.Sort : null;
        draft.SortDirection = refine ? current.SortDirection : null;
        draft.MetadataSearch = refine ? Trim(current.MetadataSearch) : null;
        draft.SemanticQuery = refine ? Trim(current.SemanticQuery) : null;
        var peopleMatch = NormalizeMatch(refine ? current.PeopleMatch : "all");

        // ---- People (owner-scoped resolution) -------------------------------
        if (raw.People.Count > 0)
        {
            var snapshot = await _people.LoadPeopleAsync(ownerUserId, cancellationToken);
            var hasIncludeOrExclude = false;
            foreach (var term in raw.People)
            {
                var mode = term.Mode is PeopleTermModes.Exclude or PeopleTermModes.Remove
                    ? term.Mode : PeopleTermModes.Include;
                var res = PersonNameResolver.Resolve(snapshot, term.Text, mode);
                switch (res.Status)
                {
                    case PersonNameResolver.ResolutionStatus.Resolved:
                        ApplyPerson(res.PersonId!.Value, mode, include, exclude);
                        if (mode != PeopleTermModes.Remove)
                        {
                            hasIncludeOrExclude = true;
                            response.ResolvedPeople.Add(new ResolvedPersonDto
                            {
                                Text = term.Text, Mode = mode, PersonId = res.PersonId!.Value, Name = res.Name,
                            });
                        }
                        break;
                    case PersonNameResolver.ResolutionStatus.Ambiguous:
                        response.Ambiguities.Add(new PersonAmbiguityDto
                        {
                            Text = term.Text,
                            Mode = mode == PeopleTermModes.Remove ? PeopleTermModes.Include : mode,
                            Candidates = res.Candidates.Select(c => new PersonCandidateDto
                            {
                                PersonId = c.Id, Name = c.DisplayName, FaceCount = c.FaceCount,
                            }).ToList(),
                        });
                        response.RequiresClarification = true;
                        break;
                    default:
                        warnings.Add("person_unresolved");
                        break;
                }
            }

            // A change to the person set only overrides the mode when the command
            // actually contributed include/exclude people.
            if (hasIncludeOrExclude)
            {
                peopleMatch = NormalizeMatch(raw.PeopleMatch);
            }
        }

        draft.PeopleInclude = include.Distinct().ToList();
        draft.PeopleExclude = exclude.Distinct().Where(id => !draft.PeopleInclude.Contains(id)).ToList();
        draft.PeopleMatch = peopleMatch;

        // ---- Scalars ---------------------------------------------------------
        if (raw.Favorite is bool fav) draft.Favorite = fav;
        if (raw.CollapseDuplicates is bool dup) draft.CollapseDuplicates = dup;
        if (raw.MinRating is int mr)
        {
            if (mr is >= 0 and <= 5) draft.MinRating = mr;
            else warnings.Add("rating_out_of_range");
        }

        if (raw.RemoveHasGps) draft.HasGps = null;
        else if (raw.HasGps is bool gps) draft.HasGps = gps;

        // ---- Sort ------------------------------------------------------------
        if (raw.Sort is not null)
        {
            if (ImageSort.TryParseField(raw.Sort, out _))
            {
                draft.Sort = raw.Sort.ToLowerInvariant();
                draft.SortDirection = ImageSort.TryParseDirection(raw.SortDirection, out var d)
                    ? (d == ImageSortDirection.Asc ? "asc" : "desc")
                    : draft.SortDirection;
            }
            else
            {
                warnings.Add("sort_unknown");
            }
        }

        // ---- Dates -----------------------------------------------------------
        var from = raw.DateTakenFrom;
        var to = raw.DateTakenTo;
        if (from.HasValue || to.HasValue)
        {
            if (from.HasValue && to.HasValue && from.Value > to.Value)
            {
                warnings.Add("date_invalid_range");
            }
            else
            {
                draft.DateTakenFrom = from?.ToUniversalTime();
                draft.DateTakenTo = to?.ToUniversalTime();
            }
        }

        // ---- Metadata vs semantic -------------------------------------------
        if (raw.MetadataSearch is not null)
        {
            var meta = Trim(raw.MetadataSearch);
            if (meta is { Length: > 0 } && meta.Length <= MaxTextLength) draft.MetadataSearch = meta;
            else if (meta is { Length: > MaxTextLength }) warnings.Add("metadata_too_long");
        }

        var semantic = Trim(raw.SemanticQuery);
        if (semantic is { Length: > MaxTextLength })
        {
            semantic = semantic[..MaxTextLength];
            warnings.Add("semantic_truncated");
        }
        draft.SemanticQuery = semantic;

        if (!string.IsNullOrWhiteSpace(draft.SemanticQuery))
        {
            draft.SemanticTopK = _options.ClampTopK(null);
            if (_options.UseEnglishSemanticTranslation && !string.IsNullOrWhiteSpace(raw.SemanticQueryEnglish))
            {
                draft.SemanticQueryEnglish = Trim(raw.SemanticQueryEnglish);
            }
        }
        else
        {
            draft.SemanticTopK = 0;
        }

        if (raw.NeedsClarification) response.RequiresClarification = true;

        // Nothing recognised (no filters, no semantic, replace): tell the client.
        if (operation == GalleryCommandOperations.Replace && IsEmptyDraft(draft))
        {
            warnings.Add("no_filters_detected");
        }

        response.Warnings = warnings.ToList();
        return response;
    }

    private static void ApplyPerson(Guid id, string mode, List<Guid> include, List<Guid> exclude)
    {
        switch (mode)
        {
            case PeopleTermModes.Exclude:
                include.RemoveAll(x => x == id);
                if (!exclude.Contains(id)) exclude.Add(id);
                break;
            case PeopleTermModes.Remove:
                include.RemoveAll(x => x == id);
                exclude.RemoveAll(x => x == id);
                break;
            default: // include
                exclude.RemoveAll(x => x == id);
                if (!include.Contains(id)) include.Add(id);
                break;
        }
    }

    private static bool IsEmptyDraft(GalleryCommandDraftDto d) =>
        d.PeopleInclude.Count == 0 && d.PeopleExclude.Count == 0
        && d.Favorite is null && d.MinRating is null && d.HasGps is null
        && d.DateTakenFrom is null && d.DateTakenTo is null && d.CollapseDuplicates is null
        && d.Sort is null && string.IsNullOrWhiteSpace(d.MetadataSearch)
        && string.IsNullOrWhiteSpace(d.SemanticQuery);

    private static string NormalizeMatch(string? m) =>
        string.Equals(m, "any", StringComparison.OrdinalIgnoreCase) ? "any" : "all";

    private static string? Trim(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = s.Trim();
        return t.Length == 0 ? null : t;
    }
}
