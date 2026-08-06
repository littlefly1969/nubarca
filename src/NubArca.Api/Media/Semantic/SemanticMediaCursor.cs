using System.Security.Cryptography;
using System.Text;
using NubArca.Api.Files;

namespace NubArca.Api.Media.Semantic;

// VSEM-03: cursor identity of one unified semantic query. Reuses the signed
// (score desc, id asc) ImageCursor machinery the photo semantic gallery
// already uses; only the FINGERPRINT differs — it folds every dimension that
// changes the ranked result set, so a stale or cross-context cursor always
// fails safely:
//
//   normalized query identity (hashed — raw text never enters the cursor)
//   + active AiProfile stable key (profile identity/version)
//   + kind (all|image|video)
//   + physical filter fingerprint
//   + ranking contract version
//   + segmentation version (a reindex changes video evidence)
//
// Owner is deliberately NOT in the fingerprint: results are re-scoped to the
// authenticated owner on EVERY request, so a cursor replayed under another
// account can only ever page that account's own results (see privacy tests).
public static class SemanticMediaCursor
{
    // Bumped whenever the ordering/merge contract changes so old cursors 400.
    // msv2: SEARCH-SEM-01 replaced GUID-prefix truncation with full-library
    // coverage, so a cursor issued against the old partial ranking must not be
    // honoured over the new complete one.
    public const string RankingVersion = "msv2";

    // SEARCH-SEM-01: the fingerprint doubles as the RANKING CACHE identity, so
    // it must fold everything that changes the ranked list — including the
    // result policy, whose thresholds decide which results exist at all.
    public static string Fingerprint(
        string normalizedQuery,
        string profileKey,
        MediaKindScope kind,
        ImageFilters filters,
        int segmentationVersion,
        int policyVersion = SemanticResultPolicyOptions.PolicyVersion)
    {
        var queryHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(normalizedQuery.ToLowerInvariant())), 0, 8);
        var raw = $"q={queryHash}|prof={profileKey}|kind={kind.ToWire()}"
            + $"|f={filters.Fingerprint() ?? string.Empty}|rv={RankingVersion}|sv={segmentationVersion}"
            + $"|pv={policyVersion}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes, 0, 12).ToLowerInvariant();
    }

    public static string Encode(double score, Guid id, string fingerprint)
        => ImageCursor.FromScore(score, id, fingerprint).Encode();

    // Binds the cursor to this query identity. Returns false for a malformed
    // cursor, a non-score cursor, or one issued for a different query/profile/
    // kind/filter/version — the caller rejects loudly, never serves stale data.
    public static bool TryDecode(
        string cursor, string fingerprint, out double score, out Guid id)
    {
        score = 0;
        id = Guid.Empty;
        if (!ImageCursor.TryParse(cursor, out var parsed)
            || parsed.PrimaryKind != ImageCursor.KindScore
            || parsed.PrimaryScore is null
            || !parsed.MatchesFilter(fingerprint))
        {
            return false;
        }

        score = parsed.PrimaryScore.Value;
        id = parsed.Id;
        return true;
    }
}
