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
/// <c>/api/v1/users</c> end to end on SQL Server: tenant isolation first, then each endpoint, the escalation rules
/// and the password-reset flow.
/// </summary>
public sealed class UsersApiTests(SqlServerAppFixture app) : IClassFixture<SqlServerAppFixture>
{
    private const string UsersPath = "/api/v1/users";
    private const string OrganizationHeader = "X-Organization-Id";

    // ---- Tenant isolation ----------------------------------------------------------------------------

    [SqlServerFact]
    public async Task Organization_admin_lists_only_their_own_organizations_users()
    {
        var mine = await CreateOrganizationAsync();
        var theirs = await CreateOrganizationAsync();
        var admin = await CreateUserAsync((mine, OrganizationRoles.Administrator));
        var colleague = await CreateUserAsync((mine, OrganizationRoles.SalesOperator));
        var stranger = await CreateUserAsync((theirs, OrganizationRoles.SalesOperator));

        var page = await GetJsonAsync($"{UsersPath}?pageSize=100", admin.Token);

        var ids = page.GetProperty("items").EnumerateArray().Select(user => user.GetProperty("id").GetGuid()).ToList();
        Assert.Equal(2, page.GetProperty("totalCount").GetInt32());
        Assert.Contains(admin.UserId, ids);
        Assert.Contains(colleague.UserId, ids);
        Assert.DoesNotContain(stranger.UserId, ids);
        Assert.All(page.GetProperty("items").EnumerateArray(), user =>
            Assert.All(user.GetProperty("memberships").EnumerateArray(), membership => Assert.Equal(mine, membership.GetProperty("organizationId").GetGuid())));
    }

    [SqlServerFact]
    public async Task Organization_admin_cannot_filter_by_another_organization_or_switch_to_it_with_the_header()
    {
        var mine = await CreateOrganizationAsync();
        var theirs = await CreateOrganizationAsync();
        var admin = await CreateUserAsync((mine, OrganizationRoles.Administrator));
        await CreateUserAsync((theirs, OrganizationRoles.Owner));

        using var filtered = await SendAsync(HttpMethod.Get, $"{UsersPath}?organizationId={theirs}", admin.Token);
        using var header = await SendAsync(HttpMethod.Get, UsersPath, admin.Token, organization: theirs);

        Assert.Equal(HttpStatusCode.Forbidden, filtered.StatusCode);
        Assert.Equal("Users.OrganizationFilterNotAllowed", await filtered.ReadProblemCodeAsync());
        Assert.Equal(HttpStatusCode.Forbidden, header.StatusCode); // not a member there: no permissions at all
        Assert.Equal("Auth.PermissionDenied", await header.ReadProblemCodeAsync());
    }

    [SqlServerFact]
    public async Task Organization_admin_cannot_read_or_change_users_of_another_organization()
    {
        var mine = await CreateOrganizationAsync();
        var theirs = await CreateOrganizationAsync();
        var admin = await CreateUserAsync((mine, OrganizationRoles.Owner));
        var stranger = await CreateUserAsync((theirs, OrganizationRoles.SalesOperator));
        var path = $"{UsersPath}/{stranger.UserId}";

        var attempts = new (HttpMethod Method, string Path, object? Body)[]
        {
            (HttpMethod.Get, path, null),
            (HttpMethod.Put, path, new { displayName = "Hijacked", phoneNumber = (string?)null }),
            (HttpMethod.Patch, $"{path}/role", new { role = OrganizationRoles.ReadOnly }),
            (HttpMethod.Patch, $"{path}/group", new { department = "Hijacked" }),
            (HttpMethod.Patch, $"{path}/status", new { isActive = false }),
            (HttpMethod.Post, $"{path}/reset-password", null),
        };

        foreach (var (method, attemptPath, body) in attempts)
        {
            using var response = await SendAsync(method, attemptPath, admin.Token, body);
            Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{method} {attemptPath} answered {response.StatusCode}");
            Assert.Equal("Users.NotFound", await response.ReadProblemCodeAsync());
        }

        var membership = await WithDbAsync(db => db.OrganizationMemberships.AsNoTracking().SingleAsync(m => m.UserId == stranger.UserId));
        Assert.Equal(OrganizationRoles.IdOf(OrganizationRoles.SalesOperator), membership.RoleId);
        Assert.True(membership.IsActive);
        Assert.Null(membership.Department);
        Assert.Empty(Outbox.SentTo(stranger.Email));
    }

