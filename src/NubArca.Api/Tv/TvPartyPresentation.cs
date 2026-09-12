using System.Security.Cryptography;
using System.Text;
using NubArca.Api.Domain;

namespace NubArca.Api.Tv;

/// <summary>
/// WHICH surface a television assigned to a party should be showing — decided
/// by the server, and projected beside the assignment rather than folded into
/// it.
///
/// <para>The assignment says what a television is FOR (general, or one
/// specific party link) and only an owner changes it. The presentation says
/// what that party wants on the screen RIGHT NOW, and it follows the party:
/// the native slideshow while no game is taking the display, the canonical game
/// stage while one is, and an honest "unavailable" when the party the
/// assignment names cannot be shown. Turning a game phase into a new
/// assignment would have made an owner decision out of a game event, and would
/// have had the television learn what `voting_open` means. It knows neither:
/// it is told one of four words.</para>
///
/// <para>This is a PROJECTION, not a state machine. It reads the game session
/// and never writes it: FINISHED stays FINISHED, and a paired television
/// returning to the slideshow is a consequence of that state rather than a
/// transition of its own.</para>
/// </summary>
public static class TvPartyPresentations
{
    /// Not a party at all: the ordinary NubArca TV experience.
    public const string General = "general";

    /// The party's own native slideshow — photos, videos, guest uploads,
    /// greetings — because no game is taking the display.
    public const string Slideshow = "slideshow";

    /// The canonical Party Game stage, hosted in the television's WebView.
    public const string Game = "game";

    /// The assignment names a party that cannot be shown (revoked, switched
    /// off, expired, or its host may no longer run parties). The television
    /// fails CLOSED on it: it neither falls back to general nor adopts another
    /// party.
    public const string Unavailable = "unavailable";

    /// <summary>
    /// How long a FINISHED game keeps the canonical stage on a paired
    /// television before the presentation becomes the slideshow again.
    ///
    /// <para>Without it the closing card — the room's "thank you" — would be on
    /// screen for anything between zero and one poll interval. The dwell is
    /// measured from the session's own <c>FinishedAt</c>, on the SERVER's
    /// clock, so every television in the room converges on the same answer and
    /// no client runs a timer of its own. It changes nothing about the game:
    /// the session is FINISHED from the first instant and stays so.</para>
    /// </summary>
    public static readonly TimeSpan FinishedDwell = TimeSpan.FromSeconds(15);

    /// <summary>
    /// The presentation for a PARTY assignment.
    ///
    /// <para><paramref name="gamesPermitted"/> is the host's Games capability as
    /// the party resolver hands it out — the role's permission with the party's
    /// PHASE already folded in, so it is true only while the party is live. That
    /// is deliberate: the lobby puts a join code on the screen, and before the
    /// party starts (or after it ends) that code leads guests to a game they
    /// cannot join. A party that is not live shows its slideshow instead.</para>
    ///
    /// <list type="bullet">
    /// <item>party cannot be shown → <see cref="Unavailable"/></item>
    /// <item>no game switched on, or no game can be played right now → <see cref="Slideshow"/></item>
    /// <item>no game yet, or a game in progress → <see cref="Game"/> (the lobby is the takeover)</item>
    /// <item>game finished → <see cref="Game"/> for <see cref="FinishedDwell"/>, then <see cref="Slideshow"/></item>
    /// </list>
    ///
    /// <para>`restart_game` puts the session back in the lobby, which is the
    /// second rule again: the takeover returns with no special case.</para>
    /// </summary>
    public static string Decide(
        bool partyShowable, bool gameEnabled, bool gamesPermitted,
        string? gameStatus, DateTime? finishedAt, DateTime now)
    {
        if (!partyShowable) return Unavailable;
        if (!gameEnabled || !gamesPermitted) return Slideshow;
        if (gameStatus != PartyGameStatuses.Finished) return Game;
        return finishedAt is DateTime at && now < at + FinishedDwell ? Game : Slideshow;
    }

    /// <summary>
    /// An opaque identity for "this television, assigned to this party link".
    ///
    /// <para>The television needs to know when the party it is showing has
    /// become a DIFFERENT party — including a new link for the same album, which
    /// is a new party everywhere else in this feature — so that it tears down
    /// and mints a fresh capability rather than letting one party's frame
    /// survive into the next. The album id cannot say that, and the link id must
    /// not cross. A digest over both ids answers exactly the one question, is
    /// different for every television, and is accepted by no endpoint: it
    /// identifies nothing a caller could use.</para>
    /// </summary>
    public static string AssignmentKey(Guid tvSessionId, Guid partyAlbumLinkId)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"nubarca-tv-assignment:{tvSessionId:N}:{partyAlbumLinkId:N}"));
        return Convert.ToHexString(digest, 0, 12).ToLowerInvariant();
    }
}
