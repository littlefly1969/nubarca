namespace NubArca.Api.Assistant;

/// Where a configured model endpoint sits relative to the NubArca trust
/// boundary.
///
/// TRUST IS NOT PROTOCOL. An endpoint speaks the OpenAI-compatible wire format
/// whether it is a hosted provider, an operator's own Ollama/vLLM/llama.cpp
/// server, or a future NubArca-managed runtime — and none of that says who ends
/// up holding the bytes. So the two are separate axes, and this one is stated by
/// the operator rather than inferred.
///
/// It is in particular NEVER inferred from the URL. `localhost`, `127.0.0.1`, an
/// RFC1918 address, a Docker service name and plaintext HTTP are all things a
/// reverse proxy in front of a cloud API also looks like; a trusted GPU box on
/// the next rack is none of them. Guessing would be wrong in both directions,
/// and wrong in the direction that leaks.
public enum AssistantModelTrust
{
    /// Data given to this model may leave the NubArca trust boundary. Only
    /// public product knowledge and what the user typed themselves may be sent.
    External,

    /// The operator asserts that this endpoint is under their control and may
    /// receive private NubArca context in features designed for it.
    ///
    /// It is an ASSERTION, not a proof: NubArca does not control the process on
    /// the other end and cannot promise it has no egress. The UI copy says so.
    LocalTrusted,

    /// Reserved for a runtime whose isolation and egress lifecycle NubArca
    /// itself owns.
    ///
    /// NubArca does not ship one. The value exists so the enum does not have to
    /// change shape when it does, and configuration validation REFUSES it — see
    /// AssistantModelResolver. Accepting it today would mean an installation
    /// could claim an isolation guarantee that nothing implements.
    ManagedLocal,
}

/// The wire format a model endpoint speaks.
public enum AssistantModelProtocol
{
    /// POST {BaseUrl}/v1/chat/completions, the interoperability target rather
    /// than a vendor. The only protocol implemented.
    OpenAiCompatible,
}
