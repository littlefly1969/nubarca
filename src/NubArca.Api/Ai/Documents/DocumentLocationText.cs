namespace NubArca.Api.Ai.Documents;

/// The typed chunk locator, said in plain English for a prompt.
///
/// One rendering, so nothing server-side disagrees about what "page" means. The
/// browser renders the same typed fields in its own language and never parses
/// this string back.
public static class DocumentLocationText
{
    /// "page 4", "slide 7", "sheet "Budget"", "section 3" — or null when the
    /// chunk has no position (native text). `Page` wins because it is the one
    /// field that is always a real PDF page.
    public static string? Describe(int? page, string? kind, int? index, string? label)
    {
        if (page is { } p) return $"page {p}";
        if (string.IsNullOrWhiteSpace(kind) || index is null) return null;
        return kind switch
        {
            DocumentLocatorKinds.Page => $"page {index}",
            DocumentLocatorKinds.Slide => $"slide {index}",
            DocumentLocatorKinds.Sheet => string.IsNullOrWhiteSpace(label)
                ? $"sheet {index}"
                : $"sheet \"{label}\"",
            DocumentLocatorKinds.Section => $"section {index}",
            _ => null,
        };
    }
}
