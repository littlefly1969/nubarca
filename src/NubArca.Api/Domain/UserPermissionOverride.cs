namespace NubArca.Api.Domain;

// Whether an override adds a permission the role does not carry, or removes one
// it does. Persisted as its name so a row is readable in psql without a lookup
// table, and so inserting a new member can never renumber an existing one.
public enum PermissionEffect
{
    Grant,
    Deny,
}

// An EXCEPTION to a user's role baseline — never the baseline itself. One row
// per (user, permission), enforced by a unique index, so "what does this
// override say" has exactly one answer and a double-write cannot leave a Grant
// and a Deny racing for the same key.
//
// PermissionKey is validated against PermissionCatalog before it is ever
// written: the catalogue is authoritative and an arbitrary string supplied by a
// browser must not reach this table.
public class UserPermissionOverride
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string PermissionKey { get; set; } = string.Empty;
    public PermissionEffect Effect { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
