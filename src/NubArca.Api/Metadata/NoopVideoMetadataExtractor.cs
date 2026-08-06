using NubArca.Api.Domain;

namespace NubArca.Api.Metadata;

// Default extractor when no video-metadata provider is configured
// (Media:VideoMetadataProvider = "none"). It never touches an external tool
// and reports a benign "skipped / disabled" outcome. In normal operation it is
// never actually invoked: the CLI, the job handler, and post-ingest all gate on
// the configured provider and do no work while it is "none". It exists so the
// IVideoMetadataExtractor dependency is always resolvable.
public sealed class NoopVideoMetadataExtractor : IVideoMetadataExtractor
{
    public Task<VideoMetadataExtractionResult> ExtractAsync(
        Func<CancellationToken, Task<Stream>> openBlobContent,
        CancellationToken cancellationToken)
        => Task.FromResult(VideoMetadataExtractionResult.ForStatus(
            MetadataStatuses.Skipped, MetadataErrorCodes.ProviderDisabled, version: 0));
}
