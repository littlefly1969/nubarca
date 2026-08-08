namespace NubArca.Api.Access;

// The stable machine keys the backend authorizes on. A key is a wire contract:
// it is stored in role_permissions rows and returned to the browser in
// `/api/auth/me`, so renaming one is a migration, not an edit.
//
// The unit here is a FEATURE SURFACE, not a CRUD verb. `people.access` means
// "may use People on data already visible to this user" — owner isolation is a
// separate, unconditional property of every query and is never expressed as a
// permission. Splitting this into people.read/people.rename/people.delete would
// describe an IAM product rather than this one, and the existing security model
// does not distinguish those cases anywhere.
public static class Permissions
{
    public const string PeopleAccess = "people.access";

    public const string SemanticSearchAccess = "semantic-search.access";

    // The Laboratory shell plus its two independent sections. A section always
    // requires the shell as well, so `laboratory.plates` alone grants nothing:
    // see PermissionPolicies.LaboratoryPlates.
    public const string LaboratoryAccess = "laboratory.access";
    public const string LaboratoryPlates = "laboratory.plates";
    public const string LaboratoryAesthetics = "laboratory.aesthetics";

    // The Cloud Functions hub and the tools reached from it: bulk staging
    // upload, the date-taken organizer and archive export. TV devices is the
    // fourth tool but has its own permission, because managing a paired screen
    // is a different authority from moving one's own files around.
    public const string CloudFunctionsAccess = "cloud-functions.access";

    public const string PrivateVaultAccess = "private-vault.access";

    // Owner-side TV device management: listing/revoking paired sessions,
    // approving a pairing request and setting the Personal Area PIN. It does
    // NOT gate anything an already-paired TV does with its own session token.
    public const string TvManage = "tv.manage";

    // Casting a video to an external receiver (Google Cast in the first
    // implementation). It authorizes the DELEGATION, not the media: holding it
    // means "may mint a short-lived, single-video playback capability for
    // something I can already play", never "may reach somebody else's files".
    // Every Cast media request re-reads this key, so losing it stops the next
    // segment — see NubArca.Api.Cast.
    public const string CastAccess = "cast.access";

    // Administration. Five separate authorities on purpose: holding one must
    // never open another's API.
    public const string AdminDashboard = "admin.dashboard";
    public const string AdminUsersManage = "admin.users.manage";
    public const string AdminImport = "admin.import";
    public const string AdminJobsManage = "admin.jobs.manage";

    // Editing the roles themselves — which is editing what every user assigned
    // to a role may do. It is deliberately NOT assignable: the catalogue marks
    // it Administrator-only, so it can only ever be held through the
    // Administrator role. Without that, a user manager could mint a role that
    // grants themselves administration and step into it.
    public const string AdminRolesManage = "admin.roles.manage";
}
