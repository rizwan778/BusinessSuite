using BusinessSuite.Application.Common.Security;
using BusinessSuite.Application.Features.Roles;
using BusinessSuite.Domain.Organizations;
using BusinessSuite.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BusinessSuite.Infrastructure.Identity;

/// <summary>
/// <see cref="IUserAccessService"/> on SQL Server: two small indexed queries (platform roles, active
/// memberships with their roles' permissions), a third only when a Super Admin acts in an organization they are not
/// a member of.
/// The rules are in <see cref="UserAccessCalculator"/>.
/// </summary>
/// <remarks>
/// A DEACTIVATED account gets nothing (no platform role, no organization, no permission), whatever signed it in. Bearer
/// tokens are already rejected for such accounts (<c>ApiSessionValidator</c>); this also closes the window in which a
/// website cookie is still accepted until the next security-stamp check (up to 5 minutes).
/// </remarks>
internal sealed class UserAccessService(AppDbContext db, ILogger<UserAccessService> logger) : IUserAccessService
{
    public async Task<UserAccess> GetAccessAsync(Guid userId, Guid? requestedOrganizationId, CancellationToken cancellationToken = default)
    {
        // Each query joins the account and requires it to be active: no extra round trip for the deactivation check.
        var platformRoles = await (
                from userRole in db.UserRoles
                join role in db.Roles on userRole.RoleId equals role.Id
                join user in db.Users on userRole.UserId equals user.Id
                where userRole.UserId == userId && role.Name != null && user.IsActive
                select role.Name!)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var rows = await (
                from membership in db.OrganizationMemberships
                join organization in db.Organizations on membership.OrganizationId equals organization.Id
                join role in db.RoleDefinitions on membership.RoleId equals role.Id
                join user in db.Users on membership.UserId equals user.Id
                where membership.UserId == userId && membership.IsActive && organization.IsActive && user.IsActive
                select new { organization.Id, organization.Name, Role = role.Name, role.SystemKey, role.Permissions })
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        // The role's permissions come from the database, so a change to a role applies on its holders' next request.
        var memberships = rows
            .Select(row => new OrganizationSummary(
                row.Id,
                row.Name,
                row.Role,
                RoleAccessRules.EffectivePermissions(row.SystemKey == OrganizationRoles.Owner, row.Permissions)))
            .ToList();

        OrganizationSummary? requestedOrganization = null;
        var isSuperAdmin = platformRoles.Contains(PlatformRoles.SuperAdministrator, StringComparer.Ordinal);
        if (isSuperAdmin
            && requestedOrganizationId is { } requestedId
            && memberships.All(membership => membership.OrganizationId != requestedId))
        {
            requestedOrganization = await db.Organizations
                .AsNoTracking()
                .Where(organization => organization.Id == requestedId && organization.IsActive)
                .Select(organization => new OrganizationSummary(organization.Id, organization.Name, null))
                .SingleOrDefaultAsync(cancellationToken);
        }

        var access = UserAccessCalculator.Calculate(userId, platformRoles, memberships, requestedOrganizationId, requestedOrganization);

        if (access.Selection == OrganizationSelection.Denied)
        {
            logger.LogWarning(
                "User {UserId} asked to act in organization {OrganizationId}, which they cannot access (not an active member, or the organization is unknown or suspended).",
                userId, requestedOrganizationId);
        }
        else if (access.IsSuperAdmin && access.Organization is { Role: null } acting)
        {
            // Cross-tenant access by a platform operator: always leave a trace.
            logger.LogWarning("Super Admin {UserId} is acting in organization {OrganizationId} without being a member.", userId, acting.OrganizationId);
        }

        return access;
    }
}
