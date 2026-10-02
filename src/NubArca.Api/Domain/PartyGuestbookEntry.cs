namespace NubArca.Api.Domain;

/// <summary>
/// A MEMORY left in the party's guest book: one photograph from the party's
/// album, framed, with a dedication and the name of whoever wrote it — the
/// third, and last, thing a guest may contribute.
///
/// <para><b>It is deliberately not a <see cref="PartyMessage"/>, and no flag on
/// one.</b> The two answer different questions. A message is written to be READ
/// OUT: it is short, it competes for the room's attention, and it stops
/// mattering when the party ends. A guest book memory is written to be KEPT: it
/// is longer, it carries a photograph, and it is worth having the morning after.
/// Giving one row a boolean would make every query about either of them a query
/// about both.</para>
///
/// <para><b>Scope is the PARTY, not the link.</b> This is the one place the
/// guest book departs from <see cref="PartyMessage"/>, and it departs
/// deliberately. A message belongs to one QR because last year's greetings must
/// not reappear at this year's party. A guest book is the opposite: it is the
/// book of the EVENT, and revoking and re-minting a QR in the middle of an
/// evening — which the product does routinely — must not start a second book.
/// The link the entry arrived through is kept beside it as provenance and is
/// authoritative for nothing.</para>
///
/// <para><b>The photograph is the memory's own.</b> The guest chooses it from
/// the party's main album, but the album is only where it was FOUND: at
/// publication the entry acquires its own reference to the same content-
/// addressed blob (<see cref="BlobObjectId"/>, through
/// <c>IBlobService.AcquireExistingAsync</c>, in the transaction that inserts
/// the row). Nothing here names the album file, so removing the photograph
/// from the album — or deleting the file — leaves the memory exactly as it
/// was, and the blob alive for as long as the memory holds it. Deleting the
/// memory releases that reference and the blob goes back to the ordinary
/// lifecycle.</para>
///
/// <para><b>Everything needed to draw it is on the row</b> — the photograph's
/// shape, the framing, the template and its version — so a surface that
/// renders the book (the guest page today, a television later) reads this
/// table and nothing else. Nothing on the row is a pixel or a URL.</para>
/// </summary>
public class PartyGuestbookEntry
{
    public Guid Id { get; set; }

    /// <summary>
    /// The event this dedication was left at. Required, and the reason the book
    /// survives a QR rotation.
    /// </summary>
    public Guid PartyId { get; set; }

    /// <summary>
    /// The party's owner at submission time. A projection of
    /// <see cref="Party.OwnerUserId"/>, kept for the same reason
    /// <see cref="PartyMessage.OwnerUserId"/> is: every owner-side listing is
    /// already owner-scoped, and joining the party on each read would be churn
    /// with no behaviour change.
    /// </summary>
    public Guid OwnerUserId { get; set; }

    /// <summary>
    /// Which QR the entry came in on, when known. PROVENANCE, never authority:
    /// nothing filters on it, and a revoked link does not hide a dedication
    /// somebody left. Nullable so a future non-QR entry point needs no backfill.
    /// </summary>
    public Guid? PartyAlbumLinkId { get; set; }

    /// <summary>
    /// Which participant session wrote it, when known. Owner-private, NEVER in
    /// a DTO — it exists so one abusive guest's whole run can be found during
    /// an incident, exactly as on <see cref="PartyMessage"/>.
    /// </summary>
    public Guid? PartyParticipantId { get; set; }

    /// <summary>
    /// The signature the guest typed. Required: a memory in the book is signed
    /// by somebody. Normalised, one line.
    /// </summary>
    public string AuthorDisplayName { get; set; } = string.Empty;

    /// <summary>
    /// The dedication. Normalised plain text (see <see cref="PartyGuestbookText"/>)
    /// that keeps its paragraphs: never HTML, never Markdown, never a link
    /// anything is expected to activate.
    /// </summary>
    public string Body { get; set; } = string.Empty;

    /// <summary>
    /// THE MEMORY'S OWN REFERENCE to the photograph's original, content-
    /// addressed bytes. One reference per row, acquired at publication and
    /// released only when the row is deleted — never through the album file the
    /// photograph was chosen from. Owner-private, never in a DTO.
    /// </summary>
    public Guid BlobObjectId { get; set; }

