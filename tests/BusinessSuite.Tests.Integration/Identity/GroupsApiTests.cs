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
using Microsoft.Extensions.Logging;

namespace BusinessSuite.Tests.Integration.Identity;

/// <summary>
/// <c>/api/v1/groups</c> end to end on SQL Server: the lifecycle, how renaming moves members, deleting only when safe,
/// tenant isolation, permissions, validation, a concurrent-create race, and what each outcome logs.
/// </summary>
public sealed class GroupsApiTests(SqlServerAppFixture app) : IClassFixture<SqlServerAppFixture>
{
    private const string GroupsPath = "/api/v1/groups";
    private const string OrganizationHeader = "X-Organization-Id";

    // ---- Lifecycle -------------------------------------------------------------------------------------------

    [SqlServerFact]
    public async Task Group_lifecycle_create_assign_rename_and_delete_when_empty()
    {
        var organization = await CreateOrganizationAsync();
        var admin = await CreateUserAsync((organization, OrganizationRoles.Administrator));
        var alice = await CreateUserAsync((organization, OrganizationRoles.ReadOnly));
        var bilal = await CreateUserAsync((organization, OrganizationRoles.ReadOnly));
        var name = $"Sales {Guid.NewGuid():N}"[..20];

        // Create: 201 + Location, trimmed, no members yet.
        using var created = await SendAsync(HttpMethod.Post, GroupsPath, admin.Token, new { name = $"  {name} ", description = "  Front counter " });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var group = await created.ReadJsonAsync();
        var id = group.GetProperty("id").GetGuid();
        Assert.EndsWith($"{GroupsPath}/{id}", created.Headers.Location!.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(name, group.GetProperty("name").GetString());
        Assert.Equal("Front counter", group.GetProperty("description").GetString());
        Assert.Equal(0, group.GetProperty("memberCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, group.GetProperty("updatedAtUtc").ValueKind);

        // Put people in it with the users API; the group counts them.
        await SetDepartmentAsync(admin.Token, alice.UserId, name);
        await SetDepartmentAsync(admin.Token, bilal.UserId, name);
        Assert.Equal(2, (await GetJsonAsync($"{GroupsPath}/{id}", admin.Token)).GetProperty("memberCount").GetInt32());

        // Deleting is not safe while members belong to it.
        using var inUse = await SendAsync(HttpMethod.Delete, $"{GroupsPath}/{id}", admin.Token);
        Assert.Equal(HttpStatusCode.Conflict, inUse.StatusCode);
        Assert.Equal("Groups.InUse", await inUse.ReadProblemCodeAsync());
        Assert.Contains("2 member(s)", (await inUse.ReadJsonAsync()).GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Contains(LogLines(LogLevel.Information, id), line => line.Contains("2 member(s) still belong", StringComparison.Ordinal));

        // Rename: the members move with it, in the same transaction.
        using var renamed = await SendAsync(HttpMethod.Put, $"{GroupsPath}/{id}", admin.Token, new { name = $"Retail {name}"[..24], description = (string?)null });
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        var afterRename = await renamed.ReadJsonAsync();
        var newName = afterRename.GetProperty("name").GetString()!;
        Assert.Equal(2, afterRename.GetProperty("memberCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, afterRename.GetProperty("description").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, afterRename.GetProperty("updatedAtUtc").ValueKind);
        Assert.Equal(newName, await DepartmentOfAsync(admin.Token, alice.UserId));
        Assert.Equal(newName, await DepartmentOfAsync(admin.Token, bilal.UserId));
        Assert.Single(LogLines(LogLevel.Information, id), line => line.Contains("renamed True, 2 member(s) moved", StringComparison.Ordinal));

        // Empty it, then delete.
        await SetDepartmentAsync(admin.Token, alice.UserId, null);
        await SetDepartmentAsync(admin.Token, bilal.UserId, null);
        using var deleted = await SendAsync(HttpMethod.Delete, $"{GroupsPath}/{id}", admin.Token);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        using var gone = await SendAsync(HttpMethod.Get, $"{GroupsPath}/{id}", admin.Token);
        Assert.Equal("Groups.NotFound", await gone.ReadProblemCodeAsync());
        Assert.Contains(LogLines(LogLevel.Information, id), line => line.Contains("deleted group", StringComparison.Ordinal));

        // Logs name ids only: never group names or e-mail addresses.
        Assert.DoesNotContain(AllLogLines(), line => line.Contains(name, StringComparison.Ordinal) || line.Contains(newName, StringComparison.Ordinal));
        Assert.DoesNotContain(AllLogLines(), line => line.Contains(admin.Email, StringComparison.OrdinalIgnoreCase));
    }

    [SqlServerFact]
    public async Task Changing_only_the_letter_case_of_a_name_still_moves_the_members()
    {
        var organization = await CreateOrganizationAsync();
        var admin = await CreateUserAsync((organization, OrganizationRoles.Administrator));
        var member = await CreateUserAsync((organization, OrganizationRoles.ReadOnly, "warehouse"));
        var id = await CreateGroupAsync(admin.Token, "warehouse");

        using var renamed = await SendAsync(HttpMethod.Put, $"{GroupsPath}/{id}", admin.Token, new { name = "WAREHOUSE" });

        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        Assert.Equal("WAREHOUSE", (await renamed.ReadJsonAsync()).GetProperty("name").GetString());
        Assert.Equal("WAREHOUSE", await DepartmentOfAsync(admin.Token, member.UserId));
    }

    [SqlServerFact]
    public async Task Names_are_unique_per_organization_and_a_refused_rename_changes_nothing()
    {
        var organization = await CreateOrganizationAsync();
        var otherOrganization = await CreateOrganizationAsync();
        var admin = await CreateUserAsync((organization, OrganizationRoles.Administrator));
        var otherAdmin = await CreateUserAsync((otherOrganization, OrganizationRoles.Administrator));
        var member = await CreateUserAsync((organization, OrganizationRoles.ReadOnly, "Packing"));
        await CreateGroupAsync(admin.Token, "Sales");
        var packing = await CreateGroupAsync(admin.Token, "Packing");

        using var duplicate = await SendAsync(HttpMethod.Post, GroupsPath, admin.Token, new { name = " SALES " });
        using var renameIntoDuplicate = await SendAsync(HttpMethod.Put, $"{GroupsPath}/{packing}", admin.Token, new { name = "sales", description = "Changed" });
        using var otherOrganizationMayReuse = await SendAsync(HttpMethod.Post, GroupsPath, otherAdmin.Token, new { name = "Sales" });

        Assert.Equal("Groups.DuplicateName", await duplicate.ReadProblemCodeAsync());
        Assert.Equal("Groups.DuplicateName", await renameIntoDuplicate.ReadProblemCodeAsync());
        Assert.Equal(HttpStatusCode.Created, otherOrganizationMayReuse.StatusCode);

        // Nothing moved: the group keeps its name and description, the member keeps their department.
        var unchanged = await GetJsonAsync($"{GroupsPath}/{packing}", admin.Token);
        Assert.Equal("Packing", unchanged.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, unchanged.GetProperty("description").ValueKind);
        Assert.Equal("Packing", await DepartmentOfAsync(admin.Token, member.UserId));
    }

    [SqlServerFact]
    public async Task Two_simultaneous_creations_of_the_same_name_make_one_group_and_one_conflict_never_an_error()
    {
        var organization = await CreateOrganizationAsync();
        var admin = await CreateUserAsync((organization, OrganizationRoles.Administrator));
        var name = $"Race {Guid.NewGuid():N}"[..20];

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ =>
        {
            using var response = await SendAsync(HttpMethod.Post, GroupsPath, admin.Token, new { name });
            return (response.StatusCode, Code: response.StatusCode == HttpStatusCode.Created ? null : await response.ReadProblemCodeAsync());
        }));

        Assert.Equal(1, responses.Count(response => response.StatusCode == HttpStatusCode.Created));
        Assert.All(responses.Where(response => response.StatusCode != HttpStatusCode.Created), response =>
        {
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal("Groups.DuplicateName", response.Code);
        });
        Assert.Equal(1, await WithDbAsync(db => db.Groups.CountAsync(group => group.OrganizationId == organization && group.Name == name)));
    }

    [SqlServerFact]
    public async Task Assigning_a_department_that_is_not_a_group_still_works_and_is_not_listed_as_a_group()
    {
        var organization = await CreateOrganizationAsync();
        var admin = await CreateUserAsync((organization, OrganizationRoles.Administrator));
        var member = await CreateUserAsync((organization, OrganizationRoles.ReadOnly));

        await SetDepartmentAsync(admin.Token, member.UserId, "Free text");

        Assert.Equal("Free text", await DepartmentOfAsync(admin.Token, member.UserId));
        Assert.Empty((await GetJsonAsync($"{GroupsPath}?search=Free", admin.Token)).GetProperty("items").EnumerateArray());
    }

    [SqlServerFact]
    public async Task List_searches_and_pages_by_name()
    {
        var organization = await CreateOrganizationAsync();
        var admin = await CreateUserAsync((organization, OrganizationRoles.Administrator));
        foreach (var name in new[] { "Delta", "alpha", "Charlie", "Bravo" })
        {
            await CreateGroupAsync(admin.Token, name);
        }

        var all = await GetJsonAsync($"{GroupsPath}?pageSize=3", admin.Token);
        Assert.Equal(new[] { "alpha", "Bravo", "Charlie" }, Names(all));
        Assert.Equal(4, all.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, all.GetProperty("totalPages").GetInt32());
        Assert.Equal(new[] { "Delta" }, Names(await GetJsonAsync($"{GroupsPath}?pageSize=3&page=2", admin.Token)));
        Assert.Equal(new[] { "Charlie" }, Names(await GetJsonAsync($"{GroupsPath}?search=harl", admin.Token)));
    }

    // ---- Tenant isolation ---------------------------------------------------------------------------------

    [SqlServerFact]
    public async Task Another_organizations_groups_look_exactly_like_missing_ones()
    {
        var mine = await CreateOrganizationAsync();
        var theirs = await CreateOrganizationAsync();
        var admin = await CreateUserAsync((mine, OrganizationRoles.Administrator));
        var otherAdmin = await CreateUserAsync((theirs, OrganizationRoles.Administrator));
        var theirMember = await CreateUserAsync((theirs, OrganizationRoles.ReadOnly, "Secret Dept"));
        var mineGroup = await CreateGroupAsync(admin.Token, "Mine");
        var theirGroup = await CreateGroupAsync(otherAdmin.Token, "Secret Dept");

        Assert.Equal(new[] { "Mine" }, Names(await GetJsonAsync(GroupsPath, admin.Token)));

        using var missing = await SendAsync(HttpMethod.Get, $"{GroupsPath}/{Guid.NewGuid()}", admin.Token);
        Assert.Equal("Groups.NotFound", await missing.ReadProblemCodeAsync());
        var missingDetail = (await missing.ReadJsonAsync()).GetProperty("detail").GetString();
        var path = $"{GroupsPath}/{theirGroup}";
        var attempts = new (HttpMethod Method, object? Body)[]
        {
            (HttpMethod.Get, null),
            (HttpMethod.Put, new { name = "Hijacked" }),
            (HttpMethod.Delete, null),
        };
        foreach (var (method, body) in attempts)
        {
            using var response = await SendAsync(method, path, admin.Token, body);
            Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{method} {path} answered {response.StatusCode}");
            Assert.Equal("Groups.NotFound", await response.ReadProblemCodeAsync());
            Assert.Equal(missingDetail, (await response.ReadJsonAsync()).GetProperty("detail").GetString());
        }

        // Naming their organization in the header does not help: an administrator is not a member of it.
        using var spoofed = await SendAsync(HttpMethod.Delete, path, admin.Token, organization: theirs);
        Assert.Equal(HttpStatusCode.Forbidden, spoofed.StatusCode);

        // Untouched.
        var untouched = await GetJsonAsync(path, otherAdmin.Token);
        Assert.Equal("Secret Dept", untouched.GetProperty("name").GetString());
        Assert.Equal(1, untouched.GetProperty("memberCount").GetInt32());
        Assert.Equal("Secret Dept", await DepartmentOfAsync(otherAdmin.Token, theirMember.UserId));
        Assert.Equal("Mine", (await GetJsonAsync($"{GroupsPath}/{mineGroup}", admin.Token)).GetProperty("name").GetString());
        Assert.Contains(LogLines(LogLevel.Information, theirGroup), line => line.Contains("does not exist in organization", StringComparison.Ordinal));
    }

    [SqlServerFact]
    public async Task Super_admins_work_in_the_organization_they_name_and_must_name_one()
    {
        var organization = await CreateOrganizationAsync();
        var admin = await CreateUserAsync((organization, OrganizationRoles.Administrator));
        var superAdmin = await CreatePlatformUserAsync(PlatformRoles.SuperAdministrator);
        var id = await CreateGroupAsync(admin.Token, "Sales");

        using var noOrganization = await SendAsync(HttpMethod.Get, GroupsPath, superAdmin.Token);
        using var created = await SendAsync(HttpMethod.Post, GroupsPath, superAdmin.Token, new { name = "Support" }, organization: organization);
        var seen = await GetJsonAsync($"{GroupsPath}/{id}", superAdmin.Token, organization);

        Assert.Equal("Groups.OrganizationRequired", await noOrganization.ReadProblemCodeAsync());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("Sales", seen.GetProperty("name").GetString());
        Assert.Equal(new[] { "Sales", "Support" }, Names(await GetJsonAsync(GroupsPath, superAdmin.Token, organization)));
        Assert.Equal(organization, await WithDbAsync(db => db.Groups.Where(group => group.Name == "Support" && group.OrganizationId == organization).Select(group => group.OrganizationId).SingleAsync()));
    }

    // ---- Permissions ---------------------------------------------------------------------------------------

    [SqlServerFact]
    public async Task Viewing_needs_users_view_and_changing_needs_users_manage()
    {
        var organization = await CreateOrganizationAsync();
        var admin = await CreateUserAsync((organization, OrganizationRoles.Administrator));
        var viewer = await CreateUserAsync((organization, OrganizationRoles.ReadOnly));       // users.view, not users.manage
        var seller = await CreateUserAsync((organization, OrganizationRoles.SalesOperator));  // neither
        var platformAdmin = await CreatePlatformUserAsync(PlatformRoles.Administrator);        // no organization permissions
        var id = await CreateGroupAsync(admin.Token, "Sales");

        using var viewerList = await SendAsync(HttpMethod.Get, GroupsPath, viewer.Token);
        using var viewerGet = await SendAsync(HttpMethod.Get, $"{GroupsPath}/{id}", viewer.Token);
        using var viewerCreate = await SendAsync(HttpMethod.Post, GroupsPath, viewer.Token, new { name = "Nope" });
        using var viewerUpdate = await SendAsync(HttpMethod.Put, $"{GroupsPath}/{id}", viewer.Token, new { name = "Nope" });
        using var viewerDelete = await SendAsync(HttpMethod.Delete, $"{GroupsPath}/{id}", viewer.Token);
        using var sellerList = await SendAsync(HttpMethod.Get, GroupsPath, seller.Token);
        using var platformList = await SendAsync(HttpMethod.Get, GroupsPath, platformAdmin.Token, organization: organization);

        Assert.Equal(HttpStatusCode.OK, viewerList.StatusCode);
        Assert.Equal(HttpStatusCode.OK, viewerGet.StatusCode);
        foreach (var refused in new[] { viewerCreate, viewerUpdate, viewerDelete, sellerList, platformList })
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Equal("Auth.PermissionDenied", await refused.ReadProblemCodeAsync());
        }

        Assert.Equal("Sales", (await GetJsonAsync($"{GroupsPath}/{id}", admin.Token)).GetProperty("name").GetString());
    }

    // ---- Input and sign-in -----------------------------------------------------------------------------------

    [SqlServerFact]
    public async Task Input_is_validated_and_unknown_groups_are_not_found()
    {
        var organization = await CreateOrganizationAsync();
        var admin = await CreateUserAsync((organization, OrganizationRoles.Administrator));
        var id = await CreateGroupAsync(admin.Token, "Sales");

        using var noName = await SendAsync(HttpMethod.Post, GroupsPath, admin.Token, new { });
        using var blankName = await SendAsync(HttpMethod.Post, GroupsPath, admin.Token, new { name = "   " });
        using var longName = await SendAsync(HttpMethod.Post, GroupsPath, admin.Token, new { name = new string('x', Group.NameMaxLength + 1) });
        using var longDescription = await SendAsync(HttpMethod.Post, GroupsPath, admin.Token, new { name = "Ok", description = new string('x', Group.DescriptionMaxLength + 1) });
        using var blankRename = await SendAsync(HttpMethod.Put, $"{GroupsPath}/{id}", admin.Token, new { name = "" });
        using var badPage = await SendAsync(HttpMethod.Get, $"{GroupsPath}?pageSize=0", admin.Token);
        using var unknownPut = await SendAsync(HttpMethod.Put, $"{GroupsPath}/{Guid.NewGuid()}", admin.Token, new { name = "Ghost" });
        using var unknownDelete = await SendAsync(HttpMethod.Delete, $"{GroupsPath}/{Guid.NewGuid()}", admin.Token);

        foreach (var response in new[] { noName, blankName, longName, longDescription, blankRename, badPage })
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("Request.ValidationFailed", await response.ReadProblemCodeAsync());
        }

        foreach (var response in new[] { unknownPut, unknownDelete })
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("Groups.NotFound", await response.ReadProblemCodeAsync());
        }

        Assert.Equal(new[] { "Sales" }, Names(await GetJsonAsync(GroupsPath, admin.Token)));
    }

    [SqlServerFact]
    public async Task Every_endpoint_needs_a_signed_in_user()
    {
        using var client = app.Factory.CreateBrowserClient();
        var id = Guid.NewGuid();
        var requests = new (HttpMethod Method, string Path)[]
        {
            (HttpMethod.Get, GroupsPath),
            (HttpMethod.Get, $"{GroupsPath}/{id}"),
            (HttpMethod.Post, GroupsPath),
            (HttpMethod.Put, $"{GroupsPath}/{id}"),
            (HttpMethod.Delete, $"{GroupsPath}/{id}"),
        };

        foreach (var (method, path) in requests)
        {
            using var response = await client.SendAsync(new HttpRequestMessage(method, new Uri(path, UriKind.Relative)));
            Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, $"{method} {path} answered {response.StatusCode}");
            Assert.Equal("Auth.Unauthorized", await response.ReadProblemCodeAsync());
        }
    }

