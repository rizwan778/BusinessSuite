using BusinessSuite.Application.Common.Security;
using BusinessSuite.Application.Features.Users;
using BusinessSuite.Domain.Common;
using BusinessSuite.Domain.Organizations;
using BusinessSuite.Tests.Unit.TestDoubles;

namespace BusinessSuite.Tests.Unit.Application.Users;

/// <summary>Who may see and change which users (<see cref="UserAccessRules"/>), and the membership department.</summary>
public sealed class UserAccessRulesTests
{
    private static readonly Guid MyOrganization = Guid.NewGuid();
    private static readonly Guid OtherOrganization = Guid.NewGuid();

    private static readonly FakeCurrentUser Owner = Member(OrganizationRoles.Owner);
    private static readonly FakeCurrentUser Administrator = Member(OrganizationRoles.Administrator);
    private static readonly FakeCurrentUser SuperAdmin = new() { IsAuthenticated = true, UserId = Guid.NewGuid(), IsSuperAdmin = true };

    // ---- Organization scope ----

    [Fact]
    public void Organization_users_are_limited_to_their_own_organization()
    {
        var scope = UserAccessRules.ListScope(Administrator, requestedOrganizationId: null);

        Assert.True(scope.IsSuccess);
        Assert.Equal(MyOrganization, scope.Value);
        Assert.Equal(MyOrganization, UserAccessRules.ListScope(Administrator, MyOrganization).Value);
        Assert.Equal(MyOrganization, UserAccessRules.ReadScope(Administrator).Value);
    }

    [Fact]
    public void A_client_supplied_organization_never_widens_an_organization_users_scope()
    {
        var scope = UserAccessRules.ListScope(Administrator, OtherOrganization);

        Assert.True(scope.IsFailure);
        Assert.Equal(UserErrors.OrganizationFilterNotAllowed, scope.Error);
    }

    [Fact]
    public void Without_a_selected_organization_an_organization_user_sees_nothing()
    {
        var noOrganization = new FakeCurrentUser { IsAuthenticated = true, UserId = Guid.NewGuid() };

        Assert.Equal(UserErrors.OrganizationRequired, UserAccessRules.ListScope(noOrganization, null).Error);
        Assert.Equal(UserErrors.OrganizationRequired, UserAccessRules.ActingOrganization(noOrganization).Error);
    }

    [Fact]
    public void Super_admins_see_every_organization_or_filter_by_any()
    {
        Assert.Null(UserAccessRules.ListScope(SuperAdmin, null).Value);
        Assert.Equal(OtherOrganization, UserAccessRules.ListScope(SuperAdmin, OtherOrganization).Value);
        Assert.Null(UserAccessRules.ReadScope(SuperAdmin).Value);
    }

    [Fact]
    public void Super_admin_changes_happen_in_the_organization_they_act_in()
    {
        var acting = new FakeCurrentUser { IsAuthenticated = true, UserId = Guid.NewGuid(), IsSuperAdmin = true, OrganizationId = OtherOrganization };

        Assert.Equal(OtherOrganization, UserAccessRules.ActingOrganization(acting).Value);
        Assert.Equal(UserErrors.OrganizationRequired, UserAccessRules.ActingOrganization(SuperAdmin).Error);
    }

    // ---- Changes ----

    [Theory]
    [InlineData(UserChange.Role)]
    [InlineData(UserChange.Status)]
    public void Nobody_changes_their_own_role_or_status(UserChange change)
    {
        foreach (var actor in new[] { Owner, Administrator, SuperAdmin })
        {
            var self = new ManagedUser(actor.UserId!.Value, HasPlatformRole: actor.IsSuperAdmin, actor.OrganizationRole);

            Assert.Equal(UserErrors.CannotChangeOwnAccess, UserAccessRules.CheckChange(actor, self, change, SystemRole(OrganizationRoles.Owner)).Error);
        }
    }

    [Theory]
    [InlineData(UserChange.Profile)]
    [InlineData(UserChange.Department)]
    [InlineData(UserChange.PasswordReset)]
    public void Users_may_change_their_own_profile_department_and_password(UserChange change)
    {
        var self = new ManagedUser(Administrator.UserId!.Value, HasPlatformRole: false, OrganizationRoles.Administrator);

        Assert.True(UserAccessRules.CheckChange(Administrator, self, change).IsSuccess);
    }

    [Fact]
    public void Administrators_manage_ordinary_members()
    {
        var seller = Target(OrganizationRoles.SalesOperator);

        Assert.True(UserAccessRules.CheckChange(Administrator, seller, UserChange.Role, SystemRole(OrganizationRoles.Manager)).IsSuccess);
        Assert.True(UserAccessRules.CheckChange(Administrator, seller, UserChange.Role, SystemRole(OrganizationRoles.Administrator)).IsSuccess);
        Assert.True(UserAccessRules.CheckChange(Administrator, seller, UserChange.Status).IsSuccess);
        Assert.True(UserAccessRules.CheckChange(Administrator, seller, UserChange.PasswordReset).IsSuccess);
    }

