using NubArca.Api.Access;
using NubArca.Api.Data;

namespace NubArca.Api.Tests;

// The built-in roles are part of an empty NubArca schema, not test data.
//
// `users.RoleKey` is a foreign key into `access_roles`, so no account can exist
// before the roles do — a test that creates a user against a bare EnsureCreated
// schema would fail on the constraint rather than on anything it meant to
// assert. A migrated database gets the roles from the migration; a test schema
// gets them from here, so the two agree about what "an empty installation"
// contains.
internal static class TestRoleSeeding
{
    public static AppDbContext SeedBuiltInRoles(this AppDbContext db)
    {
        new RoleService(db, TimeProvider.System)
            .EnsureBuiltInRolesAsync()
            .GetAwaiter()
            .GetResult();
        return db;
    }
}
