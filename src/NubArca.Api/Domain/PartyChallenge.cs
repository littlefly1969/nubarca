namespace NubArca.Api.Domain;

public sealed class PartyChallenge
{
    public Guid Id { get; set; }
    public Guid AlbumId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string Kind { get; set; } = PartyChallengeKinds.Custom;
    public Guid? MediaFileItemId { get; set; }
    public bool IsEnabled { get; set; } = true;
    public int SortOrder { get; set; }

    // --- How the activity is PLAYED, as columns rather than as text inside Body.
    //
    // Body is what the host wrote for the room to read. Rules are what the
    // runtime acts on, and a runtime that has to parse prose to learn whether an
    // activity is timed is a runtime one edit away from being wrong.

    /// <summary>
    /// The activity's own time limit. Null means "as long as it takes", which is
    /// the default and what every activity written before this column meant.
    /// </summary>
    public int? DurationSeconds { get; set; }

    /// <summary>
    /// Whether the room votes on this activity, and how. `binary` is the
    /// migration default because a pass/fail verdict is the game's ordinary
    /// shape; `none` is for an activity that is simply performed.
    /// </summary>
    public string VotingMode { get; set; } = PartyChallengeVotingModes.Binary;

    /// <summary>
    /// The question the room is asked, when the host wants to phrase it. Null
    /// falls back to the localized default, so a host never has to write one.
    /// </summary>
    public string? VoteQuestion { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// How the room decides. Deliberately a string rather than an enum: the set is
/// designed to grow — rating, multiple choice, quiz — and a wire value that
/// survives an insertion in the middle is worth more than an ordinal.
///
/// Only the two the product implements are accepted. A mode is added here when
/// the runtime can actually run it, never before, so an unimplemented mode can
/// never reach a database column.
/// </summary>
public static class PartyChallengeVotingModes
{
    /// The activity is performed and the host moves on. No vote is opened.
    public const string None = "none";

    /// One question, two answers. Did they do it?
    public const string Binary = "binary";

    /// <summary>
    /// One question, the host's OWN answers — between two and six of them, each
    /// carrying what happens to the guest of honour if the room picks it.
    ///
    /// <para>It is a separate mode rather than "binary with labels" because the
    /// two are different questions: binary asks the room to JUDGE something that
    /// already happened, choice asks it to DECIDE what happens next. A binary
    /// round has a verdict; a choice round has a winner and a consequence.</para>
    /// </summary>
    public const string Choice = "choice";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>([None, Binary, Choice], StringComparer.Ordinal);

    public static bool IsKnown(string? value) => value is not null && All.Contains(value);

    /// Whether this mode opens a voting phase at all.
    public static bool CollectsVotes(string? value) => value is Binary or Choice;

    /// Whether this mode is answered with one of the challenge's OWN options
    /// rather than with a fixed yes/no.
    public static bool UsesOptions(string? value) => value == Choice;
}

public static class PartyChallengeKinds
{
    public const string Dare = "dare";
    public const string Penalty = "penalty";
    public const string Guess = "guess";
    public const string Custom = "custom";
    public static readonly IReadOnlySet<string> All =
        new HashSet<string>([Dare, Penalty, Guess, Custom], StringComparer.Ordinal);
    public static bool IsKnown(string? value) => value is not null && All.Contains(value);
}

public static class PartyChallengeLimits
{
    public const int MaxTitleLength = 100;
    public const int MaxBodyLength = 500;
    public const int MaxVoteQuestionLength = 120;

    // Five seconds is the shortest thing anybody can actually do; an hour is
    // where a party activity stops being one. The bounds only stop absurd input.
    public const int MinDurationSeconds = 5;
    public const int MaxDurationSeconds = 3600;

    public static bool IsValidDuration(int? value) =>
        value is null || (value >= MinDurationSeconds && value <= MaxDurationSeconds);

    // TWO IS A CHOICE, SIX IS A SCREEN. Below two there is nothing to decide;
    // above six the television stops being readable from the far side of a room
    // and a guest scrolls a ballot on their phone. The bounds are the product's,
    // not the storage's, and they are asserted in three places on purpose: here,
    // in the check constraint, and in the composer.
    public const int MinOptions = 2;
    public const int MaxOptions = 6;
    public const int MaxOptionLabelLength = 60;
    public const int MaxOptionOutcomeLength = 200;

    public static bool IsValidOptionCount(int value) =>
        value >= MinOptions && value <= MaxOptions;
}

/// <summary>
/// One answer the room may pick, and what it costs.
///
/// <para><see cref="Label"/> is what a guest taps and what the television shows
/// beside the percentage. <see cref="Outcome"/> is the half that makes the mode
/// worth having: "the room has voted — 35% say this, so you are doing that". It
/// is optional, because a host may want the room to choose between things that
/// speak for themselves.</para>
///
/// <para>Rows belong to the challenge and are ordered by <see cref="Position"/>.
/// A VOTE names an option by its id, never by its position: a host who reorders
/// the answers between rounds has not changed what anybody voted for.</para>
/// </summary>
public sealed class PartyChallengeOption
{
    public Guid Id { get; set; }
    public Guid PartyChallengeId { get; set; }

    /// 0-based, dense, and the order the room sees. Uniqueness per challenge is
    /// enforced by the database so two saves racing cannot interleave.
    public int Position { get; set; }

    public string Label { get; set; } = string.Empty;

    /// What happens if this answer wins. Null is "nothing stated".
    public string? Outcome { get; set; }
}

public sealed class PartyChallengeVote
{
    public Guid Id { get; set; }
    public Guid PartyAlbumLinkId { get; set; }
    public Guid PartyParticipantId { get; set; }
    public Guid PartyChallengeId { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class PartyChallengeSession
{
    public Guid Id { get; set; }
    public Guid PartyAlbumLinkId { get; set; }
    public string Mode { get; set; } = PartyPlaybackModes.Media;
    public Guid? ActiveChallengeId { get; set; }
    public DateTime? NextChallengeAt { get; set; }
    public int CompletedCount { get; set; }
    public int Version { get; set; } = 1;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class PartyChallengeCompletion
{
    public Guid Id { get; set; }
    public Guid PartyAlbumLinkId { get; set; }
    public Guid PartyChallengeId { get; set; }
    public DateTime CompletedAt { get; set; }
}

public static class PartyPlaybackModes
{
    public const string Media = "media";
    public const string ChallengeHold = "challenge_hold";
}
