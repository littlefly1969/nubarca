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

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>([None, Binary], StringComparer.Ordinal);

    public static bool IsKnown(string? value) => value is not null && All.Contains(value);

    /// Whether this mode opens a voting phase at all.
    public static bool CollectsVotes(string? value) => value == Binary;
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
