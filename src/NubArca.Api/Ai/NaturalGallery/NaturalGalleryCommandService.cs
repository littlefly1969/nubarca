using Microsoft.Extensions.Options;
using NubArca.Api.Ai;

namespace NubArca.Api.Ai.NaturalGallery;

// Orchestrates one LOCAL interpret call: picks the configured interpreter
// backend (deterministic grammar or the isolated decoder sidecar), runs it under
// a hard timeout, then hands the raw draft to deterministic validation +
// owner-scoped person/date resolution. It maps every failure mode to a distinct
// InterpretOutcome so the endpoint can return the right user message + status
// WITHOUT ever surfacing a raw exception, model output, or the command text.
public sealed class NaturalGalleryCommandService
{
    private readonly IReadOnlyDictionary<string, INaturalGalleryCommandInterpreter> _interpreters;
    private readonly GalleryCommandValidator _validator;
    private readonly AiNaturalGallerySearchOptions _options;
    private readonly TimeProvider _clock;

    public NaturalGalleryCommandService(
        IEnumerable<INaturalGalleryCommandInterpreter> interpreters,
        GalleryCommandValidator validator,
        IOptions<AiOptions> options,
        TimeProvider clock)
    {
        _interpreters = interpreters.ToDictionary(i => InterpreterFamily(i.Key), StringComparer.OrdinalIgnoreCase);
        _validator = validator;
        _options = options.Value.NaturalGallerySearch;
        _clock = clock;
    }

    public async Task<InterpretOutcome> InterpretAsync(
        Guid ownerUserId, InterpretCommandRequest request, CancellationToken cancellationToken = default)
    {
        var command = request.Command?.Trim() ?? "";
        if (command.Length == 0 || command.Length > _options.MaxCommandLength)
        {
            return new InterpretOutcome(InterpretOutcomeKind.Unsupported, null, _options.Interpreter);
        }

        var (interpreter, key) = SelectInterpreter();
        if (interpreter is null)
        {
            return new InterpretOutcome(InterpretOutcomeKind.ModelUnavailable, null, key);
        }

        var context = new GalleryCommandContext(
            command,
            string.IsNullOrWhiteSpace(request.Locale) ? "it-IT" : request.Locale!,
            ResolveTimeZone(request.TimeZone),
            _clock.GetUtcNow(),
            request.CurrentFilters ?? new CurrentFilterStateDto());

        RawGalleryCommand raw;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.InterpretTimeoutSeconds)));
            raw = await interpreter.InterpretAsync(context, cts.Token);
        }
        catch (InterpreterBusyException)
        {
            return new InterpretOutcome(InterpretOutcomeKind.ModelBusy, null, interpreter.Key);
        }
        catch (InterpreterTimeoutException)
        {
            return new InterpretOutcome(InterpretOutcomeKind.Timeout, null, interpreter.Key);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new InterpretOutcome(InterpretOutcomeKind.Timeout, null, interpreter.Key);
        }
        catch (InterpreterMalformedException)
        {
            return new InterpretOutcome(InterpretOutcomeKind.Malformed, null, interpreter.Key);
        }
        catch (InterpreterUnavailableException)
        {
            // Selected backend went unavailable mid-call: optionally fall back.
            if (_options.FallbackToDeterministic
                && _interpreters.TryGetValue("deterministic", out var fallback)
                && !ReferenceEquals(fallback, interpreter))
            {
                raw = await fallback.InterpretAsync(context, cancellationToken);
                var fb = await _validator.ValidateAsync(ownerUserId, raw, context.CurrentFilters, cancellationToken);
                return new InterpretOutcome(InterpretOutcomeKind.Ok, fb, fallback.Key);
            }
            return new InterpretOutcome(InterpretOutcomeKind.ModelUnavailable, null, interpreter.Key);
        }

        var response = await _validator.ValidateAsync(ownerUserId, raw, context.CurrentFilters, cancellationToken);
        return new InterpretOutcome(InterpretOutcomeKind.Ok, response, interpreter.Key);
    }

    private (INaturalGalleryCommandInterpreter? Interpreter, string Key) SelectInterpreter()
    {
        var requested = InterpreterFamily(_options.Interpreter);
        if (_interpreters.TryGetValue(requested, out var chosen))
        {
            // For the sidecar, honour availability + optional fallback synchronously
            // only where cheap; deeper availability is handled in InterpretAsync.
            return (chosen, chosen.Key);
        }
        if (_options.FallbackToDeterministic && _interpreters.TryGetValue("deterministic", out var det))
        {
            return (det, det.Key);
        }
        return (null, _options.Interpreter);
    }

    private static string InterpreterFamily(string key)
    {
        var idx = key.IndexOf(':');
        return (idx < 0 ? key : key[..idx]).Trim().ToLowerInvariant();
    }

    // Client tz → server TimeZoneInfo. Falls back to Europe/Rome then UTC; never
    // throws (a bad tz must not fail interpretation).
    private static TimeZoneInfo ResolveTimeZone(string? id)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id!); }
            catch { /* fall through */ }
        }
        try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/Rome"); }
        catch { return TimeZoneInfo.Utc; }
    }
}
