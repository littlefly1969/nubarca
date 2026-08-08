using System.Net;
using NubArca.Api.Access;
using NubArca.Api.Domain;
using NubArca.Api.Tests.Endpoints;

namespace NubArca.Api.Tests.Access;

// Server-side enforcement of every gated feature surface.
//
// Frontend hiding is UX; this file is the security property. Each case proves
// BOTH directions — a permitted caller is not refused, and a caller without the
// permission is refused with 403 rather than 401 (they are authenticated; they
// simply may not) — because a gate that only ever answers 403 would also "pass"
// if the endpoint were broken for everyone.
public sealed class FeaturePermissionEndpointTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public FeaturePermissionEndpointTests()
    {
        _factory = new SqliteWebApplicationFactory();
        _factory.EnsureDatabaseCreated();
    }

    public void Dispose() => _factory.Dispose();

    // Each gated route with the permission that opens it. Kept as data so a new
    // gate is one line, and so "which routes are gated" is readable in one place.
    public static TheoryData<string, string> GatedRoutes() => new()
    {
        { "/api/people", Permissions.PeopleAccess },
        { "/api/people/suggested-groups", Permissions.PeopleAccess },
        { "/api/plates/images", Permissions.LaboratoryPlates },
        { "/api/aesthetics-lab/items", Permissions.LaboratoryAesthetics },
        { "/api/private-vault", Permissions.PrivateVaultAccess },
        { "/api/uploads/staging/config", Permissions.CloudFunctionsAccess },
        { "/api/tv-devices", Permissions.TvManage },
        { "/api/tv-personal/pin", Permissions.TvManage },
        { "/api/admin/users", Permissions.AdminUsersManage },
        { "/api/admin/permissions", Permissions.AdminUsersManage },
        { "/api/admin/storage-stats?physical=false", Permissions.AdminDashboard },
        { "/api/admin/ai/status", Permissions.AdminDashboard },
        { "/api/admin/import/roots", Permissions.AdminImport },
        { "/api/admin/jobs", Permissions.AdminJobsManage },
    };

    [Theory]
    [MemberData(nameof(GatedRoutes))]
    public async Task Restricted_User_Is_Forbidden(string route, string permissionKey)
    {
        _ = permissionKey;
        var (_, client) = await _factory.CreateRoleClientAsync(
            RoleKeys.Restricted, $"restricted-{Guid.NewGuid():N}@example.com");

        var response = await client.GetAsync(route);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(GatedRoutes))]
    public async Task An_Explicit_Grant_Opens_The_Route_For_A_Restricted_User(
        string route, string permissionKey)
    {
        var (userId, client) = await _factory.CreateRoleClientAsync(
            RoleKeys.Restricted, $"granted-{Guid.NewGuid():N}@example.com");

        await _factory.SetPermissionOverrideAsync(userId, permissionKey, PermissionEffect.Grant);
        // A Laboratory section needs the shell permission too — that is the
        // composite policy, not an accident of ordering.
        if (permissionKey is Permissions.LaboratoryPlates or Permissions.LaboratoryAesthetics)
        {
            await _factory.SetPermissionOverrideAsync(
                userId, Permissions.LaboratoryAccess, PermissionEffect.Grant);
        }

        var response = await client.GetAsync(route);

        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(GatedRoutes))]
    public async Task Administrator_Reaches_Every_Gated_Route(string route, string permissionKey)
    {
        _ = permissionKey;
        var (_, client) = await _factory.CreateRoleClientAsync(
            RoleKeys.Administrator, $"admin-{Guid.NewGuid():N}@example.com");

        var response = await client.GetAsync(route);

        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Member_Reaches_Every_Feature_Route_But_No_Admin_Route()
    {
        // The migration promise in endpoint form: an account that was an
        // ordinary user before roles existed still reaches every feature.
        var (_, client) = await _factory.CreateRoleClientAsync(
            RoleKeys.Member, "member-routes@example.com");

        foreach (var route in new[]
        {
            "/api/people", "/api/plates/images", "/api/aesthetics-lab/items",
            "/api/private-vault", "/api/uploads/staging/config", "/api/tv-devices",
        })
        {
            var response = await client.GetAsync(route);
            Assert.True(
                response.StatusCode != HttpStatusCode.Forbidden,
                $"Member was forbidden from {route}");
        }

        foreach (var route in new[]
        {
            "/api/admin/users", "/api/admin/storage-stats?physical=false",
            "/api/admin/import/roots", "/api/admin/jobs",
        })
        {
            var response = await client.GetAsync(route);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    [Fact]
    public async Task A_Deny_Override_Removes_A_Member_Feature()
    {
        var (userId, client) = await _factory.CreateRoleClientAsync(
            RoleKeys.Member, "denied-people@example.com");
        Assert.NotEqual(HttpStatusCode.Forbidden, (await client.GetAsync("/api/people")).StatusCode);

        await _factory.SetPermissionOverrideAsync(userId, Permissions.PeopleAccess, PermissionEffect.Deny);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/people")).StatusCode);
    }

    [Fact]
    public async Task Laboratory_Plates_Does_Not_Open_Aesthetics()
    {
        // The scenario the two sub-permissions exist for: Laboratory visible,
        // Plates usable, Aesthetics refused.
        var (userId, client) = await _factory.CreateRoleClientAsync(
            RoleKeys.Restricted, "lab-plates-only@example.com");
        await _factory.SetPermissionOverrideAsync(userId, Permissions.LaboratoryAccess, PermissionEffect.Grant);
        await _factory.SetPermissionOverrideAsync(userId, Permissions.LaboratoryPlates, PermissionEffect.Grant);

        Assert.NotEqual(HttpStatusCode.Forbidden, (await client.GetAsync("/api/plates/images")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/aesthetics-lab/items")).StatusCode);
    }

    [Fact]
    public async Task A_Laboratory_Section_Alone_Opens_Nothing_Without_The_Shell()
    {
        var (userId, client) = await _factory.CreateRoleClientAsync(
            RoleKeys.Restricted, "lab-section-only@example.com");
        await _factory.SetPermissionOverrideAsync(userId, Permissions.LaboratoryPlates, PermissionEffect.Grant);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/plates/images")).StatusCode);
    }

    [Fact]
    public async Task Semantic_Search_Is_Refused_While_Ordinary_Search_Keeps_Working()
    {
        // The important half of this is the second assertion: disabling the
        // whole media endpoint because it also supports semantic parameters
        // would take normal search away from a Restricted user.
        var (userId, client) = await _factory.CreateRoleClientAsync(
            RoleKeys.Restricted, "no-semantic@example.com");

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await client.GetAsync("/api/media/semantic?q=sunset")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await client.GetAsync("/api/images/semantic?q=sunset")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await client.GetAsync("/api/images?semanticQuery=sunset")).StatusCode);

        // Ordinary metadata search, filters and listing stay available.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/media?q=holiday")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/images?q=holiday&favorite=true")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/media-library/stats")).StatusCode);

        await _factory.SetPermissionOverrideAsync(
            userId, Permissions.SemanticSearchAccess, PermissionEffect.Grant);

        // With the permission the semantic route is reached; whether the
        // installation can currently ANSWER it (a profile, a model) is a
        // capability question and deliberately a different status.
        Assert.NotEqual(
            HttpStatusCode.Forbidden,
            (await client.GetAsync("/api/images?semanticQuery=sunset")).StatusCode);
    }

    [Fact]
    public async Task Core_Personal_Cloud_Stays_Open_To_A_Restricted_User()
    {
        // Files, media, albums, trash and the account itself are not
        // permission-gated at all. Restricted keeps the whole personal cloud.
        var (_, client) = await _factory.CreateRoleClientAsync(
            RoleKeys.Restricted, "restricted-core@example.com");

        foreach (var route in new[]
        {
            "/api/auth/me", "/api/folders/children", "/api/media", "/api/albums",
            "/api/trash", "/api/share-links", "/api/storage/me",
            "/api/media-library/rules", "/api/media-library/stats",
        })
        {
            var response = await client.GetAsync(route);
            Assert.True(
                response.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent,
                $"Restricted could not reach {route}: {response.StatusCode}");
        }
    }

    [Fact]
    public async Task An_Unauthenticated_Caller_Gets_401_Not_403()
    {
        var anon = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/people")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/admin/users")).StatusCode);
    }
}
