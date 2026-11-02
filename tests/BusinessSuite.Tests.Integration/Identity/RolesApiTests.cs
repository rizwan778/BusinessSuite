using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BusinessSuite.Application.Common.Security;
using BusinessSuite.Domain.Organizations;
using BusinessSuite.Infrastructure.Identity;
using BusinessSuite.Infrastructure.Persistence;
using BusinessSuite.Tests.Integration.Support;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BusinessSuite.Tests.Integration.Identity;

/// <summary>
/// <c>/api/v1/roles</c> and <c>/api/v1/permissions</c> end to end on SQL Server: the catalogue, tenant isolation,
/// custom roles, system roles, availability per organization, and how roles meet user management.
/// </summary>
public sealed class RolesApiTests(SqlServerAppFixture app) : IClassFixture<SqlServerAppFixture>
{
    private const string RolesPath = "/api/v1/roles";
    private const string OrganizationHeader = "X-Organization-Id";
    private static readonly Guid ManagerRoleId = OrganizationRoles.IdOf(OrganizationRoles.Manager);

    // ---- Seed data and catalogue -----------------------------------------------------------------------

    [SqlServerFact]
    public async Task System_roles_are_seeded_with_the_default_permissions()
    {
        var stored = await WithDbAsync(db => db.RoleDefinitions.AsNoTracking().Where(role => role.SystemKey != null).ToListAsync());

        Assert.Equal(OrganizationRoles.All.Order(), stored.Select(role => role.SystemKey!).Order());
        foreach (var role in stored.Where(role => !role.IsOwner && role.UpdatedAtUtc == null))
        {
            Assert.Equal(OrganizationRoles.IdOf(role.SystemKey!), role.Id);
            Assert.True(role.AvailableToAllOrganizations);
            Assert.Equal(RolePermissions.ForOrganizationRole(role.SystemKey).Order(StringComparer.Ordinal), role.Permissions);
        }
    }

    [SqlServerFact]
    public async Task Permission_catalogue_is_grouped_and_hides_platform_permissions_from_organizations()
    {
        var organization = await CreateOrganizationAsync();
        var admin = await CreateUserAsync((organization, OrganizationRoles.Administrator));
        var superAdmin = await CreatePlatformUserAsync(PlatformRoles.SuperAdministrator);

        var forAdmin = await GetJsonAsync("/api/v1/permissions", admin.Token);
        var forSuperAdmin = await GetJsonAsync("/api/v1/permissions", superAdmin.Token);

        Assert.Equal(
            new[] { "user-management", "product-management", "inventory", "sales", "purchases", "accounts", "reports", "organization-management" },
            forAdmin.EnumerateArray().Select(group => group.GetProperty("key").GetString()));
        Assert.Contains(forSuperAdmin.EnumerateArray(), group => group.GetProperty("isPlatform").GetBoolean());
        var userManagement = forAdmin[0].GetProperty("permissions").EnumerateArray().Select(permission => permission.GetProperty("key").GetString());
        Assert.Contains(Permissions.Roles.Manage, userManagement);
    }

    // ---- Listing and tenant isolation ------------------------------------------------------------------

