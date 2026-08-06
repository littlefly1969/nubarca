namespace NubArca.Api.Aesthetics.Sidecar;

// STRICT validation of a sidecar response before ANY value is trusted or
// persisted. Rejects partial / malformed / duplicated / non-finite / out-of-
// range output. Returns a normalized, safe result or a stable error code.
//
// The rule set (per the task's "Validate strictly" list):
//   * contract version matches;
//   * profile key matches the request;
//   * every completed capability was requested AND is a known capability;
//   * for expert_scores: EXACTLY the 12 catalog keys, each once, no extras;
//   * numeric values finite and within their declared [min,max];
//   * declared scale equals the catalog scale for known keys;
//   * text kinds known, language present, each text within the size cap;
//   * warning count/size bounded.
public static class AestheticSidecarResponseValidator
{
    public sealed record ValidationResult(
        bool Ok,
        string? ErrorCode,
        IReadOnlyList<ValidatedMetric> Metrics,
        IReadOnlyList<ValidatedText> Texts,
        IReadOnlyList<string> Warnings,
        IReadOnlyList<string> CompletedCapabilities);

    public sealed record ValidatedMetric(
        string Key, string Group, double Value, double ScaleMin, double ScaleMax,
        double? Confidence, int Version);

    public sealed record ValidatedText(
        string Kind, string Language, string Text, int? PromptTemplateVersion);

    private static ValidationResult Fail(string code) =>
        new(false, code, Array.Empty<ValidatedMetric>(), Array.Empty<ValidatedText>(),
            Array.Empty<string>(), Array.Empty<string>());

    public static ValidationResult Validate(
        AestheticSidecarResponse response,
        AestheticSidecarRequest request)
    {
        if (response is null)
        {
            return Fail(AestheticErrorCodes.InvalidModelOutput);
        }
        if (response.ContractVersion != AestheticSidecarContract.Version)
        {
            return Fail(AestheticErrorCodes.InvalidModelOutput);
        }
        if (!string.Equals(response.ProfileKey, request.ProfileKey, StringComparison.Ordinal))
        {
            return Fail(AestheticErrorCodes.InvalidModelOutput);
        }

        // Completed capabilities: each requested + known, no duplicates.
        var completed = new List<string>();
        foreach (var cap in response.CompletedCapabilities ?? Array.Empty<string>())
        {
            if (!AestheticCapabilities.IsKnown(cap)
                || !request.Capabilities.Contains(cap)
                || completed.Contains(cap))
            {
                return Fail(AestheticErrorCodes.InvalidModelOutput);
            }
            completed.Add(cap);
        }
        if (completed.Count == 0)
        {
            return Fail(AestheticErrorCodes.InvalidModelOutput);
        }

        // Metrics: bounded, unique keys, finite, in-range, valid scale.
        var metrics = response.Metrics ?? Array.Empty<AestheticSidecarMetric>();
        if (metrics.Count > AestheticSidecarContract.MaxMetrics)
        {
            return Fail(AestheticErrorCodes.InvalidModelOutput);
        }
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        var validated = new List<ValidatedMetric>(metrics.Count);
        foreach (var m in metrics)
        {
            if (string.IsNullOrWhiteSpace(m.Key) || !seenKeys.Add(m.Key))
            {
                return Fail(AestheticErrorCodes.InvalidModelOutput);
            }
            if (!IsFinite(m.Value) || !IsFinite(m.ScaleMin) || !IsFinite(m.ScaleMax)
                || m.ScaleMin >= m.ScaleMax
                || m.Value < m.ScaleMin || m.Value > m.ScaleMax)
            {
                return Fail(AestheticErrorCodes.InvalidModelOutput);
            }
            if (m.Confidence is double c && (!IsFinite(c) || c < 0 || c > 1))
            {
                return Fail(AestheticErrorCodes.InvalidModelOutput);
            }
            if (m.Version <= 0)
            {
                return Fail(AestheticErrorCodes.InvalidModelOutput);
            }

            // For KNOWN expert-score keys, the declared scale MUST match the
            // catalog exactly (guards against a mis-scaled model output).
            if (AestheticMetricCatalog.IsExpertScoreKey(m.Key))
            {
                if (m.ScaleMin != AestheticMetricCatalog.ScaleMin
                    || m.ScaleMax != AestheticMetricCatalog.ScaleMax)
                {
                    return Fail(AestheticErrorCodes.InvalidModelOutput);
                }
            }

            validated.Add(new ValidatedMetric(
                m.Key,
                AestheticMetricCatalog.GroupFor(m.Key),
                m.Value, m.ScaleMin, m.ScaleMax, m.Confidence, m.Version));
        }

        // If expert_scores completed, require EXACTLY the 12 catalog keys.
        if (completed.Contains(AestheticCapabilities.ExpertScores))
        {
            var keys = validated.Select(v => v.Key).ToHashSet(StringComparer.Ordinal);
            if (keys.Count != AestheticMetricCatalog.ExpertScoreKeys.Count
                || !AestheticMetricCatalog.ExpertScoreKeys.All(keys.Contains))
            {
                return Fail(AestheticErrorCodes.InvalidModelOutput);
            }
        }

        // Texts: bounded, known kind, language present, size-capped.
        var texts = response.Texts ?? Array.Empty<AestheticSidecarText>();
        if (texts.Count > AestheticSidecarContract.MaxTexts)
        {
            return Fail(AestheticErrorCodes.InvalidModelOutput);
        }
        var validatedTexts = new List<ValidatedText>(texts.Count);
        foreach (var t in texts)
        {
            if (!AestheticTextKinds.IsKnown(t.Kind)
                || string.IsNullOrWhiteSpace(t.Language)
                || t.Text is null
                || t.Text.Length > AestheticSidecarContract.MaxTextLength)
            {
                return Fail(AestheticErrorCodes.InvalidModelOutput);
            }
            validatedTexts.Add(new ValidatedText(
                t.Kind, t.Language.Trim(), t.Text, t.PromptTemplateVersion));
        }

        // Warnings: bounded count + length; truncate defensively.
        var warnings = new List<string>();
        foreach (var w in response.Warnings ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(w))
            {
                continue;
            }
            var safe = w.Length > AestheticSidecarContract.MaxWarningLength
                ? w[..AestheticSidecarContract.MaxWarningLength]
                : w;
            warnings.Add(safe);
            if (warnings.Count >= AestheticSidecarContract.MaxWarnings)
            {
                break;
            }
        }

        return new ValidationResult(true, null, validated, validatedTexts, warnings, completed);
    }

    private static bool IsFinite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
}
