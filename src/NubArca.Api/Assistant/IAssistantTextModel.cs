namespace NubArca.Api.Assistant;

/// One turn, in the only three roles the Assistant uses.
public enum AssistantRole
{
    System,
    User,
    Assistant,
}

public sealed record AssistantMessage(AssistantRole Role, string Text);

/// What came back, or why nothing did. `Reason` is a SANITIZED code — never a
/// provider body, never an exception message, never a URL — because it travels
/// to the browser.
public sealed record AssistantCompletion(bool Ok, string? Text, string? Reason)
{
    public static AssistantCompletion Success(string text) => new(true, text, null);
    public static AssistantCompletion Failure(string reason) => new(false, null, reason);
}

/// The TEXT-ONLY model runtime.
///
///     messages -> completion text, or a sanitized failure
///
/// That is the entire contract, and the narrowness is the point: there is
/// nowhere in this signature to pass tools, function definitions, a tool choice,
/// an attachment, an image, a document reference or a callback the model could
/// invoke. A capability that cannot be expressed here cannot be granted by a
/// later edit to a prompt.
///
/// There is deliberately no optional `tools = null` parameter "for later"
/// either. ABSENCE is the contract, and an optional parameter is presence with a
/// default. When tool calling arrives it belongs behind its own interface, so a
/// text-only external Help cannot acquire tools because a shared type grew a
/// property.
///
/// Vendor neutrality is the other half: NubArca depends on this interface and on
/// its own DTOs, never on a provider SDK, so a second protocol is a new class
/// rather than a change to everything that talks to it.
public interface IAssistantTextModel
{
    Task<AssistantCompletion> CompleteAsync(
        AssistantModelProfile profile,
        IReadOnlyList<AssistantMessage> messages,
        CancellationToken cancellationToken = default);
}
