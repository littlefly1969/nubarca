namespace NubArca.Api.Files;

// Resource caps for synchronous image decoding during upload. Bound via the
// "ImageProcessing" configuration section.
//
// Defaults are sized for personal-cloud use:
//   * MaxWidth / MaxHeight = 8192 — covers every consumer-grade camera and
//     phone (a 50 MP DSLR's longest edge is 8160).
//   * MaxPixels = 64_000_000 — 64 megapixels. A square at the dimension limit
//     would be 67 MP, so this is the tighter cap that rejects ultra-wide
//     panoramas + decompression bombs while still admitting every typical
//     phone / camera output.
//   * MaxThumbnailInputBytes = 30 MiB — most JPEGs fit; rejects pathological
//     PNG/TIFF where a small file decodes into hundreds of MB.
//   * EnableThumbnails = true — operators can flip to false to disable
//     thumbnail generation entirely (e.g., on a constrained host).
//
// Increasing any cap is a CPU / memory commitment: a 100 MP image takes
// roughly 400 MB of working memory to decode and a measurable second of CPU
// to resize. Operators should benchmark before raising.
public sealed class ImageProcessingOptions
{
    public const string SectionName = "ImageProcessing";

    public bool EnableThumbnails { get; set; } = true;

    public int MaxWidth { get; set; } = 8192;

    public int MaxHeight { get; set; } = 8192;

    public long MaxPixels { get; set; } = 64_000_000;

    public long MaxThumbnailInputBytes { get; set; } = 30L * 1024 * 1024;
}
