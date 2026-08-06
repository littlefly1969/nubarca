using Microsoft.Extensions.Options;

namespace NubArca.Api.Ai.Video;

// VSEM-01: configuration for the canonical video temporal substrate.
//
// Bound from "Ai:VideoSegmentation" (env: Ai__VideoSegmentation__Enabled, …).
// DISABLED BY DEFAULT, like every other AI capability: with Enabled=false the
// job no-ops, nothing is scheduled, and no FFmpeg process ever runs.
//
// SegmentationVersion is the reindex key. Bump it whenever the algorithm OR any
// value below changes the temporal output — the new version writes a NEW
// manifest next to the old one (unique on blob+version), so a reindex is always
// additive and reversible.
public class VideoSemanticSegmentationOptions
{
    public const string SectionName = "Ai:VideoSegmentation";

    public bool Enabled { get; set; } = false;

    // Effective output version. Must be positive; changing it makes every blob
    // a candidate again without touching the previous manifests.
    public int SegmentationVersion { get; set; } = 1;

    // FFmpeg scene score above which a frame is a scene-change CANDIDATE.
    // Candidates are only hints — normalization decides the final boundaries.
    public double SceneThreshold { get; set; } = 0.4;

    // A segment shorter than this is merged into its predecessor. Guards against
    // the burst of near-identical candidates a fast cut or a flash produces.
    public double MinimumSegmentSeconds { get; set; } = 2.0;

    // Preferred segment length, used by the uniform fallback and by the balanced
    // split of over-long segments.
    public double TargetSegmentSeconds { get; set; } = 8.0;

    // Hard ceiling: any segment longer than this is split into balanced parts,
    // so a static 40-minute shot still yields usable temporal resolution.
    public double MaximumSegmentSeconds { get; set; } = 20.0;

    // Hard cap on segments per video. When normalization would exceed it the
    // manifest is deterministically rebuilt as this many balanced uniform
    // segments — bounded cost, and the invariants (gapless, ends at duration)
    // still hold.
    public int MaximumSegmentsPerVideo { get; set; } = 400;

    // Representative timestamps per segment. Conservative by default; the schema
    // supports more without migration.
    public int SamplesPerSegment { get; set; } = 1;

    // Hard cap on samples per video (the real cost driver for later embedding
    // work).
    public int MaximumSamplesPerVideo { get; set; } = 600;

    // Wall-clock ceiling for ONE scene-detection process. Scene detection
    // decodes the whole file, so this is minutes, not seconds.
    public int ProcessTimeoutSeconds { get; set; } = 10 * 60;

    // Cap on detector stdout. Exceeding it is a retryable failure, never a
    // silently truncated manifest.
    public int MaximumProcessOutputBytes { get; set; } = 8 * 1024 * 1024;

    // ---- derived, integral-millisecond views ------------------------------
    // Every downstream computation uses whole milliseconds so the persisted
    // model has no floating-point ambiguity.
    public long MinimumSegmentMilliseconds => (long)Math.Round(MinimumSegmentSeconds * 1000d);
    public long TargetSegmentMilliseconds => (long)Math.Round(TargetSegmentSeconds * 1000d);
    public long MaximumSegmentMilliseconds => (long)Math.Round(MaximumSegmentSeconds * 1000d);

    // The longest duration this configuration can represent without violating
    // BOTH hard limits at once: MaximumSegmentsPerVideo segments of at most
    // MaximumSegmentMilliseconds each. A longer video cannot be segmented at
    // this version — the cap rebuild would silently stretch segments past
    // MaximumSegmentSeconds — so the service skips it permanently instead.
    // Overflow-safe: a product beyond long range saturates to long.MaxValue
    // (effectively "no representable video exceeds this configuration").
    public long MaximumCapacityMilliseconds
    {
        get
        {
            try
            {
                return checked(MaximumSegmentsPerVideo * MaximumSegmentMilliseconds);
            }
            catch (OverflowException)
            {
                return long.MaxValue;
            }
        }
    }
}

// Fails fast at startup on a configuration that could produce an unbounded or
// self-contradictory manifest. Validation runs even when Enabled=false so a
// broken section is caught before someone flips the switch in production.
public sealed class VideoSemanticSegmentationOptionsValidator
    : IValidateOptions<VideoSemanticSegmentationOptions>
{
    public ValidateOptionsResult Validate(string? name, VideoSemanticSegmentationOptions o)
    {
        var errors = new List<string>();

        if (o.SegmentationVersion <= 0)
        {
            errors.Add("SegmentationVersion must be a positive integer.");
        }

        if (!double.IsFinite(o.SceneThreshold) || o.SceneThreshold <= 0 || o.SceneThreshold >= 1)
        {
            errors.Add("SceneThreshold must be a finite value in the exclusive range (0,1).");
        }

        foreach (var (label, value) in new[]
        {
            ("MinimumSegmentSeconds", o.MinimumSegmentSeconds),
            ("TargetSegmentSeconds", o.TargetSegmentSeconds),
            ("MaximumSegmentSeconds", o.MaximumSegmentSeconds),
        })
        {
            if (!double.IsFinite(value) || value <= 0)
            {
                errors.Add($"{label} must be a finite positive number of seconds.");
            }
        }

        // Duration ordering: minimum <= target <= maximum. Any other order makes
        // merging and splitting fight each other.
        if (double.IsFinite(o.MinimumSegmentSeconds) && double.IsFinite(o.TargetSegmentSeconds)
            && o.MinimumSegmentSeconds > o.TargetSegmentSeconds)
        {
            errors.Add("MinimumSegmentSeconds must not exceed TargetSegmentSeconds.");
        }

        if (double.IsFinite(o.TargetSegmentSeconds) && double.IsFinite(o.MaximumSegmentSeconds)
            && o.TargetSegmentSeconds > o.MaximumSegmentSeconds)
        {
            errors.Add("TargetSegmentSeconds must not exceed MaximumSegmentSeconds.");
        }

        // Sub-millisecond durations would round to a zero-length interval.
        if (double.IsFinite(o.MinimumSegmentSeconds) && o.MinimumSegmentSeconds > 0
            && o.MinimumSegmentMilliseconds <= 0)
        {
            errors.Add("MinimumSegmentSeconds must be at least one millisecond.");
        }

        if (o.MaximumSegmentsPerVideo <= 0)
        {
            errors.Add("MaximumSegmentsPerVideo must be a positive integer.");
        }

        if (o.SamplesPerSegment <= 0)
        {
            errors.Add("SamplesPerSegment must be a positive integer.");
        }

        if (o.MaximumSamplesPerVideo <= 0)
        {
            errors.Add("MaximumSamplesPerVideo must be a positive integer.");
        }

        if (o.ProcessTimeoutSeconds <= 0)
        {
            errors.Add("ProcessTimeoutSeconds must be a positive integer.");
        }

        if (o.MaximumProcessOutputBytes <= 0)
        {
            errors.Add("MaximumProcessOutputBytes must be a positive integer.");
        }

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors.Select(e => VideoSemanticSegmentationOptions.SectionName + ":" + e));
    }
}
