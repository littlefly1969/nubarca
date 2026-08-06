namespace NubArca.Api.Ai.Onnx;

// Phase 2A: catalog of local ONNX image-embedding model candidates for the
// evaluation harness. Preprocessing config lives in CODE (versioned, reviewable)
// rather than the DB: an AiProfile links to a catalog entry via its short
// ConfigHash (the catalog key = the model's directory name). No weights are
// committed — models are loaded from Ai__Onnx__ModelDir/<ModelSubdir>/<ModelFile>
// at runtime. See docs/ai-image-onnx-evaluation.md for the full rationale,
// export path, and preprocessing assumptions (which this harness exists to
// validate before any production reindex).
public sealed record OnnxImageModelConfig(
    string Key,            // catalog key == model dir name == AiModel.Key
    string ModelSubdir,    // subfolder under Ai__Onnx__ModelDir
    string ModelFile,      // ONNX file name within the subfolder
    int InputSize,         // square input edge (HxW), pixels
    string ResizeMode,     // OnnxResizeModes.*
    float[] Mean,          // per-channel RGB mean (0..1 scale)
    float[] Std,           // per-channel RGB std (0..1 scale)
    string InputTensor,    // ONNX input tensor name
    string? OutputTensor,  // ONNX output tensor name; null = first/only output
    int Dimension,         // expected embedding dimension
    string? TextModelFile = null,
    string? TokenizerFile = null,
    string TextInputTensor = "input_ids",
    string TextAttentionMaskTensor = "attention_mask",
    string TextOutputTensor = "text_embeds",
    int TextSequenceLength = 64);

public static class OnnxResizeModes
{
    // Resize directly to InputSize x InputSize (SigLIP-style square resize).
    public const string Stretch = "stretch";

    // Resize so the shortest side == InputSize, then center-crop to a square
    // (ImageNet/DINOv2-style).
    public const string ShortestCrop = "shortest-crop";
}

public static class OnnxImageModels
{
    public const string DefaultModelFile = "model.onnx";
    public const string DefaultTextModelFile = "text_model.onnx";
    public const string DefaultTokenizerFile = "tokenizer.json";

    // SigLIP normalization maps [0,1] → [-1,1] (mean=std=0.5). DINOv2 uses the
    // ImageNet statistics. These are documented assumptions the harness verifies.
    private static readonly float[] SigLipMean = { 0.5f, 0.5f, 0.5f };
    private static readonly float[] SigLipStd = { 0.5f, 0.5f, 0.5f };
    // The production catalog deliberately contains one high-quality multimodal
    // profile. The former 768-dim base/DINO evaluation profiles were removed:
    // image similarity and text-to-image retrieval now share the SAME 1152-dim
    // SigLIP2 So400m space and can never accidentally mix model generations.
    public const string SiglipSo400mKey = "siglip2-so400m-patch14-384";

    public static readonly IReadOnlyDictionary<string, OnnxImageModelConfig> Catalog =
        new Dictionary<string, OnnxImageModelConfig>(StringComparer.Ordinal)
        {
            [SiglipSo400mKey] = new(
                Key: SiglipSo400mKey, ModelSubdir: SiglipSo400mKey, ModelFile: DefaultModelFile,
                InputSize: 384, ResizeMode: OnnxResizeModes.Stretch,
                Mean: SigLipMean, Std: SigLipStd,
                InputTensor: "pixel_values", OutputTensor: "image_embeds", Dimension: 1152,
                TextModelFile: DefaultTextModelFile, TokenizerFile: DefaultTokenizerFile),
        };

    public const string SiglipSo400mProfileKey = "photo-siglip2-so400m-patch14-384-v2";

    // Profile key → catalog key (stored in AiProfile.ConfigHash at seed time).
    public static readonly IReadOnlyDictionary<string, string> ProfileToCatalogKey =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [SiglipSo400mProfileKey] = SiglipSo400mKey,
        };

    // Resolve a catalog config for a profile: prefer the profile's ConfigHash
    // (the catalog key), else derive from the profile key. Returns null when the
    // profile is not a known ONNX image profile.
    public static OnnxImageModelConfig? ResolveConfig(string? configHash, string profileKey)
    {
        if (!string.IsNullOrWhiteSpace(configHash) && Catalog.TryGetValue(configHash, out var byHash))
        {
            return byHash;
        }

        if (ProfileToCatalogKey.TryGetValue(profileKey, out var catalogKey)
            && Catalog.TryGetValue(catalogKey, out var byProfile))
        {
            return byProfile;
        }

        return null;
    }
}
