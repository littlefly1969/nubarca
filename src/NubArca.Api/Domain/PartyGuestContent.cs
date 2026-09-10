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
