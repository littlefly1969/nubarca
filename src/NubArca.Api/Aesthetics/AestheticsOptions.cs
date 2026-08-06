namespace NubArca.Api.Aesthetics;

// Configuration for the owner-private Aesthetics Lab + HumanAesExpert sidecar.
// Bound from the "HumanAesExpert" section (env binding:
// HumanAesExpert__Enabled=false, …). The feature is DISABLED by default in
// committed production config; when disabled, lab browsing still works but
// "Start analysis" returns a controlled unavailable response and enqueues
// nothing.
public sealed class AestheticsOptions
{
    public const string SectionName = "HumanAesExpert";

    // Master switch. When false the analysis path is unavailable (no jobs are
    // created); item browsing/upload/remove still work.
    public bool Enabled { get; set; } = false;

    // Stable profile key recorded on every run + sent to the sidecar.
    public string ProfileKey { get; set; } = "human-aesexpert-1b-expert-v1";

    // Default requested capability when the client does not specify one.
    public string DefaultCapabilities { get; set; } = AestheticCapabilities.ExpertScores;

    // Per-capability enablement gates. Only ExpertScores is on by default; the
    // other three are prepared but OFF until separately benchmarked + validated.
    public bool AllowExpertScores { get; set; } = true;
    public bool AllowScoreHead { get; set; } = false;
    public bool AllowMetaVoter { get; set; } = false;
    public bool AllowTextAssessment { get; set; } = false;

    // Maximum images per manual analysis request (also enforced in the UI).
    public int MaximumBatchItems { get; set; } = 20;

    // Conservative per-request hard timeout for a single sidecar inference. The
    // 1B model on CPU can be slow; keep this generous but bounded.
    public int RequestTimeoutSeconds { get; set; } = 120;

    // Internal sidecar base URL (e.g. http://human-aesexpert:8091). Empty ⇒ the
    // sidecar client reports unavailable (feature effectively off).
    public string SidecarBaseUrl { get; set; } = string.Empty;

    // Preprocessing profile requested from the sidecar. official-v1 preserves the
    // checkpoint's own preprocessing; a reduced profile MUST use a different key.
    public string PreprocessingProfileKey { get; set; } = AestheticPreprocessingProfiles.OfficialV1;

    // Max bytes accepted for a single direct lab upload (independent of the
    // global blob-store cap). Default 25 MiB.
    public long MaxUploadBytes { get; set; } = 25L * 1024 * 1024;

    // Server-side pepper for the owner-scoped logical container key. Stable
    // across restarts; a dev fallback is used when empty.
    public string Pepper { get; set; } = string.Empty;

    // --- TV "Beauty Lab" QR mobile-upload session (slice feature/tv-beauty-lab) ---
    // Short-lived capability that lets a phone upload straight into the lab. All
    // three are safety ceilings; the per-file limit is the existing MaxUploadBytes.

    // Session lifetime. Default ~10 minutes (short by design).
    public int UploadSessionTtlMinutes { get; set; } = 10;

    // Bounded file count per session (a leaked token can't exceed this).
    public int UploadSessionMaxFiles { get; set; } = 40;

    // Bounded total bytes per session across all files.
    public long UploadSessionMaxTotalBytes { get; set; } = 500L * 1024 * 1024;

    // How long an expired/revoked session row is retained before the cleanup
    // sweeper hard-deletes it (kept briefly so the TV/mobile can render a final
    // "expired"/"completed" state). Default 60 minutes.
    public int UploadSessionRetentionMinutes { get; set; } = 60;

    // Cleanup sweeper cadence (minutes). Disabled hosts still refuse expired
    // tokens at resolve time — the sweeper only reclaims rows.
    public int UploadSessionCleanupIntervalMinutes { get; set; } = 10;

    // Master switch for the background cleanup sweeper. Off ⇒ no reclaim loop
    // (rows still expire logically); mirrors the janitor/sweeper convention.
    public bool UploadSessionCleanupEnabled { get; set; } = true;

    // Resolve the effective, gate-filtered capability list for a requested set.
    // Unknown or disabled capabilities are dropped; returns the allowed subset.
    public IReadOnlyList<string> FilterAllowed(IEnumerable<string> requested)
    {
        var result = new List<string>();
        foreach (var cap in requested)
        {
            if (IsCapabilityAllowed(cap) && !result.Contains(cap))
            {
                result.Add(cap);
            }
        }
        return result;
    }

    public bool IsCapabilityAllowed(string? capability) => capability switch
    {
        AestheticCapabilities.ExpertScores => AllowExpertScores,
        AestheticCapabilities.ScoreHead => AllowScoreHead,
        AestheticCapabilities.MetaVoter => AllowMetaVoter,
        AestheticCapabilities.TextAssessment => AllowTextAssessment,
        _ => false,
    };
}
