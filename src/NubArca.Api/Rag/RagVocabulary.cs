namespace NubArca.Api.Rag;

/// What kind of document a source is. Every owner-private document is
/// `documentation`; the field stays so evidence carries an explicit kind rather
/// than an empty string.
public static class RagSourceKinds
{
    public const string Documentation = "documentation";
}

/// Natural language of a source's prose.
public static class RagLanguages
{
    public const string Italian = "it";
    public const string English = "en";
    public const string Unknown = "";
}
