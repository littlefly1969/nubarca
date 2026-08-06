using System.Text.Json;
using System.Text.Json.Serialization;
using NubArca.Api.Files;

namespace NubArca.Api.Folders;

// ---------------------------------------------------------------------------
// Files UI v2 (directory listing): sort + seek pagination for a folder's
// contents. Folders are returned in full on the first page (their count is
// bounded in practice); files are seek-paginated so a directory with many
// files never forces an unbounded scan or a full client reload.
// ---------------------------------------------------------------------------

// Allowed sort fields for GET /api/folders[/{id}]/children.
public enum DirectorySortField
{
    Name,
    Created,
    Size,
    Type,
}

public enum DirectorySortDirection
{
    Asc,
    Desc,
}

public static class DirectoryListingDefaults
{
    public const int DefaultLimit = 200;
    public const int MaxLimit = 500;
}

// Parser shared between the endpoint and tests. Returns false on unknown values
// so the endpoint can map them to 400 (mirrors ImageSort for the gallery).
public static class DirectorySort
{
    public const DirectorySortField DefaultField = DirectorySortField.Name;
    public const DirectorySortDirection DefaultDirection = DirectorySortDirection.Asc;

    public static bool TryParseField(string? raw, out DirectorySortField field)
    {
        switch (raw?.Trim().ToLowerInvariant())
        {
            case null: case "": field = DefaultField; return true;
            case "name": field = DirectorySortField.Name; return true;
            case "created": field = DirectorySortField.Created; return true;
            case "size": field = DirectorySortField.Size; return true;
            case "type": field = DirectorySortField.Type; return true;
            default: field = DefaultField; return false;
        }
    }

    public static bool TryParseDirection(string? raw, out DirectorySortDirection direction)
    {
        switch (raw?.Trim().ToLowerInvariant())
        {
            case null: case "": direction = DefaultDirection; return true;
            case "asc": direction = DirectorySortDirection.Asc; return true;
            case "desc": direction = DirectorySortDirection.Desc; return true;
            default: direction = DefaultDirection; return false;
        }
    }
}

// One page of files within a folder. `NextCursor` is null at the end of the
// list; `HasMore` is the same signal exposed as a boolean.
public sealed record DirectoryFilesPage(
    IReadOnlyList<FileSummary> Files,
    string? NextCursor,
    bool HasMore);

// Opaque seek cursor for the directory file listing. Encodes the sort field +
// direction + the boundary row's primary sort value + the FileItem id (the
// deterministic tie-breaker) + the parent-folder scope it was issued under.
// The wire form is a base64url string of a compact JSON document, intentionally
// opaque to clients. The encoded sort/direction/scope are checked against the
// request so a stale cursor produces an explicit 400 rather than nonsense
// pagination. The id IS the public FileItem id (already in every DTO + URL) —
// there is no internal id hidden inside. Mirrors ImageCursor.
public sealed record DirectoryCursor(
    DirectorySortField Sort,
    DirectorySortDirection Direction,
    [property: JsonPropertyName("vk")] string PrimaryKind,
    [property: JsonPropertyName("vs")] string? PrimaryString,
    [property: JsonPropertyName("vn")] long? PrimaryNumber,
    [property: JsonPropertyName("vd")] DateTime? PrimaryDate,
    [property: JsonPropertyName("i")] Guid Id,
    [property: JsonPropertyName("sc")] string Scope)
{
    public const string KindDate = "d";
    public const string KindString = "s";
    public const string KindNumber = "n";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // Folder scope token. A cursor issued while listing folder A can never be
    // replayed against folder B (or the root), so paging always stays within
    // the directory it started in.
    public static string ScopeFor(Guid? parentFolderId)
        => parentFolderId?.ToString("N") ?? "root";

    public static DirectoryCursor FromDate(
        DirectorySortField sort, DirectorySortDirection direction, DateTime value, Guid id, string scope)
        => new(sort, direction, KindDate, null, null, DateTime.SpecifyKind(value, DateTimeKind.Utc), id, scope);

    public static DirectoryCursor FromString(
        DirectorySortField sort, DirectorySortDirection direction, string value, Guid id, string scope)
        => new(sort, direction, KindString, value, null, null, id, scope);

    public static DirectoryCursor FromNumber(
        DirectorySortField sort, DirectorySortDirection direction, long value, Guid id, string scope)
        => new(sort, direction, KindNumber, null, value, null, id, scope);

    public string Encode()
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(this, JsonOptions);
        return Base64Url(bytes);
    }

    // Returns false on any failure mode (not base64, not JSON, missing fields,
    // wrong kind) so the endpoint can map malformed cursors to a single 400.
    public static bool TryParse(string? encoded, out DirectoryCursor cursor)
    {
        cursor = default!;
        if (string.IsNullOrWhiteSpace(encoded))
        {
            return false;
        }

        try
        {
            var bytes = FromBase64Url(encoded);
            var parsed = JsonSerializer.Deserialize<DirectoryCursor>(bytes, JsonOptions);
            if (parsed is null) return false;
            if (string.IsNullOrEmpty(parsed.Scope)) return false;

            var hasDate = parsed.PrimaryDate.HasValue;
            var hasString = parsed.PrimaryString is not null;
            var hasNumber = parsed.PrimaryNumber.HasValue;
            var ok = parsed.PrimaryKind switch
            {
                KindDate => hasDate && !hasString && !hasNumber,
                KindString => hasString && !hasDate && !hasNumber,
                KindNumber => hasNumber && !hasDate && !hasString,
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

    public bool MatchesSort(DirectorySortField sort, DirectorySortDirection direction)
        => Sort == sort && Direction == direction;

    public bool MatchesScope(Guid? parentFolderId)
        => string.Equals(Scope, ScopeFor(parentFolderId), StringComparison.Ordinal);

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