    [SqlServerFact]
    public async Task Organization_sees_system_roles_and_its_own_custom_roles_only()
    {
        var mine = await CreateOrganizationAsync();
        var theirs = await CreateOrganizationAsync();
        var admin = await CreateUserAsync((mine, OrganizationRoles.Administrator));
        var otherAdmin = await CreateUserAsync((theirs, OrganizationRoles.Administrator));
        var myRole = await CreateRoleAsync(admin.Token, "Cashier", Permissions.Sales.View);
        var theirRole = await CreateRoleAsync(otherAdmin.Token, "Packer", Permissions.Inventory.View);

        var list = await GetJsonAsync(RolesPath, admin.Token);

        var names = list.EnumerateArray().Select(role => role.GetProperty("name").GetString()).ToList();
        Assert.Equal(OrganizationRoles.All, names.Take(OrganizationRoles.All.Count));
        Assert.Contains("Cashier", names);
        Assert.DoesNotContain("Packer", names);
        var owner = list[0];
        Assert.True(owner.GetProperty("isSystem").GetBoolean());
        Assert.True(owner.GetProperty("permissionsLocked").GetBoolean());
        Assert.False(owner.GetProperty("canManage").GetBoolean());
        Assert.Equal(JsonValueKind.Null, owner.GetProperty("availability").ValueKind); // platform configuration: Super Admins only
        Assert.Equal(1, list.EnumerateArray().Single(role => role.GetProperty("name").GetString() == OrganizationRoles.Administrator).GetProperty("memberCount").GetInt32());

        using var filtered = await SendAsync(HttpMethod.Get, $"{RolesPath}?organizationId={theirs}", admin.Token);
        Assert.Equal("Roles.OrganizationFilterNotAllowed", await filtered.ReadProblemCodeAsync());

        // Every way of reaching another organization's custom role looks like "not found".
        var path = $"{RolesPath}/{theirRole}";
        var attempts = new (HttpMethod Method, string Path, object? Body)[]
        {
            (HttpMethod.Get, path, null),
            (HttpMethod.Get, $"{path}/permissions", null),
            (HttpMethod.Put, path, new { name = "Hijacked" }),
            (HttpMethod.Put, $"{path}/permissions", new { permissions = Array.Empty<string>() }),
            (HttpMethod.Delete, path, null),
        };
        foreach (var (method, attemptPath, body) in attempts)
        {
            using var response = await SendAsync(method, attemptPath, admin.Token, body);
            Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{method} {attemptPath} answered {response.StatusCode}");
            Assert.Equal("Roles.NotFound", await response.ReadProblemCodeAsync());
        }

        var untouched = await WithDbAsync(db => db.RoleDefinitions.AsNoTracking().SingleAsync(role => role.Id == theirRole));
        Assert.Equal("Packer", untouched.Name);
        Assert.Equal(new[] { Permissions.Inventory.View }, untouched.Permissions);
    }

    [SqlServerFact]
    public async Task Another_organizations_custom_role_cannot_be_assigned()
    {
        var mine = await CreateOrganizationAsync();
        var theirs = await CreateOrganizationAsync();
        var admin = await CreateUserAsync((mine, OrganizationRoles.Administrator));
        var member = await CreateUserAsync((mine, OrganizationRoles.ReadOnly));
        var otherAdmin = await CreateUserAsync((theirs, OrganizationRoles.Administrator));
        var theirRole = await CreateRoleAsync(otherAdmin.Token, "Packer", Permissions.Inventory.View);

        using var byId = await SendAsync(HttpMethod.Patch, $"/api/v1/users/{member.UserId}/role", admin.Token, new { role = theirRole.ToString() });
        using var byName = await SendAsync(HttpMethod.Patch, $"/api/v1/users/{member.UserId}/role", admin.Token, new { role = "Packer" });

        Assert.Equal("Users.InvalidRole", await byId.ReadProblemCodeAsync());
        Assert.Equal("Users.InvalidRole", await byName.ReadProblemCodeAsync());
    }

    // ---- Custom roles ------------------------------------------------------------------------------------

