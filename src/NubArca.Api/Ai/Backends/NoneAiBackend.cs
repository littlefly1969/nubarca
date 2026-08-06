using NubArca.Api.Domain.Ai;

namespace NubArca.Api.Ai.Backends;

// The "none" provider: the default, no-op backend. It serves NO capability, so
// any profile bound to a none-provider model resolves as unavailable. This is
// an ENVIRONMENT/config state, not a content failure — the resolver returns an
// unavailable result and callers do NOTHING (they must never write skipped/
// failed per-blob status rows for a none/unavailable provider).
//
// It implements only IAiBackend (no capability interfaces) so the resolver can
// never hand it out as a usable embedder/extractor/etc.
public sealed class NoneAiBackend : IAiBackend
{
    public string Provider => AiProviders.None;

    public bool Supports(string capability) => false;
}
