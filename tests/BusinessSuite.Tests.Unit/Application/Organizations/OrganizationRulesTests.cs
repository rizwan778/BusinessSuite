using BusinessSuite.Application.Common.Security;
using BusinessSuite.Application.Features.Organizations;
using BusinessSuite.Domain.Common;
using BusinessSuite.Domain.Organizations;
using BusinessSuite.Tests.Unit.TestDoubles;

namespace BusinessSuite.Tests.Unit.Application.Organizations;

/// <summary><see cref="OrganizationAccessRules"/>, the error contract and the <see cref="Organization"/> entity.</summary>
public sealed class OrganizationRulesTests
{
    private static readonly Guid MyOrganization = Guid.NewGuid();
    private static readonly Guid OtherOrganization = Guid.NewGuid();

    private static readonly FakeCurrentUser Administrator = new()
    {
        IsAuthenticated = true,
        UserId = Guid.NewGuid(),
        OrganizationId = MyOrganization,
        OrganizationRole = OrganizationRoles.Administrator,
        Permissions = RolePermissions.ForOrganizationRole(OrganizationRoles.Administrator).ToArray(),
    };

    private static readonly FakeCurrentUser SuperAdmin = new() { IsAuthenticated = true, UserId = Guid.NewGuid(), IsSuperAdmin = true };

    // A Platform Administrator holds the platform permissions only and is a member of nothing.
    private static readonly FakeCurrentUser PlatformAdministrator = new()
    {
        IsAuthenticated = true,
        UserId = Guid.NewGuid(),
        Permissions = RolePermissions.ForPlatformRole(PlatformRoles.Administrator).ToArray(),
    };

    // ---- Who is a platform operator ----

    [Fact]
    public void Super_admins_and_platform_administrators_are_platform_operators_organization_administrators_are_not()
    {
        Assert.True(OrganizationAccessRules.IsPlatformOperator(SuperAdmin));
        Assert.True(OrganizationAccessRules.IsPlatformOperator(PlatformAdministrator));
        Assert.False(OrganizationAccessRules.IsPlatformOperator(Administrator));
        Assert.False(OrganizationAccessRules.IsPlatformOperator(FakeCurrentUser.Anonymous()));
    }

    // ---- Listing ----

    [Fact]
    public void Platform_operators_list_every_organization_and_others_only_the_one_they_act_in()
    {
        Assert.Null(OrganizationAccessRules.ListScope(SuperAdmin).Value);
        Assert.Null(OrganizationAccessRules.ListScope(PlatformAdministrator).Value);
        Assert.Equal(MyOrganization, OrganizationAccessRules.ListScope(Administrator).Value);
    }

    [Fact]
    public void Organization_users_acting_in_no_organization_must_choose_one()
    {
        var withoutOrganization = FakeCurrentUser.SignedIn();

        Assert.Equal(OrganizationErrors.OrganizationRequired, OrganizationAccessRules.ListScope(withoutOrganization).Error);
        Assert.Equal(OrganizationErrors.OrganizationRequired, OrganizationAccessRules.CheckAccess(withoutOrganization, MyOrganization).Error);
    }

    // ---- Reaching one organization (tenant isolation) ----

    [Fact]
    public void Organization_users_reach_only_the_organization_they_act_in_and_everything_else_looks_missing()
    {
        Assert.True(OrganizationAccessRules.CheckAccess(Administrator, MyOrganization).IsSuccess);
        Assert.Equal(OrganizationErrors.NotFound, OrganizationAccessRules.CheckAccess(Administrator, OtherOrganization).Error);
        Assert.Equal(OrganizationErrors.NotFound, OrganizationAccessRules.CheckAccess(Administrator, Guid.Empty).Error);
    }

    [Fact]
    public void Platform_operators_reach_any_organization()
    {
        Assert.True(OrganizationAccessRules.CheckAccess(SuperAdmin, OtherOrganization).IsSuccess);
        Assert.True(OrganizationAccessRules.CheckAccess(PlatformAdministrator, OtherOrganization).IsSuccess);
    }

    // ---- Create and status: platform business ----

