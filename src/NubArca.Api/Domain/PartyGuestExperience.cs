namespace NubArca.Api.Domain;

/// <summary>Which of the three surfaces a guest is standing in front of.</summary>
public enum PartyGuestPhase
{
    /// <summary>The invitation. The party is announced but has not begun.</summary>
    Before,

    /// <summary>The party itself, and the whole of what a guest can do at one.</summary>
    Live,

    /// <summary>Afterwards: the thank-you and the memories.</summary>
    After,
}

/// <summary>How much of the experience is still open.</summary>
public enum PartyGuestAccessMode
{
    /// <summary>Everything the phase, the host's role and the party's settings allow.</summary>
    Full,

    /// <summary>The guest window has closed but the memories are still there.</summary>
    LibraryOnly,
}

/// <summary>
/// THE public experience policy: one answer to "what is this guest looking at,
/// and how much of it is open".
///
/// <para>A pure function of the party's own state and the clock. It exists so
/// the lifecycle is decided ONCE rather than as an <c>if (status …)</c> copied
/// into ten endpoints — which is exactly how a surface the frontend stops
/// drawing stays reachable by typing its route. Every public Party request and
/// the print capability resolver compute it from the same inputs with the same
/// code.</para>
///
/// <para>It is deliberately NOT an authorization framework. It says nothing
/// about who the caller is, what the host's role permits, or whether the
/// capability they present is live; those stay where they already are, and this
/// composes with them rather than replacing them.</para>
///
/// <para><b>Three things stay separate, and this is what keeps them that
/// way.</b> <c>Party.Status</c> is the phase of the experience,
/// <see cref="PartyAlbumLink"/> is the technical capability and its revocation,
/// and the two windows are product decisions. A status never revokes a token,
/// and a token is never invalid merely because the party has not started or has
/// finished — the SAME QR carries a guest from the invitation to the
/// memories.</para>
/// </summary>
public sealed record PartyGuestExperience(
    PartyGuestPhase Phase,
    PartyGuestAccessMode Access,
    /// <summary>Whether the memories are reachable right now.</summary>
    bool LibraryAvailable,
    /// <summary>When they stop being reachable, when the host set an end.</summary>
    DateTime? LibraryAccessEndsAt)
{
    /// <summary>
    /// The whole of what "during the party" means: contributing, greetings, the
    /// game, printing, finding your face. Every capability endpoint asks this
    /// one question rather than testing a status of its own.
    /// </summary>
    public bool AllowsLiveCapabilities =>
        Phase == PartyGuestPhase.Live && Access == PartyGuestAccessMode.Full;

    /// <summary>
    /// Whether the party's photographs may be served at all. During the party
    /// they are the party; afterwards they are the memories, and they last
    /// exactly as long as the library does. Before it, there is nothing to show
    /// — an invitation is not an empty album.
    /// </summary>
    public bool AllowsAlbumMedia => AllowsLiveCapabilities || LibraryAvailable;

    /// <summary>
    /// What a guest is looking at, or null when there is nothing to show them.
    ///
    /// <para>Null means the same generic unavailable an unknown token gets: a
    /// party still in Draft (which has no business holding a public capability),
    /// a guest window that closed while the party was still being prepared or
    /// held, and both windows expired all collapse to one answer, because none
    /// of them may be told apart from outside.</para>
    /// </summary>
    public static PartyGuestExperience? Resolve(
        string status,
        DateTime? guestAccessExpiresAt,
        DateTime? libraryAccessExpiresAt,
        DateTime now)
    {
        var phase = status switch
        {
            PartyStatuses.Published => PartyGuestPhase.Before,
            PartyStatuses.Live => PartyGuestPhase.Live,
            PartyStatuses.Ended => PartyGuestPhase.After,
            // Draft, or anything a future release adds without deciding what a
            // guest should see. Least privilege, not a guess.
            _ => (PartyGuestPhase?)null,
        };
        if (phase is not PartyGuestPhase resolved)
        {
            return null;
        }

        var full = guestAccessExpiresAt is null || now < guestAccessExpiresAt;

        // An UNSET library end is not a second window: it means the memories
        // last as long as the guest experience does. A set one may outlive it,
        // which is the whole point of the column — and may also fall short of
        // it, in which case the memories close first and the thank-you stays.
        var libraryAvailable = libraryAccessExpiresAt is null
            ? full
            : now < libraryAccessExpiresAt;

        if (full)
        {
            return new PartyGuestExperience(
                resolved,
                PartyGuestAccessMode.Full,
                // Memories are an AFTER idea. During the party the photographs
                // are simply the party, and `AllowsLiveCapabilities` covers them.
                LibraryAvailable: resolved == PartyGuestPhase.After && libraryAvailable,
                libraryAccessExpiresAt);
        }

        // The guest window has closed. The only thing that can still be open is
        // the library, and only once the party is over: a party that is still
        // being held has not produced memories yet.
        if (resolved == PartyGuestPhase.After && libraryAvailable)
        {
            return new PartyGuestExperience(
                resolved,
                PartyGuestAccessMode.LibraryOnly,
                LibraryAvailable: true,
                libraryAccessExpiresAt);
        }

        return null;
    }
}

/// <summary>The wire spellings, so a client never receives a C# enum name.</summary>
public static class PartyGuestPhases
{
    public const string Before = "before";
    public const string Live = "live";
    public const string After = "after";

    public static string Wire(PartyGuestPhase phase) => phase switch
    {
        PartyGuestPhase.Before => Before,
        PartyGuestPhase.Live => Live,
        _ => After,
    };
}

public static class PartyGuestAccessModes
{
    public const string Full = "full";
    public const string LibraryOnly = "library-only";

    public static string Wire(PartyGuestAccessMode access) =>
        access == PartyGuestAccessMode.Full ? Full : LibraryOnly;
}
