using Microsoft.Extensions.Options;

namespace NubArca.Api.Ai.Video.Faces;

// VFACE-01: configuration for the canonical video face-track substrate.
//
// Bound from "Ai:VideoFaceAnalysis" (env: Ai__VideoFaceAnalysis__Enabled, …).
// DISABLED BY DEFAULT, like every AI capability: with Enabled=false nothing is
// scheduled, no FFmpeg frame process ever runs and no inference happens. Video
// face analysis is deliberately NOT inferred from the photo face flag, nor from
// the VSEM-02 visual-embedding flag.
//
// AnalysisVersion is the reanalysis key. Bump it whenever the SAMPLING policy or
// the TRACKING/AGGREGATION semantics change the produced tracks — the new version
// writes a NEW analysis next to the old one (unique on manifest + version +
// profile pair), so a reanalysis is always additive and reversible.
//
// The AI profile is NOT configured here: video face tracks reuse the ACTIVE face
// package profile (payload override > Ai__FaceProfileKey > the face-embedding
// capability default), so video tracks always live in the SAME recognition space
// as the photo face substrate.
//
// Face analysis reuses the VSEM-02 FFmpeg frame extractor, but NOT its
// resolution: FrameMaxEdge below is this pipeline's own setting and is passed to
// the extractor per invocation. The two pipelines have genuinely different needs
// (a SigLIP2 tower resizes to 384 anyway, while a face detector's usable minimum
// face size scales directly with the frame edge), so changing one must never
// move the other. Only the per-process TRANSPORT limits (timeout, stdout cap)
// remain shared with "Ai:VideoVisualEmbeddings".
public class VideoFaceAnalysisOptions
{
    public const string SectionName = "Ai:VideoFaceAnalysis";

    public bool Enabled { get; set; } = false;

    // Effective output version. Must be positive; changing it makes every blob a
    // candidate again without touching the previous analyses.
    public int AnalysisVersion { get; set; } = 1;

    // ---- sampling policy (Gate 1) ------------------------------------------
    // VSEM-01 samples ONE frame per segment (2–20 s, target 8 s). That density
    // is right for semantic retrieval and far too sparse for tracking: at an
    // 8-second spacing bounding-box continuity carries no information and the
    // association degenerates into clustering. Face analysis therefore uses its
    // OWN deterministic sampling policy over the SAME segment boundaries, and
    // never reads or writes a VSEM sample row.

    // Nominal spacing between sampled frames inside a segment.
    public int FrameIntervalMilliseconds { get; set; } = 1000;

    public int MaximumFramesPerSegment { get; set; } = 60;

    // Hard cap per video. When the plan exceeds it the frames are thinned
    // EVENLY across the whole video (never truncated at the head), so a long
    // video keeps uniform coverage at a coarser effective interval.
    public int MaximumFramesPerVideo { get; set; } = 900;

    public int MaximumFacesPerFrame { get; set; } = 8;

    // Frames are downscaled to fit within this box, SOURCE ASPECT PRESERVED
    // (never cropped, padded or stretched). It is the single biggest lever on
    // both cost and detection reach: MinimumFaceSizePixels is measured against
    // the resulting frame, so raising this makes distant faces detectable and
    // makes every frame more expensive to decode and detect on.
    //
    // INDEPENDENT of Ai:VideoVisualEmbeddings:FrameMaxEdge by construction.
    public int FrameMaxEdge { get; set; } = DefaultFrameMaxEdge;

    public const int DefaultFrameMaxEdge = 768;

    // ---- detection acceptance ----------------------------------------------

    public double MinimumDetectionConfidence { get; set; } = 0.7;

    // Minimum face box edge, in pixels of the ACTUAL extracted frame (measured
    // from the decoded frame dimensions, not assumed from configuration).
    public int MinimumFaceSizePixels { get; set; } = 40;

    // Face size at which the size component of the quality score saturates.
    public int QualityReferenceFaceSizePixels { get; set; } = 160;

    public double MinimumQualityScore { get; set; } = 0.2;

    // ---- association (Gate 2) ----------------------------------------------

    // A track with no detection for longer than this is closed. A real person
    // reappearing later legitimately starts a NEW track.
    public int MaximumTrackGapMilliseconds { get; set; } = 2000;

    // Evidence floor: a track with fewer accepted detections is discarded.
    public int MinimumTrackDetections { get; set; } = 3;

    // A candidate association must clear the embedding gate AND one of the two
    // spatial gates (box overlap, or a small centre move with a compatible
    // scale). All three are unit-scale on normalized coordinates.
    public double MinimumAssociationSimilarity { get; set; } = 0.35;

    public double MinimumAssociationIou { get; set; } = 0.2;

    public double MaximumAssociationCenterDistance { get; set; } = 0.15;

    public double MaximumAssociationScaleRatio { get; set; } = 2.0;

    // ---- aggregation (Gate 3) ----------------------------------------------

    // Detections whose cosine similarity to the provisional track mean falls
    // below this are rejected as outliers before the final aggregate is built.
    public double TrackOutlierSimilarity { get; set; } = 0.3;

    // ---- cost ceiling -------------------------------------------------------