    [SqlServerFact]
    public async Task Created_user_always_joins_the_callers_organization_whatever_the_body_says()
    {
        var mine = await CreateOrganizationAsync();
        var theirs = await CreateOrganizationAsync();
        var admin = await CreateUserAsync((mine, OrganizationRoles.Administrator));
        var email = NewEmail();

        using var response = await SendAsync(HttpMethod.Post, UsersPath, admin.Token,
            new { email, role = OrganizationRoles.SalesOperator, organizationId = theirs }); // over-posted field is ignored

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.ReadJsonAsync();
        var memberships = await WithDbAsync(db => db.OrganizationMemberships.AsNoTracking()
            .Where(m => m.UserId == created.GetProperty("id").GetGuid()).Select(m => m.OrganizationId).ToListAsync());
        Assert.Equal(new[] { mine }, memberships);
    }

    // ---- Super Admin -----------------------------------------------------------------------------------

    [SqlServerFact]
    public async Task Super_admin_lists_every_organization_and_can_filter_by_one()
    {
        var shop = await CreateOrganizationAsync();
        var warehouse = await CreateOrganizationAsync();
        var seller = await CreateUserAsync((shop, OrganizationRoles.SalesOperator));
        var keeper = await CreateUserAsync((warehouse, OrganizationRoles.InventoryManager));
        var superAdmin = await CreatePlatformUserAsync(PlatformRoles.SuperAdministrator);

        var all = await GetJsonAsync($"{UsersPath}?pageSize=100", superAdmin.Token);
        var onlyShop = await GetJsonAsync($"{UsersPath}?organizationId={shop}&pageSize=100", superAdmin.Token);

        var allIds = Ids(all);
        Assert.Contains(seller.UserId, allIds);
        Assert.Contains(keeper.UserId, allIds);
        Assert.Contains(superAdmin.UserId, allIds);
        Assert.Equal(new[] { seller.UserId }, Ids(onlyShop));

        var me = all.GetProperty("items").EnumerateArray().Single(user => user.GetProperty("id").GetGuid() == superAdmin.UserId);
        Assert.Equal(new[] { PlatformRoles.SuperAdministrator }, me.GetProperty("platformRoles").EnumerateArray().Select(role => role.GetString()));
    }

    [SqlServerFact]
    public async Task Super_admin_without_an_organization_deactivates_the_account_and_ends_its_sessions()
    {
        var organization = await CreateOrganizationAsync();
        var user = await CreateUserAsync((organization, OrganizationRoles.Manager));
        var superAdmin = await CreatePlatformUserAsync(PlatformRoles.SuperAdministrator);

        using var deactivate = await SendAsync(HttpMethod.Patch, $"{UsersPath}/{user.UserId}/status", superAdmin.Token, new { isActive = false });

        Assert.Equal(HttpStatusCode.OK, deactivate.StatusCode);
        Assert.False((await deactivate.ReadJsonAsync()).GetProperty("isActive").GetBoolean());
        using var afterwards = await SendAsync(HttpMethod.Get, "/api/v1/auth/me", user.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, afterwards.StatusCode);
    }

