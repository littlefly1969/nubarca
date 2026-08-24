using System.Text.Json;
using System.Text.Json.Serialization;

namespace NubArca.Api.Albums.Sharing;

// Opaque keyset cursor for one page of a shared album's items.
//
// The album's CURATED order is the only order a recipient ever sees, so the
// boundary is exactly the pair that order is built from: `(SortOrder,
// FileItemId)`. Nothing else is encoded — no owner id, no blob id, no storage
// fact — and `FileItemId` is already in every shared item DTO and in every
// album-scoped media URL, so a decoded cursor tells its holder nothing they
// were not already told.
//
// The requested KIND is bound into the cursor for the same reason the gallery
// binds its filter fingerprint: a cursor issued while browsing Videos would
// otherwise resume a Photos listing at a boundary that means something else
// there. A mismatch is an explicit 400, never silent nonsense.
//
// A cursor is not a capability. Replaying one from another account still meets
// the membership check on the request that carries it.
public sealed record SharedAlbumItemCursor(
    [property: JsonPropertyName("k")] string Kind,
    [property: JsonPropertyName("o")] int SortOrder,
    [property: JsonPropertyName("i")] Guid FileItemId)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string Encode() => Base64Url(JsonSerializer.SerializeToUtf8Bytes(this, JsonOptions));

    // Every failure mode — not base64, not JSON, missing fields, a kind that is
    // not in the vocabulary — is one `false`, so the endpoint answers a single
    // 400 and no shape of a malformed cursor is distinguishable from another.
    public static bool TryParse(string? encoded, out SharedAlbumItemCursor cursor)
    {
        cursor = default!;
        if (string.IsNullOrWhiteSpace(encoded))
        {
            return false;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<SharedAlbumItemCursor>(
                FromBase64Url(encoded), JsonOptions);
            if (parsed is null || !SharedAlbumItemKinds.IsKnown(parsed.Kind))
            {
                return false;
            }

            cursor = parsed;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool MatchesKind(string kind) => string.Equals(Kind, kind, StringComparison.Ordinal);

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

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