    // Wall-clock ceiling for the WHOLE analysis of ONE video (staging, frame
    // extraction, detection, embedding and tracking together). Exceeding it is a
    // retryable `timeout`, never a content skip.
    public int ProcessTimeoutSeconds { get; set; } = 15 * 60;
}

// Fails fast at startup on a configuration that could produce unbounded work or
// meaningless tracks. Validation runs even when Enabled=false so a broken section
// is caught before someone flips the switch in production.
public sealed class VideoFaceAnalysisOptionsValidator
    : IValidateOptions<VideoFaceAnalysisOptions>
{
    // The detector's own letterboxed input edge (SCRFD in both antelopev2 and
    // buffalo packages). A frame smaller than this is upsampled into the
    // detector, so nothing is gained and small faces are lost outright — the
    // same reasoning VSEM-02 applies with the SigLIP2 384 input edge.
    public const int MinimumFrameEdge = 640;

    // Mirrors the repository's existing ceiling for a derived image edge
    // (MediaDerivativesOptions.MaxMediumPreviewMaxEdge): beyond it a single
    // decoded frame stops being a bounded cost.
    public const int MaximumFrameEdge = 8192;

    public ValidateOptionsResult Validate(string? name, VideoFaceAnalysisOptions o)
    {
        var errors = new List<string>();

        void Positive(string label, int value)
        {
            if (value <= 0)
            {
                errors.Add($"{label} must be a positive integer.");
            }
        }

        void Unit(string label, double value)
        {
            if (!double.IsFinite(value) || value < 0d || value > 1d)
            {
                errors.Add($"{label} must be a finite value within [0, 1].");
            }
        }

        Positive(nameof(o.AnalysisVersion), o.AnalysisVersion);
        Positive(nameof(o.FrameIntervalMilliseconds), o.FrameIntervalMilliseconds);
        Positive(nameof(o.MaximumFramesPerSegment), o.MaximumFramesPerSegment);
        Positive(nameof(o.MaximumFramesPerVideo), o.MaximumFramesPerVideo);
        Positive(nameof(o.MaximumFacesPerFrame), o.MaximumFacesPerFrame);
        Positive(nameof(o.MinimumFaceSizePixels), o.MinimumFaceSizePixels);
        Positive(nameof(o.QualityReferenceFaceSizePixels), o.QualityReferenceFaceSizePixels);
        Positive(nameof(o.MaximumTrackGapMilliseconds), o.MaximumTrackGapMilliseconds);
        Positive(nameof(o.MinimumTrackDetections), o.MinimumTrackDetections);
        Positive(nameof(o.ProcessTimeoutSeconds), o.ProcessTimeoutSeconds);

        Unit(nameof(o.MinimumDetectionConfidence), o.MinimumDetectionConfidence);
        Unit(nameof(o.MinimumQualityScore), o.MinimumQualityScore);
        Unit(nameof(o.MinimumAssociationSimilarity), o.MinimumAssociationSimilarity);
        Unit(nameof(o.MinimumAssociationIou), o.MinimumAssociationIou);
        Unit(nameof(o.MaximumAssociationCenterDistance), o.MaximumAssociationCenterDistance);
        Unit(nameof(o.TrackOutlierSimilarity), o.TrackOutlierSimilarity);

        if (!double.IsFinite(o.MaximumAssociationScaleRatio) || o.MaximumAssociationScaleRatio < 1d)
        {
            errors.Add("MaximumAssociationScaleRatio must be a finite value of at least 1.");
        }

        if (o.FrameMaxEdge < MinimumFrameEdge || o.FrameMaxEdge > MaximumFrameEdge)
        {
            errors.Add(
                $"FrameMaxEdge must be within [{MinimumFrameEdge}, {MaximumFrameEdge}] pixels.");
        }

        // A gate no face could ever clear makes the whole analysis silently
        // empty at this resolution.
        if (o.FrameMaxEdge > 0 && o.MinimumFaceSizePixels > o.FrameMaxEdge)
        {
            errors.Add("MinimumFaceSizePixels must not exceed FrameMaxEdge.");
        }

        if (o.MinimumFaceSizePixels > o.QualityReferenceFaceSizePixels)
        {
            errors.Add(
                "MinimumFaceSizePixels must not exceed QualityReferenceFaceSizePixels "
                + "(every accepted face would otherwise saturate the quality score).");
        }

        // A per-video cap below the per-segment cap makes the segment cap dead
        // configuration and hides the real bound from the operator.
        if (o.MaximumFramesPerVideo > 0 && o.MaximumFramesPerSegment > o.MaximumFramesPerVideo)
        {
            errors.Add("MaximumFramesPerSegment must not exceed MaximumFramesPerVideo.");
        }

        // A gap shorter than the sampling interval closes every track after a
        // single detection, so no track could ever reach the evidence floor.
        if (o.FrameIntervalMilliseconds > 0 && o.MaximumTrackGapMilliseconds > 0
            && o.MaximumTrackGapMilliseconds < o.FrameIntervalMilliseconds)
        {
            errors.Add(
                "MaximumTrackGapMilliseconds must be at least FrameIntervalMilliseconds "
                + "(otherwise every track closes after one detection).");
        }

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors.Select(e => VideoFaceAnalysisOptions.SectionName + ":" + e));
    }
}