    [Fact]
    public void Only_owners_make_owners_or_change_owners()
    {
        var seller = Target(OrganizationRoles.SalesOperator);
        var owner = Target(OrganizationRoles.Owner);
        var ownerRole = SystemRole(OrganizationRoles.Owner);

        Assert.Equal(UserErrors.OwnerRequired, UserAccessRules.CheckChange(Administrator, seller, UserChange.Role, ownerRole).Error);
        Assert.Equal(UserErrors.OwnerRequired, UserAccessRules.CheckChange(Administrator, owner, UserChange.Role, SystemRole(OrganizationRoles.ReadOnly)).Error);
        Assert.Equal(UserErrors.OwnerRequired, UserAccessRules.CheckChange(Administrator, owner, UserChange.Status).Error);
        Assert.Equal(UserErrors.OwnerRequired, UserAccessRules.CheckChange(Administrator, owner, UserChange.PasswordReset).Error);
        Assert.Equal(UserErrors.OwnerRequired, UserAccessRules.CheckCreate(Administrator, ownerRole).Error);

        Assert.True(UserAccessRules.CheckChange(Owner, seller, UserChange.Role, ownerRole).IsSuccess);
        Assert.True(UserAccessRules.CheckChange(Owner, owner, UserChange.Role, SystemRole(OrganizationRoles.Administrator)).IsSuccess);
        Assert.True(UserAccessRules.CheckCreate(Owner, ownerRole).IsSuccess);
        Assert.True(UserAccessRules.CheckCreate(SuperAdmin, ownerRole).IsSuccess);
    }

    [Fact]
    public void Nobody_assigns_a_role_granting_more_than_they_hold()
    {
        // A custom role the Owner made with a permission Administrators do not hold.
        var billing = new AssignableRole(Guid.NewGuid(), "Billing", IsOwner: false, [Permissions.Subscription.Manage, Permissions.Reports.View]);
        var billingHolder = new ManagedUser(Guid.NewGuid(), HasPlatformRole: false, "Billing", billing.Permissions);

        Assert.Equal(UserErrors.RoleExceedsYourPermissions, UserAccessRules.CheckCreate(Administrator, billing).Error);
        Assert.Equal(UserErrors.RoleExceedsYourPermissions,
            UserAccessRules.CheckChange(Administrator, Target(OrganizationRoles.ReadOnly), UserChange.Role, billing).Error);
        Assert.Equal(UserErrors.RoleExceedsYourPermissions, UserAccessRules.CheckChange(Administrator, billingHolder, UserChange.Status).Error);

        Assert.True(UserAccessRules.CheckCreate(Owner, billing).IsSuccess);
        Assert.True(UserAccessRules.CheckCreate(SuperAdmin, billing).IsSuccess);
    }

    [Fact]
    public void Platform_accounts_are_changed_by_super_admins_only()
    {
        var platformAccount = new ManagedUser(Guid.NewGuid(), HasPlatformRole: true, OrganizationRoles.ReadOnly);

        Assert.Equal(UserErrors.PlatformAccountProtected, UserAccessRules.CheckChange(Owner, platformAccount, UserChange.Profile).Error);
        Assert.Equal(UserErrors.PlatformAccountProtected, UserAccessRules.CheckChange(Owner, platformAccount, UserChange.PasswordReset).Error);
        Assert.True(UserAccessRules.CheckChange(SuperAdmin, platformAccount, UserChange.Status).IsSuccess);
    }

    // ---- Department ----

    [Fact]
    public void Department_is_trimmed_blank_removes_it_and_length_is_limited()
    {
        var membership = OrganizationMembership.Create(MyOrganization, Guid.NewGuid(), OrganizationRoles.ReadOnly, "  Sales ");
        Assert.Equal("Sales", membership.Department);

        membership.ChangeDepartment("   ");
        Assert.Null(membership.Department);

        Assert.Throws<DomainException>(() => membership.ChangeDepartment(new string('x', OrganizationMembership.DepartmentMaxLength + 1)));
    }

    /// <summary>A member holding a system role, with that role's default permissions (as the claims would carry).</summary>
    private static FakeCurrentUser Member(string role) => new()
    {
        IsAuthenticated = true,
        UserId = Guid.NewGuid(),
        OrganizationId = MyOrganization,
        OrganizationRole = role,
        Permissions = SystemRole(role).Permissions,
    };

    private static AssignableRole SystemRole(string role)
    {
        var isOwner = role == OrganizationRoles.Owner;
        return new AssignableRole(OrganizationRoles.IdOf(role), role, isOwner,
            isOwner ? Permissions.OrganizationScoped : RolePermissions.ForOrganizationRole(role).ToArray());
    }

    private static ManagedUser Target(string role) => new(Guid.NewGuid(), HasPlatformRole: false, role, SystemRole(role).Permissions);
}
