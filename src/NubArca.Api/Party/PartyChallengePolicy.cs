namespace NubArca.Api.Party;

public sealed record PartyChallengeCandidate(
    Guid Id, int Votes, bool IsEnabled = true, bool IsCompleted = false);

public static class PartyChallengePolicy
{
    public static DateTime NextDeadline(DateTime now, int minSeconds, int maxSeconds, int sample)
    {
        if (maxSeconds < minSeconds) throw new ArgumentOutOfRangeException(nameof(maxSeconds));
        return now.AddSeconds(minSeconds + Math.Clamp(sample, 0, maxSeconds - minSeconds));
    }

    // MostVotedRemaining. The sample is supplied by the runtime RNG, keeping
    // policy deterministic and exhaustively testable.
    public static Guid? Select(IReadOnlyList<PartyChallengeCandidate> candidates, int tieSample)
    {
        var eligible = candidates.Where(x => x.IsEnabled && !x.IsCompleted).ToList();
        if (eligible.Count == 0) return null;
        var highest = eligible.Max(x => x.Votes);
        var tied = eligible.Where(x => x.Votes == highest).OrderBy(x => x.Id).ToList();
        return tied[Math.Abs(tieSample % tied.Count)].Id;
    }
}
