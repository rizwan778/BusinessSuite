using BusinessSuite.Domain.Common;

namespace BusinessSuite.Domain.Organizations;

/// <summary>
/// A person's place in one organization: which <see cref="RoleDefinition">role</see> they hold there.
/// One user can be a member of several organizations with a different role in each.
/// </summary>
/// <remarks>
/// The role must be one the organization may use (a system or platform role available to it, or its own custom
/// role); the Application layer checks that before calling <see cref="ChangeRole(Guid)"/>.
/// </remarks>
public sealed class OrganizationMembership : AuditableEntity
{
    /// <summary>Maximum length of <see cref="Department"/>.</summary>
    public const int DepartmentMaxLength = 100;

    private OrganizationMembership()
    {
    }

    public Guid OrganizationId { get; private set; }

    /// <summary>The member (<c>identity.Users.Id</c>).</summary>
    public Guid UserId { get; private set; }

    /// <summary>The role held (<c>platform.RoleDefinitions.Id</c>).</summary>
    public Guid RoleId { get; private set; }

    /// <summary>
    /// Department or group the person works in within this organization, e.g. "Sales" or "Warehouse".
    /// Free text chosen by the organization; grants no permissions. <see langword="null"/> when not assigned.
    /// </summary>
    public string? Department { get; private set; }

    /// <summary>Inactive memberships give no access; kept for history instead of deleting.</summary>
    public bool IsActive { get; private set; } = true;

    /// <summary>A membership with any role (system or custom) by id.</summary>
    public static OrganizationMembership Create(Guid organizationId, Guid userId, Guid roleId, string? department = null)
    {
        if (organizationId == Guid.Empty)
        {
            throw new DomainException("A membership needs an organization.");
        }

        if (userId == Guid.Empty)
        {
            throw new DomainException("A membership needs a user.");
        }

        var membership = new OrganizationMembership { OrganizationId = organizationId, UserId = userId };
        membership.ChangeRole(roleId);
        membership.ChangeDepartment(department);
        return membership;
    }

    /// <summary>A membership with a SYSTEM role, by name (one of <see cref="OrganizationRoles.All"/>, case-sensitive).</summary>
    public static OrganizationMembership Create(Guid organizationId, Guid userId, string systemRole, string? department = null) =>
        Create(organizationId, userId, OrganizationRoles.IdOf(systemRole), department);

    /// <summary>Assigns a department (trimmed); <see langword="null"/> or blank removes it.</summary>
    public void ChangeDepartment(string? department)
    {
        var trimmed = string.IsNullOrWhiteSpace(department) ? null : department.Trim();
        if (trimmed?.Length > DepartmentMaxLength)
        {
            throw new DomainException($"A department name cannot be longer than {DepartmentMaxLength} characters.");
        }

        Department = trimmed;
    }

    public void ChangeRole(Guid roleId)
    {
        if (roleId == Guid.Empty)
        {
            throw new DomainException("A membership needs a role.");
        }

        RoleId = roleId;
    }

    public void Activate() => IsActive = true;

    public void Deactivate() => IsActive = false;
}
