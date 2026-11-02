using BusinessSuite.Application.Common.Security;
using BusinessSuite.Application.Features.Roles;
using BusinessSuite.Domain.Common;
using BusinessSuite.Domain.Organizations;
using BusinessSuite.Tests.Unit.TestDoubles;

namespace BusinessSuite.Tests.Unit.Application.Roles;

/// <summary>The permission catalogue, <see cref="RoleAccessRules"/> and the <see cref="RoleDefinition"/> entity.</summary>
public sealed class RoleRulesTests
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

    // ---- Permission catalogue ----

    [Fact]
    public void Catalogue_lists_every_permission_exactly_once()
    {
        var listed = PermissionCatalogue.Groups.SelectMany(group => group.Permissions).Select(permission => permission.Key).ToList();

        Assert.Equal(listed.Count, listed.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(Permissions.All.Order(StringComparer.Ordinal), listed.Order(StringComparer.Ordinal));
        Assert.All(PermissionCatalogue.Groups.SelectMany(group => group.Permissions), permission =>
        {
            Assert.False(string.IsNullOrWhiteSpace(permission.Name));
            Assert.False(string.IsNullOrWhiteSpace(permission.Description));
        });
    }

    [Fact]
    public void Catalogue_has_the_required_groups_and_keeps_platform_permissions_apart()
    {
        Assert.Equal(
            new[] { "User Management", "Product Management", "Inventory", "Sales", "Purchases", "Accounts", "Reports", "Organization Management" },
            PermissionCatalogue.OrganizationGroups.Select(group => group.Name));

        Assert.All(PermissionCatalogue.OrganizationGroups.SelectMany(group => group.Permissions), permission =>
            Assert.True(Permissions.IsOrganizationScoped(permission.Key), permission.Key));
        Assert.All(PermissionCatalogue.Groups.Single(group => group.IsPlatform).Permissions, permission =>
            Assert.StartsWith(Permissions.PlatformPrefix, permission.Key, StringComparison.Ordinal));
    }

    // ---- Scope ----

    [Fact]
    public void Organization_users_list_their_own_organizations_roles_only()
    {
        Assert.Equal(MyOrganization, RoleAccessRules.ListScope(Administrator, null).Value);
        Assert.Equal(RoleErrors.OrganizationFilterNotAllowed, RoleAccessRules.ListScope(Administrator, OtherOrganization).Error);
        Assert.Null(RoleAccessRules.ListScope(SuperAdmin, null).Value);
        Assert.Equal(OtherOrganization, RoleAccessRules.ListScope(SuperAdmin, OtherOrganization).Value);
    }

    [Fact]
    public void Only_super_admins_create_platform_roles()
    {
        Assert.Equal(RoleErrors.SuperAdminRequired, RoleAccessRules.CheckCreate(Administrator, RoleScope.Platform).Error);
        Assert.True(RoleAccessRules.CheckCreate(Administrator, RoleScope.Organization).IsSuccess);
        Assert.True(RoleAccessRules.CheckCreate(SuperAdmin, RoleScope.Platform).IsSuccess);
        Assert.Equal(RoleErrors.OrganizationRequired, RoleAccessRules.CheckCreate(SuperAdmin, RoleScope.Organization).Error);
    }

    // ---- Managing roles ----

    [Fact]
    public void Organization_admins_manage_their_own_custom_roles_but_not_shared_or_foreign_ones()
    {
        var mine = Role(MyOrganization, Permissions.Inventory.View);
        var foreign = Role(OtherOrganization, Permissions.Inventory.View);
        var shared = new RoleFacts(OrganizationRoles.IdOf(OrganizationRoles.Manager), null, IsSystem: true, IsOwner: false, [Permissions.Inventory.View]);

        Assert.True(RoleAccessRules.CheckManage(Administrator, mine).IsSuccess);
        Assert.Equal(RoleErrors.NotFound, RoleAccessRules.CheckManage(Administrator, foreign).Error);
        Assert.Equal(RoleErrors.SuperAdminRequired, RoleAccessRules.CheckManage(Administrator, shared).Error);
        Assert.True(RoleAccessRules.CheckManage(SuperAdmin, shared).IsSuccess);
    }

    [Fact]
    public void Nobody_changes_a_role_granting_more_than_they_hold()
    {
        var billing = Role(MyOrganization, Permissions.Subscription.Manage); // Administrators lack subscription.manage

        Assert.Equal(RoleErrors.RoleExceedsYourPermissions, RoleAccessRules.CheckManage(Administrator, billing).Error);
    }

    [Fact]
    public void Only_held_organization_permissions_can_be_granted()
    {
        Assert.True(RoleAccessRules.CheckPermissions(Administrator, [Permissions.Inventory.View, Permissions.Users.Manage]).IsSuccess);

        var notHeld = RoleAccessRules.CheckPermissions(Administrator, [Permissions.Subscription.Manage]);
        Assert.Equal("Roles.CannotGrantPermission", notHeld.Error.Code);
        Assert.Contains(Permissions.Subscription.Manage, notHeld.Error.Description, StringComparison.Ordinal);

        Assert.Equal("Roles.UnknownPermission", RoleAccessRules.CheckPermissions(SuperAdmin, [Permissions.Platform.UsersManage]).Error.Code);
        Assert.Equal("Roles.UnknownPermission", RoleAccessRules.CheckPermissions(SuperAdmin, ["users.everything"]).Error.Code);
        Assert.True(RoleAccessRules.CheckPermissions(SuperAdmin, [Permissions.Subscription.Manage]).IsSuccess);
    }

    [Fact]
    public void Owner_always_holds_every_organization_permission_and_stale_values_grant_nothing()
    {
        Assert.Equal(Permissions.OrganizationScoped, RoleAccessRules.EffectivePermissions(isOwner: true, []));
        Assert.Equal(
            new[] { Permissions.Inventory.View },
            RoleAccessRules.EffectivePermissions(isOwner: false, [Permissions.Inventory.View, "removed.permission", Permissions.Platform.UsersManage, Permissions.Inventory.View]));
    }

    // ---- RoleDefinition ----

    [Fact]
    public void Custom_roles_cannot_take_a_system_role_name()
    {
        Assert.Throws<DomainException>(() => RoleDefinition.CreateForOrganization(MyOrganization, " owner ", null));
        Assert.Throws<DomainException>(() => RoleDefinition.CreateForPlatform("ADMINISTRATOR", null));
        Assert.Throws<DomainException>(() => RoleDefinition.CreateForOrganization(MyOrganization, new string('x', RoleDefinition.NameMaxLength + 1), null));
        Assert.Equal("Cashier", RoleDefinition.CreateForOrganization(MyOrganization, "  Cashier ", "  ").Name);
    }

    [Fact]
    public void Permissions_are_stored_distinct_and_sorted()
    {
        var role = RoleDefinition.CreateForOrganization(MyOrganization, "Cashier", null);

        role.SetPermissions([Permissions.Sales.View, Permissions.Inventory.View, Permissions.Sales.View]);

        Assert.Equal(new[] { Permissions.Inventory.View, Permissions.Sales.View }, role.Permissions);
    }

    [Fact]
    public void Only_platform_roles_have_an_availability()
    {
        Assert.Throws<DomainException>(() => RoleDefinition.CreateForOrganization(MyOrganization, "Cashier", null).SetAvailableToAllOrganizations(true));

        var shared = RoleDefinition.CreateForPlatform("Auditor", null);
        Assert.False(shared.AvailableToAllOrganizations); // nobody can use a new platform role until the Super Admin says so
        shared.SetAvailableToAllOrganizations(true);
        Assert.True(shared.AvailableToAllOrganizations);
    }

    [Fact]
    public void System_role_ids_and_names_are_fixed_and_reserved()
    {
        Assert.Equal(OrganizationRoles.All.Count, OrganizationRoles.Ids.Values.Distinct().Count());
        Assert.All(OrganizationRoles.All, role => Assert.True(OrganizationRoles.IsReservedName(role.ToUpperInvariant())));
        Assert.Throws<DomainException>(() => OrganizationRoles.IdOf("Cashier"));
    }

    private static RoleFacts Role(Guid organizationId, params string[] permissions) =>
        new(Guid.NewGuid(), organizationId, IsSystem: false, IsOwner: false, permissions);
}
