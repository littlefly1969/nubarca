namespace NubArca.Api.Access;

// The three built-in roles, as stable keys rather than user-editable rows.
//
// A role table would only be worth its cost once roles can be created, and
// custom roles are explicitly out of scope. Keys are simpler and safer: there is
// no way to edit the Administrator bundle out from under the last administrator,
// no row to delete while users still reference it, and no migration needed to
// keep a fresh installation and an upgraded one in agreement about what
// "Member" means.
public static class RoleKeys
{
    public const string Administrator = "Administrator";
    public const string Member = "Member";
    public const string Restricted = "Restricted";

    public static readonly IReadOnlyList<string> All =
        [Administrator, Member, Restricted];

    public static bool IsKnown(string? roleKey) =>
        roleKey is not null && All.Contains(roleKey, StringComparer.Ordinal);

    // Normalises a caller-supplied role key case-insensitively so an admin UI or
    // CLI can pass "member"; anything unrecognised fails rather than defaulting.
    public static bool TryNormalize(string? raw, out string roleKey)
    {
        if (!string.IsNullOrWhiteSpace(raw))
        {
            var trimmed = raw.Trim();
            foreach (var known in All)
            {
                if (string.Equals(known, trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    roleKey = known;
                    return true;
                }
            }
        }

        roleKey = Member;
        return false;
    }
}
