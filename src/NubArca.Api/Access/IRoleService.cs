namespace NubArca.Api.Access;

public interface IRoleService
{
    // Creates the three built-in roles if they are missing and re-synchronises
    // the Administrator permission set with the catalogue. Idempotent, and it
    // never rewrites an existing Member/Restricted/custom permission set — an
    // operator's edit has to survive the next deploy.
    Task EnsureBuiltInRolesAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RoleDto>> ListAsync(CancellationToken cancellationToken = default);

    Task<RoleDto?> GetAsync(string roleKey, CancellationToken cancellationToken = default);

    // The permission keys a role carries, as authorization reads them. An
    // Administrator always resolves to the complete catalogue whatever the rows
    // say, so no edit and no stray row can strip the authority that lets another
    // administrator put it back.
    Task<IReadOnlySet<string>> GetEffectivePermissionsAsync(
        string roleKey, CancellationToken cancellationToken = default);

    Task<(RoleMutationResult Result, RoleDto? Role)> CreateAsync(
        CreateRoleRequest request, CancellationToken cancellationToken = default);

    Task<(RoleMutationResult Result, RoleDto? Role)> UpdateAsync(
        string roleKey, UpdateRoleRequest request, CancellationToken cancellationToken = default);

    Task<RoleMutationResult> DeleteAsync(string roleKey, CancellationToken cancellationToken = default);

    // Resolves an operator-supplied value (a key, or a role name typed at the
    // CLI) to a real role key, or null. Never falls back to a default: an
    // unrecognised role has to fail rather than silently become Member.
    Task<string?> ResolveRoleKeyAsync(string? raw, CancellationToken cancellationToken = default);
}
