namespace NubArca.Api.Files;

// Single source of truth for the "what the user actually sees" media name.
//
// The owner-scoped FileItemUserMetadata.Title (when set) replaces the file name
// in every media surface — cards, viewer header, aria labels. It is a LABEL, not
// a rename: FileItem.Name stays the logical file name and keeps driving the
// path, the extension, downloads and diagnostics. Title never touches the blob.
//
// MetadataService.NormalizeText already stores a whitespace-only title as NULL,
// so the SQL-side rule (COALESCE(title, name)) and this in-memory rule agree on
// every row written through the metadata endpoint. IsNullOrWhiteSpace is kept
// here as the defensive form for rows that predate that normalisation.
public static class MediaDisplayName
{
    public static string Resolve(string? title, string name)
        => string.IsNullOrWhiteSpace(title) ? name : title;

    // Ordering/seek key for sort=name. Lower-cased so the gallery orders the way
    // the user reads it (case-insensitively) and identically on PostgreSQL and
    // SQLite, whose default ORDER BY collations disagree on case. The cursor
    // stores this exact key, so the seek predicate and the ORDER BY always
    // compare the same value.
    public static string SortKey(string? title, string name)
        => Resolve(title, name).ToLowerInvariant();
}