    [SqlServerFact]
    public async Task Super_admin_creates_users_in_the_organization_named_by_the_header()
    {
        var organization = await CreateOrganizationAsync();
        var superAdmin = await CreatePlatformUserAsync(PlatformRoles.SuperAdministrator);

        using var withoutOrganization = await SendAsync(HttpMethod.Post, UsersPath, superAdmin.Token, new { email = NewEmail(), role = OrganizationRoles.Owner });
        using var withOrganization = await SendAsync(HttpMethod.Post, UsersPath, superAdmin.Token, new { email = NewEmail(), role = OrganizationRoles.Owner }, organization);

        Assert.Equal(HttpStatusCode.BadRequest, withoutOrganization.StatusCode);
        Assert.Equal("Users.OrganizationRequired", await withoutOrganization.ReadProblemCodeAsync());
        Assert.Equal(HttpStatusCode.Created, withOrganization.StatusCode);
        var membership = (await withOrganization.ReadJsonAsync()).GetProperty("memberships")[0];
        Assert.Equal(organization, membership.GetProperty("organizationId").GetGuid());
        Assert.Equal(OrganizationRoles.Owner, membership.GetProperty("role").GetString());
    }

    // ---- Permissions -------------------------------------------------------------------------------------

    [SqlServerFact]
    public async Task Viewing_needs_users_view_and_changing_needs_users_manage()
    {
        var organization = await CreateOrganizationAsync();
        var viewer = await CreateUserAsync((organization, OrganizationRoles.ReadOnly));  // users.view only
        var seller = await CreateUserAsync((organization, OrganizationRoles.SalesOperator)); // neither

        using var viewerList = await SendAsync(HttpMethod.Get, UsersPath, viewer.Token);
        using var viewerCreate = await SendAsync(HttpMethod.Post, UsersPath, viewer.Token, new { email = NewEmail(), role = OrganizationRoles.ReadOnly });
        using var sellerList = await SendAsync(HttpMethod.Get, UsersPath, seller.Token);
        using var anonymousClient = app.Factory.CreateBrowserClient();
        using var anonymous = await anonymousClient.GetAsync(new Uri(UsersPath, UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, viewerList.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, viewerCreate.StatusCode);
        Assert.Contains(Permissions.Users.Manage, (await viewerCreate.ReadJsonAsync()).GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Forbidden, sellerList.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    // ---- Search, filters, sorting, paging ------------------------------------------------------------

    [SqlServerFact]
    public async Task List_supports_search_filters_sorting_and_paging()
    {
        var organization = await CreateOrganizationAsync();
        var admin = await CreateUserAsync((organization, OrganizationRoles.Administrator));
        var alice = await CreateNamedUserAsync("Alice Ahmed", (organization, OrganizationRoles.SalesOperator, "Sales"));
        var bilal = await CreateNamedUserAsync("Bilal Butt", (organization, OrganizationRoles.SalesOperator, "Sales"));
        var chand = await CreateNamedUserAsync("Chand Chaudhry", (organization, OrganizationRoles.Accountant, "Finance"));
        await WithDbAsync(db => db.OrganizationMemberships.Where(m => m.UserId == chand.UserId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(m => m.IsActive, false)));

        Assert.Equal(new[] { bilal.UserId }, Ids(await GetJsonAsync($"{UsersPath}?search=bilal", admin.Token)));
        Assert.Equal(new[] { alice.UserId }, Ids(await GetJsonAsync($"{UsersPath}?search={alice.Email[..12]}", admin.Token)));
        Assert.Equal(new[] { chand.UserId }, Ids(await GetJsonAsync($"{UsersPath}?role={OrganizationRoles.Accountant}", admin.Token)));
        Assert.Equal(new[] { alice.UserId, bilal.UserId }, Ids(await GetJsonAsync($"{UsersPath}?department=sales&sortBy=displayName", admin.Token)));
        Assert.Equal(new[] { chand.UserId }, Ids(await GetJsonAsync($"{UsersPath}?status=inactive", admin.Token)));
        Assert.DoesNotContain(chand.UserId, Ids(await GetJsonAsync($"{UsersPath}?status=active", admin.Token)));
        Assert.Equal(new[] { bilal.UserId, alice.UserId }, Ids(await GetJsonAsync($"{UsersPath}?department=Sales&sortBy=displayName&sortDirection=desc", admin.Token)));

        var firstPage = await GetJsonAsync($"{UsersPath}?sortBy=createdAt&pageSize=2&page=1", admin.Token);
        var secondPage = await GetJsonAsync($"{UsersPath}?sortBy=createdAt&pageSize=2&page=2", admin.Token);
        Assert.Equal(4, firstPage.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, firstPage.GetProperty("totalPages").GetInt32());
        Assert.True(firstPage.GetProperty("hasNextPage").GetBoolean());
        Assert.Equal(new[] { admin.UserId, alice.UserId, bilal.UserId, chand.UserId }, Ids(firstPage).Concat(Ids(secondPage)));
    }

    [SqlServerFact]
    public async Task List_rejects_invalid_filters()
    {
        var organization = await CreateOrganizationAsync();
        var admin = await CreateUserAsync((organization, OrganizationRoles.Administrator));
        var superAdmin = await CreatePlatformUserAsync(PlatformRoles.SuperAdministrator);

        using var unknownRole = await SendAsync(HttpMethod.Get, $"{UsersPath}?role=Boss", admin.Token);
        using var hugePage = await SendAsync(HttpMethod.Get, $"{UsersPath}?pageSize=1000", admin.Token);
        using var badStatus = await SendAsync(HttpMethod.Get, $"{UsersPath}?status=sleeping", admin.Token);
        using var roleSortEverywhere = await SendAsync(HttpMethod.Get, $"{UsersPath}?sortBy=role", superAdmin.Token);

        Assert.Equal("Users.InvalidRole", await unknownRole.ReadProblemCodeAsync());
        Assert.Equal("Request.ValidationFailed", await hugePage.ReadProblemCodeAsync());
        Assert.Equal("Request.ValidationFailed", await badStatus.ReadProblemCodeAsync());
        Assert.Equal("Users.InvalidSort", await roleSortEverywhere.ReadProblemCodeAsync());
    }

    // ---- Create, update, role, group, status -------------------------------------------------------------

    [SqlServerFact]
    public async Task Created_user_sets_their_password_from_the_emailed_code_and_signs_in()
    {
        var organization = await CreateOrganizationAsync();
        var admin = await CreateUserAsync((organization, OrganizationRoles.Administrator));
        var email = NewEmail();

        using var response = await SendAsync(HttpMethod.Post, UsersPath, admin.Token, new
        {
            email,
            displayName = "New Seller",
            phoneNumber = "+92 300 1234567",
            role = OrganizationRoles.SalesOperator,
            department = "Sales",
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.ReadJsonAsync();
        Assert.EndsWith($"{UsersPath}/{created.GetProperty("id").GetGuid()}", response.Headers.Location!.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("New Seller", created.GetProperty("displayName").GetString());
        Assert.False(created.GetProperty("emailConfirmed").GetBoolean());
        var membership = created.GetProperty("memberships")[0];
        Assert.Equal(OrganizationRoles.SalesOperator, membership.GetProperty("role").GetString());
        Assert.Equal("Sales", membership.GetProperty("department").GetString());

        var password = NewPassword();
        using var reset = await ResetPasswordAsync(email, Outbox.LatestCodeFor(email), password);
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        var token = await LoginAsync(email, password);
        var access = await GetJsonAsync("/api/v1/auth/me/permissions", token);
        Assert.Equal(organization, access.GetProperty("organization").GetProperty("id").GetGuid());
    }

    [SqlServerFact]
    public async Task Create_refuses_existing_accounts_weak_passwords_and_unknown_roles()
    {
        var organization = await CreateOrganizationAsync();
        var admin = await CreateUserAsync((organization, OrganizationRoles.Administrator));
        var member = await CreateUserAsync((organization, OrganizationRoles.ReadOnly));
        var outsider = await CreateUserAsync();

        using var duplicateMember = await SendAsync(HttpMethod.Post, UsersPath, admin.Token, new { email = member.Email, role = OrganizationRoles.ReadOnly });
        using var existingAccount = await SendAsync(HttpMethod.Post, UsersPath, admin.Token, new { email = outsider.Email, role = OrganizationRoles.ReadOnly });
        using var weakPassword = await SendAsync(HttpMethod.Post, UsersPath, admin.Token, new { email = NewEmail(), role = OrganizationRoles.ReadOnly, password = "short" });
        using var unknownRole = await SendAsync(HttpMethod.Post, UsersPath, admin.Token, new { email = NewEmail(), role = "Boss" });
        using var missingRole = await SendAsync(HttpMethod.Post, UsersPath, admin.Token, new { email = NewEmail() });

        Assert.Equal("Users.AlreadyMember", await duplicateMember.ReadProblemCodeAsync());
        Assert.Equal("Account.DuplicateEmail", await existingAccount.ReadProblemCodeAsync());
        Assert.Equal("Account.WeakPassword", await weakPassword.ReadProblemCodeAsync());
        Assert.Equal("Users.InvalidRole", await unknownRole.ReadProblemCodeAsync());
        Assert.Equal("Request.ValidationFailed", await missingRole.ReadProblemCodeAsync());
        Assert.False(await WithDbAsync(db => db.OrganizationMemberships.AnyAsync(m => m.UserId == outsider.UserId)));
    }

    [SqlServerFact]
    public async Task Update_changes_only_the_permitted_profile_fields_and_keeps_the_user_signed_in()
    {
        var organization = await CreateOrganizationAsync();
        var admin = await CreateUserAsync((organization, OrganizationRoles.Administrator));
        var user = await CreateUserAsync((organization, OrganizationRoles.SalesOperator));

        using var response = await SendAsync(HttpMethod.Put, $"{UsersPath}/{user.UserId}", admin.Token,
            new { displayName = "  Renamed  ", phoneNumber = "+92 42 1234567", email = "hijack@example.com", isActive = false });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.ReadJsonAsync();
        Assert.Equal("Renamed", updated.GetProperty("displayName").GetString());
        Assert.Equal("+92 42 1234567", updated.GetProperty("phoneNumber").GetString());
        Assert.Equal(user.Email, updated.GetProperty("email").GetString());
        Assert.True(updated.GetProperty("isActive").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, updated.GetProperty("updatedAtUtc").ValueKind);

        using var stillSignedIn = await SendAsync(HttpMethod.Get, "/api/v1/auth/me", user.Token);
        Assert.Equal(HttpStatusCode.OK, stillSignedIn.StatusCode);
    }

    [SqlServerFact]
    public async Task Role_change_applies_on_the_users_next_request()
    {
        var organization = await CreateOrganizationAsync();
        var admin = await CreateUserAsync((organization, OrganizationRoles.Administrator));
        var user = await CreateUserAsync((organization, OrganizationRoles.ReadOnly));

        using var response = await SendAsync(HttpMethod.Patch, $"{UsersPath}/{user.UserId}/role", admin.Token, new { role = OrganizationRoles.Manager });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(OrganizationRoles.Manager, (await response.ReadJsonAsync()).GetProperty("memberships")[0].GetProperty("role").GetString());
        var access = await GetJsonAsync("/api/v1/auth/me/permissions", user.Token);
        Assert.Equal(OrganizationRoles.Manager, access.GetProperty("organization").GetProperty("role").GetString());
    }

    [SqlServerFact]
    public async Task Group_change_sets_and_clears_the_department()
    {
        var organization = await CreateOrganizationAsync();
        var admin = await CreateUserAsync((organization, OrganizationRoles.Administrator));
        var user = await CreateUserAsync((organization, OrganizationRoles.SalesOperator));
        var path = $"{UsersPath}/{user.UserId}/group";

        using var set = await SendAsync(HttpMethod.Patch, path, admin.Token, new { department = " Warehouse " });
        Assert.Equal("Warehouse", (await set.ReadJsonAsync()).GetProperty("memberships")[0].GetProperty("department").GetString());

        using var cleared = await SendAsync(HttpMethod.Patch, path, admin.Token, new { department = (string?)null });
        Assert.Equal(JsonValueKind.Null, (await cleared.ReadJsonAsync()).GetProperty("memberships")[0].GetProperty("department").ValueKind);

        using var tooLong = await SendAsync(HttpMethod.Patch, path, admin.Token, new { department = new string('x', 101) });
        Assert.Equal("Request.ValidationFailed", await tooLong.ReadProblemCodeAsync());
    }

    [SqlServerFact]
    public async Task Deactivated_member_loses_access_to_the_organization_until_reactivated()
    {
        var organization = await CreateOrganizationAsync();
        var admin = await CreateUserAsync((organization, OrganizationRoles.Administrator));
        var user = await CreateUserAsync((organization, OrganizationRoles.SalesOperator));
        var path = $"{UsersPath}/{user.UserId}/status";

        using var deactivated = await SendAsync(HttpMethod.Patch, path, admin.Token, new { isActive = false });
        var body = await deactivated.ReadJsonAsync();
        Assert.True(body.GetProperty("isActive").GetBoolean()); // the account itself is untouched
        Assert.False(body.GetProperty("memberships")[0].GetProperty("isActive").GetBoolean());
        Assert.Equal(JsonValueKind.Null, (await GetJsonAsync("/api/v1/auth/me/permissions", user.Token)).GetProperty("organization").ValueKind);

        using var missing = await SendAsync(HttpMethod.Patch, path, admin.Token, new { });
        Assert.Equal("Request.ValidationFailed", await missing.ReadProblemCodeAsync());

        using var reactivated = await SendAsync(HttpMethod.Patch, path, admin.Token, new { isActive = true });
        Assert.Equal(HttpStatusCode.OK, reactivated.StatusCode);
        Assert.Equal(organization, (await GetJsonAsync("/api/v1/auth/me/permissions", user.Token)).GetProperty("organization").GetProperty("id").GetGuid());
    }

    // ---- Escalation rules ------------------------------------------------------------------------------

    [SqlServerFact]
    public async Task Administrators_cannot_create_promote_or_change_owners_but_owners_can()
    {
        var organization = await CreateOrganizationAsync();
        var owner = await CreateUserAsync((organization, OrganizationRoles.Owner));
        var admin = await CreateUserAsync((organization, OrganizationRoles.Administrator));
        var seller = await CreateUserAsync((organization, OrganizationRoles.SalesOperator));

        using var promote = await SendAsync(HttpMethod.Patch, $"{UsersPath}/{seller.UserId}/role", admin.Token, new { role = OrganizationRoles.Owner });
        using var create = await SendAsync(HttpMethod.Post, UsersPath, admin.Token, new { email = NewEmail(), role = OrganizationRoles.Owner });
        using var demoteOwner = await SendAsync(HttpMethod.Patch, $"{UsersPath}/{owner.UserId}/role", admin.Token, new { role = OrganizationRoles.ReadOnly });
        using var deactivateOwner = await SendAsync(HttpMethod.Patch, $"{UsersPath}/{owner.UserId}/status", admin.Token, new { isActive = false });
        using var ownerPromotes = await SendAsync(HttpMethod.Patch, $"{UsersPath}/{seller.UserId}/role", owner.Token, new { role = OrganizationRoles.Owner });

        Assert.Equal("Users.OwnerRequired", await promote.ReadProblemCodeAsync());
        Assert.Equal("Users.OwnerRequired", await create.ReadProblemCodeAsync());
        Assert.Equal("Users.OwnerRequired", await demoteOwner.ReadProblemCodeAsync());
        Assert.Equal("Users.OwnerRequired", await deactivateOwner.ReadProblemCodeAsync());
        Assert.Equal(HttpStatusCode.OK, ownerPromotes.StatusCode);
    }

    [SqlServerFact]
    public async Task Nobody_changes_their_own_role_or_status()
    {
        var organization = await CreateOrganizationAsync();
        var admin = await CreateUserAsync((organization, OrganizationRoles.Administrator));

        using var role = await SendAsync(HttpMethod.Patch, $"{UsersPath}/{admin.UserId}/role", admin.Token, new { role = OrganizationRoles.Owner });
        using var status = await SendAsync(HttpMethod.Patch, $"{UsersPath}/{admin.UserId}/status", admin.Token, new { isActive = false });

        Assert.Equal("Users.CannotChangeOwnAccess", await role.ReadProblemCodeAsync());
        Assert.Equal("Users.CannotChangeOwnAccess", await status.ReadProblemCodeAsync());
    }

    [SqlServerFact]
    public async Task The_last_active_owner_cannot_be_demoted_or_deactivated()
    {
        var organization = await CreateOrganizationAsync();
        var owner = await CreateUserAsync((organization, OrganizationRoles.Owner));
        var secondOwner = await CreateUserAsync((organization, OrganizationRoles.Owner));
        var superAdmin = await CreatePlatformUserAsync(PlatformRoles.SuperAdministrator);

        using var demoteFirst = await SendAsync(HttpMethod.Patch, $"{UsersPath}/{secondOwner.UserId}/role", owner.Token, new { role = OrganizationRoles.Administrator });
        using var demoteLast = await SendAsync(HttpMethod.Patch, $"{UsersPath}/{owner.UserId}/role", superAdmin.Token, new { role = OrganizationRoles.Administrator }, organization);
        using var deactivateLast = await SendAsync(HttpMethod.Patch, $"{UsersPath}/{owner.UserId}/status", superAdmin.Token, new { isActive = false }, organization);

        Assert.Equal(HttpStatusCode.OK, demoteFirst.StatusCode);
        Assert.Equal("Users.LastOwner", await demoteLast.ReadProblemCodeAsync());
        Assert.Equal("Users.LastOwner", await deactivateLast.ReadProblemCodeAsync());
    }

    [SqlServerFact]
    public async Task Platform_accounts_cannot_be_changed_by_organization_owners()
    {
        var organization = await CreateOrganizationAsync();
        var owner = await CreateUserAsync((organization, OrganizationRoles.Owner));
        var platformAdmin = await CreatePlatformUserAsync(PlatformRoles.Administrator, (organization, OrganizationRoles.ReadOnly));

        using var update = await SendAsync(HttpMethod.Put, $"{UsersPath}/{platformAdmin.UserId}", owner.Token, new { displayName = "x" });
        using var reset = await SendAsync(HttpMethod.Post, $"{UsersPath}/{platformAdmin.UserId}/reset-password", owner.Token);

        Assert.Equal("Users.PlatformAccountProtected", await update.ReadProblemCodeAsync());
        Assert.Equal("Users.PlatformAccountProtected", await reset.ReadProblemCodeAsync());
    }

    // ---- Password reset -----------------------------------------------------------------------------------

    [SqlServerFact]
    public async Task Password_reset_emails_a_single_use_code_and_signs_the_user_out_everywhere()
    {
        var organization = await CreateOrganizationAsync();
        var admin = await CreateUserAsync((organization, OrganizationRoles.Administrator));
        var user = await CreateUserAsync((organization, OrganizationRoles.SalesOperator));

        using var initiate = await SendAsync(HttpMethod.Post, $"{UsersPath}/{user.UserId}/reset-password", admin.Token);
        Assert.Equal(HttpStatusCode.Accepted, initiate.StatusCode);
        Assert.Empty(await initiate.Content.ReadAsStringAsync()); // the caller never sees the code

        // Nothing changes until the user completes the reset.
        await LoginAsync(user.Email, user.Password);

        var code = Outbox.LatestCodeFor(user.Email);
        var newPassword = NewPassword();
        using var wrongCode = await ResetPasswordAsync(user.Email, code[..^4] + "AAAA", newPassword);
        using var unknownEmail = await ResetPasswordAsync(NewEmail(), code, newPassword);
        using var weak = await ResetPasswordAsync(user.Email, code, "weak");
        using var completed = await ResetPasswordAsync(user.Email, code, newPassword);
        using var reused = await ResetPasswordAsync(user.Email, code, NewPassword());

        Assert.Equal("Account.InvalidResetCode", await wrongCode.ReadProblemCodeAsync());
        Assert.Equal("Account.InvalidResetCode", await unknownEmail.ReadProblemCodeAsync());
        Assert.Equal("Account.WeakPassword", await weak.ReadProblemCodeAsync());
        Assert.Equal(HttpStatusCode.NoContent, completed.StatusCode);
        Assert.Equal("Account.InvalidResetCode", await reused.ReadProblemCodeAsync());

        using var oldToken = await SendAsync(HttpMethod.Get, "/api/v1/auth/me", user.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, oldToken.StatusCode);
        await LoginAsync(user.Email, newPassword);
        using var loginClient = app.Factory.CreateBrowserClient();
        using var oldPassword = await loginClient.PostJsonAsync("/api/v1/auth/login", new { email = user.Email, password = user.Password });
        Assert.Equal(HttpStatusCode.Unauthorized, oldPassword.StatusCode);
    }

    // ---- Helpers -----------------------------------------------------------------------------------------

    private sealed record TestUser(Guid UserId, string Email, string Password, string Token);

    private TestEmailOutbox Outbox => app.Factory.Services.GetRequiredService<TestEmailOutbox>();

    private static string NewEmail() => $"users-{Guid.NewGuid():N}@example.com";

    private static string NewPassword() => $"Pw-{Guid.NewGuid():N}aA1!";

    private static List<Guid> Ids(JsonElement page) =>
        page.GetProperty("items").EnumerateArray().Select(user => user.GetProperty("id").GetGuid()).ToList();

    private async Task<Guid> CreateOrganizationAsync()
    {
        var organization = Organization.Create($"Users Test {Guid.NewGuid():N}");
        await WithDbAsync(async db =>
        {
            db.Organizations.Add(organization);
            await db.SaveChangesAsync();
            return 0;
        });
        return organization.Id;
    }

    private Task<TestUser> CreateUserAsync(params (Guid OrganizationId, string Role)[] memberships) =>
        CreateUserCoreAsync(null, null, memberships.Select(m => (m.OrganizationId, m.Role, (string?)null)).ToArray());

    private Task<TestUser> CreateNamedUserAsync(string displayName, params (Guid OrganizationId, string Role, string? Department)[] memberships) =>
        CreateUserCoreAsync(null, displayName, memberships);

    private Task<TestUser> CreatePlatformUserAsync(string platformRole, params (Guid OrganizationId, string Role)[] memberships) =>
        CreateUserCoreAsync(platformRole, null, memberships.Select(m => (m.OrganizationId, m.Role, (string?)null)).ToArray());

    private async Task<TestUser> CreateUserCoreAsync(string? platformRole, string? displayName, (Guid OrganizationId, string Role, string? Department)[] memberships)
    {
        var email = NewEmail();
        var password = NewPassword();

        using (var scope = app.Factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, DisplayName = displayName };
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
            return new TestUser(user.Id, email, password, await LoginAsync(email, password));
        }
    }

    private async Task<string> LoginAsync(string email, string password)
    {
        using var client = app.Factory.CreateBrowserClient();
        using var response = await client.PostJsonAsync("/api/v1/auth/login", new { email, password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.ReadJsonAsync()).GetProperty("accessToken").GetString()!;
    }

    private async Task<HttpResponseMessage> ResetPasswordAsync(string email, string code, string newPassword)
    {
        using var client = app.Factory.CreateBrowserClient();
        return await client.PostJsonAsync("/api/v1/auth/reset-password", new { email, code, newPassword });
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
