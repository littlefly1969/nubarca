namespace NubArca.Api.Ai.NaturalGallery;

// A local command interpreter: turns a natural-language gallery command into a
// strict RAW structured draft (person text spans, normalised dates, semantic vs
// metadata split, operation). Implementations are LOCAL only — a built-in
// deterministic IT/EN grammar, or a call to an isolated internal-only decoder
// sidecar. No cloud, ever. The interpreter proposes; the server validates.
public interface INaturalGalleryCommandInterpreter
{
    // Stable key for audit/metrics (safe: "deterministic", "onnx:<model>"). Never
    // a value derived from user text.
    string Key { get; }

    // True when this backend can serve requests right now (e.g. sidecar reachable
    // + warm). A false result lets the service fall back or report unavailable.
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);

    // Throws InterpreterBusyException / InterpreterTimeoutException /
    // InterpreterUnavailableException / InterpreterMalformedException so the
    // service can map each to the right outcome. Returns a raw draft otherwise.
    Task<RawGalleryCommand> InterpretAsync(
        GalleryCommandContext context, CancellationToken cancellationToken = default);
}

public sealed class InterpreterUnavailableException : Exception
{
    public InterpreterUnavailableException(string? message = null) : base(message) { }
}

public sealed class InterpreterBusyException : Exception
{
    public InterpreterBusyException(string? message = null) : base(message) { }
}

public sealed class InterpreterTimeoutException : Exception
{
    public InterpreterTimeoutException(string? message = null) : base(message) { }
}

public sealed class InterpreterMalformedException : Exception
{
    public InterpreterMalformedException(string? message = null) : base(message) { }
}
