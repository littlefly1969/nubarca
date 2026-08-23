using Microsoft.Extensions.Options;

namespace NubArca.Api.Help;

public sealed record HelpTurn(bool FromUser, string Text);

public sealed record HelpAnswer(
    bool Ok,
    string? Text,
    string? Reason,
    IReadOnlyList<string> Sources);

public sealed record ExternalHelpStatus(bool Enabled, string ProviderLabel, bool KnowledgeAvailable);

/// The whole external Help feature, in one place.
///
/// LOOK AT THE CONSTRUCTOR. It takes a provider client, a public-corpus
/// retriever, options and a logger. There is no DbContext, no file service, no
/// folder service, no storage service, no people or face service, no album
/// service, no media or semantic search, no OCR, no metadata service, no AI
/// artifact service. This class could not reach a user's library if its prompt
/// asked it to, because nothing here can read one.
///
/// That is the whole design: the privacy boundary is a dependency boundary, and
/// a reviewer can check it by reading four constructor parameters rather than by
/// auditing a prompt for instructions the model might ignore.
public sealed class ExternalHelpService
{
    private readonly IExternalHelpChatClient _client;
    private readonly IHelpKnowledgeRetriever _knowledge;
    private readonly ExternalHelpOptions _options;
    private readonly ILogger<ExternalHelpService> _log;

    public ExternalHelpService(
        IExternalHelpChatClient client,
        IHelpKnowledgeRetriever knowledge,
        IOptions<ExternalHelpOptions> options,
        ILogger<ExternalHelpService> log)
    {
        _client = client;
        _knowledge = knowledge;
        _options = options.Value;
        _log = log;
    }

    public ExternalHelpStatus GetStatus() => new(
        Enabled: _options.IsUsable,
        ProviderLabel: _options.ProviderLabel,
        KnowledgeAvailable: _knowledge.IsAvailable);

    public async Task<HelpAnswer> AskAsync(
        string question,
        IReadOnlyList<HelpTurn> history,
        CancellationToken cancellationToken = default)
    {
        if (!_options.IsUsable)
        {
            return new HelpAnswer(false, null, HelpFailureReasons.Disabled, Array.Empty<string>());
        }

        var trimmed = (question ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return new HelpAnswer(false, null, HelpFailureReasons.ProviderEmpty, Array.Empty<string>());
        }
        if (trimmed.Length > _options.EffectiveQuestionCharacters)
        {
            trimmed = trimmed[.._options.EffectiveQuestionCharacters];
        }

        // FAIL CLOSED without approved product knowledge.
        //
        // The retriever already refuses a corpus that is missing or built from a
        // different revision. Calling the provider anyway would still be a
        // request that LEAVES NubArca — carrying the user's question to a third
        // party — in exchange for an answer improvised with no product
        // documentation behind it, which is the answer most likely to be wrong
        // about the version actually installed. Paying an outbound call and a
        // privacy boundary crossing for that is the wrong trade.
        //
        // Still an optional-feature failure, never an application-health one.
        if (!_knowledge.IsAvailable)
        {
            _log.LogInformation(
                "external help: refused, no approved product knowledge for the running revision");
            return new HelpAnswer(
                false, null, HelpFailureReasons.KnowledgeUnavailable, Array.Empty<string>());
        }

        var excerpts = _knowledge.Retrieve(
            trimmed, _options.EffectiveContextExcerpts, _options.EffectiveContextCharacters);

        var messages = new List<HelpChatMessage>
        {
            new(HelpChatRole.System, BuildSystemPrompt(excerpts)),
        };
        messages.AddRange(BoundedHistory(history));
        messages.Add(new HelpChatMessage(HelpChatRole.User, trimmed));

        var result = await _client.CompleteAsync(messages, cancellationToken);
        var sources = excerpts.Select(e => e.Path).Distinct().ToList();

        if (!result.Ok)
        {
            // A provider that is down is not NubArca being down: the caller gets
            // a sanitized reason and every other part of the product is
            // unaffected.
            _log.LogInformation("external help: answer failed reason={Reason}", result.Reason);
            return new HelpAnswer(false, null, result.Reason, Array.Empty<string>());
        }
        return new HelpAnswer(true, result.Text, null, sources);
    }

    /// The history the client sent back, bounded twice: by turns and by total
    /// characters, newest first. A client is not trusted to bound itself — the
    /// browser owns this conversation, so an oversized history is a request
    /// shape, not an impossibility.
    private IReadOnlyList<HelpChatMessage> BoundedHistory(IReadOnlyList<HelpTurn> history)
    {
        if (history.Count == 0 || _options.EffectiveHistoryTurns == 0)
        {
            return Array.Empty<HelpChatMessage>();
        }
        var recent = history
            .Where(t => !string.IsNullOrWhiteSpace(t.Text))
            .TakeLast(_options.EffectiveHistoryTurns)
            .ToList();

        var kept = new LinkedList<HelpChatMessage>();
        var budget = _options.EffectiveHistoryCharacters;
        for (var i = recent.Count - 1; i >= 0; i--)
        {
            var text = recent[i].Text.Trim();
            if (text.Length > budget) break;
            budget -= text.Length;
            kept.AddFirst(new HelpChatMessage(
                recent[i].FromUser ? HelpChatRole.User : HelpChatRole.Assistant, text));
        }
        return kept.ToList();
    }

    // The prompt describes the JOB, and carries the public excerpts. It does not
    // ask the model to avoid private data — the model was never given a way to
    // reach any, and an instruction would only suggest that the capability
    // exists.
    private static string BuildSystemPrompt(IReadOnlyList<HelpExcerpt> excerpts)
    {
        var prompt = new System.Text.StringBuilder();
        prompt.AppendLine(
            "You are the help assistant for NubArca, a self-hosted personal media library.");
        prompt.AppendLine(
            "Answer questions about what NubArca is and how to use it, using the product documentation below.");
        prompt.AppendLine(
            "If the documentation does not cover the question, say so plainly instead of guessing.");
        prompt.AppendLine(
            "You cannot see the user's library, files, photos or people, and you cannot perform actions.");
        if (excerpts.Count > 0)
        {
            prompt.AppendLine();
            prompt.AppendLine("--- NubArca product documentation ---");
            foreach (var excerpt in excerpts)
            {
                prompt.AppendLine();
                prompt.AppendLine($"[{excerpt.Path}] {excerpt.Title}");
                prompt.AppendLine(excerpt.Text);
            }
        }
        return prompt.ToString();
    }
}
