using Microsoft.Extensions.Options;

namespace NubArca.Api.Ai.Video;

// VSEM-02: configuration for canonical video sample embeddings.
//
// Bound from "Ai:VideoVisualEmbeddings" (env: Ai__VideoVisualEmbeddings__…).
// DISABLED BY DEFAULT like every AI capability: with Enabled=false nothing is
// scheduled, no FFmpeg frame process ever runs and no inference happens. Video
// enablement is deliberately NOT inferred from the photo-embedding flag.
//
// The AI profile is NOT configured here: video samples reuse the ACTIVE photo
// image-embedding profile (payload override > Ai__PhotoSimilarityProfileKey >
// capability default), so the video index always lives in the same embedding
// space as the photo index and its paired text tower.
public class VideoVisualEmbeddingOptions
{
    public const string SectionName = "Ai:VideoVisualEmbeddings";

    public bool Enabled { get; set; } = false;

    // Wall-clock ceiling for ONE frame-extraction process. Each process is a
    // single accurate seek + one decoded frame, so seconds — not minutes.
    public int FrameTimeoutSeconds { get; set; } = 60;

    // Cap on one extracted frame (stdout of one FFmpeg process). Exceeding it
    // is a retryable per-sample failure, never a silently truncated image.
    public int MaximumFrameOutputBytes { get; set; } = 32 * 1024 * 1024;

    // Frames are downscaled to fit within this box, SOURCE ASPECT PRESERVED
    // (never cropped, padded or stretched — the embedder's own preprocessing
    // does the model-input resize). Must not go below the SigLIP2 input edge
    // (384) or inference quality degrades.
    public int FrameMaxEdge { get; set; } = 768;
}

// Fails fast at startup on a configuration that could produce unbounded
// processes or degraded inference input. Runs even when Enabled=false so a
// broken section is caught before the switch is flipped.
public sealed class VideoVisualEmbeddingOptionsValidator
    : IValidateOptions<VideoVisualEmbeddingOptions>
{
    // The SigLIP2 So400m input edge; frames smaller than this would be
    // upsampled by the model preprocessing.
    public const int MinimumFrameEdge = 384;

    public ValidateOptionsResult Validate(string? name, VideoVisualEmbeddingOptions o)
    {
        var errors = new List<string>();

        if (o.FrameTimeoutSeconds <= 0)
        {
            errors.Add("FrameTimeoutSeconds must be a positive integer.");
        }

        if (o.MaximumFrameOutputBytes <= 0)
        {
            errors.Add("MaximumFrameOutputBytes must be a positive integer.");
        }

        if (o.FrameMaxEdge < MinimumFrameEdge)
        {
            errors.Add($"FrameMaxEdge must be at least {MinimumFrameEdge} pixels.");
        }

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors.Select(e => VideoVisualEmbeddingOptions.SectionName + ":" + e));
    }
}
