namespace NubArca.Api.Domain;

/// <summary>
/// One TYPED slot of what a party tells its guests: the invitation, where it
/// is, what to wear, what there is to eat, one note, and the thank-you.
///
/// <para><b>This is deliberately not a page builder.</b> There is no slug, no
/// sort order, no block list, no component registry, no HTML and no Markdown —
/// and at most ONE slot per kind, which the composite key enforces. The order
/// the guest sees is a product decision
/// (<see cref="PartyGuestContentKinds.All"/>), not data somebody drags around.
/// A party has a handful of things to say, and saying them as six named shapes
/// is smaller, safer and better-looking than a CMS that could say anything.</para>
///
/// <para><see cref="ContentJson"/> is validated SERVER-SIDE against the shape
/// its <see cref="Kind"/> declares and re-serialized canonically, so what is
/// stored is what was checked — a browser that knows the route still cannot
/// persist arbitrary JSON.</para>
/// </summary>
public class PartyGuestContent
{
    public Guid PartyId { get; set; }

    /// <summary>One of <see cref="PartyGuestContentKinds"/>.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>
    /// The host's master switch for this slot. A disabled slot is absent from
    /// the guest context entirely — never an empty card, never a heading with
    /// nothing under it.
    /// </summary>
    public bool Enabled { get; set; }

    // Which PHASES this slot belongs to. Three booleans rather than a set,
    // because there are exactly three surfaces and there will not be a fourth
    // without a slice that decides what it means. Defaults come from
    // PartyGuestContentKinds.DefaultsFor: the menu belongs on the invitation and
    // at the party, the thank-you only afterwards.
    public bool VisibleBefore { get; set; }
    public bool VisibleLive { get; set; }
    public bool VisibleAfter { get; set; }

    /// <summary>The validated, canonically re-serialized payload for this kind.</summary>
    public string ContentJson { get; set; } = "{}";

    /// <summary>
    /// The one photograph this slot may carry — the menu's food detail, the
    /// venue's front door — or null.
    ///
    /// <para>A REFERENCE to the owner's ordinary <see cref="FileItem"/>: not a
    /// copy, and not an album membership. It lives beside
    /// <see cref="ContentJson"/> rather than inside it because it is a relation
    /// the database holds to account — it becomes null when the file is
    /// permanently deleted — and text a payload validator re-serializes is not.
    /// Whether a guest may SEE it is decided on every request by
    /// <c>PartyMediaReference</c>: a file sent to Trash or into the Private
    /// Vault keeps its id here and simply stops being served.</para>
    /// </summary>
    public Guid? MediaFileItemId { get; set; }

    /// <summary>
    /// HOW that photograph participates in the guest's surface — one of
    /// <see cref="PartyGuestContentMediaPresentations"/>.
    ///
    /// <para>A slot answers three independent questions, and this is the third:
    /// <see cref="ContentJson"/> is what it SAYS, <see cref="MediaFileItemId"/>
    /// is WHICH image it uses, and this is HOW that image is presented. It lives
    /// as a column rather than inside the payload because it is structural — the
    /// surface reads it to decide what to render before it reads a word — and
    /// because a payload validator re-serializes its shape per kind, which would
    /// make one presentation rule six.</para>
    ///
    /// <para><c>inline</c> is the default and is what every P4 row means: the
    /// photograph sits in the slot's composition, above its words. <c>poster</c>
    /// means the photograph IS the document — the surface offers a deterministic
    /// navigation affordance and opens it whole, which is what lets a host use a
    /// 1080x1920 menu graphic without it being cropped into a banner.</para>
    ///
    /// <para>It is not a second way to hide text: a slot switched to
    /// <c>poster</c> keeps every word it had, and switching back restores them
    /// exactly. And it grants nothing — the same reference, resolved by the same
    /// <c>PartyMediaReference</c> rule, served by the same relation-scoped
    /// route.</para>
    /// </summary>
    public string MediaPresentation { get; set; } = PartyGuestContentMediaPresentations.Inline;

    /// <summary>
    /// How the slot's WORDS are aligned — one of
    /// <see cref="PartyGuestContentTextAligns"/> — or null for the surface's own
    /// default.
    ///
    /// <para>A fourth independent answer, a column for the same reason
    /// <see cref="MediaPresentation"/> is: it is structural, read before a word
    /// is rendered, and one rule across six payload shapes rather than six. Null
    /// rather than a stored default because the right default differs by WHERE
    /// the words land — a section reads left, the thank-you sits centred in its
    /// hero — and every row written before the choice existed must keep looking
    /// exactly as it did.</para>
    /// </summary>
    public string? TextAlign { get; set; }

    /// <summary>
    /// How the INLINE photograph is framed in its section — one of
    /// <see cref="PartyGuestContentMediaOrientations"/> — or null for the whole
    /// photograph at its own proportions.
    ///
    /// <para>Null is the default because a fixed band is a choice, not a
    /// given: a portrait photograph cropped into a landscape strip is exactly
    /// what a host could not undo before this existed. A fixed format crops,
    /// and the three numbers below say where — the same zoom and centre the
    /// party print's crop editor stores, with the same limits.</para>
    /// </summary>
    public string? MediaOrientation { get; set; }

    /// <summary>How far in, from 1 (the frame filled) to <see cref="PartyGuestContentMediaOrientations.MaxZoom"/>.</summary>
    public double? MediaCropZoom { get; set; }

    /// <summary>The centre of what shows, as fractions of the photograph (0..1).</summary>
    public double? MediaCropCenterX { get; set; }