    /// <summary>
    /// The derived, metadata-free preview the book is drawn with, or null until
    /// somebody first asks for it. REGENERABLE CACHE, like every derivative:
    /// it is rendered from <see cref="BlobObjectId"/>, owns one reference to its
    /// own derived blob, and is redrawn if its bytes are ever lost. A guest is
    /// never served the original. Owner-private, never in a DTO.
    /// </summary>
    public Guid? PreviewBlobObjectId { get; set; }

    /// <summary>
    /// The photograph's DISPLAY size — after its EXIF orientation — taken when
    /// the memory was published. Its shape is what the framing is measured
    /// against, so it is kept here rather than looked up on a file the memory
    /// does not depend on.
    /// </summary>
    public int PhotoWidth { get; set; }

    public int PhotoHeight { get; set; }

    /// <summary>
    /// Which design the memory is drawn with — one of
    /// <see cref="PartyGuestbookTemplates"/> — and in which VERSION. The client
    /// names the key; the server decides the version, so a later redesign of a
    /// template never silently changes a memory somebody already published.
    /// </summary>
    public string TemplateKey { get; set; } = PartyGuestbookTemplates.NubArca;

    public int TemplateVersion { get; set; } = 1;

    /// <summary>
    /// The framing — semantically a PHOTO PLACEMENT (see <c>PhotoPlacementGeometry</c>),
    /// still called "crop" on the wire and in the columns, which are not renamed:
    /// the centre of what shows as fractions (0..1) of the photograph, and a zoom
    /// relative to the photograph just covering the design's frame — above 1 in,
    /// down to the whole photograph inside the frame out (the frame's well then
    /// shows beside it), at most <see cref="PartyGuestContentMediaOrientations.MaxZoom"/>.
    /// A memory stored at zoom ≥ 1 draws exactly as it always did. Independent of
    /// any pixel and any screen, which is what lets a phone and a television
    /// draw the same memory.
    /// </summary>
    public double CropCenterX { get; set; } = 0.5;

    public double CropCenterY { get; set; } = 0.5;

    public double CropZoom { get; set; } = 1;

    /// <summary>
    /// One of <see cref="PartyMessageStatuses"/>. The vocabulary and the state
    /// machine are SHARED with the party's other contributions on purpose:
    /// "what can happen to something a guest left" has one answer in this
    /// product, and a second moderation framework would be two places to read it
    /// and two places for it to drift.
    /// </summary>
    public string Status { get; set; } = PartyMessageStatuses.Visible;

    public DateTime CreatedAt { get; set; }

    public DateTime? ModeratedAt { get; set; }

    /// <summary>Owner-private; never in a DTO.</summary>
    public Guid? ModeratedByUserId { get; set; }

    public DateTime UpdatedAt { get; set; }

    /// <summary>Only a Visible entry is ever shown to a guest.</summary>
    public bool IsPublic => Status == PartyMessageStatuses.Visible;
}

/// <summary>
/// What a dedication is, and how long it may be.
///
/// <para>Longer than a greeting because it is a different act: a message is
/// read out over music, a dedication is read afterwards, alone. Counted in
/// Unicode code points for the reason <see cref="PartyMessageText"/> gives at
/// length — it is the one unit .NET and a browser agree on exactly.</para>
/// </summary>
public static class PartyGuestbookLimits
{
    public const int MaxAuthorDisplayNameLength = 80;
    public const int MaxBodyLength = 1000;
}

/// <summary>
/// The guest book's text rules, which are the party's text rules with the guest
/// book's limits.
///
/// <para>Normalisation is <see cref="PartyMessageText.Normalize"/> itself, not
/// a copy of it. That is where the security-relevant part lives — the bidi
/// overrides that make stored text render as something other than what it says,
/// the zero-width padding, the control characters — and there must be exactly
/// one implementation of it. What differs is only how much text is allowed, so
/// only that is stated here.</para>
///
/// <para>A dedication keeps its PARAGRAPHS, through
/// <see cref="PartyMessageText.NormalizeMultiline"/> — which is that same
/// normaliser applied line by line, not a second one. The signature stays on
/// one line, and is required.</para>
/// </summary>
public static class PartyGuestbookText
{
    /// <summary>
    /// The signature: normalised, non-empty, within the limit. False when it is
    /// missing or too long — a name over the limit is a refusal, never a silent
    /// truncation of what somebody calls themselves.
    /// </summary>
    public static bool TryNormalizeAuthor(string? value, out string normalized)
    {
        normalized = PartyMessageText.Normalize(value);
        return normalized.Length > 0
            && PartyMessageText.Length(normalized) <= PartyGuestbookLimits.MaxAuthorDisplayNameLength;
    }

