using BusinessSuite.Application.Common.Security;
using BusinessSuite.Application.Features.Audit;
using BusinessSuite.Domain.Auditing;
using BusinessSuite.Domain.Organizations;
using BusinessSuite.Tests.Unit.TestDoubles;

namespace BusinessSuite.Tests.Unit.Application.Audit;

/// <summary>
/// Who reads which audit entries (<see cref="AuditAccessRules"/>), who holds the audit permissions by default, and the
/// <see cref="AuditLog"/> entry itself.
/// </summary>
public sealed class AuditRulesTests
{
    private static readonly Guid Mine = Guid.NewGuid();
    private static readonly Guid Theirs = Guid.NewGuid();

    // ---- List scope ----

    [Fact]
    public void Organization_users_are_limited_to_the_organization_they_act_in()
    {
        var admin = OrganizationAdmin(Mine);

        Assert.Equal(Mine, AuditAccessRules.ListScope(admin, requestedOrganizationId: null).Value);
        Assert.Equal(Mine, AuditAccessRules.ListScope(admin, Mine).Value);
    }

    [Fact]
    public void Organization_users_cannot_name_another_organization()
    {
        var result = AuditAccessRules.ListScope(OrganizationAdmin(Mine), Theirs);

        Assert.True(result.IsFailure);
        Assert.Equal(AuditErrors.OrganizationFilterNotAllowed, result.Error);
    }

    [Fact]
    public void Organization_users_must_act_in_an_organization()
    {
        var result = AuditAccessRules.ListScope(OrganizationAdmin(organizationId: null), requestedOrganizationId: null);

        Assert.Equal(AuditErrors.OrganizationRequired, result.Error);
    }

    [Fact]
    public void Platform_operators_see_everything_or_the_organization_they_name()
    {
        foreach (var operatorUser in new[] { SuperAdmin(), PlatformAdministrator() })
        {
            Assert.True(AuditAccessRules.IsPlatformOperator(operatorUser));
            Assert.Null(AuditAccessRules.ListScope(operatorUser, requestedOrganizationId: null).Value);
            Assert.Equal(Theirs, AuditAccessRules.ListScope(operatorUser, Theirs).Value);
        }
    }

    [Fact]
    public void Holding_another_platform_permission_does_not_make_a_platform_operator_for_audit()
    {
        var organizationsOnly = new FakeCurrentUser
        {
            IsAuthenticated = true,
            UserId = Guid.NewGuid(),
            Permissions = [Permissions.Platform.OrganizationsManage],
        };

        Assert.False(AuditAccessRules.IsPlatformOperator(organizationsOnly));
        Assert.Equal(AuditErrors.OrganizationRequired, AuditAccessRules.ListScope(organizationsOnly, null).Error);
    }

    // ---- Reading one entry ----

    [Fact]
    public void Organization_users_read_only_their_organizations_entries_never_platform_ones()
    {
        var admin = OrganizationAdmin(Mine);

        Assert.True(AuditAccessRules.CanRead(admin, Mine));
        Assert.False(AuditAccessRules.CanRead(admin, Theirs));
        Assert.False(AuditAccessRules.CanRead(admin, entryOrganizationId: null));
        Assert.False(AuditAccessRules.CanRead(OrganizationAdmin(organizationId: null), entryOrganizationId: null));
    }

    [Fact]
    public void Platform_operators_read_every_entry()
    {
        Assert.True(AuditAccessRules.CanRead(SuperAdmin(), Theirs));
        Assert.True(AuditAccessRules.CanRead(PlatformAdministrator(), entryOrganizationId: null));
    }

    // ---- Default permissions ----

    [Fact]
    public void Owners_and_administrators_view_the_audit_log_by_default_viewers_do_not()
    {
        Assert.Contains(Permissions.Audit.View, RolePermissions.ForOrganizationRole(OrganizationRoles.Owner));
        Assert.Contains(Permissions.Audit.View, RolePermissions.ForOrganizationRole(OrganizationRoles.Administrator));
        foreach (var role in OrganizationRoles.All.Except([OrganizationRoles.Owner, OrganizationRoles.Administrator]))
        {
            Assert.DoesNotContain(Permissions.Audit.View, RolePermissions.ForOrganizationRole(role));
        }
    }

