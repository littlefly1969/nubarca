using Microsoft.Extensions.Options;
using NubArca.Api.Ai.Backends;
using NubArca.Api.Ai.Diagnostics;
using NubArca.Api.Ai.Resolution;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Jobs;

namespace NubArca.Api.Ai.Jobs;

public sealed class AiDocumentsEmbeddingsBackfillJobHandler : AiSkeletonBackfillJobHandler<ITextEmbedder>
{
    public AiDocumentsEmbeddingsBackfillJobHandler(
        IOptions<AiOptions> options, IAiBackendResolver resolver, IAiDiagnosticsWriter diagnostics)
        : base(options, resolver, diagnostics)
    {
    }

    public override string JobType => JobTypes.AiDocumentsEmbeddingsBackfill;
    protected override string Capability => AiCapabilities.DocumentEmbedding;
    protected override bool CapabilityEnabled(AiOptions options) => options.DocumentEmbeddingsEnabled;
}
