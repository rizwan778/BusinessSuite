using System.Net;
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
/// GET /api/v1/auth/me/permissions and the authorization framework (permissions, organization roles,
/// Super Admin, platform roles, organization selection, revocation) end to end on SQL Server.
/// </summary>
public sealed class AccessControlTests(SqlServerAppFixture app) : IClassFixture<SqlServerAppFixture>
{
    private const string PermissionsPath = "/api/v1/auth/me/permissions";
    private const string OrganizationHeader = "X-Organization-Id";

    // ---- GET /api/v1/auth/me/permissions -----------------------------------------------------------

    [SqlServerFact]
    public async Task User_without_organizations_has_no_permissions()
    {
        var user = await CreateUserAsync();

        var access = await GetPermissionsAsync(user.Token);

        Assert.False(access.GetProperty("isSuperAdmin").GetBoolean());
        Assert.Equal(JsonValueKind.Null, access.GetProperty("organization").ValueKind);
        Assert.Empty(access.GetProperty("organizations").EnumerateArray());
        Assert.Empty(access.GetProperty("permissions").EnumerateArray());
        Assert.Equal(user.Email, access.GetProperty("email").GetString());
    }

    [SqlServerFact]
    public async Task Only_organization_is_selected_automatically_with_the_role_permissions()
    {
        var organization = await CreateOrganizationAsync();
        var user = await CreateUserAsync((organization, OrganizationRoles.SalesOperator));

        var access = await GetPermissionsAsync(user.Token);

        var current = access.GetProperty("organization");
        Assert.Equal(organization, current.GetProperty("id").GetGuid());
        Assert.Equal(OrganizationRoles.SalesOperator, current.GetProperty("role").GetString());
        Assert.Equal(
            RolePermissions.ForOrganizationRole(OrganizationRoles.SalesOperator).Order(StringComparer.Ordinal),
            access.GetProperty("permissions").EnumerateArray().Select(permission => permission.GetString()));
    }

    [SqlServerFact]
    public async Task With_several_organizations_the_header_selects_one()
    {
        var shop = await CreateOrganizationAsync("Shop");
        var warehouse = await CreateOrganizationAsync("Warehouse");
        var user = await CreateUserAsync((shop, OrganizationRoles.Owner), (warehouse, OrganizationRoles.ReadOnly));

        var none = await GetPermissionsAsync(user.Token);
        Assert.Equal(JsonValueKind.Null, none.GetProperty("organization").ValueKind);
        Assert.Equal(2, none.GetProperty("organizations").GetArrayLength());
        Assert.Empty(none.GetProperty("permissions").EnumerateArray());

        var inWarehouse = await GetPermissionsAsync(user.Token, warehouse);
        Assert.Equal(OrganizationRoles.ReadOnly, inWarehouse.GetProperty("organization").GetProperty("role").GetString());
        Assert.DoesNotContain(inWarehouse.GetProperty("permissions").EnumerateArray(), permission => permission.GetString()!.EndsWith(".manage", StringComparison.Ordinal));
    }

    [SqlServerFact]
    public async Task Organization_the_user_does_not_belong_to_is_refused()
    {
        var mine = await CreateOrganizationAsync();
        var someoneElses = await CreateOrganizationAsync();
        var user = await CreateUserAsync((mine, OrganizationRoles.Owner));
        using var client = app.Factory.CreateBrowserClient();

        foreach (var header in new[] { someoneElses.ToString(), Guid.NewGuid().ToString(), "not-a-guid" })
        {
            using var response = await SendAsync(client, HttpMethod.Get, PermissionsPath, user.Token, header);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("Organization.AccessDenied", await response.ReadProblemCodeAsync());
        }
    }

