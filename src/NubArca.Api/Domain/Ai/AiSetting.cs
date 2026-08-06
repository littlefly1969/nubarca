namespace NubArca.Api.Domain.Ai;

// Minimal persisted key/value store for NON-SECRET AI settings overrides (e.g.
// admin-tuned face similarity thresholds). Values layer OVER the config defaults
// in AiOptions. NEVER stores secrets, tokens, keys, or model internals — only
// safe, admin-editable numeric/string settings. Keys are stable dotted tokens
// (see FaceSettingKeys).
public class AiSetting
{
    // Stable setting key (PK).
    public string Key { get; set; } = string.Empty;

    // Serialized value (invariant-culture for numbers). Non-secret only.
    public string Value { get; set; } = string.Empty;

    public DateTime UpdatedAt { get; set; }

    // Admin who last changed it (audit); nullable.
    public Guid? UpdatedByUserId { get; set; }
}
