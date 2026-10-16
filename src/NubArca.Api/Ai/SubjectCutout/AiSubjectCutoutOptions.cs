namespace NubArca.Api.Ai.SubjectCutout;

// Owner-private "copy subject": a salient-object segmentation model lifts the
// subject of a photograph off its background and the owner gets a transparent
// PNG to copy, download or keep as a new file. Bound from "Ai:SubjectCutout"
// (env Ai__SubjectCutout__*).
//
// OFF by default, and additionally inert while the AI master switch
// (Ai:Enabled) is off. Nothing runs in the background: a cutout is computed
// only when the owner asks for one, so there is no backfill, no per-blob status
// row and no job.
//
// The model is BiRefNet-lite (MIT), the stock 1024x1024 ONNX export. See
// docs/model-deployment/subject-cutout.md for the file, its checksum and the
// measured cost.
public class AiSubjectCutoutOptions
{
    public bool Enabled { get; set; } = false;

    // Model file, relative to Ai:Onnx:ModelDir (an absolute path is used as is).
    public string ModelFile { get; set; } = "birefnet-lite/model.onnx";

    // Square input edge of the model. The stock export is static 1024x1024; a
    // different value only works with a model exported at that size.
    public int InputSize { get; set; } = 1024;

    // How long a request waits for the single inference slot before the API
    // answers "busy" instead of piling requests up behind a multi-second model.
    public int QueueTimeoutSeconds { get; set; } = 20;

    // Hard ceiling on one inference (0 disables it).
    public int TimeoutSeconds { get; set; } = 120;
}

public static class AiSubjectCutoutLimits
{
    public const int MinInputSize = 256;
    public const int MaxInputSize = 2048;
}