    [SqlServerFact]
    public async Task Super_admin_holds_every_permission_and_may_act_in_any_organization()
    {
        var anyOrganization = await CreateOrganizationAsync();
        var admin = await CreateUserAsync(platformRole: PlatformRoles.SuperAdministrator);

        var access = await GetPermissionsAsync(admin.Token, anyOrganization);

        Assert.True(access.GetProperty("isSuperAdmin").GetBoolean());
        Assert.Contains(PlatformRoles.SuperAdministrator, access.GetProperty("platformRoles").EnumerateArray().Select(role => role.GetString()));
        Assert.Equal(anyOrganization, access.GetProperty("organization").GetProperty("id").GetGuid());
        Assert.Equal(JsonValueKind.Null, access.GetProperty("organization").GetProperty("role").ValueKind); // not a member
        Assert.Equal(Permissions.All, access.GetProperty("permissions").EnumerateArray().Select(permission => permission.GetString()));
    }

    [SqlServerFact]
    public async Task Website_session_can_read_its_permissions()
    {
        var organization = await CreateOrganizationAsync();
        var user = await CreateUserAsync((organization, OrganizationRoles.Accountant));
        using var browser = app.Factory.CreateBrowserClient();
        using var signIn = await browser.SignInAsync(user.Email, user.Password);

        using var response = await browser.GetAsync(new Uri(PermissionsPath, UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(OrganizationRoles.Accountant, (await response.ReadJsonAsync()).GetProperty("organization").GetProperty("role").GetString());
    }

    [SqlServerFact]
    public async Task Permissions_endpoint_requires_sign_in()
    {
        using var client = app.Factory.CreateBrowserClient();

        using var response = await client.GetAsync(new Uri(PermissionsPath, UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- Permission-based authorization -------------------------------------------------------------

    [SqlServerFact]
    public async Task Permission_protected_endpoint_allows_holders_and_refuses_others_with_the_reason()
    {
        var organization = await CreateOrganizationAsync();
        var seller = await CreateUserAsync((organization, OrganizationRoles.SalesOperator));
        var viewer = await CreateUserAsync((organization, OrganizationRoles.ReadOnly));
        using var client = app.Factory.CreateBrowserClient();

        using var allowed = await SendAsync(client, HttpMethod.Post, "/api/v1/test/access/invoices", seller.Token);
        using var refused = await SendAsync(client, HttpMethod.Post, "/api/v1/test/access/invoices", viewer.Token);

        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("Auth.PermissionDenied", await refused.ReadProblemCodeAsync());
        Assert.Contains("invoices.create", (await refused.ReadJsonAsync()).GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    [SqlServerFact]
    public async Task Several_permission_attributes_require_all_of_them()
    {
        var organization = await CreateOrganizationAsync();
        var seller = await CreateUserAsync((organization, OrganizationRoles.SalesOperator)); // invoices.view, not invoices.cancel
        var manager = await CreateUserAsync((organization, OrganizationRoles.Manager));
        using var client = app.Factory.CreateBrowserClient();

        using var refused = await SendAsync(client, HttpMethod.Post, "/api/v1/test/access/invoices/cancel", seller.Token);
        using var allowed = await SendAsync(client, HttpMethod.Post, "/api/v1/test/access/invoices/cancel", manager.Token);

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    [SqlServerFact]
    public async Task Any_permission_attribute_needs_just_one_of_them_and_the_refusal_names_all()
    {
        var organization = await CreateOrganizationAsync();
        var accountant = await CreateUserAsync((organization, OrganizationRoles.Accountant)); // reports.export, not invoices.cancel
        var manager = await CreateUserAsync((organization, OrganizationRoles.Manager));       // both
        var viewer = await CreateUserAsync((organization, OrganizationRoles.ReadOnly));       // neither
        var superAdmin = await CreateUserAsync(PlatformRoles.SuperAdministrator);
        using var client = app.Factory.CreateBrowserClient();
        const string path = "/api/v1/test/access/invoices/cancel-or-export";

        using var byExport = await SendAsync(client, HttpMethod.Post, path, accountant.Token);
        using var byBoth = await SendAsync(client, HttpMethod.Post, path, manager.Token);
        using var bySuperAdmin = await SendAsync(client, HttpMethod.Post, path, superAdmin.Token);
        using var refused = await SendAsync(client, HttpMethod.Post, path, viewer.Token);

        Assert.Equal(HttpStatusCode.OK, byExport.StatusCode);
        Assert.Equal(HttpStatusCode.OK, byBoth.StatusCode);
        Assert.Equal(HttpStatusCode.OK, bySuperAdmin.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("Auth.PermissionDenied", await refused.ReadProblemCodeAsync());
        var detail = (await refused.ReadJsonAsync()).GetProperty("detail").GetString();
        Assert.Contains("invoices.cancel", detail, StringComparison.Ordinal);
        Assert.Contains("reports.export", detail, StringComparison.Ordinal);
    }

    [SqlServerFact]
    public async Task Without_a_selected_organization_organization_permissions_are_refused()
    {
        var a = await CreateOrganizationAsync();
        var b = await CreateOrganizationAsync();
        var user = await CreateUserAsync((a, OrganizationRoles.Owner), (b, OrganizationRoles.Owner));
        using var client = app.Factory.CreateBrowserClient();

        using var noHeader = await SendAsync(client, HttpMethod.Post, "/api/v1/test/access/invoices", user.Token);
        using var withHeader = await SendAsync(client, HttpMethod.Post, "/api/v1/test/access/invoices", user.Token, a.ToString());

        Assert.Equal(HttpStatusCode.Forbidden, noHeader.StatusCode);
        Assert.Contains("X-Organization-Id", (await noHeader.ReadJsonAsync()).GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, withHeader.StatusCode);
    }

    // ---- Role-based authorization ----------------------------------------------------------------------

    [SqlServerFact]
    public async Task Organization_role_endpoint_checks_the_role_in_the_current_organization()
    {
        var organization = await CreateOrganizationAsync();
        var owner = await CreateUserAsync((organization, OrganizationRoles.Owner));
        var manager = await CreateUserAsync((organization, OrganizationRoles.Manager));
        using var client = app.Factory.CreateBrowserClient();

        using var allowed = await SendAsync(client, HttpMethod.Get, "/api/v1/test/access/owners", owner.Token);
        using var refused = await SendAsync(client, HttpMethod.Get, "/api/v1/test/access/owners", manager.Token);

        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("Auth.OrganizationRoleRequired", await refused.ReadProblemCodeAsync());
    }

    [SqlServerFact]
    public async Task Super_admin_and_platform_endpoints_check_platform_roles()
    {
        var organization = await CreateOrganizationAsync();
        var owner = await CreateUserAsync((organization, OrganizationRoles.Owner));
        var platformAdmin = await CreateUserAsync(platformRole: PlatformRoles.Administrator);
        var superAdmin = await CreateUserAsync(platformRole: PlatformRoles.SuperAdministrator);
        using var client = app.Factory.CreateBrowserClient();

        using var ownerSuper = await SendAsync(client, HttpMethod.Get, "/api/v1/test/access/super-admin", owner.Token);
        using var platformSuper = await SendAsync(client, HttpMethod.Get, "/api/v1/test/access/super-admin", platformAdmin.Token);
        using var superSuper = await SendAsync(client, HttpMethod.Get, "/api/v1/test/access/super-admin", superAdmin.Token);
        using var ownerPlatform = await SendAsync(client, HttpMethod.Get, "/api/v1/test/access/platform", owner.Token);
        using var platformPlatform = await SendAsync(client, HttpMethod.Get, "/api/v1/test/access/platform", platformAdmin.Token);
        using var superPlatform = await SendAsync(client, HttpMethod.Get, "/api/v1/test/access/platform", superAdmin.Token);
        using var superOrgRole = await SendAsync(client, HttpMethod.Get, "/api/v1/test/access/owners", superAdmin.Token, organization.ToString());

        Assert.Equal(HttpStatusCode.Forbidden, ownerSuper.StatusCode);
        Assert.Equal("Auth.SuperAdminRequired", await ownerSuper.ReadProblemCodeAsync());
        Assert.Equal(HttpStatusCode.Forbidden, platformSuper.StatusCode);
        Assert.Equal(HttpStatusCode.OK, superSuper.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, ownerPlatform.StatusCode);
        Assert.Equal(HttpStatusCode.OK, platformPlatform.StatusCode);
        Assert.Equal(HttpStatusCode.OK, superPlatform.StatusCode);
        Assert.Equal(HttpStatusCode.OK, superOrgRole.StatusCode);
    }

    [SqlServerFact]
    public async Task Application_services_see_the_organization_and_permissions_through_ICurrentUser()
    {
        var organization = await CreateOrganizationAsync();
        var seller = await CreateUserAsync((organization, OrganizationRoles.SalesOperator));
        using var client = app.Factory.CreateBrowserClient();

        using var response = await SendAsync(client, HttpMethod.Get, "/api/v1/test/access/current-user", seller.Token);
        var body = await response.ReadJsonAsync();

        Assert.Equal(seller.UserId, body.GetProperty("userId").GetGuid());
        Assert.Equal(organization, body.GetProperty("organizationId").GetGuid());
        Assert.False(body.GetProperty("isSuperAdmin").GetBoolean());
        Assert.True(body.GetProperty("canCreateInvoices").GetBoolean());
    }

    // ---- Changes take effect immediately, without a new token --------------------------------------

    [SqlServerFact]
    public async Task Role_change_membership_removal_and_suspension_apply_on_the_next_request()
    {
        var organization = await CreateOrganizationAsync();
        var user = await CreateUserAsync((organization, OrganizationRoles.SalesOperator));
        using var client = app.Factory.CreateBrowserClient();

        await WithDbAsync(db => db.OrganizationMemberships.Where(m => m.UserId == user.UserId)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.RoleId, OrganizationRoles.IdOf(OrganizationRoles.ReadOnly))));
        using var afterRoleChange = await SendAsync(client, HttpMethod.Post, "/api/v1/test/access/invoices", user.Token);
        Assert.Equal(HttpStatusCode.Forbidden, afterRoleChange.StatusCode);

        await WithDbAsync(db => db.OrganizationMemberships.Where(m => m.UserId == user.UserId)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.RoleId, OrganizationRoles.OwnerId)));
        Assert.Contains("subscription.manage", (await GetPermissionsAsync(user.Token)).GetProperty("permissions").EnumerateArray().Select(p => p.GetString()));

        await WithDbAsync(db => db.Organizations.Where(o => o.Id == organization)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.IsActive, false)));
        var suspended = await GetPermissionsAsync(user.Token);
        Assert.Equal(JsonValueKind.Null, suspended.GetProperty("organization").ValueKind);
        Assert.Empty(suspended.GetProperty("permissions").EnumerateArray());
    }

    // ---- Token / session revocation -----------------------------------------------------------------

    [SqlServerFact]
    public async Task Deactivating_a_user_rejects_their_access_token_immediately()
    {
        var user = await CreateUserAsync();
        using var client = app.Factory.CreateBrowserClient();

        await WithUserManagerAsync(async users =>
        {
            var entity = await users.FindByIdAsync(user.UserId.ToString());
            entity!.Deactivate();
            await users.UpdateAsync(entity);
        });

        using var response = await SendAsync(client, HttpMethod.Get, PermissionsPath, user.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [SqlServerFact]
    public async Task Changing_the_password_rejects_existing_access_tokens()
    {
        var user = await CreateUserAsync();
        using var client = app.Factory.CreateBrowserClient();

        await WithUserManagerAsync(async users =>
        {
            var entity = await users.FindByIdAsync(user.UserId.ToString());
            var changed = await users.ChangePasswordAsync(entity!, user.Password, $"New-{Guid.NewGuid():N}aA1!");
            Assert.True(changed.Succeeded);
        });

        using var response = await SendAsync(client, HttpMethod.Get, PermissionsPath, user.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [SqlServerFact]
    public async Task Logout_all_signs_out_every_device()
    {
        var user = await CreateUserAsync();
        var otherDevice = await LoginAsync(user.Email, user.Password);
        using var client = app.Factory.CreateBrowserClient();

        using var logoutAll = await SendAsync(client, HttpMethod.Post, "/api/v1/auth/logout-all", user.Token);
        Assert.Equal(HttpStatusCode.NoContent, logoutAll.StatusCode);

        using var first = await SendAsync(client, HttpMethod.Get, PermissionsPath, user.Token);
        using var second = await SendAsync(client, HttpMethod.Get, PermissionsPath, otherDevice.GetProperty("accessToken").GetString()!);
        using var refresh = await client.PostJsonAsync("/api/v1/auth/refresh", new { refreshToken = otherDevice.GetProperty("refreshToken").GetString() });
        Assert.Equal(HttpStatusCode.Unauthorized, first.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);

        await WithDbAsync(async db => Assert.False(await db.UserSessions.AnyAsync(s => s.UserId == user.UserId && s.RevokedAtUtc == null)));
    }

    [SqlServerFact]
    public async Task Sessions_never_outlive_their_absolute_limit()
    {
        var user = await CreateUserAsync();

        await WithDbAsync(async db =>
        {
            var session = await db.UserSessions.AsNoTracking().SingleAsync(s => s.UserId == user.UserId);
            Assert.True(session.AbsoluteExpiresAtUtc <= session.CreatedAtUtc.AddDays(30).AddSeconds(5));
            Assert.True(session.ExpiresAtUtc <= session.AbsoluteExpiresAtUtc);
        });
    }

    // ---- Helpers -----------------------------------------------------------------------------------------

    private sealed record TestUser(Guid UserId, string Email, string Password, string Token);

    private async Task<Guid> CreateOrganizationAsync(string name = "Test Business")
    {
        var organization = Organization.Create($"{name} {Guid.NewGuid():N}"[..(name.Length + 9)]);
        await WithDbAsync(async db =>
        {
            db.Organizations.Add(organization);
            await db.SaveChangesAsync();
        });
        return organization.Id;
    }

    private async Task<TestUser> CreateUserAsync(params (Guid OrganizationId, string Role)[] memberships) =>
        await CreateUserAsync(null, memberships);

    private async Task<TestUser> CreateUserAsync(string? platformRole, params (Guid OrganizationId, string Role)[] memberships)
    {
        var email = $"access-{Guid.NewGuid():N}@example.com";
        var password = $"Pw-{Guid.NewGuid():N}aA1!";
        var userId = Guid.Empty;

        await WithUserManagerAsync(async users =>
        {
            var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true };
            Assert.True((await users.CreateAsync(user, password)).Succeeded);
            if (platformRole is not null)
            {
                Assert.True((await users.AddToRoleAsync(user, platformRole)).Succeeded);
            }

            userId = user.Id;
        });

        await WithDbAsync(async db =>
        {
            foreach (var (organizationId, role) in memberships)
            {
                db.OrganizationMemberships.Add(OrganizationMembership.Create(organizationId, userId, role));
            }

            await db.SaveChangesAsync();
        });

        var tokens = await LoginAsync(email, password);
        return new TestUser(userId, email, password, tokens.GetProperty("accessToken").GetString()!);
    }

    private async Task<JsonElement> LoginAsync(string email, string password)
    {
        using var client = app.Factory.CreateBrowserClient();
        using var response = await client.PostJsonAsync("/api/v1/auth/login", new { email, password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.ReadJsonAsync();
    }

    private async Task<JsonElement> GetPermissionsAsync(string token, Guid? organizationId = null)
    {
        using var client = app.Factory.CreateBrowserClient();
        using var response = await SendAsync(client, HttpMethod.Get, PermissionsPath, token, organizationId?.ToString());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.ReadJsonAsync();
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, string token, string? organizationHeader = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        if (organizationHeader is not null)
        {
            request.Headers.TryAddWithoutValidation(OrganizationHeader, organizationHeader);
        }

        return await client.SendAsync(request);
    }

    private async Task WithDbAsync(Func<AppDbContext, Task> action)
    {
        using var scope = app.Factory.Services.CreateScope();
        await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    private async Task WithUserManagerAsync(Func<UserManager<ApplicationUser>, Task> action)
    {
        using var scope = app.Factory.Services.CreateScope();
        await action(scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>());
    }
}
