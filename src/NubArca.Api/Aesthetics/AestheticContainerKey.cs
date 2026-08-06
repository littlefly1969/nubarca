using System.Security.Cryptography;
using System.Text;

namespace NubArca.Api.Aesthetics;

// Hidden, owner-scoped logical container key for a user's Aesthetics Lab:
// __nubarca_aesthetics_{ownerScopedHash}. Mirrors PlateContainerKey exactly —
// an HMAC-SHA256 of the owner id under a configured pepper, versioned and
// non-reversible. Stored on AestheticLabItem.LogicalContainerKey but NEVER
// returned through any API/DTO/log.
public static class AestheticContainerKey
{
    // PERSISTED in AestheticLabItem.LogicalContainerKey. Rewritten in place by
    // migration RenameLogicalContainerKeyPrefixes; see PlateContainerKey for why
    // that is safe (the prefix is concatenated, and nothing queries this column).
    public const string Prefix = "__nubarca_aesthetics_";
    public const string Scheme = "aesthetics:v1:";

    // HMAC key material: changing it changes every key derived WITHOUT a
    // configured pepper. Production configures HumanAesExpert__Pepper, so this
    // fallback is a dev/test convenience there rather than live key material.
    private const string DevelopmentFallbackPepper = "nubarca-aesthetics-dev-pepper-v1";

    public static string Compute(string? pepper, Guid ownerUserId)
    {
        var key = Encoding.UTF8.GetBytes(
            string.IsNullOrEmpty(pepper) ? DevelopmentFallbackPepper : pepper);
        var message = Encoding.UTF8.GetBytes(Scheme + ownerUserId.ToString("N"));
        var mac = HMACSHA256.HashData(key, message);
        return Prefix + Convert.ToHexStringLower(mac);
    }
}

// Stable text kinds for AestheticTextResult (LM head / MetaVoter output). No
// rows are written for expert_scores-only runs; these are prepared for the
// disabled text_assessment / meta_voter capabilities.
public static class AestheticTextKinds
{
    public const string Summary = "summary";
    public const string DetailedAssessment = "detailed_assessment";
    public const string Strengths = "strengths";
    public const string Limitations = "limitations";
    public const string FaceCommentary = "face_commentary";
    public const string OutfitCommentary = "outfit_commentary";
    public const string BodyPresentationCommentary = "body_presentation_commentary";
    public const string EnvironmentCommentary = "environment_commentary";
    public const string RawGeneration = "raw_generation";

    public static bool IsKnown(string? kind) => kind is
        Summary or DetailedAssessment or Strengths or Limitations or FaceCommentary
        or OutfitCommentary or BodyPresentationCommentary or EnvironmentCommentary
        or RawGeneration;
}