    /// <summary>
    /// The dedication: normalised with its paragraphs, non-empty, within the
    /// limit. There is no such thing as a blank dedication.
    /// </summary>
    public static bool TryNormalizeBody(string? value, out string normalized)
    {
        normalized = PartyMessageText.NormalizeMultiline(value);
        if (normalized.Length == 0)
        {
            return false;
        }

        return PartyMessageText.Length(normalized) <= PartyGuestbookLimits.MaxBodyLength;
    }
}


/// <summary>
/// The designs a memory may be drawn with, and the version of each that a NEW
/// memory gets.
///
/// <para>The client names a key and nothing more. The SERVER decides the
/// version, from this table, and stores both — so introducing <c>polaroid</c>
/// version 2 one day changes what the next guest gets and leaves every memory
/// already in a book exactly as its author saw it. A renderer for a version is
/// therefore never removed while a row still names it.</para>
///
/// <para>The keys are a closed vocabulary because the clients draw them: a key
/// no client knows would be a memory nobody can render.</para>
/// </summary>
public static class PartyGuestbookTemplates
{
    public const string NubArca = "nubarca";
    public const string Polaroid = "polaroid";
    public const string Editorial = "editorial";
    public const string Celebration = "celebration";

    /// <summary>The longest key the column holds.</summary>
    public const int MaxKeyLength = 32;

    /// <summary>What a memory published NOW is drawn with: key → current version.</summary>
    private static readonly IReadOnlyDictionary<string, int> Publishable =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [NubArca] = 1,
            [Polaroid] = 1,
            [Editorial] = 1,
            [Celebration] = 1,
        };

    public static IReadOnlyCollection<string> Keys => (IReadOnlyCollection<string>)Publishable.Keys;

    /// <summary>
    /// The version a new memory drawn with <paramref name="key"/> gets, or
    /// false for a key nobody may publish with. Exact, case-sensitive match:
    /// the vocabulary is ASCII lower case and nothing is guessed.
    /// </summary>
    public static bool TryCurrentVersion(string? key, out int version)
    {
        version = 0;
        return key is not null && Publishable.TryGetValue(key, out version);
    }

    /// <summary>
    /// The photograph's frame (width / height) in the design <paramref name="key"/>
    /// at <paramref name="version"/>, for a photograph of <paramref name="photoAspect"/> —
    /// the same table as every client's template registry (the shared
    /// <c>photoPlacement.cases.json</c> holds them equal). The server needs it for
    /// one thing: how far a guest may zoom OUT, which depends on the frame.
    /// A pair it does not know falls back as the clients do: the same key's
    /// current design, else the default.
    /// </summary>
    public static double FrameAspect(string key, int version, double photoAspect)
    {
        var rule = Frames.TryGetValue($"{key}@{version}", out var exact)
            ? exact
            : Frames.TryGetValue($"{key}@{(Publishable.TryGetValue(key, out var current) ? current : 0)}", out var latest)
                ? latest
                : Frames[$"{NubArca}@1"];
        return photoAspect > 1.02 ? rule.Landscape : photoAspect < 0.98 ? rule.Portrait : rule.Square;
    }

    private static readonly IReadOnlyDictionary<string, (double Landscape, double Portrait, double Square)> Frames =
        new Dictionary<string, (double, double, double)>(StringComparer.Ordinal)
        {
            [$"{NubArca}@1"] = (4.0 / 3, 4.0 / 5, 1),
            [$"{Polaroid}@1"] = (1, 1, 1),
            [$"{Editorial}@1"] = (3.0 / 2, 3.0 / 4, 1),
            [$"{Celebration}@1"] = (4.0 / 3, 4.0 / 5, 1),
        };
}

/// <summary>
/// A photograph's shape in the one vocabulary every client reads, so the web,
/// the phone and a television agree on where "square" ends.
/// </summary>
public static class PartyGuestbookPhotoShape
{
    public const string Portrait = "portrait";
    public const string Landscape = "landscape";
    public const string Square = "square";

    public static string Orientation(int width, int height) =>
        width > height ? Landscape : width < height ? Portrait : Square;
}
