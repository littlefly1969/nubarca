using System.Buffers.Text;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NubArca.Api.Files;

// Opaque cursor for slice-60 gallery pagination. Encodes the sort field +
// direction + the boundary row's primary sort value + the FileItem id (for
// the deterministic tie-breaker). The wire form is a base64url string of a
// compact JSON document; it is intentionally opaque to clients so the schema
// can evolve without breaking them. The encoded sort/direction is checked
// against the request's sort/direction so an old cursor under a new sort
// gives an explicit 400 rather than nonsense pagination.
//
// The id field IS exposed as the public FileItem id (which appears in every
// gallery DTO + URL) — there is no "internal" id hidden inside.
public sealed record ImageCursor(
    ImageSortField Sort,
    ImageSortDirection Direction,
    // Boundary value for the primary sort key, serialised by type:
    //   * Created / DateTaken → DateTime (ticks-precision UTC instant)
    //   * Name                → string
    //   * Size                → long
    [property: JsonPropertyName("vk")] string PrimaryKind,
    [property: JsonPropertyName("vs")] string? PrimaryString,
    [property: JsonPropertyName("vn")] long? PrimaryNumber,
    [property: JsonPropertyName("vd")] DateTime? PrimaryDate,
    [property: JsonPropertyName("i")] Guid Id)
{
    public const string KindDate = "d";
    public const string KindString = "s";
    public const string KindNumber = "n";
    // Slice 100: semantic-relevance ordering. The primary key is a descending
    // cosine score (double, JSON-native round-trip) with the FileItem id as the
    // deterministic ascending tie-break. Only produced/consumed on the
    // physical-filter-first semantic-ranked gallery path.
    public const string KindScore = "c";

    // Boundary cosine score for KindScore cursors (null for all other kinds).
    [JsonPropertyName("vc")]
    public double? PrimaryScore { get; init; }

    // Slice 61: bind a cursor to the (q + filters + folderId) fingerprint
    // under which it was issued. Mismatch → 400. Older cursors that predate
    // this field deserialise with Filter=null; the endpoint treats null as
    // "no filter binding", which only matches an unfiltered request.
    [JsonPropertyName("f")]
    public string? Filter { get; init; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static ImageCursor FromDate(
        ImageSortField sort, ImageSortDirection direction, DateTime value, Guid id, string? filter = null)
        => new ImageCursor(sort, direction, KindDate, null, null, DateTime.SpecifyKind(value, DateTimeKind.Utc), id)
        { Filter = filter };

    public static ImageCursor FromString(
        ImageSortField sort, ImageSortDirection direction, string value, Guid id, string? filter = null)
        => new ImageCursor(sort, direction, KindString, value, null, null, id) { Filter = filter };

    public static ImageCursor FromNumber(
        ImageSortField sort, ImageSortDirection direction, long value, Guid id, string? filter = null)
        => new ImageCursor(sort, direction, KindNumber, null, value, null, id) { Filter = filter };

    // Semantic-relevance boundary: (score desc, id asc). Sort/direction are
    // pinned to Created/Desc as inert placeholders — the semantic path binds the
    // cursor by its own fingerprint (filter + semantic query + profile + Top-K),
    // not by the metadata sort vocabulary, so these are never used to order.
    public static ImageCursor FromScore(double score, Guid id, string? filter = null)
        => new ImageCursor(ImageSortField.Created, ImageSortDirection.Desc, KindScore, null, null, null, id)
        { Filter = filter, PrimaryScore = score };

    public string Encode()
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(this, JsonOptions);
        return Base64Url(bytes);
    }

    // Decodes a base64url-encoded cursor. Returns false on any failure mode
    // (not base64, not JSON, missing fields, wrong kind) so the endpoint
    // can map malformed cursors to a single 400 without leaking internals.
    public static bool TryParse(
        string? encoded,
        out ImageCursor cursor)
    {
        cursor = default!;
        if (string.IsNullOrWhiteSpace(encoded))
        {
            return false;
        }

        try
        {
            var bytes = FromBase64Url(encoded);
            var parsed = JsonSerializer.Deserialize<ImageCursor>(bytes, JsonOptions);
            if (parsed is null) return false;

            // Type-of-primary sanity check: the kind discriminator must agree
            // with which primary field is populated.
            var hasDate = parsed.PrimaryDate.HasValue;
            var hasString = parsed.PrimaryString is not null;
            var hasNumber = parsed.PrimaryNumber.HasValue;
            var hasScore = parsed.PrimaryScore.HasValue;
            var ok = parsed.PrimaryKind switch
            {
                KindDate => hasDate && !hasString && !hasNumber && !hasScore,
                KindString => hasString && !hasDate && !hasNumber && !hasScore,
                KindNumber => hasNumber && !hasDate && !hasString && !hasScore,
                KindScore => hasScore && !hasDate && !hasString && !hasNumber,
                _ => false,
            };
            if (!ok) return false;

            cursor = parsed;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool MatchesSort(ImageSortField sort, ImageSortDirection direction)
        => Sort == sort && Direction == direction;

    // Slice 61: cursor + filter binding. A null Filter on either side
    // (old cursor pre-slice-61, or unfiltered current request) is treated
    // as "" so equivalent un-filtered requests match.
    public bool MatchesFilter(string? currentFingerprint)
        => string.Equals(Filter ?? string.Empty, currentFingerprint ?? string.Empty,
            StringComparison.Ordinal);

    private static string Base64Url(byte[] data)
    {
        var s = Convert.ToBase64String(data);
        return s.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static byte[] FromBase64Url(string s)
    {
        var t = s.Replace('-', '+').Replace('_', '/');
        var pad = t.Length % 4;
        if (pad == 2) t += "==";
        else if (pad == 3) t += "=";
        else if (pad != 0) throw new FormatException("Invalid base64url length.");
        return Convert.FromBase64String(t);
    }
}

// Result of one gallery page (slice 60). `NextCursor` is null when there are
// no further pages — clients should treat null as "end of list". `HasMore`
// is the same signal as `nextCursor != null`, exposed explicitly for clients
// that prefer a boolean check.
//
// `TotalCount` is the server-authoritative number of items matching the CURRENT
// filter set (the same parsed filters that produced `Items`, minus cursor seek
// / ordering / paging). It reflects duplicate-collapsed rows when collapsing is
// requested, and is stable across the pages of one query — so a client can show
// "position / total" without loading every page. See ListMediaRowsAsync.
public sealed record ImagePage(
    IReadOnlyList<ImageItem> Items,
    string? NextCursor,
    bool HasMore,
    int TotalCount);

// A physically filtered gallery candidate: the logical FileItem id plus its
// content-addressed blob id (used to look up the blob's embedding vector for
// semantic ranking). Owner-private; carries no storage path / SHA / metadata.
public sealed record GalleryCandidateRef(Guid Id, Guid BlobObjectId);
