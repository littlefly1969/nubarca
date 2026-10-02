namespace NubArca.Api.Party;

/// <summary>
/// THE GUEST BOOK, LIVE: the two decisions the regia makes about the book
/// during the evening.
///
/// <list type="bullet">
/// <item><b>Letting the room read it.</b> While the party is live a guest reads
/// the memories THEY wrote; with viewing on, the whole book, and the party
/// offers "Guarda il Guestbook". It never changes what a guest may write.</item>
/// <item><b>Putting it on the television.</b> A request the presentation rule
/// honours only while nothing else holds the screen and the book has something
/// to show. The game and the guest book are never both granted the screen: the
/// commands of either serialise on the party link and refuse while the other
/// holds it.</item>
/// </list>
///
/// <para>One service for the host and for the Party Crew: the routes differ in
/// who may call them, the semantics do not. What a caller may do is passed in as
/// <see cref="PartyGuestbookLiveRights"/> and every command is re-checked on
/// arrival, whatever the read model offered.</para>
/// </summary>
public interface IPartyGuestbookLiveService
{
    /// <summary>The control read model, or null when the album has no live party link.</summary>
    Task<PartyGuestbookLiveControlDto?> GetAsync(
        Guid ownerUserId, Guid albumId, PartyGuestbookLiveRights rights,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// One command, quoting the version it was decided against. A refusal
    /// carries the CURRENT read model, so the regia ends the request correct
    /// rather than merely told off.
    /// </summary>
    Task<PartyGuestbookLiveResult> ExecuteAsync(
        Guid ownerUserId, Guid albumId, string? command, int expectedVersion,
        PartyGuestbookLiveRights rights, CancellationToken cancellationToken = default);
}

/// <summary>The vocabulary. Four commands, each the inverse of another.</summary>
public static class PartyGuestbookLiveCommands
{
    public const string EnableViewing = "enable_viewing";
    public const string DisableViewing = "disable_viewing";
    public const string ShowOnTv = "show_on_tv";
    public const string ReturnToSlideshow = "return_to_slideshow";

    public static readonly IReadOnlyList<string> All =
        [EnableViewing, DisableViewing, ShowOnTv, ReturnToSlideshow];

    public static bool IsKnown(string? command) => command is not null && All.Contains(command);

    /// <summary>Whether the command is about the room's PHONES rather than the television.</summary>
    public static bool IsViewing(string command) => command is EnableViewing or DisableViewing;
}

/// <summary>
/// What a caller may do here. The host may do everything; a collaborator holds
/// the guests' half through <c>contributions.moderate</c> (letting the room read
/// what guests left is a decision about their contributions) and the
/// television's half through <c>screens.manage</c> (the screen in the room).
/// </summary>
public sealed record PartyGuestbookLiveRights(bool Viewing, bool Tv)
{
    public static readonly PartyGuestbookLiveRights Owner = new(true, true);

    public bool Any => Viewing || Tv;

    public bool Allows(string command) =>
        PartyGuestbookLiveCommands.IsViewing(command) ? Viewing : Tv;
}

/// <summary>
/// The guest book's live controls as the regia sees them — every word the
/// server's: what the television is really showing, what is in the book, and
/// which commands are legal for THIS caller right now.
/// </summary>
public sealed record PartyGuestbookLiveControlDto(
    int Version,
    // The guests' half.
    bool ViewingEnabled,
    // The regia's request to put the book on the television…
    bool TvActive,
    // …and what a paired television is actually told to show: "slideshow",
    // "game", "guestbook" or "unavailable". The two can differ — an emptied
    // book yields to the slideshow — and the control room follows this one.
    string TvPresentation,
    int VisibleEntries,
    int PendingEntries,
    // Decided here, never inferred by a client from the flags above.
    IReadOnlyList<string> AvailableCommands,
    // Why "show_on_tv" is not offered, when it is not and the book is not
    // already there: "party_not_live", "guestbook_not_available",
    // "game_active" or "guestbook_empty". Null otherwise.
    string? TvUnavailableReason,
    bool PartyLive,
    bool GuestbookEnabled);

public enum PartyGuestbookLiveError
{
    NotFound,
    UnknownCommand,
    Forbidden,
    PartyNotLive,
    GuestbookNotAvailable,
    GuestbookEmpty,
    GameActive,
    VersionConflict,
    IllegalTransition,
}

public sealed record PartyGuestbookLiveResult(
    PartyGuestbookLiveControlDto? Control, PartyGuestbookLiveError? Error)
{
    public static PartyGuestbookLiveResult Ok(PartyGuestbookLiveControlDto control) => new(control, null);

    public static PartyGuestbookLiveResult Fail(
        PartyGuestbookLiveError error, PartyGuestbookLiveControlDto? control = null) => new(control, error);
}

/// <summary>What the regia sends: a command, and the version it decided against.</summary>
public sealed record PartyGuestbookLiveCommandRequest(string? Command, int? ExpectedVersion);

/// <summary>
/// A refused command: a stable machine code the client translates, and the
/// state the refusal was measured against.
/// </summary>
public sealed record PartyGuestbookLiveRefusalDto(string Code, PartyGuestbookLiveControlDto? Control);
