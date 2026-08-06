using Microsoft.Extensions.Options;
using NubArca.Api.Ai.Backends;
using NubArca.Api.Ai.Diagnostics;
using NubArca.Api.Ai.Resolution;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Jobs;

namespace NubArca.Api.Ai.Jobs;

public sealed class AiTagsGenerateBackfillJobHandler : AiSkeletonBackfillJobHandler<IAiTagger>
{
    public AiTagsGenerateBackfillJobHandler(
        IOptions<AiOptions> options, IAiBackendResolver resolver, IAiDiagnosticsWriter diagnostics)
        : base(options, resolver, diagnostics)
    {
    }

    public override string JobType => JobTypes.AiTagsGenerateBackfill;
    protected override string Capability => AiCapabilities.Tagging;
    protected override bool CapabilityEnabled(AiOptions options) => options.TagsEnabled;
}
