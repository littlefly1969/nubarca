using NubArca.Api.Domain;

namespace NubArca.Api.Party;

/// <summary>
/// WHEN a guest may say which activities they would like to see.
///
/// <para>Pure, and deliberately one function: the guest surface, the write path
/// and the control room all ask the same question, and three answers that
/// happened to agree would stop agreeing the first time one of them was
/// edited.</para>
///
/// <para>THE TWO VOTES ARE NOT THE SAME VOTE, and this policy is half of what
/// keeps them apart. A preference is cast BEFORE the match, on an activity, to
/// tell the host what the room wants. The live vote is cast DURING an activity,
/// on a round, to say whether it was done. They share no row, no budget, no
/// phase and no consequence — the only thing they share is the anonymous
/// participant who casts them, which is the identity the party already had.</para>
/// </summary>
public static class PartyGamePreferencePolicy
{
    /// <summary>
    /// Whether preferences may be READ at all: the party offers a game, the host
    /// switched priority voting on, and the party is live (the Games capability
    /// arrives phase-folded, so it is already false before and after the party).
    /// </summary>
    public static bool IsOffered(bool gamesPermitted, bool gameEnabled, bool priorityVotingEnabled) =>
        gamesPermitted && gameEnabled && priorityVotingEnabled;

    /// <summary>
    /// Whether they may be CHANGED.
    ///
    /// <para>Open for as long as the match has not begun — no session row at
    /// all, or one still in its lobby — and closed from the first <c>start</c>.
    /// <c>restart_game</c> returns the session to the lobby and therefore
    /// reopens them, WITHOUT deleting anything: the preferences belong to the
    /// party and the guests who cast them, not to the match that has just been
    /// discarded, so a replay starts from what the room already said.</para>
    ///
    /// <para>An intermission is deliberately NOT open. The match has begun; the
    /// host is mid-evening and planning from the numbers the room gave them
    /// before it started, and a preference arriving now would change a count the
    /// host is looking at while they decide.</para>
    /// </summary>
    public static bool IsOpen(
        bool gamesPermitted, bool gameEnabled, bool priorityVotingEnabled, string? sessionPhase) =>
        IsOffered(gamesPermitted, gameEnabled, priorityVotingEnabled)
        && (sessionPhase is null || sessionPhase == PartyGamePhases.Lobby);
}

/// <summary>
/// One activity as a guest choosing preferences sees it.
///
/// <para>It carries NO vote count. A guest deciding what they would like to see
/// must not be told what everybody else picked first — that is a bandwagon, and
/// the number exists to inform the HOST's planning rather than to steer the
/// room. The control room gets the counts; the phone gets its own answer.</para>
/// </summary>
public sealed record PartyGamePreferenceItemDto(
    Guid Id, string Title, string Body, string? MediaUrl, bool Selected);

/// <summary>
/// The guest's pre-game preference surface, complete. Absent from the public
/// snapshot entirely when the host did not switch priority voting on — a
/// capability that is off has no shape, exactly as everywhere else in Party.
/// </summary>
public sealed record PartyGamePreferencesDto(
    /// Whether preferences may still be changed. False once the match has begun.
    bool Open,
    int VotesPerGuest,
    int VotesUsed,
    int VotesRemaining,
    IReadOnlyList<PartyGamePreferenceItemDto> Items);

public enum PartyGamePreferenceError
{
    /// The token, the party, the game switch or priority voting does not resolve.
    NotFound,

    /// <summary>
    /// The caller holds no identity this party issued. Same rule as the live
    /// vote: a cookie is a claim, and a claim the server never minted is not a
    /// guest. Joining is what mints one.
    /// </summary>
    NotJoined,

    /// The match has begun. Preferences are read-only until a restart.
    Closed,

    /// That activity is not in this party's deck, or is switched off.
    UnknownChallenge,

    /// This guest has spent every preference the host allowed them.
    LimitReached,
}

/// <summary>
/// The outcome of one preference tap. A refusal carries the CURRENT surface
/// wherever one exists, for the same reason every other Party Game refusal
/// does: the guest is holding a phone, and the useful answer is the truth.
/// </summary>
public sealed record PartyGamePreferenceResult(
    PartyGamePreferencesDto? Preferences,
    PartyGamePreferenceError? Error)
{
    public static PartyGamePreferenceResult Ok(PartyGamePreferencesDto preferences) =>
        new(preferences, null);

    public static PartyGamePreferenceResult Fail(
        PartyGamePreferenceError error, PartyGamePreferencesDto? current = null) =>
        new(current, error);
}

public sealed record PartyGamePreferenceRequest(Guid? ChallengeId, bool? Selected);
