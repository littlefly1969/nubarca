namespace NubArca.Api.Cast;

// Operator configuration for Google Cast (`Cast:*` / `Cast__*`).
//
// Two settings, both deliberately conservative:
//
//   GrantLifetimeMinutes  how long a delegated playback capability stays valid
//                         even if nothing ever revokes it. It is the backstop
//                         for a browser that vanished mid-cast, so it has to be
//                         long enough for a film and short enough that a leaked
//                         URL is not an open door.
//
//   AllowedReceiverOrigins the EXACT origins a Cast receiver may present. There
//                         is no default and no wildcard: an unlisted origin
//                         simply gets no CORS permission. Grant creation still
//                         works with an empty list — the failure is visible on
//                         the television rather than silently permissive.
public sealed class CastOptions
{
    public const string SectionName = "Cast";

    // The conservative allowed range. A configured value outside it is rejected
    // in favour of the nearest bound rather than throwing: a mistyped lifetime
    // must not take an installation's API down, and clamping is what "reject
    // absurd values" means for a value that always has a safe answer.
    public const int MinimumGrantLifetimeMinutes = 30;
    public const int MaximumGrantLifetimeMinutes = 720;
    public const int DefaultGrantLifetimeMinutes = 360;

    public int GrantLifetimeMinutes { get; set; } = DefaultGrantLifetimeMinutes;

    public string[] AllowedReceiverOrigins { get; set; } = [];

    public int EffectiveGrantLifetimeMinutes => Math.Clamp(
        GrantLifetimeMinutes, MinimumGrantLifetimeMinutes, MaximumGrantLifetimeMinutes);

    public TimeSpan EffectiveGrantLifetime =>
        TimeSpan.FromMinutes(EffectiveGrantLifetimeMinutes);

    // Normalised allowlist: trimmed, trailing slash removed, empties dropped.
    // Compared ordinally against the request's Origin header, which browsers
    // and receivers send lowercase without a trailing slash.
    public IReadOnlyList<string> NormalizedReceiverOrigins =>
        AllowedReceiverOrigins
            .Where(o => !string.IsNullOrWhiteSpace(o))
            .Select(o => o.Trim().TrimEnd('/'))
            .Where(o => o.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
}
