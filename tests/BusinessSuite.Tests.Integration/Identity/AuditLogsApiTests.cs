using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BusinessSuite.Application.Common.Security;
using BusinessSuite.Application.Features.Audit;
using BusinessSuite.Domain.Organizations;
using BusinessSuite.Infrastructure.Identity;
using BusinessSuite.Infrastructure.Persistence;
using BusinessSuite.Tests.Integration.Support;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BusinessSuite.Tests.Integration.Identity;

/// <summary>
/// The audit trail end to end on SQL Server: every tracked administrative change leaves exactly one entry (and refused or
/// no-op changes none), <c>/api/v1/audit-logs</c> isolates organizations, the table is append-only, and the security
/// hardening that came with it (last Owner on account deactivation, sessions of deactivated accounts).
/// </summary>
public sealed class AuditLogsApiTests(SqlServerAppFixture app) : IClassFixture<SqlServerAppFixture>
{
    private const string AuditPath = "/api/v1/audit-logs";
    private const string OrganizationHeader = "X-Organization-Id";

    // ---- What is recorded --------------------------------------------------------------------------------

    [SqlServerFact]
    public async Task Every_user_administration_change_is_recorded_once_with_actor_organization_and_correlation_id()
    {
        var organization = await CreateOrganizationAsync();
        var owner = await CreateUserAsync((organization, OrganizationRoles.Owner));

        // Create (with a department), profile, role, group, deactivate, activate, password reset.
        using var created = await SendAsync(HttpMethod.Post, "/api/v1/users", owner.Token,
            new { email = $"audit-{Guid.NewGuid():N}@example.com", role = OrganizationRoles.ReadOnly, department = "Sales", phoneNumber = "+92 300 1234567" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var createdBody = await created.ReadJsonAsync();
        var userId = createdBody.GetProperty("id").GetGuid();
        var email = createdBody.GetProperty("email").GetString()!;
        var correlationId = created.Headers.GetValues("X-Correlation-ID").Single();

        await ExpectOkAsync(HttpMethod.Put, $"/api/v1/users/{userId}", owner.Token, new { displayName = "Ayesha", phoneNumber = "+92 300 7654321" });
        await ExpectOkAsync(HttpMethod.Patch, $"/api/v1/users/{userId}/role", owner.Token, new { role = OrganizationRoles.Accountant });
        await ExpectOkAsync(HttpMethod.Patch, $"/api/v1/users/{userId}/group", owner.Token, new { department = "Finance" });
        await ExpectOkAsync(HttpMethod.Patch, $"/api/v1/users/{userId}/status", owner.Token, new { isActive = false });
        await ExpectOkAsync(HttpMethod.Patch, $"/api/v1/users/{userId}/status", owner.Token, new { isActive = true });
        using var reset = await SendAsync(HttpMethod.Post, $"/api/v1/users/{userId}/reset-password", owner.Token);
        Assert.Equal(HttpStatusCode.Accepted, reset.StatusCode);

        // Newest first, one entry per change.
        var page = await GetJsonAsync($"{AuditPath}?entityId={userId}", owner.Token);
        Assert.Equal(
            new[]
            {
                AuditActions.UserPasswordResetInitiated, AuditActions.UserActivated, AuditActions.UserDeactivated,
                AuditActions.UserGroupChanged, AuditActions.UserRoleChanged, AuditActions.UserProfileUpdated, AuditActions.UserCreated,
            },
            Actions(page));
        Assert.All(page.GetProperty("items").EnumerateArray(), item =>
        {
            Assert.Equal(owner.UserId, item.GetProperty("actorUserId").GetGuid());
            Assert.Equal(organization, item.GetProperty("organizationId").GetGuid());
            Assert.Equal("User", item.GetProperty("entityType").GetString());
        });

        // The detail carries the changed values and the request's correlation id.
        var creation = await GetEntryAsync(owner.Token, page, AuditActions.UserCreated);
        Assert.Equal(correlationId, creation.GetProperty("correlationId").GetString());
        Assert.Equal(OrganizationRoles.ReadOnly, creation.GetProperty("details").GetProperty("role").GetString());
        Assert.Equal("Sales", creation.GetProperty("details").GetProperty("department").GetString());
        Assert.True(creation.GetProperty("details").GetProperty("setupEmailSent").GetBoolean());

        var roleChange = (await GetEntryAsync(owner.Token, page, AuditActions.UserRoleChanged)).GetProperty("details");
        Assert.Equal(OrganizationRoles.ReadOnly, roleChange.GetProperty("fromRole").GetString());
        Assert.Equal(OrganizationRoles.Accountant, roleChange.GetProperty("toRole").GetString());
        Assert.Equal(OrganizationRoles.IdOf(OrganizationRoles.Accountant), roleChange.GetProperty("toRoleId").GetGuid());

        var groupChange = (await GetEntryAsync(owner.Token, page, AuditActions.UserGroupChanged)).GetProperty("details");
        Assert.Equal("Sales", groupChange.GetProperty("from").GetString());
        Assert.Equal("Finance", groupChange.GetProperty("to").GetString());

        var profile = (await GetEntryAsync(owner.Token, page, AuditActions.UserProfileUpdated)).GetProperty("details");
        Assert.True(profile.GetProperty("phoneNumberChanged").GetBoolean());

        // Personal data never reaches the audit log: no e-mail address, no phone number, no reset code.
        var stored = await WithDbAsync(db => db.AuditLogs.AsNoTracking().Where(entry => entry.EntityId == userId).Select(entry => entry.Details ?? string.Empty).ToListAsync());
        Assert.DoesNotContain(stored, details => details.Contains(email, StringComparison.OrdinalIgnoreCase)
            || details.Contains("7654321", StringComparison.Ordinal) || details.Contains("1234567", StringComparison.Ordinal));
    }

    [SqlServerFact]
    public async Task Changes_that_alter_nothing_or_are_refused_record_nothing()
    {
        var organization = await CreateOrganizationAsync();
        var owner = await CreateUserAsync((organization, OrganizationRoles.Owner));
        var admin = await CreateUserAsync((organization, OrganizationRoles.Administrator));
        var member = await CreateUserAsync((organization, OrganizationRoles.ReadOnly));
        var superAdmin = await CreatePlatformUserAsync(PlatformRoles.SuperAdministrator);

        // No-ops: same role, already active.
        await ExpectOkAsync(HttpMethod.Patch, $"/api/v1/users/{member.UserId}/role", owner.Token, new { role = OrganizationRoles.ReadOnly });
        await ExpectOkAsync(HttpMethod.Patch, $"/api/v1/users/{member.UserId}/status", owner.Token, new { isActive = true });

        // Refused: the last active Owner, an Administrator touching the Owner, self-promotion, escalation by role.
        using var lastOwner = await SendAsync(HttpMethod.Patch, $"/api/v1/users/{owner.UserId}/status", superAdmin.Token, new { isActive = false }, organization);
        using var ownerProtected = await SendAsync(HttpMethod.Patch, $"/api/v1/users/{owner.UserId}/status", admin.Token, new { isActive = false });
        using var selfPromotion = await SendAsync(HttpMethod.Patch, $"/api/v1/users/{admin.UserId}/role", admin.Token, new { role = OrganizationRoles.Owner });
        using var escalation = await SendAsync(HttpMethod.Patch, $"/api/v1/users/{member.UserId}/role", admin.Token, new { role = OrganizationRoles.Owner });
        Assert.Equal("Users.LastOwner", await lastOwner.ReadProblemCodeAsync());
        Assert.Equal("Users.OwnerRequired", await ownerProtected.ReadProblemCodeAsync());
        Assert.Equal("Users.CannotChangeOwnAccess", await selfPromotion.ReadProblemCodeAsync());
        Assert.Equal("Users.OwnerRequired", await escalation.ReadProblemCodeAsync());

        Assert.Equal(0, await WithDbAsync(db => db.AuditLogs.CountAsync(entry => entry.OrganizationId == organization)));
    }

    [SqlServerFact]
    public async Task Group_role_permission_and_organization_changes_are_recorded()
    {
        var superAdmin = await CreatePlatformUserAsync(PlatformRoles.SuperAdministrator);

        // Organization: created, renamed, suspended, reactivated (platform operator), in the organization's own log.
        using var createdOrganization = await SendAsync(HttpMethod.Post, "/api/v1/organizations", superAdmin.Token, new { name = "Audit Shop" });
        Assert.Equal(HttpStatusCode.Created, createdOrganization.StatusCode);
        var organization = (await createdOrganization.ReadJsonAsync()).GetProperty("id").GetGuid();
        await ExpectOkAsync(HttpMethod.Put, $"/api/v1/organizations/{organization}", superAdmin.Token, new { name = "Audit Shop Ltd" });
        await ExpectOkAsync(HttpMethod.Patch, $"/api/v1/organizations/{organization}/status", superAdmin.Token, new { isActive = false });
        await ExpectOkAsync(HttpMethod.Patch, $"/api/v1/organizations/{organization}/status", superAdmin.Token, new { isActive = true });

        var admin = await CreateUserAsync((organization, OrganizationRoles.Administrator));

        // Group: created, renamed, deleted.
        using var group = await SendAsync(HttpMethod.Post, "/api/v1/groups", admin.Token, new { name = "Packing" });
        var groupId = (await group.ReadJsonAsync()).GetProperty("id").GetGuid();
        await ExpectOkAsync(HttpMethod.Put, $"/api/v1/groups/{groupId}", admin.Token, new { name = "Shipping" });
        using var deletedGroup = await SendAsync(HttpMethod.Delete, $"/api/v1/groups/{groupId}", admin.Token);
        Assert.Equal(HttpStatusCode.NoContent, deletedGroup.StatusCode);

        // Custom role: created, permissions changed, renamed, deleted.
        using var role = await SendAsync(HttpMethod.Post, "/api/v1/roles", admin.Token, new { name = "Cashier", permissions = new[] { Permissions.Sales.View } });
        Assert.Equal(HttpStatusCode.Created, role.StatusCode);
        var roleId = (await role.ReadJsonAsync()).GetProperty("id").GetGuid();
        await ExpectOkAsync(HttpMethod.Put, $"/api/v1/roles/{roleId}/permissions", admin.Token, new { permissions = new[] { Permissions.Sales.View, Permissions.Invoices.View } });
        await ExpectOkAsync(HttpMethod.Put, $"/api/v1/roles/{roleId}", admin.Token, new { name = "Senior Cashier" });
        using var deletedRole = await SendAsync(HttpMethod.Delete, $"/api/v1/roles/{roleId}", admin.Token);
        Assert.Equal(HttpStatusCode.NoContent, deletedRole.StatusCode);

        var log = await GetJsonAsync($"{AuditPath}?pageSize=100", admin.Token);
        Assert.Equal(
            new[]
            {
                AuditActions.RoleDeleted, AuditActions.RoleUpdated, AuditActions.RolePermissionsChanged, AuditActions.RoleCreated,
                AuditActions.GroupDeleted, AuditActions.GroupUpdated, AuditActions.GroupCreated,
                AuditActions.OrganizationReactivated, AuditActions.OrganizationSuspended, AuditActions.OrganizationRenamed, AuditActions.OrganizationCreated,
            },
            Actions(log));

        var permissions = (await GetEntryAsync(admin.Token, log, AuditActions.RolePermissionsChanged)).GetProperty("details");
        Assert.Equal(new[] { Permissions.Invoices.View }, permissions.GetProperty("added").EnumerateArray().Select(value => value.GetString()));
        Assert.Empty(permissions.GetProperty("removed").EnumerateArray());

        var renamed = (await GetEntryAsync(admin.Token, log, AuditActions.GroupUpdated)).GetProperty("details");
        Assert.Equal("Packing", renamed.GetProperty("fromName").GetString());
        Assert.Equal("Shipping", renamed.GetProperty("toName").GetString());

        Assert.Equal(superAdmin.UserId, Single(log, AuditActions.OrganizationSuspended).GetProperty("actorUserId").GetGuid());

        // Filters: by action and by entity type.
        Assert.Equal(new[] { AuditActions.GroupCreated }, Actions(await GetJsonAsync($"{AuditPath}?action=group.created", admin.Token)));
        Assert.Equal(4, (await GetJsonAsync($"{AuditPath}?entityType=Organization", admin.Token)).GetProperty("totalCount").GetInt32());
    }

    [SqlServerFact]
    public async Task Creating_an_organization_with_its_first_administrator_records_both_in_that_organization()
    {
        var superAdmin = await CreatePlatformUserAsync(PlatformRoles.SuperAdministrator);

        using var created = await SendAsync(HttpMethod.Post, "/api/v1/organizations", superAdmin.Token,
            new { name = "Audit Initial", initialAdmin = new { email = $"first-{Guid.NewGuid():N}@example.com" } });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var organization = (await created.ReadJsonAsync()).GetProperty("id").GetGuid();

        var log = await GetJsonAsync($"{AuditPath}?organizationId={organization}", superAdmin.Token);
        Assert.Equal(new[] { AuditActions.OrganizationCreated, AuditActions.UserCreated }.Order(), Actions(log).Order());
        var userCreated = (await GetEntryAsync(superAdmin.Token, log, AuditActions.UserCreated)).GetProperty("details");
        Assert.Equal(OrganizationRoles.Owner, userCreated.GetProperty("role").GetString());
    }

    // ---- Organization isolation and IDOR -----------------------------------------------------------------

    [SqlServerFact]
    public async Task Organization_admins_see_only_their_organizations_log_and_other_entries_look_missing()
    {
        var mine = await CreateOrganizationAsync();
        var theirs = await CreateOrganizationAsync();
        var admin = await CreateUserAsync((mine, OrganizationRoles.Administrator));
        var otherAdmin = await CreateUserAsync((theirs, OrganizationRoles.Administrator));
        var superAdmin = await CreatePlatformUserAsync(PlatformRoles.SuperAdministrator);
        await CreateGroupAsync(admin.Token, "Mine");
        await CreateGroupAsync(otherAdmin.Token, "Theirs");

        // A platform-wide entry: a shared role's availability.
        using var platformRole = await SendAsync(HttpMethod.Post, "/api/v1/roles", superAdmin.Token, new { name = $"Shared {Guid.NewGuid():N}"[..20], scope = "platform" });
        Assert.Equal(HttpStatusCode.Created, platformRole.StatusCode);
        var platformRoleId = (await platformRole.ReadJsonAsync()).GetProperty("id").GetGuid();
        await ExpectOkAsync(HttpMethod.Put, $"/api/v1/roles/{platformRoleId}/availability", superAdmin.Token, new { allOrganizations = true });

        var myLog = await GetJsonAsync(AuditPath, admin.Token);
        Assert.Equal(new[] { AuditActions.GroupCreated }, Actions(myLog));
        Assert.All(myLog.GetProperty("items").EnumerateArray(), item => Assert.Equal(mine, item.GetProperty("organizationId").GetGuid()));

        var theirEntry = await WithDbAsync(db => db.AuditLogs.Where(entry => entry.OrganizationId == theirs).Select(entry => entry.Id).SingleAsync());
        var platformEntry = await WithDbAsync(db => db.AuditLogs
            .Where(entry => entry.EntityId == platformRoleId && entry.Action == AuditActions.RoleAvailabilityChanged)
            .Select(entry => new { entry.Id, entry.OrganizationId }).SingleAsync());
        Assert.Null(platformEntry.OrganizationId);

        // Another organization's entry and a platform-wide entry answer exactly like an unknown id.
        using var missing = await SendAsync(HttpMethod.Get, $"{AuditPath}/{Guid.NewGuid()}", admin.Token);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("Audit.NotFound", await missing.ReadProblemCodeAsync());
        var missingDetail = (await missing.ReadJsonAsync()).GetProperty("detail").GetString();
        foreach (var id in new[] { theirEntry, platformEntry.Id })
        {
            using var response = await SendAsync(HttpMethod.Get, $"{AuditPath}/{id}", admin.Token);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("Audit.NotFound", await response.ReadProblemCodeAsync());
            Assert.Equal(missingDetail, (await response.ReadJsonAsync()).GetProperty("detail").GetString());
        }

        Assert.Contains(LogLines(LogLevel.Information, theirEntry), line => line.Contains("outside their organization", StringComparison.Ordinal));

        // Naming their organization: refused in the filter, and in the header (not a member).
        using var filtered = await SendAsync(HttpMethod.Get, $"{AuditPath}?organizationId={theirs}", admin.Token);
        Assert.Equal(HttpStatusCode.Forbidden, filtered.StatusCode);
        Assert.Equal("Audit.OrganizationFilterNotAllowed", await filtered.ReadProblemCodeAsync());
        using var spoofed = await SendAsync(HttpMethod.Get, AuditPath, admin.Token, organization: theirs);
        Assert.Equal(HttpStatusCode.Forbidden, spoofed.StatusCode);

        // The Super Admin reads everything, including the platform-wide entry, and can filter by organization.
        var all = await GetJsonAsync($"{AuditPath}?entityId={platformRoleId}", superAdmin.Token);
        Assert.Equal(new[] { AuditActions.RoleAvailabilityChanged, AuditActions.RoleCreated }, Actions(all));
        Assert.Equal(JsonValueKind.Null, Single(all, AuditActions.RoleAvailabilityChanged).GetProperty("organizationId").ValueKind);
        Assert.Equal(new[] { AuditActions.GroupCreated }, Actions(await GetJsonAsync($"{AuditPath}?organizationId={theirs}", superAdmin.Token)));
        Assert.Equal(theirEntry, (await GetJsonAsync($"{AuditPath}/{theirEntry}", superAdmin.Token)).GetProperty("id").GetGuid());
    }

    [SqlServerFact]
    public async Task Reading_needs_audit_view_or_the_platform_audit_permission()
    {
        var organization = await CreateOrganizationAsync();
        var owner = await CreateUserAsync((organization, OrganizationRoles.Owner));
        var manager = await CreateUserAsync((organization, OrganizationRoles.Manager));     // users.view, no audit.view
        var viewer = await CreateUserAsync((organization, OrganizationRoles.ReadOnly));     // every *.view except audit
        var platformAdmin = await CreatePlatformUserAsync(PlatformRoles.Administrator);     // platform.audit.view
        await CreateGroupAsync(owner.Token, "Front");

        Assert.Equal(new[] { AuditActions.GroupCreated }, Actions(await GetJsonAsync(AuditPath, owner.Token)));

        foreach (var refused in new[] { manager, viewer })
        {
            using var list = await SendAsync(HttpMethod.Get, AuditPath, refused.Token);
            using var get = await SendAsync(HttpMethod.Get, $"{AuditPath}/{Guid.NewGuid()}", refused.Token);
            Assert.Equal("Auth.PermissionDenied", await list.ReadProblemCodeAsync());
            Assert.Equal("Auth.PermissionDenied", await get.ReadProblemCodeAsync());
        }

        // Platform Administrators hold no organization permission, but read every organization's log.
        var platformView = await GetJsonAsync($"{AuditPath}?organizationId={organization}", platformAdmin.Token);
        Assert.Equal(new[] { AuditActions.GroupCreated }, Actions(platformView));
    }

    [SqlServerFact]
    public async Task Input_is_validated_and_every_endpoint_needs_a_signed_in_user()
    {
        var organization = await CreateOrganizationAsync();
        var owner = await CreateUserAsync((organization, OrganizationRoles.Owner));

        using var reversed = await SendAsync(HttpMethod.Get, $"{AuditPath}?from=2026-11-02T00:00:00Z&to=2026-11-01T00:00:00Z", owner.Token);
        using var badPage = await SendAsync(HttpMethod.Get, $"{AuditPath}?pageSize=0", owner.Token);
        using var longAction = await SendAsync(HttpMethod.Get, $"{AuditPath}?action={new string('a', 101)}", owner.Token);
        Assert.Equal("Audit.InvalidRange", await reversed.ReadProblemCodeAsync());
        Assert.Equal("Request.ValidationFailed", await badPage.ReadProblemCodeAsync());
        Assert.Equal("Request.ValidationFailed", await longAction.ReadProblemCodeAsync());

        using var client = app.Factory.CreateBrowserClient();
        foreach (var path in new[] { AuditPath, $"{AuditPath}/{Guid.NewGuid()}" })
        {
            using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative)));
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("Auth.Unauthorized", await response.ReadProblemCodeAsync());
        }
    }

    // ---- Tamper resistance -------------------------------------------------------------------------------

    [SqlServerFact]
    public async Task Entries_cannot_be_changed_or_deleted_even_with_direct_sql()
    {
        var organization = await CreateOrganizationAsync();
        var owner = await CreateUserAsync((organization, OrganizationRoles.Owner));
        await CreateGroupAsync(owner.Token, "Locked");
        var id = await WithDbAsync(db => db.AuditLogs.Where(entry => entry.OrganizationId == organization).Select(entry => entry.Id).SingleAsync());

        var update = await Assert.ThrowsAsync<SqlException>(() => WithDbAsync(db =>
            db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [audit].[AuditLogs] SET [Action] = N'group.deleted' WHERE [Id] = {id}")));
        var delete = await Assert.ThrowsAsync<SqlException>(() => WithDbAsync(db =>
            db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM [audit].[AuditLogs] WHERE [Id] = {id}")));

        Assert.Contains("cannot be changed or deleted", update.Message, StringComparison.Ordinal);
        Assert.Contains("cannot be changed or deleted", delete.Message, StringComparison.Ordinal);
        Assert.Equal(AuditActions.GroupCreated, await WithDbAsync(db => db.AuditLogs.Where(entry => entry.Id == id).Select(entry => entry.Action).SingleAsync()));
    }

    // ---- Account deactivation: last Owner, sessions, audit -----------------------------------------------

    [SqlServerFact]
    public async Task A_super_admin_cannot_deactivate_the_account_of_an_organizations_last_active_owner()
    {
        var organization = await CreateOrganizationAsync();
        var owner = await CreateUserAsync((organization, OrganizationRoles.Owner));
        var superAdmin = await CreatePlatformUserAsync(PlatformRoles.SuperAdministrator);

        using var refused = await SendAsync(HttpMethod.Patch, $"/api/v1/users/{owner.UserId}/status", superAdmin.Token, new { isActive = false });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("Users.LastOwner", await refused.ReadProblemCodeAsync());
        Assert.True(await WithDbAsync(db => db.Users.Where(user => user.Id == owner.UserId).Select(user => user.IsActive).SingleAsync()));
        Assert.Equal(0, await WithDbAsync(db => db.AuditLogs.CountAsync(entry => entry.EntityId == owner.UserId)));
        Assert.Contains(LogLines(LogLevel.Information, owner.UserId), line => line.Contains("Users.LastOwner", StringComparison.Ordinal));

        // A suspended organization does not need an Owner: then it is allowed.
        await ExpectOkAsync(HttpMethod.Patch, $"/api/v1/organizations/{organization}/status", superAdmin.Token, new { isActive = false });
        await ExpectOkAsync(HttpMethod.Patch, $"/api/v1/users/{owner.UserId}/status", superAdmin.Token, new { isActive = false });
    }

    [SqlServerFact]
    public async Task Deactivating_an_account_ends_its_sessions_removes_all_access_and_is_recorded_platform_wide()
    {
        var organization = await CreateOrganizationAsync();
        await CreateUserAsync((organization, OrganizationRoles.Owner)); // another active Owner remains
        var target = await CreateUserAsync((organization, OrganizationRoles.Owner));
        var superAdmin = await CreatePlatformUserAsync(PlatformRoles.SuperAdministrator);

        using var before = await SendAsync(HttpMethod.Get, "/api/v1/auth/me", target.Token);
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);

        await ExpectOkAsync(HttpMethod.Patch, $"/api/v1/users/{target.UserId}/status", superAdmin.Token, new { isActive = false });

        // The access token stops working at once, and every API session is revoked.
        using var after = await SendAsync(HttpMethod.Get, "/api/v1/auth/me", target.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
        Assert.Equal(0, await WithDbAsync(db => db.UserSessions.CountAsync(session => session.UserId == target.UserId && session.RevokedAtUtc == null)));

        // Whatever still signs the account in (e.g. a website cookie before its next stamp check) gets no access at all.
        using (var scope = app.Factory.Services.CreateScope())
        {
            var access = await scope.ServiceProvider.GetRequiredService<IUserAccessService>().GetAccessAsync(target.UserId, organization);
            Assert.Empty(access.Permissions);
            Assert.Empty(access.Memberships);
            Assert.Equal(OrganizationSelection.Denied, access.Selection);
        }

        // Recorded once, platform-wide: the organization's administrators do not see account-level changes.
        var entry = await WithDbAsync(db => db.AuditLogs.AsNoTracking().SingleAsync(candidate => candidate.EntityId == target.UserId));
        Assert.Equal(AuditActions.UserAccountDeactivated, entry.Action);
        Assert.Null(entry.OrganizationId);
        Assert.Equal(superAdmin.UserId, entry.ActorUserId);
    }

    // ---- Helpers -----------------------------------------------------------------------------------------

    private sealed record TestUser(Guid UserId, string Email, string Token);

    private static List<string?> Actions(JsonElement page) =>
        page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("action").GetString()).ToList();

    private static JsonElement Single(JsonElement page, string action) =>
        page.GetProperty("items").EnumerateArray().Single(item => item.GetProperty("action").GetString() == action);

    private Task<JsonElement> GetEntryAsync(string token, JsonElement page, string action) =>
        GetJsonAsync($"{AuditPath}/{Single(page, action).GetProperty("id").GetGuid()}", token);

    private IEnumerable<string> LogLines(LogLevel level, Guid id) =>
        app.Factory.Services.GetRequiredService<TestLogSink>().Entries
            .Where(entry => entry.Category.StartsWith("BusinessSuite.", StringComparison.Ordinal))
            .Where(entry => entry.Level == level && entry.Message.Contains(id.ToString(), StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.Message);

    private async Task CreateGroupAsync(string token, string name)
    {
        using var response = await SendAsync(HttpMethod.Post, "/api/v1/groups", token, new { name });
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"Creating group {name} answered {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    private async Task ExpectOkAsync(HttpMethod method, string path, string token, object body)
    {
        using var response = await SendAsync(method, path, token, body);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{method} {path} answered {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    private async Task<Guid> CreateOrganizationAsync()
    {
        var organization = Organization.Create($"Audit Test {Guid.NewGuid():N}");
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
        var email = $"audit-{Guid.NewGuid():N}@example.com";
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

    private async Task<JsonElement> GetJsonAsync(string path, string token, Guid? organization = null)
    {
        using var response = await SendAsync(HttpMethod.Get, path, token, organization: organization);
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
