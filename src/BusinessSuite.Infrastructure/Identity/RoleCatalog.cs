using BusinessSuite.Application.Features.Roles;
using BusinessSuite.Application.Features.Users;
using BusinessSuite.Domain.Organizations;
using BusinessSuite.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BusinessSuite.Infrastructure.Identity;

/// <summary>
/// Role lookups shared by user management, role management and per-request access: which roles an organization may
/// use, and resolving a role reference (id or name) among them.
/// </summary>
/// <remarks>
/// A role is available to organization O when it is O's own custom role, or a platform role (system or Super
/// Admin-defined) that is available to every organization or explicitly to O (<c>platform.RoleAvailability</c>).
/// </remarks>
internal sealed class RoleCatalog(AppDbContext db)
{
    /// <summary>The roles <paramref name="organizationId"/> may use (not tracked).</summary>
    public IQueryable<RoleDefinition> AvailableTo(Guid organizationId) =>
        db.RoleDefinitions.AsNoTracking().Where(role =>
            role.OrganizationId == organizationId
            || (role.OrganizationId == null
                && (role.AvailableToAllOrganizations
                    || db.RoleAvailability.Any(availability => availability.RoleId == role.Id && availability.OrganizationId == organizationId))));

    /// <summary>
    /// The role named by <paramref name="reference"/> (its id, or its name in any letter case) among the roles
    /// <paramref name="organizationId"/> may use; <see langword="null"/> when there is none.
    /// </summary>
    public async Task<AssignableRole?> FindAssignableAsync(Guid organizationId, string? reference, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return null;
        }

        var trimmed = reference.Trim();
        var available = AvailableTo(organizationId);
        var role = Guid.TryParse(trimmed, out var roleId)
            ? await available.SingleOrDefaultAsync(candidate => candidate.Id == roleId, cancellationToken)
            : await available.SingleOrDefaultAsync(candidate => candidate.Name == trimmed, cancellationToken);

        return role is null ? null : ToAssignable(role);
    }

    /// <summary>Name and effective permissions of a role by id; <see langword="null"/> if it does not exist.</summary>
    public async Task<AssignableRole?> FindAsync(Guid roleId, CancellationToken cancellationToken)
    {
        var role = await db.RoleDefinitions.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == roleId, cancellationToken);
        return role is null ? null : ToAssignable(role);
    }

    public static AssignableRole ToAssignable(RoleDefinition role) =>
        new(role.Id, role.Name, role.IsOwner, RoleAccessRules.EffectivePermissions(role.IsOwner, role.Permissions));
}
