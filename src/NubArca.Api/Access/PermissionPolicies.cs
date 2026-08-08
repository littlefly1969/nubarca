using Microsoft.AspNetCore.Authorization;

namespace NubArca.Api.Access;

// The bridge between permission keys and ASP.NET Core's policy names.
//
// Every catalogue key gets exactly one policy, plus the two composite policies
// the Laboratory needs (a section requires the shell as well). Endpoints name a
// permission through RequirePermission(...) and never spell a policy string, so
// there is one place where "which policy is this key" is decided.
public static class PermissionPolicies
{
    private const string Prefix = "perm:";

    // A Laboratory section is reachable only with the shell permission too, so
    // `laboratory.access` + `laboratory.plates` opens Plates while
    // `laboratory.plates` on its own opens nothing.
    public const string LaboratoryPlates = Prefix + "laboratory:plates+access";
    public const string LaboratoryAesthetics = Prefix + "laboratory:aesthetics+access";

    public static string For(string permissionKey) => Prefix + permissionKey;

    public static void AddNubArcaPermissionPolicies(this AuthorizationOptions options)
    {
        foreach (var key in PermissionCatalog.AllKeys)
        {
            options.AddPolicy(For(key), policy =>
            {
                policy.RequireAuthenticatedUser();
                policy.AddRequirements(new PermissionRequirement(key));
            });
        }

        options.AddPolicy(LaboratoryPlates, policy =>
        {
            policy.RequireAuthenticatedUser();
            policy.AddRequirements(new PermissionRequirement(
                Permissions.LaboratoryAccess, Permissions.LaboratoryPlates));
        });

        options.AddPolicy(LaboratoryAesthetics, policy =>
        {
            policy.RequireAuthenticatedUser();
            policy.AddRequirements(new PermissionRequirement(
                Permissions.LaboratoryAccess, Permissions.LaboratoryAesthetics));
        });
    }
}

public static class PermissionEndpointExtensions
{
    // The idiom every gated endpoint uses:
    //     .RequirePermission(Permissions.PeopleAccess)
    // rather than a hand-written role comparison inside the handler.
    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, string permissionKey)
        where TBuilder : IEndpointConventionBuilder
        => builder.RequireAuthorization(PermissionPolicies.For(permissionKey));

    public static TBuilder RequireLaboratoryPlates<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
        => builder.RequireAuthorization(PermissionPolicies.LaboratoryPlates);

    public static TBuilder RequireLaboratoryAesthetics<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
        => builder.RequireAuthorization(PermissionPolicies.LaboratoryAesthetics);
}
