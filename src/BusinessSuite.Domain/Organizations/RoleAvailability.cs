namespace BusinessSuite.Domain.Organizations;

/// <summary>
/// A platform role made available to one organization by a Super Admin (<c>platform.RoleAvailability</c>).
/// Only used for platform roles that are not <see cref="RoleDefinition.AvailableToAllOrganizations"/>.
/// </summary>
public sealed class RoleAvailability
{
    private RoleAvailability()
    {
    }

    public Guid RoleId { get; private set; }

    public Guid OrganizationId { get; private set; }

    public static RoleAvailability Create(Guid roleId, Guid organizationId) => new() { RoleId = roleId, OrganizationId = organizationId };
}