    // ---- Helpers -----------------------------------------------------------------------------------------

    private sealed record TestUser(Guid UserId, string Email, string Token);

    private static List<string?> Names(JsonElement page) =>
        page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("name").GetString()).ToList();

    private IEnumerable<LogEntry> ApplicationLog() =>
        app.Factory.Services.GetRequiredService<TestLogSink>().Entries
            .Where(entry => entry.Category.StartsWith("BusinessSuite.", StringComparison.Ordinal));

    private IEnumerable<string> AllLogLines() => ApplicationLog().Select(entry => entry.Message);

    private IEnumerable<string> LogLines(LogLevel level, Guid id) =>
        ApplicationLog()
            .Where(entry => entry.Level == level && entry.Message.Contains(id.ToString(), StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.Message);

    private async Task<Guid> CreateGroupAsync(string token, string name)
    {
        using var response = await SendAsync(HttpMethod.Post, GroupsPath, token, new { name });
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"Creating group {name} answered {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.ReadJsonAsync()).GetProperty("id").GetGuid();
    }

    private async Task SetDepartmentAsync(string token, Guid userId, string? department)
    {
        using var response = await SendAsync(HttpMethod.Patch, $"/api/v1/users/{userId}/group", token, new { department });
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"Setting the department answered {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    private async Task<string?> DepartmentOfAsync(string token, Guid userId) =>
        (await GetJsonAsync($"/api/v1/users/{userId}", token)).GetProperty("memberships")[0].GetProperty("department").GetString();

    private async Task<Guid> CreateOrganizationAsync()
    {
        var organization = Organization.Create($"Groups Test {Guid.NewGuid():N}");
        await WithDbAsync(async db =>
        {
            db.Organizations.Add(organization);
            return await db.SaveChangesAsync();
        });
        return organization.Id;
    }

    private Task<TestUser> CreateUserAsync(params (Guid OrganizationId, string Role)[] memberships) =>
        CreateUserCoreAsync(null, memberships.Select(membership => (membership.OrganizationId, membership.Role, (string?)null)).ToArray());

    private Task<TestUser> CreateUserAsync(params (Guid OrganizationId, string Role, string? Department)[] memberships) =>
        CreateUserCoreAsync(null, memberships);

    private Task<TestUser> CreatePlatformUserAsync(string platformRole) => CreateUserCoreAsync(platformRole, []);

    private async Task<TestUser> CreateUserCoreAsync(string? platformRole, (Guid OrganizationId, string Role, string? Department)[] memberships)
    {
        var email = $"groups-{Guid.NewGuid():N}@example.com";
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
        foreach (var (organizationId, role, department) in memberships)
        {
            db.OrganizationMemberships.Add(OrganizationMembership.Create(organizationId, user.Id, role, department));
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