    /// <summary>The centre of what shows, as fractions of the photograph (0..1).</summary>
    public double? MediaCropCenterY { get; set; }

    /// <summary>
    /// Where the slot's words sit when it has an inline photograph — one of
    /// <see cref="PartyGuestContentTextPlacements"/> — or null for below it.
    /// "overlay" lays them across the lower part of the picture, the way the
    /// cover carries the party's name, while the picture keeps its frame.
    /// </summary>
    public string? TextPlacement { get; set; }

    /// <summary>
    /// Optimistic concurrency for THIS slot.
    ///
    /// <para>Its own, not the root's: editing the menu and renaming the party
    /// are unrelated decisions, and making them contend for one version would
    /// mean a host lost their menu because somebody moved the date. A slot that
    /// does not exist yet is version 0, which is what an upsert states to create
    /// one.</para>
    /// </summary>
    public int Version { get; set; } = 1;

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Is this slot part of the guest's current surface?</summary>
    public bool IsVisibleIn(PartyGuestPhase phase) => Enabled && phase switch
    {
        PartyGuestPhase.Before => VisibleBefore,
        PartyGuestPhase.Live => VisibleLive,
        _ => VisibleAfter,
    };
}

/// <summary>
/// The six things a party has to say, in the order it says them.
///
/// <para>The order is fixed on purpose. A guest reading an invitation wants the
/// welcome, then where, then what to wear, then what there is to eat, then
/// anything else — and no host needs to discover that by dragging cards
/// around.</para>
/// </summary>
public static class PartyGuestContentKinds
{
    public const string Invitation = "invitation";
    public const string Location = "location";
    public const string DressCode = "dress-code";
    public const string Menu = "menu";
    public const string Info = "info";
    public const string ThankYou = "thank-you";

    /// <summary>Every kind, in the order the guest surface renders them.</summary>
    public static readonly IReadOnlyList<string> All =
        [Invitation, Location, DressCode, Menu, Info, ThankYou];

    public static bool IsKnown(string? kind) => kind is not null && All.Contains(kind);

    /// <summary>
    /// Where a slot belongs when the host first creates it.
    ///
    /// <para>Server-side, because it is the product's answer rather than a
    /// client's guess: the invitation is for beforehand, the thank-you is for
    /// afterwards, and the practical details — where, what to wear, what to eat
    /// — are wanted both before and during. The host may change any of it.</para>
    /// </summary>
    public static (bool Before, bool Live, bool After) DefaultsFor(string kind) => kind switch
    {
        Invitation => (true, false, false),
        ThankYou => (false, false, true),
        _ => (true, true, false),
    };
}

/// <summary>
/// The two ways a slot's photograph can participate in the guest's surface.
///
/// <para>Two, and deliberately not an open vocabulary: each one is a different
/// thing for the renderer to DO, so a third would be a slice that decides what
/// it means rather than a string somebody adds. Application strings rather than
/// a PostgreSQL enum, on the same reasoning as
/// <c>PartyMediaSource.Role</c> — a new value should cost code, not a
/// migration and a database type.</para>
/// </summary>
public static class PartyGuestContentMediaPresentations
{
    /// <summary>The photograph is part of the slot's composition: picture, then words.</summary>
    public const string Inline = "inline";

    /// <summary>
    /// The photograph is the guest-facing document. The surface shows a
    /// deterministic navigation affordance and opens the picture full-screen.
    /// </summary>
    public const string Poster = "poster";

    public static readonly IReadOnlyList<string> All = [Inline, Poster];

    public static bool IsKnown(string? presentation) =>
        presentation is not null && All.Contains(presentation);
}

/// <summary>
/// The two ways a host may align what a slot says. Closed for the same reason
/// the presentations are: each value is something the renderer DOES.
/// </summary>
public static class PartyGuestContentTextAligns
{
    public const string Left = "left";
    public const string Center = "center";

    public static readonly IReadOnlyList<string> All = [Left, Center];

    public static bool IsKnown(string? align) => align is not null && All.Contains(align);
}

/// <summary>
/// The fixed formats a slot's inline photograph may be framed in. The whole
/// photograph is the absence of one — "auto" on the wire, null in the row.
/// </summary>
public static class PartyGuestContentMediaOrientations
{
    public const string Portrait = "portrait";
    public const string Landscape = "landscape";

    /// <summary>The wire's word for "the whole photograph". Never stored.</summary>
    public const string Auto = "auto";

    public static readonly IReadOnlyList<string> All = [Portrait, Landscape];

    public static bool IsKnown(string? orientation) =>
        orientation is not null && All.Contains(orientation);

    /// <summary>
    /// The print crop editor's limit, mirrored: past it a picture is visibly
    /// soft, so the editor does not go there.
    /// </summary>
    public const double MaxZoom = 4;

    public static bool IsValidCrop(double zoom, double centerX, double centerY) =>
        double.IsFinite(zoom) && zoom >= 1 && zoom <= MaxZoom
        && double.IsFinite(centerX) && centerX >= 0 && centerX <= 1
        && double.IsFinite(centerY) && centerY >= 0 && centerY <= 1;
}

/// <summary>
/// Where a slot's words sit beside its inline photograph: below it — the
/// default, "below" on the wire and null in the row — or on it.
/// </summary>
public static class PartyGuestContentTextPlacements
{
    /// <summary>The wire's word for the default. Never stored.</summary>
    public const string Below = "below";

    public const string Overlay = "overlay";

    public static bool IsKnownWire(string? placement) => placement is Below or Overlay;
}
