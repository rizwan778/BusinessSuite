using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BusinessSuite.Application.Common.Abstractions;
using BusinessSuite.Application.Common.Security;
using BusinessSuite.Domain.Organizations;
using BusinessSuite.Infrastructure.Identity;
using BusinessSuite.Infrastructure.Persistence;
using BusinessSuite.Tests.Integration.Support;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BusinessSuite.Tests.Integration.Identity;

/// <summary>
/// <c>/api/v1/organizations</c> end to end on SQL Server: the platform operator lifecycle, tenant isolation for
/// organization users, suspension, the member and role lists, validation, and what each outcome logs.
/// </summary>
public sealed class OrganizationsApiTests(SqlServerAppFixture app) : IClassFixture<SqlServerAppFixture>
{
    private const string OrganizationsPath = "/api/v1/organizations";
    private const string OrganizationHeader = "X-Organization-Id";

    // ---- Platform operators ------------------------------------------------------------------------------

    [SqlServerFact]
    public async Task Super_admin_creates_renames_suspends_and_reactivates_an_organization()
    {
        var superAdmin = await CreatePlatformUserAsync(PlatformRoles.SuperAdministrator);
        var token = Guid.NewGuid().ToString("N");

        // Create: 201 + Location, active, no members, name trimmed.
        using var created = await SendAsync(HttpMethod.Post, OrganizationsPath, superAdmin.Token, new { name = $"  Orgs Test {token}  " });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var organization = await created.ReadJsonAsync();
        var id = organization.GetProperty("id").GetGuid();
        Assert.EndsWith($"{OrganizationsPath}/{id}", created.Headers.Location!.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal($"Orgs Test {token}", organization.GetProperty("name").GetString());
        Assert.True(organization.GetProperty("isActive").GetBoolean());
        Assert.Equal(0, organization.GetProperty("memberCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, organization.GetProperty("updatedAtUtc").ValueKind);

        // The documented way to give it its first Owner: act in it and create a user with the Owner role.
        using var owner = await SendAsync(HttpMethod.Post, "/api/v1/users", superAdmin.Token,
            new { email = $"owner-{token}@example.com", role = OrganizationRoles.Owner }, organization: id);
        Assert.Equal(HttpStatusCode.Created, owner.StatusCode);
        Assert.Equal(1, (await GetJsonAsync($"{OrganizationsPath}/{id}", superAdmin.Token)).GetProperty("memberCount").GetInt32());

        // Rename.
        using var renamed = await SendAsync(HttpMethod.Put, $"{OrganizationsPath}/{id}", superAdmin.Token, new { name = $"Orgs Renamed {token}" });
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        var afterRename = await renamed.ReadJsonAsync();
        Assert.Equal($"Orgs Renamed {token}", afterRename.GetProperty("name").GetString());
        Assert.NotEqual(JsonValueKind.Null, afterRename.GetProperty("updatedAtUtc").ValueKind);

        // Search and status filters.
        Assert.Equal(new[] { id }, Ids(await GetJsonAsync($"{OrganizationsPath}?search={token}", superAdmin.Token)));
        Assert.Empty(Ids(await GetJsonAsync($"{OrganizationsPath}?search={token}&status=suspended", superAdmin.Token)));

        // Suspend: logged as a warning, once; repeating it changes nothing and logs nothing.
        using var suspended = await SendAsync(HttpMethod.Patch, $"{OrganizationsPath}/{id}/status", superAdmin.Token, new { isActive = false });
        Assert.Equal(HttpStatusCode.OK, suspended.StatusCode);
        var afterSuspend = await suspended.ReadJsonAsync();
        Assert.False(afterSuspend.GetProperty("isActive").GetBoolean());
        using var again = await SendAsync(HttpMethod.Patch, $"{OrganizationsPath}/{id}/status", superAdmin.Token, new { isActive = false });
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(afterSuspend.GetProperty("updatedAtUtc").GetString(), (await again.ReadJsonAsync()).GetProperty("updatedAtUtc").GetString());
        Assert.Equal(new[] { id }, Ids(await GetJsonAsync($"{OrganizationsPath}?search={token}&status=suspended", superAdmin.Token)));
        Assert.Empty(Ids(await GetJsonAsync($"{OrganizationsPath}?search={token}&status=active", superAdmin.Token)));
        var suspension = Assert.Single(LogLines(LogLevel.Warning, id), line => line.Contains("suspended organization", StringComparison.Ordinal));
        Assert.Contains(superAdmin.UserId.ToString(), suspension, StringComparison.OrdinalIgnoreCase);

        // Reactivate.
        using var reactivated = await SendAsync(HttpMethod.Patch, $"{OrganizationsPath}/{id}/status", superAdmin.Token, new { isActive = true });
        Assert.True((await reactivated.ReadJsonAsync()).GetProperty("isActive").GetBoolean());
        Assert.Single(LogLines(LogLevel.Warning, id), line => line.Contains("reactivated organization", StringComparison.Ordinal));

        // Logs name ids, never the organization's name or anyone's e-mail address.
        Assert.DoesNotContain(AllLogLines(), line => line.Contains(token, StringComparison.Ordinal));
    }

    [SqlServerFact]
    public async Task Platform_administrators_manage_organizations_but_have_no_access_inside_them()
    {
        var platformAdmin = await CreatePlatformUserAsync(PlatformRoles.Administrator);
        var token = Guid.NewGuid().ToString("N");

        using var created = await SendAsync(HttpMethod.Post, OrganizationsPath, platformAdmin.Token, new { name = $"Orgs Test {token}" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.ReadJsonAsync()).GetProperty("id").GetGuid();

        Assert.Equal(new[] { id }, Ids(await GetJsonAsync($"{OrganizationsPath}?search={token}", platformAdmin.Token)));
        Assert.Equal(id, (await GetJsonAsync($"{OrganizationsPath}/{id}", platformAdmin.Token)).GetProperty("id").GetGuid());
        using var renamed = await SendAsync(HttpMethod.Put, $"{OrganizationsPath}/{id}", platformAdmin.Token, new { name = $"Orgs Renamed {token}" });
        using var suspended = await SendAsync(HttpMethod.Patch, $"{OrganizationsPath}/{id}/status", platformAdmin.Token, new { isActive = false });
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        Assert.False((await suspended.ReadJsonAsync()).GetProperty("isActive").GetBoolean());

        // Members and roles are organization data: platform administrators hold no organization permissions.
        using var users = await SendAsync(HttpMethod.Get, $"{OrganizationsPath}/{id}/users", platformAdmin.Token);
        using var roles = await SendAsync(HttpMethod.Get, $"{OrganizationsPath}/{id}/roles", platformAdmin.Token);
        Assert.Equal("Auth.PermissionDenied", await users.ReadProblemCodeAsync());
        Assert.Equal("Auth.PermissionDenied", await roles.ReadProblemCodeAsync());
    }

    // ---- Initial Organization Admin -------------------------------------------------------------------------

    [SqlServerFact]
    public async Task Super_admin_creates_an_organization_together_with_its_first_administrator()
    {
        var superAdmin = await CreatePlatformUserAsync(PlatformRoles.SuperAdministrator);
        var token = Guid.NewGuid().ToString("N");
        var name = $"Orgs Test {token}";
        var email = $"first-admin-{token}@example.com";

        using var created = await SendAsync(HttpMethod.Post, OrganizationsPath, superAdmin.Token,
            new { name, initialAdmin = new { email, displayName = "First Admin", phoneNumber = "+92 300 1234567" } });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var organization = await created.ReadJsonAsync();
        var id = organization.GetProperty("id").GetGuid();
        Assert.Equal(1, organization.GetProperty("memberCount").GetInt32());

        // The administrator is the organization's Owner, with the profile they were given, and is set up by e-mail.
        var members = await GetJsonAsync($"{OrganizationsPath}/{id}/users", superAdmin.Token);
        var member = Assert.Single(members.GetProperty("items").EnumerateArray());
        Assert.Equal(email, member.GetProperty("email").GetString());
        Assert.Equal("First Admin", member.GetProperty("displayName").GetString());
        var membership = Assert.Single(member.GetProperty("memberships").EnumerateArray());
        Assert.Equal(id, membership.GetProperty("organizationId").GetGuid());
        Assert.Equal(OrganizationRoles.Owner, membership.GetProperty("role").GetString());
        Assert.True(membership.GetProperty("isActive").GetBoolean());
        var setUp = Assert.Single(Outbox.SentTo(email));
        Assert.Contains("Set up your", setUp.Subject, StringComparison.Ordinal);

        // They choose their password with the e-mailed code, sign in, and administer their own organization.
        const string password = "Chosen-Passw0rd!x";
        using (var client = app.Factory.CreateBrowserClient())
        {
            using var reset = await client.PostJsonAsync("/api/v1/auth/reset-password", new { email, code = Outbox.LatestCodeFor(email), newPassword = password });
            Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        }

        var adminToken = await LoginAsync(email, password);
        var own = await GetJsonAsync($"{OrganizationsPath}/{id}", adminToken);
        Assert.Equal(name, own.GetProperty("name").GetString());
        Assert.Equal(new[] { id }, Ids(await GetJsonAsync(OrganizationsPath, adminToken)));
        using var renamed = await SendAsync(HttpMethod.Put, $"{OrganizationsPath}/{id}", adminToken, new { name = $"Orgs Renamed {token}" });
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);

        // The creation is logged with ids; neither the e-mail address, the set-up code nor the name appears in any log line.
        Assert.Single(LogLines(LogLevel.Information, id), line => line.Contains("with first administrator", StringComparison.Ordinal));
        Assert.DoesNotContain(AllLogLines(), line =>
            line.Contains(email, StringComparison.OrdinalIgnoreCase)
            || line.Contains(token, StringComparison.Ordinal)
            || line.Contains(password, StringComparison.Ordinal));
    }

    [SqlServerFact]
    public async Task First_administrator_with_a_password_signs_in_at_once_and_is_sent_no_e_mail()
    {
        var superAdmin = await CreatePlatformUserAsync(PlatformRoles.SuperAdministrator);
        var token = Guid.NewGuid().ToString("N");
        var email = $"first-admin-{token}@example.com";
        const string password = "Given-Passw0rd!x";

        using var created = await SendAsync(HttpMethod.Post, OrganizationsPath, superAdmin.Token,
            new { name = $"Orgs Test {token}", initialAdmin = new { email, password } });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.ReadJsonAsync()).GetProperty("id").GetGuid();

        var adminToken = await LoginAsync(email, password);
        Assert.Equal(id, (await GetJsonAsync($"{OrganizationsPath}/{id}", adminToken)).GetProperty("id").GetGuid());
        Assert.Empty(Outbox.SentTo(email));
        Assert.DoesNotContain(AllLogLines(), line => line.Contains(password, StringComparison.Ordinal));
    }

    [SqlServerFact]
    public async Task Creating_an_organization_with_a_first_administrator_is_all_or_nothing()
    {
        var superAdmin = await CreatePlatformUserAsync(PlatformRoles.SuperAdministrator);
        var existingOrganization = await CreateOrganizationAsync();
        var existing = await CreateUserAsync((existingOrganization.Id, OrganizationRoles.ReadOnly));
        var token = Guid.NewGuid().ToString("N");
        var name = $"Orgs Test {token}";
        var newEmail = $"first-admin-{token}@example.com";

        using var existingAccount = await SendAsync(HttpMethod.Post, OrganizationsPath, superAdmin.Token, new { name, initialAdmin = new { email = existing.Email } });
        using var weakPassword = await SendAsync(HttpMethod.Post, OrganizationsPath, superAdmin.Token, new { name, initialAdmin = new { email = newEmail, password = "short" } });
        using var badEmail = await SendAsync(HttpMethod.Post, OrganizationsPath, superAdmin.Token, new { name, initialAdmin = new { email = "not-an-address" } });
        using var noEmail = await SendAsync(HttpMethod.Post, OrganizationsPath, superAdmin.Token, new { name, initialAdmin = new { displayName = "Nobody" } });
        using var badName = await SendAsync(HttpMethod.Post, OrganizationsPath, superAdmin.Token, new { name = new string('x', Organization.NameMaxLength + 1), initialAdmin = new { email = newEmail } });
        using var longPhone = await SendAsync(HttpMethod.Post, OrganizationsPath, superAdmin.Token, new { name, initialAdmin = new { email = newEmail, phoneNumber = new string('1', 40) } });

        Assert.Equal(HttpStatusCode.Conflict, existingAccount.StatusCode);
        Assert.Equal("Account.DuplicateEmail", await existingAccount.ReadProblemCodeAsync());
        Assert.Equal("Account.WeakPassword", await weakPassword.ReadProblemCodeAsync());
        foreach (var response in new[] { badEmail, noEmail, badName, longPhone })
        {
            Assert.Equal("Request.ValidationFailed", await response.ReadProblemCodeAsync());
        }

        // Every refusal left nothing behind: no organization, no account.
        Assert.Equal(0, await WithDbAsync(db => db.Organizations.CountAsync(organization => organization.Name == name)));
        Assert.Equal(0, await WithDbAsync(db => db.Users.CountAsync(user => user.Email == newEmail)));
        Assert.Empty(Outbox.SentTo(newEmail));
        Assert.Contains(AllLogLines(), line => line.Contains("first administrator's e-mail address belongs to existing user", StringComparison.Ordinal));
    }

    [SqlServerFact]
    public async Task A_set_up_e_mail_that_cannot_be_sent_creates_nothing_and_is_a_logged_server_error()
    {
        var superAdmin = await CreatePlatformUserAsync(PlatformRoles.SuperAdministrator);
        var token = Guid.NewGuid().ToString("N");
        var name = $"Orgs Test {token}";
        var email = $"first-admin-{token}@example.com";

        // The same application and database, but the e-mail service is down.
        await using var broken = app.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton<IEmailSender, UnavailableEmailSender>()));
        using var client = broken.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false, HandleCookies = true });
        using var login = await client.PostJsonAsync("/api/v1/auth/login", new { email = superAdmin.Email, password = superAdmin.Password });
        var adminToken = (await login.ReadJsonAsync()).GetProperty("accessToken").GetString()!;

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(OrganizationsPath, UriKind.Relative))
        {
            Content = JsonContent.Create(new { name, initialAdmin = new { email } }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("Server.UnexpectedError", await response.ReadProblemCodeAsync());
        Assert.DoesNotContain(token, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal); // no details leak to the client
        Assert.Equal(0, await WithDbAsync(db => db.Organizations.CountAsync(organization => organization.Name == name)));
        Assert.Equal(0, await WithDbAsync(db => db.Users.CountAsync(user => user.Email == email)));
        Assert.Contains(broken.Services.GetRequiredService<TestLogSink>().Entries, entry => entry.Level == LogLevel.Error && entry.Message.Contains("Unhandled exception", StringComparison.Ordinal));
    }

    [SqlServerFact]
    public async Task Platform_administrators_may_create_organizations_but_not_their_first_administrator()
    {
        var platformAdmin = await CreatePlatformUserAsync(PlatformRoles.Administrator);
        var token = Guid.NewGuid().ToString("N");
        var name = $"Orgs Test {token}";
        var email = $"first-admin-{token}@example.com";

        using var withAdmin = await SendAsync(HttpMethod.Post, OrganizationsPath, platformAdmin.Token, new { name, initialAdmin = new { email } });
        Assert.Equal(HttpStatusCode.Forbidden, withAdmin.StatusCode);
        Assert.Equal("Organizations.SuperAdminRequired", await withAdmin.ReadProblemCodeAsync());
        Assert.Equal(0, await WithDbAsync(db => db.Organizations.CountAsync(organization => organization.Name == name)));
        Assert.Equal(0, await WithDbAsync(db => db.Users.CountAsync(user => user.Email == email)));
        Assert.Empty(Outbox.SentTo(email));

        using var without = await SendAsync(HttpMethod.Post, OrganizationsPath, platformAdmin.Token, new { name });
        Assert.Equal(HttpStatusCode.Created, without.StatusCode);
    }

    // ---- Organization administrators stay inside their own organization ------------------------------------

    [SqlServerFact]
    public async Task Organization_administrators_cannot_create_or_modify_other_organizations()
    {
        foreach (var role in new[] { OrganizationRoles.Owner, OrganizationRoles.Administrator })
        {
            var mine = await CreateOrganizationAsync();
            var theirs = await CreateOrganizationAsync();
            var admin = await CreateUserAsync((mine.Id, role));
            await CreateUserAsync((theirs.Id, OrganizationRoles.Owner));
            var token = Guid.NewGuid().ToString("N");
            var attempts = new (string What, HttpMethod Method, string Path, object? Body, Guid? Header, HttpStatusCode Status, string Code)[]
            {
                ("create an organization", HttpMethod.Post, OrganizationsPath, new { name = $"Orgs Test {token}" }, null, HttpStatusCode.Forbidden, "Auth.PermissionDenied"),
                ("create one with an administrator", HttpMethod.Post, OrganizationsPath, new { name = $"Orgs Test {token}", initialAdmin = new { email = $"x-{token}@example.com" } }, null, HttpStatusCode.Forbidden, "Auth.PermissionDenied"),
                ("rename another", HttpMethod.Put, $"{OrganizationsPath}/{theirs.Id}", new { name = "Hijacked" }, null, HttpStatusCode.NotFound, "Organizations.NotFound"),
                ("rename another naming it in the header", HttpMethod.Put, $"{OrganizationsPath}/{theirs.Id}", new { name = "Hijacked" }, theirs.Id, HttpStatusCode.Forbidden, "Auth.PermissionDenied"),
                ("suspend another", HttpMethod.Patch, $"{OrganizationsPath}/{theirs.Id}/status", new { isActive = false }, null, HttpStatusCode.Forbidden, "Auth.PermissionDenied"),
                ("suspend their own", HttpMethod.Patch, $"{OrganizationsPath}/{mine.Id}/status", new { isActive = false }, null, HttpStatusCode.Forbidden, "Auth.PermissionDenied"),
                ("view another", HttpMethod.Get, $"{OrganizationsPath}/{theirs.Id}", null, null, HttpStatusCode.NotFound, "Organizations.NotFound"),
                ("add a user to another", HttpMethod.Post, "/api/v1/users", new { email = $"y-{token}@example.com", role = OrganizationRoles.Owner }, theirs.Id, HttpStatusCode.Forbidden, "Auth.PermissionDenied"),
                ("create a platform role", HttpMethod.Post, "/api/v1/roles", new { name = $"Platform {token}"[..20], scope = "Platform" }, null, HttpStatusCode.Forbidden, "Roles.SuperAdminRequired"),
                ("grant themselves platform.organizations.manage", HttpMethod.Post, "/api/v1/roles", new { name = $"Sneaky {token}"[..20], permissions = new[] { Permissions.Platform.OrganizationsManage } }, null, HttpStatusCode.BadRequest, "Roles.UnknownPermission"),
            };

            foreach (var (what, method, path, body, header, status, code) in attempts)
            {
                using var response = await SendAsync(method, path, admin.Token, body, header);
                Assert.True(response.StatusCode == status, $"{role} tried to {what}: {method} {path} answered {response.StatusCode}, expected {status}");
                Assert.Equal(code, await response.ReadProblemCodeAsync());
            }

            // Nothing changed anywhere.
            var other = await GetOrganizationFromDbAsync(theirs.Id);
            Assert.Equal(theirs.Name, other.Name);
            Assert.True(other.IsActive);
            Assert.True((await GetOrganizationFromDbAsync(mine.Id)).IsActive);
            Assert.Equal(0, await WithDbAsync(db => db.Organizations.CountAsync(organization => organization.Name.Contains(token))));
            Assert.Equal(0, await WithDbAsync(db => db.Users.CountAsync(user => user.Email!.Contains(token))));
            Assert.Equal(1, await WithDbAsync(db => db.OrganizationMemberships.CountAsync(membership => membership.OrganizationId == theirs.Id)));

            // What they may do: rename their own organization.
            using var renamed = await SendAsync(HttpMethod.Put, $"{OrganizationsPath}/{mine.Id}", admin.Token, new { name = $"{mine.Name} (renamed)" });
            Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        }
    }

    // ---- Tenant isolation -----------------------------------------------------------------------------------

    [SqlServerFact]
    public async Task Organization_users_see_and_rename_only_their_own_organization()
    {
        var mine = await CreateOrganizationAsync();
        var theirs = await CreateOrganizationAsync();
        var admin = await CreateUserAsync((mine.Id, OrganizationRoles.Administrator));
        var otherAdmin = await CreateUserAsync((theirs.Id, OrganizationRoles.Administrator));

        // The list holds exactly one organization, whatever else exists.
        var list = await GetJsonAsync(OrganizationsPath, admin.Token);
        Assert.Equal(new[] { mine.Id }, Ids(list));
        Assert.Equal(1, list.GetProperty("totalCount").GetInt32());
        var own = await GetJsonAsync($"{OrganizationsPath}/{mine.Id}", admin.Token);
        Assert.Equal(mine.Name, own.GetProperty("name").GetString());
        Assert.Equal(1, own.GetProperty("memberCount").GetInt32());

        // Every way of reaching another organization looks exactly like an id that does not exist.
        using var missing = await SendAsync(HttpMethod.Get, $"{OrganizationsPath}/{Guid.NewGuid()}", admin.Token);
        Assert.Equal("Organizations.NotFound", await missing.ReadProblemCodeAsync());
        var missingDetail = (await missing.ReadJsonAsync()).GetProperty("detail").GetString();
        var attempts = new (HttpMethod Method, string Path, object? Body)[]
        {
            (HttpMethod.Get, $"{OrganizationsPath}/{theirs.Id}", null),
            (HttpMethod.Put, $"{OrganizationsPath}/{theirs.Id}", new { name = "Hijacked" }),
            (HttpMethod.Get, $"{OrganizationsPath}/{theirs.Id}/users", null),
            (HttpMethod.Get, $"{OrganizationsPath}/{theirs.Id}/roles", null),
        };
        foreach (var (method, path, body) in attempts)
        {
            using var response = await SendAsync(method, path, admin.Token, body);
            Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{method} {path} answered {response.StatusCode}");
            Assert.Equal("Organizations.NotFound", await response.ReadProblemCodeAsync());
            Assert.Equal(missingDetail, (await response.ReadJsonAsync()).GetProperty("detail").GetString());
        }

        Assert.Equal(theirs.Name, (await GetOrganizationFromDbAsync(theirs.Id)).Name);
        Assert.Contains(LogLines(LogLevel.Information, theirs.Id), line => line.Contains("was refused", StringComparison.Ordinal));
        Assert.DoesNotContain(AllLogLines(), line => line.Contains(admin.Email, StringComparison.OrdinalIgnoreCase));

        // The organization's administrator may rename their own.
        using var renamed = await SendAsync(HttpMethod.Put, $"{OrganizationsPath}/{mine.Id}", admin.Token, new { name = $"{mine.Name} (renamed)" });
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        Assert.Equal($"{mine.Name} (renamed)", (await GetOrganizationFromDbAsync(mine.Id)).Name);
        Assert.Equal(theirs.Id, Ids(await GetJsonAsync(OrganizationsPath, otherAdmin.Token)).Single());
    }

    [SqlServerFact]
    public async Task Organizations_cannot_create_themselves_or_change_their_own_status()
    {
        var organization = await CreateOrganizationAsync();
        var owner = await CreateUserAsync((organization.Id, OrganizationRoles.Owner)); // holds every organization permission

        using var create = await SendAsync(HttpMethod.Post, OrganizationsPath, owner.Token, new { name = "Another business" });
        using var suspendOwn = await SendAsync(HttpMethod.Patch, $"{OrganizationsPath}/{organization.Id}/status", owner.Token, new { isActive = false });
        using var suspendOther = await SendAsync(HttpMethod.Patch, $"{OrganizationsPath}/{Guid.NewGuid()}/status", owner.Token, new { isActive = false });

        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        Assert.Equal("Auth.PermissionDenied", await create.ReadProblemCodeAsync());
        var detail = (await create.ReadJsonAsync()).GetProperty("detail").GetString()!;
        Assert.Contains(Permissions.Platform.OrganizationsManage, detail, StringComparison.Ordinal);
        Assert.DoesNotContain("X-Organization-Id", detail, StringComparison.Ordinal); // a platform permission belongs to no organization
        Assert.Equal("Auth.PermissionDenied", await suspendOwn.ReadProblemCodeAsync());
        Assert.Equal("Auth.PermissionDenied", await suspendOther.ReadProblemCodeAsync());
        Assert.True((await GetOrganizationFromDbAsync(organization.Id)).IsActive);
    }

    [SqlServerFact]
    public async Task Viewing_needs_organization_view_and_renaming_needs_organization_manage()
    {
        var organization = await CreateOrganizationAsync();
        var viewer = await CreateUserAsync((organization.Id, OrganizationRoles.ReadOnly)); // organization.view only
        var nobody = await CreateUserWithCustomRoleAsync(organization.Id);                // no permissions at all

        using var view = await SendAsync(HttpMethod.Get, $"{OrganizationsPath}/{organization.Id}", viewer.Token);
        using var rename = await SendAsync(HttpMethod.Put, $"{OrganizationsPath}/{organization.Id}", viewer.Token, new { name = "Nope" });
        using var noViewList = await SendAsync(HttpMethod.Get, OrganizationsPath, nobody.Token);
        using var noViewGet = await SendAsync(HttpMethod.Get, $"{OrganizationsPath}/{organization.Id}", nobody.Token);

        Assert.Equal(HttpStatusCode.OK, view.StatusCode);
        Assert.Equal("Auth.PermissionDenied", await rename.ReadProblemCodeAsync());
        Assert.Equal("Auth.PermissionDenied", await noViewList.ReadProblemCodeAsync());
        Assert.Equal("Auth.PermissionDenied", await noViewGet.ReadProblemCodeAsync());
        Assert.Equal(organization.Name, (await GetOrganizationFromDbAsync(organization.Id)).Name);
    }

    // ---- Suspension ------------------------------------------------------------------------------------------

    [SqlServerFact]
    public async Task Suspension_removes_members_access_until_reactivation_and_deletes_nothing()
    {
        var organization = await CreateOrganizationAsync();
        var admin = await CreateUserAsync((organization.Id, OrganizationRoles.Administrator));
        var superAdmin = await CreatePlatformUserAsync(PlatformRoles.SuperAdministrator);
        var path = $"{OrganizationsPath}/{organization.Id}";

        Assert.Equal(HttpStatusCode.OK, (await GetStatusAsync(path, admin.Token)));

        using var suspended = await SendAsync(HttpMethod.Patch, $"{path}/status", superAdmin.Token, new { isActive = false });
        Assert.Equal(HttpStatusCode.OK, suspended.StatusCode);

        // From the very next request the member has no organization and so no permissions ...
        using var refused = await SendAsync(HttpMethod.Get, path, admin.Token);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("Auth.PermissionDenied", await refused.ReadProblemCodeAsync());

        // ... a Super Admin cannot act inside it either, but still sees it and its members from the platform side.
        using var actingInside = await SendAsync(HttpMethod.Get, "/api/v1/auth/me/permissions", superAdmin.Token, organization: organization.Id);
        Assert.Equal(HttpStatusCode.Forbidden, actingInside.StatusCode);
        Assert.Equal("Organization.AccessDenied", await actingInside.ReadProblemCodeAsync());
        var members = await GetJsonAsync($"{path}/users", superAdmin.Token);
        Assert.Equal(1, members.GetProperty("totalCount").GetInt32());
        Assert.False((await GetJsonAsync(path, superAdmin.Token)).GetProperty("isActive").GetBoolean());

        // Reactivating restores everything.
        using var reactivated = await SendAsync(HttpMethod.Patch, $"{path}/status", superAdmin.Token, new { isActive = true });
        Assert.Equal(HttpStatusCode.OK, reactivated.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GetStatusAsync(path, admin.Token)));
        Assert.Equal(1, (await GetJsonAsync($"{path}/users", admin.Token)).GetProperty("totalCount").GetInt32());
    }

    // ---- Users and roles of an organization ------------------------------------------------------------

    [SqlServerFact]
    public async Task Organization_users_list_only_their_own_members_and_super_admins_any_organizations()
    {
        var mine = await CreateOrganizationAsync();
        var theirs = await CreateOrganizationAsync();
        var admin = await CreateUserAsync((mine.Id, OrganizationRoles.Administrator));
        var member = await CreateUserAsync((mine.Id, OrganizationRoles.ReadOnly));
        var stranger = await CreateUserAsync((theirs.Id, OrganizationRoles.Owner));
        var seller = await CreateUserAsync((mine.Id, OrganizationRoles.SalesOperator)); // lacks users.view
        var superAdmin = await CreatePlatformUserAsync(PlatformRoles.SuperAdministrator);

        var mineUsers = await GetJsonAsync($"{OrganizationsPath}/{mine.Id}/users", admin.Token);
        var emails = mineUsers.GetProperty("items").EnumerateArray().Select(user => user.GetProperty("email").GetString()).ToList();
        Assert.Equal(3, mineUsers.GetProperty("totalCount").GetInt32());
        Assert.Contains(member.Email, emails);
        Assert.DoesNotContain(stranger.Email, emails);
        Assert.Equal(3, (await GetJsonAsync($"{OrganizationsPath}/{mine.Id}", admin.Token)).GetProperty("memberCount").GetInt32());

        // The users API's filters carry over; an organizationId in the query string cannot redirect it.
        var readOnly = await GetJsonAsync($"{OrganizationsPath}/{mine.Id}/users?role={OrganizationRoles.ReadOnly}&search=roles-", admin.Token);
        Assert.Equal(new[] { member.Email }, readOnly.GetProperty("items").EnumerateArray().Select(user => user.GetProperty("email").GetString()));
        var redirected = await GetJsonAsync($"{OrganizationsPath}/{mine.Id}/users?organizationId={theirs.Id}", admin.Token);
        Assert.Equal(3, redirected.GetProperty("totalCount").GetInt32());

        // Super Admins list any organization's members; an unknown organization is not an empty list but a 404.
        var theirUsers = await GetJsonAsync($"{OrganizationsPath}/{theirs.Id}/users", superAdmin.Token);
        Assert.Equal(new[] { stranger.Email }, theirUsers.GetProperty("items").EnumerateArray().Select(user => user.GetProperty("email").GetString()));
        using var unknown = await SendAsync(HttpMethod.Get, $"{OrganizationsPath}/{Guid.NewGuid()}/users", superAdmin.Token);
        Assert.Equal("Organizations.NotFound", await unknown.ReadProblemCodeAsync());

        using var noPermission = await SendAsync(HttpMethod.Get, $"{OrganizationsPath}/{mine.Id}/users", seller.Token);
        Assert.Equal("Auth.PermissionDenied", await noPermission.ReadProblemCodeAsync());
    }

    [SqlServerFact]
    public async Task Organization_roles_are_the_roles_that_organization_may_use()
    {
        var shop = await CreateOrganizationAsync();
        var warehouse = await CreateOrganizationAsync();
        var shopAdmin = await CreateUserAsync((shop.Id, OrganizationRoles.Administrator));
        var warehouseAdmin = await CreateUserAsync((warehouse.Id, OrganizationRoles.Administrator));
        var superAdmin = await CreatePlatformUserAsync(PlatformRoles.SuperAdministrator);
        var cashier = await CreateRoleAsync(shopAdmin.Token, "Cashier", Permissions.Sales.View);
        var packer = await CreateRoleAsync(warehouseAdmin.Token, "Packer", Permissions.Inventory.View);

        // A shared role enabled for the shop only.
        var sharedName = $"Auditor {Guid.NewGuid():N}"[..20];
        using var shared = await SendAsync(HttpMethod.Post, "/api/v1/roles", superAdmin.Token, new { name = sharedName, scope = "Platform" });
        var sharedId = (await shared.ReadJsonAsync()).GetProperty("id").GetGuid();
        using var enabled = await SendAsync(HttpMethod.Put, $"/api/v1/roles/{sharedId}/availability", superAdmin.Token,
            new { allOrganizations = false, organizationIds = new[] { shop.Id } });
        Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);

        var shopRoles = RoleIds(await GetJsonAsync($"{OrganizationsPath}/{shop.Id}/roles", shopAdmin.Token));
        var warehouseRoles = RoleIds(await GetJsonAsync($"{OrganizationsPath}/{warehouse.Id}/roles", warehouseAdmin.Token));
        Assert.Contains(cashier, shopRoles);
        Assert.DoesNotContain(packer, shopRoles);
        Assert.Contains(sharedId, shopRoles);
        Assert.Contains(packer, warehouseRoles);
        Assert.DoesNotContain(sharedId, warehouseRoles);
        Assert.Equal(OrganizationRoles.All.Count, shopRoles.Count(id => OrganizationRoles.Ids.Values.Contains(id)));

        // Super Admins ask for any organization; the answer matches GET /roles?organizationId=.
        Assert.Equal(shopRoles, RoleIds(await GetJsonAsync($"{OrganizationsPath}/{shop.Id}/roles", superAdmin.Token)));
        Assert.Equal(warehouseRoles, RoleIds(await GetJsonAsync($"/api/v1/roles?organizationId={warehouse.Id}", superAdmin.Token)));
        Assert.Equal(new[] { cashier }, RoleIds(await GetJsonAsync($"{OrganizationsPath}/{shop.Id}/roles?search=cash", shopAdmin.Token)));

        using var unknown = await SendAsync(HttpMethod.Get, $"{OrganizationsPath}/{Guid.NewGuid()}/roles", superAdmin.Token);
        Assert.Equal("Organizations.NotFound", await unknown.ReadProblemCodeAsync());
    }

    // ---- Input and sign-in -----------------------------------------------------------------------------------

    [SqlServerFact]
    public async Task Input_is_validated_and_unknown_organizations_are_not_found()
    {
        var superAdmin = await CreatePlatformUserAsync(PlatformRoles.SuperAdministrator);
        var organization = await CreateOrganizationAsync();

        using var noName = await SendAsync(HttpMethod.Post, OrganizationsPath, superAdmin.Token, new { });
        using var blankName = await SendAsync(HttpMethod.Post, OrganizationsPath, superAdmin.Token, new { name = "   " });
        using var longName = await SendAsync(HttpMethod.Post, OrganizationsPath, superAdmin.Token, new { name = new string('x', Organization.NameMaxLength + 1) });
        using var blankRename = await SendAsync(HttpMethod.Put, $"{OrganizationsPath}/{organization.Id}", superAdmin.Token, new { name = "" });
        using var noStatus = await SendAsync(HttpMethod.Patch, $"{OrganizationsPath}/{organization.Id}/status", superAdmin.Token, new { });
        using var badPage = await SendAsync(HttpMethod.Get, $"{OrganizationsPath}?pageSize=0", superAdmin.Token);
        using var unknownGet = await SendAsync(HttpMethod.Get, $"{OrganizationsPath}/{Guid.NewGuid()}", superAdmin.Token);
        using var unknownPut = await SendAsync(HttpMethod.Put, $"{OrganizationsPath}/{Guid.NewGuid()}", superAdmin.Token, new { name = "Ghost" });
        using var unknownStatus = await SendAsync(HttpMethod.Patch, $"{OrganizationsPath}/{Guid.NewGuid()}/status", superAdmin.Token, new { isActive = false });

        foreach (var response in new[] { noName, blankName, longName, blankRename, noStatus, badPage })
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("Request.ValidationFailed", await response.ReadProblemCodeAsync());
        }

        foreach (var response in new[] { unknownGet, unknownPut, unknownStatus })
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("Organizations.NotFound", await response.ReadProblemCodeAsync());
        }

        Assert.Equal(organization.Name, (await GetOrganizationFromDbAsync(organization.Id)).Name);
    }

    [SqlServerFact]
    public async Task Every_endpoint_needs_a_signed_in_user()
    {
        using var client = app.Factory.CreateBrowserClient();
        var id = Guid.NewGuid();
        var requests = new (HttpMethod Method, string Path)[]
        {
            (HttpMethod.Get, OrganizationsPath),
            (HttpMethod.Get, $"{OrganizationsPath}/{id}"),
            (HttpMethod.Post, OrganizationsPath),
            (HttpMethod.Put, $"{OrganizationsPath}/{id}"),
            (HttpMethod.Patch, $"{OrganizationsPath}/{id}/status"),
            (HttpMethod.Get, $"{OrganizationsPath}/{id}/users"),
            (HttpMethod.Get, $"{OrganizationsPath}/{id}/roles"),
        };

        foreach (var (method, path) in requests)
        {
            using var response = await client.SendAsync(new HttpRequestMessage(method, new Uri(path, UriKind.Relative)));
            Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, $"{method} {path} answered {response.StatusCode}");
            Assert.Equal("Auth.Unauthorized", await response.ReadProblemCodeAsync());
        }
    }

    // ---- Helpers -----------------------------------------------------------------------------------------

    private sealed record TestUser(Guid UserId, string Email, string Password, string Token);

    private sealed record TestOrganization(Guid Id, string Name);

    /// <summary>An e-mail service that is down.</summary>
    private sealed class UnavailableEmailSender : IEmailSender
    {
        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The mail server is unavailable.");
    }

    private TestEmailOutbox Outbox => app.Factory.Services.GetRequiredService<TestEmailOutbox>();

    private async Task<string> LoginAsync(string email, string password)
    {
        using var client = app.Factory.CreateBrowserClient();
        using var login = await client.PostJsonAsync("/api/v1/auth/login", new { email, password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return (await login.ReadJsonAsync()).GetProperty("accessToken").GetString()!;
    }

    private static List<Guid> Ids(JsonElement page) => page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).ToList();

    private static List<Guid> RoleIds(JsonElement roles) => roles.EnumerateArray().Select(role => role.GetProperty("id").GetGuid()).ToList();

    /// <summary>What BusinessSuite itself logged (framework categories echo request URLs, which are not ours to police).</summary>
    private IEnumerable<LogEntry> ApplicationLog() =>
        app.Factory.Services.GetRequiredService<TestLogSink>().Entries
            .Where(entry => entry.Category.StartsWith("BusinessSuite.", StringComparison.Ordinal));

    private IEnumerable<string> AllLogLines() => ApplicationLog().Select(entry => entry.Message);

    /// <summary>Log lines of exactly this level that mention <paramref name="organizationId"/>.</summary>
    private IEnumerable<string> LogLines(LogLevel level, Guid organizationId) =>
        ApplicationLog()
            .Where(entry => entry.Level == level && entry.Message.Contains(organizationId.ToString(), StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.Message);

    private async Task<Guid> CreateRoleAsync(string token, string name, params string[] permissions)
    {
        using var response = await SendAsync(HttpMethod.Post, "/api/v1/roles", token, new { name, permissions });
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"Creating role {name} answered {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.ReadJsonAsync()).GetProperty("id").GetGuid();
    }

    private async Task<TestOrganization> CreateOrganizationAsync()
    {
        var organization = Organization.Create($"Orgs Test {Guid.NewGuid():N}");
        await WithDbAsync(async db =>
        {
            db.Organizations.Add(organization);
            return await db.SaveChangesAsync();
        });
        return new TestOrganization(organization.Id, organization.Name);
    }

    private Task<Organization> GetOrganizationFromDbAsync(Guid id) =>
        WithDbAsync(db => db.Organizations.AsNoTracking().SingleAsync(organization => organization.Id == id));

    private Task<TestUser> CreateUserAsync(params (Guid OrganizationId, string Role)[] memberships) => CreateUserCoreAsync(null, null, memberships);

    private Task<TestUser> CreatePlatformUserAsync(string platformRole) => CreateUserCoreAsync(platformRole, null, []);

    /// <summary>A member whose custom role grants no permissions at all.</summary>
    private Task<TestUser> CreateUserWithCustomRoleAsync(Guid organizationId) => CreateUserCoreAsync(null, organizationId, []);

    private async Task<TestUser> CreateUserCoreAsync(string? platformRole, Guid? customRoleOrganizationId, (Guid OrganizationId, string Role)[] memberships)
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

        if (customRoleOrganizationId is { } owner)
        {
            var role = RoleDefinition.CreateForOrganization(owner, $"Nobody {Guid.NewGuid():N}"[..20], null);
            db.RoleDefinitions.Add(role);
            await db.SaveChangesAsync();
            db.OrganizationMemberships.Add(OrganizationMembership.Create(owner, user.Id, role.Id));
        }

        await db.SaveChangesAsync();

        return new TestUser(user.Id, email, password, await LoginAsync(email, password));
    }

    private async Task<HttpStatusCode> GetStatusAsync(string path, string token)
    {
        using var response = await SendAsync(HttpMethod.Get, path, token);
        return response.StatusCode;
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
