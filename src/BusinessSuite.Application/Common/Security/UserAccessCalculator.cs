namespace BusinessSuite.Application.Common.Security;

/// <summary>
/// The rules that turn stored facts (platform roles, memberships, requested organization) into a
/// <see cref="UserAccess"/>. Pure and deterministic, so every rule is unit-tested.
/// </summary>
/// <remarks>
/// <list type="number">
/// <item><b>Organization:</b> the requested one if the user is an active member of it; a Super Admin may act in
/// any active organization. Without a request: the user's only organization; with several, none (the client must choose).</item>
/// <item><b>Permissions:</b> those of the user's platform roles, plus those of their role in the selected organization
/// (from the database; only defined, organization-scoped permissions count).</item>
/// <item><b>Super Admin:</b> every permission, always.</item>
/// <item>Unknown role names (e.g. typed by hand in the database) grant nothing.</item>
/// </list>
/// </remarks>
public static class UserAccessCalculator
{
    /// <param name="userId">The user.</param>
    /// <param name="platformRoles">The user's Identity roles.</param>
    /// <param name="memberships">The user's ACTIVE memberships in ACTIVE organizations.</param>
    /// <param name="requestedOrganizationId">The organization the client asked to act in, if any.</param>
    /// <param name="requestedOrganization">
    /// The requested organization when it exists, is active and the user is NOT a member (used only for Super Admins).
    /// </param>
    public static UserAccess Calculate(
        Guid userId,
        IEnumerable<string> platformRoles,
        IReadOnlyList<OrganizationSummary> memberships,
        Guid? requestedOrganizationId,
        OrganizationSummary? requestedOrganization = null)
    {
        ArgumentNullException.ThrowIfNull(platformRoles);
        ArgumentNullException.ThrowIfNull(memberships);

        var roles = platformRoles
            .Where(role => PlatformRoles.All.Contains(role, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var isSuperAdmin = roles.Contains(PlatformRoles.SuperAdministrator, StringComparer.Ordinal);

        OrganizationSummary? organization;
        OrganizationSelection selection;
        if (requestedOrganizationId is { } requestedId)
        {
            organization = memberships.FirstOrDefault(membership => membership.OrganizationId == requestedId)
                ?? (isSuperAdmin && requestedOrganization?.OrganizationId == requestedId ? requestedOrganization with { Role = null } : null);
            selection = organization is null ? OrganizationSelection.Denied : OrganizationSelection.Selected;
        }
        else if (memberships.Count == 1)
        {
            organization = memberships[0];
            selection = OrganizationSelection.Selected;
        }
        else
        {
            organization = null;
            selection = OrganizationSelection.None;
        }

        var permissions = new HashSet<string>(StringComparer.Ordinal);
        if (isSuperAdmin)
        {
            permissions.UnionWith(Permissions.All);
        }
        else
        {
            foreach (var role in roles)
            {
                permissions.UnionWith(RolePermissions.ForPlatformRole(role));
            }

            if (organization is not null)
            {
                // Only real organization permissions count: a stale or hand-edited database value grants nothing,
                // and a role can never grant platform permissions.
                var granted = organization.RolePermissions ?? RolePermissions.ForOrganizationRole(organization.Role);
                permissions.UnionWith(granted.Where(Permissions.IsOrganizationScoped));
            }
        }

        return new UserAccess(
            userId,
            isSuperAdmin,
            roles,
            organization,
            selection,
            memberships.OrderBy(membership => membership.Name, StringComparer.OrdinalIgnoreCase).ToArray(),
            permissions.Order(StringComparer.Ordinal).ToArray());
    }
}