    [Fact]
    public void Only_platform_operators_create_organizations_and_change_their_status()
    {
        Assert.True(OrganizationAccessRules.CheckCreate(SuperAdmin).IsSuccess);
        Assert.True(OrganizationAccessRules.CheckCreate(PlatformAdministrator).IsSuccess);
        Assert.Equal(OrganizationErrors.PlatformOperatorRequired, OrganizationAccessRules.CheckCreate(Administrator).Error);

        Assert.True(OrganizationAccessRules.CheckChangeStatus(SuperAdmin).IsSuccess);
        Assert.True(OrganizationAccessRules.CheckChangeStatus(PlatformAdministrator).IsSuccess);
        Assert.Equal(OrganizationErrors.PlatformOperatorRequired, OrganizationAccessRules.CheckChangeStatus(Administrator).Error);
    }

    [Fact]
    public void An_organization_owner_cannot_suspend_their_own_organization()
    {
        var owner = new FakeCurrentUser
        {
            IsAuthenticated = true,
            UserId = Guid.NewGuid(),
            OrganizationId = MyOrganization,
            OrganizationRole = OrganizationRoles.Owner,
            Permissions = Permissions.OrganizationScoped.ToArray(),
        };

        Assert.Equal(OrganizationErrors.PlatformOperatorRequired, OrganizationAccessRules.CheckChangeStatus(owner).Error);
    }

    // ---- Initial administrator ----

    [Fact]
    public void Only_super_admins_create_an_organization_together_with_its_first_administrator()
    {
        Assert.True(OrganizationAccessRules.CheckCreateInitialAdmin(SuperAdmin).IsSuccess);

        // Platform Administrators may create organizations but hold no access inside them, and an Owner account is access.
        Assert.Equal(OrganizationErrors.SuperAdminRequired, OrganizationAccessRules.CheckCreateInitialAdmin(PlatformAdministrator).Error);
        Assert.Equal(OrganizationErrors.SuperAdminRequired, OrganizationAccessRules.CheckCreateInitialAdmin(Administrator).Error);
        Assert.Equal(OrganizationErrors.SuperAdminRequired, OrganizationAccessRules.CheckCreateInitialAdmin(FakeCurrentUser.Anonymous()).Error);
    }

    [Fact]
    public void The_initial_administrators_password_never_appears_in_text()
    {
        var admin = new InitialAdminCommand("owner@example.com", "Owner", null, "Secret-Passw0rd!");

        Assert.DoesNotContain("Secret-Passw0rd!", admin.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Secret-Passw0rd!", new CreateOrganizationCommand("Al-Noor", admin).ToString(), StringComparison.Ordinal);
    }

    // ---- Error contract ----

    [Fact]
    public void Error_codes_and_kinds_are_part_of_the_api_contract()
    {
        Assert.Equal("Organizations.SuperAdminRequired", OrganizationErrors.SuperAdminRequired.Code);
        Assert.Equal("Organizations.NotFound", OrganizationErrors.NotFound.Code);
        Assert.Equal("Organizations.OrganizationRequired", OrganizationErrors.OrganizationRequired.Code);
        Assert.Equal("Organizations.PlatformOperatorRequired", OrganizationErrors.PlatformOperatorRequired.Code);
        Assert.Equal("Organizations.ConcurrentChange", OrganizationErrors.ConcurrentChange.Code);
        Assert.Equal("Organizations.Invalid", OrganizationErrors.Invalid("x").Code);
    }

    // ---- Organization entity ----

    [Fact]
    public void Organization_names_are_trimmed_and_limited()
    {
        Assert.Equal("Al-Noor Traders", Organization.Create("  Al-Noor Traders ").Name);
        Assert.Equal(new string('x', Organization.NameMaxLength), Organization.Create(new string('x', Organization.NameMaxLength)).Name);
        Assert.Throws<DomainException>(() => Organization.Create(new string('x', Organization.NameMaxLength + 1)));
        Assert.Throws<ArgumentException>(() => Organization.Create("   "));
    }

    [Fact]
    public void New_organizations_are_active_and_can_be_suspended_and_reactivated()
    {
        var organization = Organization.Create("Al-Noor Traders");
        Assert.True(organization.IsActive);

        organization.Suspend();
        Assert.False(organization.IsActive);

        organization.Activate();
        Assert.True(organization.IsActive);
    }
}