    [SqlServerFact]
    public async Task Custom_role_lifecycle_create_assign_configure_rename_and_delete()
    {
        var organization = await CreateOrganizationAsync();
        var admin = await CreateUserAsync((organization, OrganizationRoles.Administrator));
        var member = await CreateUserAsync((organization, OrganizationRoles.ReadOnly));

        // Create (201 + Location) and read back.
        using var created = await SendAsync(HttpMethod.Post, RolesPath, admin.Token,
            new { name = "Cashier", description = "Front counter", permissions = new[] { Permissions.Sales.View, Permissions.Invoices.Create } });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var role = await created.ReadJsonAsync();
        var roleId = role.GetProperty("id").GetGuid();
        Assert.EndsWith($"{RolesPath}/{roleId}", created.Headers.Location!.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Organization", role.GetProperty("scope").GetString());
        Assert.Equal(organization, role.GetProperty("organizationId").GetGuid());
        Assert.True(role.GetProperty("canManage").GetBoolean());

        // Assign by name; the holder gets exactly the role's permissions.
        using var assigned = await SendAsync(HttpMethod.Patch, $"/api/v1/users/{member.UserId}/role", admin.Token, new { role = "cashier" });
        Assert.Equal(HttpStatusCode.OK, assigned.StatusCode);
        var membership = (await assigned.ReadJsonAsync()).GetProperty("memberships")[0];
        Assert.Equal("Cashier", membership.GetProperty("role").GetString());
        Assert.Equal(roleId, membership.GetProperty("roleId").GetGuid());
        Assert.Equal(new[] { Permissions.Invoices.Create, Permissions.Sales.View }, await PermissionsOfAsync(member.Token));

        // Configure permissions: applies on the holder's next request.
        using var configured = await SendAsync(HttpMethod.Put, $"{RolesPath}/{roleId}/permissions", admin.Token,
            new { permissions = new[] { Permissions.Sales.View, Permissions.Sales.Manage, Permissions.Sales.View } });
        Assert.Equal(HttpStatusCode.OK, configured.StatusCode);
        Assert.Equal(new[] { Permissions.Sales.Manage, Permissions.Sales.View }, await PermissionsOfAsync(member.Token));
        var viewed = await GetJsonAsync($"{RolesPath}/{roleId}/permissions", admin.Token);
        Assert.Equal(new[] { Permissions.Sales.Manage, Permissions.Sales.View }, viewed.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()));

        // Rename; the holder's role name follows.
        using var renamed = await SendAsync(HttpMethod.Put, $"{RolesPath}/{roleId}", admin.Token, new { name = "Senior Cashier", description = (string?)null });
        Assert.Equal("Senior Cashier", (await renamed.ReadJsonAsync()).GetProperty("name").GetString());
        Assert.Equal("Senior Cashier", (await GetJsonAsync("/api/v1/auth/me/permissions", member.Token)).GetProperty("organization").GetProperty("role").GetString());

        // Delete only when nobody holds it.
        using var inUse = await SendAsync(HttpMethod.Delete, $"{RolesPath}/{roleId}", admin.Token);
        Assert.Equal("Roles.InUse", await inUse.ReadProblemCodeAsync());
        using var reassigned = await SendAsync(HttpMethod.Patch, $"/api/v1/users/{member.UserId}/role", admin.Token, new { role = OrganizationRoles.ReadOnly });
        Assert.Equal(HttpStatusCode.OK, reassigned.StatusCode);
        using var deleted = await SendAsync(HttpMethod.Delete, $"{RolesPath}/{roleId}", admin.Token);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        using var gone = await SendAsync(HttpMethod.Get, $"{RolesPath}/{roleId}", admin.Token);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [SqlServerFact]
    public async Task Create_validates_names_and_permissions()
    {
        var organization = await CreateOrganizationAsync();
        var admin = await CreateUserAsync((organization, OrganizationRoles.Administrator));
        await CreateRoleAsync(admin.Token, "Cashier", Permissions.Sales.View);

        using var reserved = await SendAsync(HttpMethod.Post, RolesPath, admin.Token, new { name = "owner" });
        using var shadowsSystem = await SendAsync(HttpMethod.Post, RolesPath, admin.Token, new { name = " ReadOnly " });
        using var duplicate = await SendAsync(HttpMethod.Post, RolesPath, admin.Token, new { name = "CASHIER" });
        using var unknown = await SendAsync(HttpMethod.Post, RolesPath, admin.Token, new { name = "Odd", permissions = new[] { "users.everything" } });
        using var platformPermission = await SendAsync(HttpMethod.Post, RolesPath, admin.Token, new { name = "Odd", permissions = new[] { Permissions.Platform.UsersManage } });
        using var missingName = await SendAsync(HttpMethod.Post, RolesPath, admin.Token, new { description = "x" });

        Assert.Equal("Roles.Invalid", await reserved.ReadProblemCodeAsync());
        Assert.Equal("Roles.Invalid", await shadowsSystem.ReadProblemCodeAsync());
        Assert.Equal("Roles.DuplicateName", await duplicate.ReadProblemCodeAsync());
        Assert.Equal("Roles.UnknownPermission", await unknown.ReadProblemCodeAsync());
        Assert.Equal("Roles.UnknownPermission", await platformPermission.ReadProblemCodeAsync());
        Assert.Equal("Request.ValidationFailed", await missingName.ReadProblemCodeAsync());
    }

    // ---- Escalation ----------------------------------------------------------------------------------------

    [SqlServerFact]
    public async Task Administrators_cannot_grant_or_hand_out_permissions_they_lack()
    {
        var organization = await CreateOrganizationAsync();
        var owner = await CreateUserAsync((organization, OrganizationRoles.Owner));
        var admin = await CreateUserAsync((organization, OrganizationRoles.Administrator));
        var member = await CreateUserAsync((organization, OrganizationRoles.ReadOnly));

        // Administrators lack subscription.manage.
        using var grant = await SendAsync(HttpMethod.Post, RolesPath, admin.Token, new { name = "Billing", permissions = new[] { Permissions.Subscription.Manage } });
        Assert.Equal("Roles.CannotGrantPermission", await grant.ReadProblemCodeAsync());

        // The Owner can make such a role ...
        var billing = await CreateRoleAsync(owner.Token, "Billing", Permissions.Subscription.Manage, Permissions.Reports.View);

        // ... but the Administrator can neither change it nor give it to anyone.
        using var change = await SendAsync(HttpMethod.Put, $"{RolesPath}/{billing}/permissions", admin.Token, new { permissions = new[] { Permissions.Reports.View } });
        using var assign = await SendAsync(HttpMethod.Patch, $"/api/v1/users/{member.UserId}/role", admin.Token, new { role = billing.ToString() });
        Assert.Equal("Roles.RoleExceedsYourPermissions", await change.ReadProblemCodeAsync());
        Assert.Equal("Users.RoleExceedsYourPermissions", await assign.ReadProblemCodeAsync());
        Assert.False((await GetJsonAsync($"{RolesPath}/{billing}", admin.Token)).GetProperty("canManage").GetBoolean());

        using var ownerAssigns = await SendAsync(HttpMethod.Patch, $"/api/v1/users/{member.UserId}/role", owner.Token, new { role = billing.ToString() });
        Assert.Equal(HttpStatusCode.OK, ownerAssigns.StatusCode);
    }

    [SqlServerFact]
    public async Task Viewing_needs_roles_view_and_changing_needs_roles_manage()
    {
        var organization = await CreateOrganizationAsync();
        var viewer = await CreateUserAsync((organization, OrganizationRoles.ReadOnly));   // roles.view
        var seller = await CreateUserAsync((organization, OrganizationRoles.SalesOperator)); // neither

        using var viewerList = await SendAsync(HttpMethod.Get, RolesPath, viewer.Token);
        using var viewerCreate = await SendAsync(HttpMethod.Post, RolesPath, viewer.Token, new { name = "Nope" });
        using var sellerList = await SendAsync(HttpMethod.Get, RolesPath, seller.Token);
        using var sellerCatalogue = await SendAsync(HttpMethod.Get, "/api/v1/permissions", seller.Token);

        Assert.Equal(HttpStatusCode.OK, viewerList.StatusCode);
        Assert.Equal("Auth.PermissionDenied", await viewerCreate.ReadProblemCodeAsync());
        Assert.Equal("Auth.PermissionDenied", await sellerList.ReadProblemCodeAsync());
        Assert.Equal("Auth.PermissionDenied", await sellerCatalogue.ReadProblemCodeAsync());
    }

    // ---- System roles (Super Admin) -----------------------------------------------------------------

    [SqlServerFact]
    public async Task System_roles_are_configured_by_super_admins_only_and_keep_their_identity()
    {
        var organization = await CreateOrganizationAsync();
        var owner = await CreateUserAsync((organization, OrganizationRoles.Owner));
        var superAdmin = await CreatePlatformUserAsync(PlatformRoles.SuperAdministrator);
        var ownerRole = OrganizationRoles.OwnerId;
        var readOnly = OrganizationRoles.IdOf(OrganizationRoles.ReadOnly);

        using var ownerEdits = await SendAsync(HttpMethod.Put, $"{RolesPath}/{readOnly}/permissions", owner.Token, new { permissions = Array.Empty<string>() });
        using var rename = await SendAsync(HttpMethod.Put, $"{RolesPath}/{readOnly}", superAdmin.Token, new { name = "Viewer" });
        using var delete = await SendAsync(HttpMethod.Delete, $"{RolesPath}/{readOnly}", superAdmin.Token);
        using var ownerPermissions = await SendAsync(HttpMethod.Put, $"{RolesPath}/{ownerRole}/permissions", superAdmin.Token, new { permissions = Array.Empty<string>() });
        using var ownerAvailability = await SendAsync(HttpMethod.Put, $"{RolesPath}/{ownerRole}/availability", superAdmin.Token, new { allOrganizations = false });

        Assert.Equal("Roles.SuperAdminRequired", await ownerEdits.ReadProblemCodeAsync());
        Assert.Equal("Roles.SystemRoleLocked", await rename.ReadProblemCodeAsync());
        Assert.Equal("Roles.SystemRoleLocked", await delete.ReadProblemCodeAsync());
        Assert.Equal("Roles.OwnerPermissionsLocked", await ownerPermissions.ReadProblemCodeAsync());
        Assert.Equal("Roles.OwnerAvailabilityLocked", await ownerAvailability.ReadProblemCodeAsync());
    }

    [SqlServerFact]
    public async Task Super_admin_changes_to_a_system_role_apply_to_every_holder()
    {
        var organization = await CreateOrganizationAsync();
        var manager = await CreateUserAsync((organization, OrganizationRoles.Manager));
        var superAdmin = await CreatePlatformUserAsync(PlatformRoles.SuperAdministrator);
        var original = RolePermissions.ForOrganizationRole(OrganizationRoles.Manager).Order(StringComparer.Ordinal).ToArray();

        try
        {
            using var changed = await SendAsync(HttpMethod.Put, $"{RolesPath}/{ManagerRoleId}/permissions", superAdmin.Token,
                new { permissions = original.Where(permission => permission != Permissions.Invoices.Cancel).ToArray() });
            Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
            Assert.DoesNotContain(Permissions.Invoices.Cancel, await PermissionsOfAsync(manager.Token));
        }
        finally
        {
            // Other tests in this class rely on the defaults.
            using var restored = await SendAsync(HttpMethod.Put, $"{RolesPath}/{ManagerRoleId}/permissions", superAdmin.Token, new { permissions = original });
            Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        }

        Assert.Contains(Permissions.Invoices.Cancel, await PermissionsOfAsync(manager.Token));
    }

    // ---- Availability per organization (Super Admin) ------------------------------------------------

    [SqlServerFact]
    public async Task Super_admin_controls_which_organizations_may_use_a_shared_role()
    {
        var shop = await CreateOrganizationAsync();
        var warehouse = await CreateOrganizationAsync();
        var shopAdmin = await CreateUserAsync((shop, OrganizationRoles.Administrator));
        var shopMember = await CreateUserAsync((shop, OrganizationRoles.ReadOnly));
        var warehouseAdmin = await CreateUserAsync((warehouse, OrganizationRoles.Administrator));
        var superAdmin = await CreatePlatformUserAsync(PlatformRoles.SuperAdministrator);
        var name = $"Auditor {Guid.NewGuid():N}"[..20];

        // Only Super Admins create shared roles; a new one is available nowhere.
        using var adminTries = await SendAsync(HttpMethod.Post, RolesPath, shopAdmin.Token, new { name, scope = "Platform" });
        Assert.Equal("Roles.SuperAdminRequired", await adminTries.ReadProblemCodeAsync());
        using var created = await SendAsync(HttpMethod.Post, RolesPath, superAdmin.Token, new { name, scope = "Platform", permissions = new[] { Permissions.Reports.View } });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var roleId = (await created.ReadJsonAsync()).GetProperty("id").GetGuid();
        using var notYet = await SendAsync(HttpMethod.Get, $"{RolesPath}/{roleId}", shopAdmin.Token);
        Assert.Equal(HttpStatusCode.NotFound, notYet.StatusCode);
        using var cannotAssignYet = await SendAsync(HttpMethod.Patch, $"/api/v1/users/{shopMember.UserId}/role", shopAdmin.Token, new { role = name });
        Assert.Equal("Users.InvalidRole", await cannotAssignYet.ReadProblemCodeAsync());

        // Enable for the shop only: the shop may use it, the warehouse may not.
        using var onlyShop = await SendAsync(HttpMethod.Put, $"{RolesPath}/{roleId}/availability", superAdmin.Token, new { allOrganizations = false, organizationIds = new[] { shop } });
        var availability = (await onlyShop.ReadJsonAsync()).GetProperty("availability");
        Assert.False(availability.GetProperty("allOrganizations").GetBoolean());
        Assert.Equal(new[] { shop }, availability.GetProperty("organizationIds").EnumerateArray().Select(id => id.GetGuid()));
        Assert.Contains(roleId, Ids(await GetJsonAsync(RolesPath, shopAdmin.Token)));
        Assert.DoesNotContain(roleId, Ids(await GetJsonAsync(RolesPath, warehouseAdmin.Token)));
        Assert.Contains(roleId, Ids(await GetJsonAsync($"{RolesPath}?organizationId={shop}", superAdmin.Token)));
        Assert.DoesNotContain(roleId, Ids(await GetJsonAsync($"{RolesPath}?organizationId={warehouse}", superAdmin.Token)));

        using var assigned = await SendAsync(HttpMethod.Patch, $"/api/v1/users/{shopMember.UserId}/role", shopAdmin.Token, new { role = name });
        Assert.Equal(HttpStatusCode.OK, assigned.StatusCode);

        // The shop cannot lose it while someone there holds it; enabling it everywhere is fine.
        using var takeAway = await SendAsync(HttpMethod.Put, $"{RolesPath}/{roleId}/availability", superAdmin.Token, new { allOrganizations = false, organizationIds = new[] { warehouse } });
        Assert.Equal("Roles.InUse", await takeAway.ReadProblemCodeAsync());
        using var everywhere = await SendAsync(HttpMethod.Put, $"{RolesPath}/{roleId}/availability", superAdmin.Token, new { allOrganizations = true });
        Assert.Equal(HttpStatusCode.OK, everywhere.StatusCode);
        Assert.Contains(roleId, Ids(await GetJsonAsync(RolesPath, warehouseAdmin.Token)));

        // Organization administrators can see a shared role but not change it or its availability.
        using var adminRenames = await SendAsync(HttpMethod.Put, $"{RolesPath}/{roleId}", shopAdmin.Token, new { name = "Mine" });
        using var adminAvailability = await SendAsync(HttpMethod.Put, $"{RolesPath}/{roleId}/availability", shopAdmin.Token, new { allOrganizations = false });
        using var unknownOrganization = await SendAsync(HttpMethod.Put, $"{RolesPath}/{roleId}/availability", superAdmin.Token, new { allOrganizations = false, organizationIds = new[] { shop, Guid.NewGuid() } });
        Assert.Equal("Roles.SuperAdminRequired", await adminRenames.ReadProblemCodeAsync());
        Assert.Equal("Auth.SuperAdminRequired", await adminAvailability.ReadProblemCodeAsync());
        Assert.Equal("Roles.UnknownOrganization", await unknownOrganization.ReadProblemCodeAsync());
    }

    [SqlServerFact]
    public async Task Availability_applies_to_platform_roles_only()
    {
        var organization = await CreateOrganizationAsync();
        var admin = await CreateUserAsync((organization, OrganizationRoles.Administrator));
        var superAdmin = await CreatePlatformUserAsync(PlatformRoles.SuperAdministrator);
        var custom = await CreateRoleAsync(admin.Token, "Cashier", Permissions.Sales.View);

        using var response = await SendAsync(HttpMethod.Put, $"{RolesPath}/{custom}/availability", superAdmin.Token, new { allOrganizations = true });

        Assert.Equal("Roles.NotAPlatformRole", await response.ReadProblemCodeAsync());
    }

    // ---- Helpers -----------------------------------------------------------------------------------------

    private sealed record TestUser(Guid UserId, string Email, string Token);

    private static List<Guid> Ids(JsonElement roles) => roles.EnumerateArray().Select(role => role.GetProperty("id").GetGuid()).ToList();

    private async Task<Guid> CreateRoleAsync(string token, string name, params string[] permissions)
    {
        using var response = await SendAsync(HttpMethod.Post, RolesPath, token, new { name, permissions });
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"Creating role {name} answered {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.ReadJsonAsync()).GetProperty("id").GetGuid();
    }

    private async Task<string?[]> PermissionsOfAsync(string token) =>
        (await GetJsonAsync("/api/v1/auth/me/permissions", token)).GetProperty("permissions").EnumerateArray().Select(permission => permission.GetString()).ToArray();

    private async Task<Guid> CreateOrganizationAsync()
    {
        var organization = Organization.Create($"Roles Test {Guid.NewGuid():N}");
        await WithDbAsync(async db =>
        {
            db.Organizations.Add(organization);
            return await db.SaveChangesAsync();
        });
        return organization.Id;
    }

    private Task<TestUser> CreateUserAsync(params (Guid OrganizationId, string Role)[] memberships) => CreateUserCoreAsync(null, memberships);

    private Task<TestUser> CreatePlatformUserAsync(string platformRole) => CreateUserCoreAsync(platformRole, []);

    private async Task<TestUser> CreateUserCoreAsync(string? platformRole, (Guid OrganizationId, string Role)[] memberships)
    {
        var email = $"roles-{Guid.NewGuid():N}@example.com";
        var password = $"Pw-{Guid.NewGuid():N}aA1!";

        using var scope = app.Factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true };
        Assert.True((await users.CreateAsync(user, password)).Succeeded);
        if (platformRole is not null)
        {
            Assert.True((await users.AddToRoleAsync(user, platformRole)).Succeeded);
        }

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        foreach (var (organizationId, role) in memberships)
        {
            db.OrganizationMemberships.Add(OrganizationMembership.Create(organizationId, user.Id, role));
        }

        await db.SaveChangesAsync();

        using var client = app.Factory.CreateBrowserClient();
        using var login = await client.PostJsonAsync("/api/v1/auth/login", new { email, password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return new TestUser(user.Id, email, (await login.ReadJsonAsync()).GetProperty("accessToken").GetString()!);
    }

    private async Task<JsonElement> GetJsonAsync(string path, string token)
    {
        using var response = await SendAsync(HttpMethod.Get, path, token);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"GET {path} answered {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await response.ReadJsonAsync();
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string token, object? body = null, Guid? organization = null)
    {
        using var client = app.Factory.CreateBrowserClient();
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (organization is { } organizationId)
        {
            request.Headers.Add(OrganizationHeader, organizationId.ToString());
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await client.SendAsync(request);
    }

    private async Task<T> WithDbAsync<T>(Func<AppDbContext, Task<T>> action)
    {
        using var scope = app.Factory.Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }
}
