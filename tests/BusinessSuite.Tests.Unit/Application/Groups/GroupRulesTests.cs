using BusinessSuite.Application.Features.Groups;
using BusinessSuite.Domain.Common;
using BusinessSuite.Domain.Organizations;
using BusinessSuite.Tests.Unit.TestDoubles;

namespace BusinessSuite.Tests.Unit.Application.Groups;

/// <summary><see cref="GroupAccessRules"/>, the error contract and the <see cref="Group"/> entity.</summary>
public sealed class GroupRulesTests
{
    private static readonly Guid MyOrganization = Guid.NewGuid();

    // ---- Which organization ----

    [Fact]
    public void Groups_belong_to_the_organization_the_request_acts_in()
    {
        var administrator = new FakeCurrentUser { IsAuthenticated = true, UserId = Guid.NewGuid(), OrganizationId = MyOrganization };
        var superAdminInOrganization = new FakeCurrentUser { IsAuthenticated = true, UserId = Guid.NewGuid(), IsSuperAdmin = true, OrganizationId = MyOrganization };

        Assert.Equal(MyOrganization, GroupAccessRules.ActingOrganization(administrator).Value);
        Assert.Equal(MyOrganization, GroupAccessRules.ActingOrganization(superAdminInOrganization).Value);
    }

    [Fact]
    public void Acting_in_no_organization_must_choose_one_even_for_super_admins()
    {
        var superAdmin = new FakeCurrentUser { IsAuthenticated = true, UserId = Guid.NewGuid(), IsSuperAdmin = true };

        Assert.Equal(GroupErrors.OrganizationRequired, GroupAccessRules.ActingOrganization(superAdmin).Error);
        Assert.Equal(GroupErrors.OrganizationRequired, GroupAccessRules.ActingOrganization(FakeCurrentUser.SignedIn()).Error);
    }

    // ---- Error contract ----

    [Fact]
    public void Error_codes_are_part_of_the_api_contract()
    {
        Assert.Equal("Groups.NotFound", GroupErrors.NotFound.Code);
        Assert.Equal("Groups.OrganizationRequired", GroupErrors.OrganizationRequired.Code);
        Assert.Equal("Groups.DuplicateName", GroupErrors.DuplicateName.Code);
        Assert.Equal("Groups.ConcurrentChange", GroupErrors.ConcurrentChange.Code);
        Assert.Equal("Groups.InUse", GroupErrors.InUse(2).Code);
        Assert.Equal("Groups.Invalid", GroupErrors.Invalid("x").Code);
        Assert.Contains("2 member(s)", GroupErrors.InUse(2).Description, StringComparison.Ordinal);
    }

    // ---- Group entity ----

    [Fact]
    public void Names_and_descriptions_are_trimmed_and_a_blank_description_is_removed()
    {
        var group = Group.Create(MyOrganization, "  Sales ", "  Front counter ");
        Assert.Equal("Sales", group.Name);
        Assert.Equal("Front counter", group.Description);
        Assert.Equal(MyOrganization, group.OrganizationId);

        group.Describe("Warehouse", "   ");
        Assert.Equal("Warehouse", group.Name);
        Assert.Null(group.Description);
    }

    [Fact]
    public void Names_and_descriptions_are_limited_and_names_are_required()
    {
        Assert.Equal(OrganizationMembership.DepartmentMaxLength, Group.NameMaxLength); // a group name IS a department
        Assert.Equal(new string('x', Group.NameMaxLength), Group.Create(MyOrganization, new string('x', Group.NameMaxLength), null).Name);

        Assert.Throws<DomainException>(() => Group.Create(MyOrganization, new string('x', Group.NameMaxLength + 1), null));
        Assert.Throws<DomainException>(() => Group.Create(MyOrganization, "Sales", new string('x', Group.DescriptionMaxLength + 1)));
        Assert.Throws<ArgumentException>(() => Group.Create(MyOrganization, "   ", null));
        Assert.Throws<ArgumentNullException>(() => Group.Create(MyOrganization, null!, null));
        Assert.Throws<DomainException>(() => Group.Create(Guid.Empty, "Sales", null));
    }

    [Fact]
    public void A_failed_change_leaves_the_group_as_it_was()
    {
        var group = Group.Create(MyOrganization, "Sales", "Front counter");

        Assert.Throws<DomainException>(() => group.Describe(new string('x', Group.NameMaxLength + 1), "Changed"));

        Assert.Equal("Sales", group.Name);
        Assert.Equal("Front counter", group.Description);
    }
}
