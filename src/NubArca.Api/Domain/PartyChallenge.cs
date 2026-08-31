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
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
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