    [Fact]
    public void Platform_administrators_hold_the_platform_audit_permission_and_organizations_never_can()
    {
        Assert.Contains(Permissions.Platform.AuditView, RolePermissions.ForPlatformRole(PlatformRoles.Administrator));
        Assert.False(Permissions.IsOrganizationScoped(Permissions.Platform.AuditView));
        Assert.True(Permissions.IsOrganizationScoped(Permissions.Audit.View));
    }

    [Fact]
    public void Both_audit_permissions_are_in_the_catalogue()
    {
        var catalogued = PermissionCatalogue.Groups.SelectMany(group => group.Permissions).Select(permission => permission.Key).ToList();

        Assert.Contains(Permissions.Audit.View, catalogued);
        Assert.Contains(Permissions.Platform.AuditView, catalogued);
        Assert.DoesNotContain(PermissionCatalogue.OrganizationGroups.SelectMany(group => group.Permissions), permission => permission.Key == Permissions.Platform.AuditView);
    }

    // ---- The entry ----

    [Fact]
    public void An_entry_keeps_what_it_is_given_and_cuts_overlong_values_instead_of_failing()
    {
        var at = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
        var entityId = Guid.NewGuid();

        var entry = AuditLog.Record(at, Mine, null, AuditActions.UserCreated, AuditActions.EntityTypes.User, entityId,
            "{\"role\":\"Owner\"}", new string('c', 100), new string('9', 60));

        Assert.Equal(at, entry.OccurredAtUtc);
        Assert.Equal(Mine, entry.OrganizationId);
        Assert.Null(entry.ActorUserId);
        Assert.Equal("user.created", entry.Action);
        Assert.Equal("User", entry.EntityType);
        Assert.Equal(entityId, entry.EntityId);
        Assert.Equal("{\"role\":\"Owner\"}", entry.Details);
        Assert.Equal(AuditLog.CorrelationIdMaxLength, entry.CorrelationId!.Length);
        Assert.Equal(AuditLog.IpAddressMaxLength, entry.IpAddress!.Length);
    }

    [Fact]
    public void An_entry_needs_an_action_and_an_entity_type()
    {
        Assert.ThrowsAny<ArgumentException>(() => AuditLog.Record(DateTimeOffset.UtcNow, null, null, " ", "User", null, null, null, null));
        Assert.ThrowsAny<ArgumentException>(() => AuditLog.Record(DateTimeOffset.UtcNow, null, null, "user.created", "", null, null, null, null));
    }

    [Fact]
    public void Action_codes_are_stable_entity_dot_verb_strings()
    {
        var codes = typeof(AuditActions).GetFields()
            .Where(field => field.IsLiteral)
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToList();

        Assert.NotEmpty(codes);
        Assert.Equal(codes.Count, codes.Distinct(StringComparer.Ordinal).Count());
        Assert.All(codes, code => Assert.Matches(
            "^(user|role|group|organization|stock_adjustment|stock_transfer|product|variant|attribute|category|unit|warehouse|batch)\\.[a-z_]+$", code));
        Assert.All(codes, code => Assert.True(code.Length <= AuditLog.ActionMaxLength));
    }

    private static FakeCurrentUser OrganizationAdmin(Guid? organizationId) => new()
    {
        IsAuthenticated = true,
        UserId = Guid.NewGuid(),
        OrganizationId = organizationId,
        OrganizationRole = OrganizationRoles.Administrator,
        Permissions = [.. RolePermissions.ForOrganizationRole(OrganizationRoles.Administrator)],
    };

    private static FakeCurrentUser SuperAdmin() => new() { IsAuthenticated = true, UserId = Guid.NewGuid(), IsSuperAdmin = true };

    private static FakeCurrentUser PlatformAdministrator() => new()
    {
        IsAuthenticated = true,
        UserId = Guid.NewGuid(),
        Permissions = [.. RolePermissions.ForPlatformRole(PlatformRoles.Administrator)],
    };
}
