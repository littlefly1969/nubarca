using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;

namespace NubArca.Api.Ai.NaturalGallery;

// Deterministic, STRICTLY owner-scoped resolution of a person-name TEXT span to
// an owner-private person id. The command model only ever emits name text — it
// never invents ids and never chooses a person. Resolution order:
//   1. exact normalised name (trim + collapse whitespace + case + diacritics)
//   2. (aliases would go here — NOT supported by the current Person model)
//   3. conservative fuzzy: a single prefix / edit-distance-1 candidate
//   4. otherwise → ambiguity (multiple) or unresolved (none)
//
// Names are never translated. A person from another owner can never be returned
// (the query is filtered by OwnerUserId), so a name that matches nothing simply
// yields "unresolved" — no cross-owner existence leak.
public sealed class PersonNameResolver
{
    private readonly AppDbContext _db;

    public PersonNameResolver(AppDbContext db) => _db = db;

    public sealed record PersonRecord(Guid Id, string? DisplayName, int FaceCount)
    {
        public string Normalized { get; } = Normalize(DisplayName);
    }

    public enum ResolutionStatus { Resolved, Ambiguous, Unresolved }

    public sealed record PersonResolution(
        string Text,
        string Mode,
        ResolutionStatus Status,
        Guid? PersonId,
        string? Name,
        IReadOnlyList<PersonRecord> Candidates);

    // Loads the owner's active (non-archived) named people with face counts.
    // Owner-scoped; unnamed people are excluded (nothing to match text against).
    public async Task<IReadOnlyList<PersonRecord>> LoadPeopleAsync(
        Guid ownerUserId, CancellationToken cancellationToken = default)
    {
        var rows = await _db.People.AsNoTracking()
            .Where(p => p.OwnerUserId == ownerUserId && !p.IsArchived && p.DisplayName != null)
            .Select(p => new
            {
                p.Id,
                p.DisplayName,
                FaceCount = _db.PersonFaceAssignments.Count(a =>
                    a.OwnerUserId == ownerUserId && a.PersonId == p.Id),
            })
            .ToListAsync(cancellationToken);

        return rows.Select(r => new PersonRecord(r.Id, r.DisplayName, r.FaceCount)).ToList();
    }

    // Pure resolution against an already-loaded owner snapshot (testable without
    // a database). `people` MUST already be owner-scoped by the caller.
    public static PersonResolution Resolve(
        IReadOnlyList<PersonRecord> people, string text, string mode)
    {
        var needle = Normalize(text);
        if (needle.Length == 0)
        {
            return new PersonResolution(text, mode, ResolutionStatus.Unresolved, null, null,
                Array.Empty<PersonRecord>());
        }

        // 1. Exact normalised match.
        var exact = people.Where(p => p.Normalized == needle).ToList();
        if (exact.Count == 1)
        {
            return Resolved(text, mode, exact[0]);
        }
        if (exact.Count > 1)
        {
            return Ambiguous(text, mode, exact);
        }

        // 3. Conservative fuzzy. First try "first token" exact (e.g. the user
        // typed "Anna" and the person is "Anna Rossi"): a name whose first
        // whitespace-delimited token equals the needle.
        var firstToken = people.Where(p => FirstToken(p.Normalized) == needle).ToList();
        if (firstToken.Count == 1)
        {
            return Resolved(text, mode, firstToken[0]);
        }
        if (firstToken.Count > 1)
        {
            return Ambiguous(text, mode, firstToken);
        }

        // Then a single prefix or edit-distance-1 match — only accept when
        // exactly one candidate qualifies (never guess between several).
        var fuzzy = people
            .Where(p => p.Normalized.StartsWith(needle, StringComparison.Ordinal)
                || needle.StartsWith(p.Normalized, StringComparison.Ordinal)
                || LevenshteinAtMost(p.Normalized, needle, 1))
            .ToList();
        if (fuzzy.Count == 1)
        {
            return Resolved(text, mode, fuzzy[0]);
        }
        if (fuzzy.Count > 1)
        {
            return Ambiguous(text, mode, fuzzy);
        }

        return new PersonResolution(text, mode, ResolutionStatus.Unresolved, null, null,
            Array.Empty<PersonRecord>());
    }

    private static PersonResolution Resolved(string text, string mode, PersonRecord p) =>
        new(text, mode, ResolutionStatus.Resolved, p.Id, p.DisplayName, new[] { p });

    private static PersonResolution Ambiguous(string text, string mode, IReadOnlyList<PersonRecord> c) =>
        new(text, mode, ResolutionStatus.Ambiguous, null, null,
            c.OrderByDescending(p => p.FaceCount).ThenBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase).ToList());

    // Trim → collapse internal whitespace → lower-invariant → strip diacritics.
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var collapsed = string.Join(' ', value.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var lowered = collapsed.ToLowerInvariant();
        var decomposed = lowered.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(ch);
            }
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    private static string FirstToken(string normalized)
    {
        var idx = normalized.IndexOf(' ');
        return idx < 0 ? normalized : normalized[..idx];
    }

    // Bounded Levenshtein: returns true iff edit distance <= max (early-exits).
    private static bool LevenshteinAtMost(string a, string b, int max)
    {
        if (Math.Abs(a.Length - b.Length) > max) return false;
        if (a.Length == 0) return b.Length <= max;
        if (b.Length == 0) return a.Length <= max;

        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            var rowMin = curr[0];
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(prev[j] + 1, curr[j - 1] + 1), prev[j - 1] + cost);
                rowMin = Math.Min(rowMin, curr[j]);
            }
            if (rowMin > max) return false;
            (prev, curr) = (curr, prev);
        }
        return prev[b.Length] <= max;
    }
}
