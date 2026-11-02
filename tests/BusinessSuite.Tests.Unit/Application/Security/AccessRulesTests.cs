using BusinessSuite.Application.Common.Security;
using BusinessSuite.Domain.Common;
using BusinessSuite.Domain.Organizations;

namespace BusinessSuite.Tests.Unit.Application.Security;

/// <summary>The rules that decide what a user may do (<see cref="UserAccessCalculator"/>, <see cref="RolePermissions"/>).</summary>
public sealed class AccessRulesTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly OrganizationSummary Shop = new(Guid.NewGuid(), "Shop", OrganizationRoles.SalesOperator);
    private static readonly OrganizationSummary Warehouse = new(Guid.NewGuid(), "Warehouse", OrganizationRoles.InventoryManager);

    // ---- Permission catalogue and role map ----

    [Fact]
    public void Permission_catalogue_is_complete_and_unique()
    {
        Assert.NotEmpty(Permissions.All);
        Assert.Equal(Permissions.All.Count, Permissions.All.Distinct(StringComparer.Ordinal).Count());
        Assert.All(Permissions.All, permission => Assert.Matches("^[a-z]+(\\.[a-z]+)+$", permission));
        Assert.Contains(Permissions.Invoices.Create, Permissions.All);
    }

    [Fact]
    public void Every_role_grants_only_defined_permissions_and_no_organization_role_grants_platform_ones()
    {
        foreach (var role in OrganizationRoles.All)
        {
            var granted = RolePermissions.ForOrganizationRole(role);
            Assert.NotEmpty(granted);
            Assert.All(granted, permission => Assert.True(Permissions.IsDefined(permission), $"{role}: unknown permission {permission}"));
            Assert.DoesNotContain(granted, permission => permission.StartsWith(Permissions.PlatformPrefix, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Owner_has_everything_in_the_organization_and_administrator_everything_but_the_subscription()
    {
        Assert.Equal(Permissions.OrganizationScoped.ToHashSet(), RolePermissions.ForOrganizationRole(OrganizationRoles.Owner).ToHashSet());

        var administrator = RolePermissions.ForOrganizationRole(OrganizationRoles.Administrator);
        Assert.DoesNotContain(Permissions.Subscription.Manage, administrator);
        Assert.Contains(Permissions.Users.Manage, administrator);
    }

    [Fact]
    public void Read_only_can_view_but_never_change()
    {
        var readOnly = RolePermissions.ForOrganizationRole(OrganizationRoles.ReadOnly);

        Assert.All(readOnly, permission => Assert.EndsWith(".view", permission, StringComparison.Ordinal));
        Assert.Contains(Permissions.Invoices.View, readOnly);
    }

    [Fact]
    public void Unknown_roles_grant_nothing()
    {
        Assert.Empty(RolePermissions.ForOrganizationRole("Owner ")); // typo / trailing space
        Assert.Empty(RolePermissions.ForOrganizationRole(null));
        Assert.Empty(RolePermissions.ForPlatformRole("Admin"));
    }

    // ---- Organization selection ----

    [Fact]
    public void Only_membership_is_selected_without_a_request()
    {
        var access = UserAccessCalculator.Calculate(UserId, [], [Shop], requestedOrganizationId: null);

        Assert.Equal(OrganizationSelection.Selected, access.Selection);
        Assert.Equal(Shop, access.Organization);
        Assert.Equal(RolePermissions.ForOrganizationRole(OrganizationRoles.SalesOperator).Order(StringComparer.Ordinal), access.Permissions);
    }

    [Fact]
    public void Several_memberships_and_no_request_select_nothing()
    {
        var access = UserAccessCalculator.Calculate(UserId, [], [Shop, Warehouse], requestedOrganizationId: null);

        Assert.Equal(OrganizationSelection.None, access.Selection);
        Assert.Null(access.Organization);
        Assert.Empty(access.Permissions);
        Assert.Equal(2, access.Memberships.Count);
    }

    [Fact]
    public void Requested_membership_is_selected()
    {
        var access = UserAccessCalculator.Calculate(UserId, [], [Shop, Warehouse], Warehouse.OrganizationId);

        Assert.Equal(Warehouse, access.Organization);
        Assert.True(access.HasPermission(Permissions.Inventory.Manage));
        Assert.False(access.HasPermission(Permissions.Invoices.Create));
    }

    [Fact]
    public void Requesting_a_foreign_organization_is_denied_with_no_permissions()
    {
        var foreign = new OrganizationSummary(Guid.NewGuid(), "Foreign", null);

        var access = UserAccessCalculator.Calculate(UserId, [], [Shop], foreign.OrganizationId, foreign);

        Assert.Equal(OrganizationSelection.Denied, access.Selection);
        Assert.Null(access.Organization);
        Assert.Empty(access.Permissions);
    }

    // ---- Platform roles and Super Admin ----

    [Fact]
    public void Super_admin_has_every_permission_even_without_an_organization()
    {
        var access = UserAccessCalculator.Calculate(UserId, [PlatformRoles.SuperAdministrator], [], requestedOrganizationId: null);

        Assert.True(access.IsSuperAdmin);
        Assert.Equal(Permissions.All, access.Permissions);
    }

    [Fact]
    public void Super_admin_may_act_in_an_organization_they_are_not_a_member_of()
    {
        var foreign = new OrganizationSummary(Guid.NewGuid(), "Foreign", null);

        var access = UserAccessCalculator.Calculate(UserId, [PlatformRoles.SuperAdministrator], [], foreign.OrganizationId, foreign);

        Assert.Equal(OrganizationSelection.Selected, access.Selection);
        Assert.Equal(foreign.OrganizationId, access.Organization!.OrganizationId);
        Assert.Null(access.Organization.Role);
    }

    [Fact]
    public void Platform_administrator_gets_platform_permissions_only()
    {
        var access = UserAccessCalculator.Calculate(UserId, [PlatformRoles.Administrator], [], requestedOrganizationId: null);

        Assert.False(access.IsSuperAdmin);
        Assert.Equal(Permissions.PlatformScoped, access.Permissions);
    }

    [Fact]
    public void Unknown_identity_roles_are_ignored()
    {
        var access = UserAccessCalculator.Calculate(UserId, ["SomethingElse", PlatformRoles.Administrator], [], requestedOrganizationId: null);

        Assert.Equal(new[] { PlatformRoles.Administrator }, access.PlatformRoles);
    }

    // ---- Domain rules ----

    [Fact]
    public void Membership_rejects_unknown_roles()
    {
        Assert.Throws<DomainException>(() => OrganizationMembership.Create(Guid.NewGuid(), Guid.NewGuid(), "God"));
        Assert.Throws<DomainException>(() => OrganizationMembership.Create(Guid.NewGuid(), Guid.NewGuid(), "owner")); // case-sensitive
        Assert.Equal(OrganizationRoles.OwnerId, OrganizationMembership.Create(Guid.NewGuid(), Guid.NewGuid(), OrganizationRoles.Owner).RoleId);
    }

    [Fact]
    public void Organization_name_is_trimmed_and_limited()
    {
        Assert.Equal("Shop", Organization.Create("  Shop  ").Name);
        Assert.Throws<DomainException>(() => Organization.Create(new string('x', Organization.NameMaxLength + 1)));
        Assert.ThrowsAny<ArgumentException>(() => Organization.Create(" "));
    }
}
