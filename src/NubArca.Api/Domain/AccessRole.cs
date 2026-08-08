namespace NubArca.Api.Domain;

// A role: the ONE thing that decides what a user may do.
//
// There is no per-user exception any more. A user holds exactly one role and
// the role owns its permissions, so "what can this person do" has a single
// answer that is also the answer for everybody else in that role. When a
// different combination is needed, the operator makes another role — which is
// a thing they can name, describe and reason about — rather than an invisible
// exception on one account.
//
// `Key` is identity and is never edited. `Name` is the label an operator reads
// and may change freely; two roles may share a name without becoming the same
// role.
public class AccessRole
{
    public string Key { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    // A built-in role. It cannot be deleted, and its key cannot change, because
    // code and migrations name it: an installation without Member has nothing
    // for a new account to default to.
    public bool IsSystem { get; set; }

    // The single administrative role. Set by the seeder and never by the API:
    // a role cannot be promoted into an administrator, so the only way to hold
    // administration is to be assigned the built-in Administrator role.
    public bool IsAdministrator { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // Optimistic concurrency. A role editor sends a whole permission set at
    // once, so two administrators editing the same role would otherwise
    // silently overwrite each other's work rather than one of them being told.
    public int Version { get; set; } = 1;
}

// One permission a role carries. `PermissionKey` is validated against
// PermissionCatalog before it is ever written: the catalogue is authoritative
// and an arbitrary string supplied by a browser must not reach this table.
public class RolePermission
{
    public string RoleKey { get; set; } = string.Empty;
    public string PermissionKey { get; set; } = string.Empty;
}
