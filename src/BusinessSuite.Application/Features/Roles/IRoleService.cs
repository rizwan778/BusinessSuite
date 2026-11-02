using BusinessSuite.Application.Common.Results;
using BusinessSuite.Application.Common.Security;

namespace BusinessSuite.Application.Features.Roles;

/// <summary>
/// Role and permission management, independent of user management. Every method acts as the current user
/// (<c>ICurrentUser</c>); the rules are in <see cref="RoleAccessRules"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Tenant isolation.</b> Organization users see only the roles available to their organization (resolved on the
/// server) and change only their organization's custom roles; other roles answer <see cref="RoleErrors.NotFound"/>.
/// </para>
/// <para>
/// <b>Super Admins</b> see every role, manage shared (platform) roles including the system roles' permissions, and
/// control which platform roles each organization may use (<see cref="SetAvailabilityAsync"/>). Organization
/// administrators can then only assign roles available to their organization (enforced by user management).
/// </para>
/// Callers (controllers) also require <c>roles.view</c> for reads and <c>roles.manage</c> for changes.
/// </remarks>
public interface IRoleService
{
    /// <summary>The permission catalogue, grouped; the platform group is included for Super Admins only.</summary>
    IReadOnlyList<PermissionGroup> ListPermissions();

    /// <summary>The roles available to the caller's organization (Super Admins: every role, or one organization's).</summary>
    Task<Result<IReadOnlyList<RoleDetails>>> ListAsync(RoleListQuery query, CancellationToken cancellationToken = default);

    /// <summary>One role.</summary>
    Task<Result<RoleDetails>> GetAsync(Guid roleId, CancellationToken cancellationToken = default);

    /// <summary>Creates a custom role in the caller's organization, or a platform role (Super Admins).</summary>
    Task<Result<RoleDetails>> CreateAsync(CreateRoleCommand command, CancellationToken cancellationToken = default);

    /// <summary>Renames a custom role and replaces its description.</summary>
    Task<Result<RoleDetails>> UpdateAsync(Guid roleId, UpdateRoleCommand command, CancellationToken cancellationToken = default);

    /// <summary>Deletes a custom role nobody holds.</summary>
    Task<Result> DeleteAsync(Guid roleId, CancellationToken cancellationToken = default);

    /// <summary>A role's permissions.</summary>
    Task<Result<RolePermissionsDetails>> GetPermissionsAsync(Guid roleId, CancellationToken cancellationToken = default);

    /// <summary>Replaces a role's permissions. Applies to every holder on their next request.</summary>
    Task<Result<RolePermissionsDetails>> SetPermissionsAsync(Guid roleId, IReadOnlyCollection<string> permissions, CancellationToken cancellationToken = default);

    /// <summary>Super Admins: which organizations may use a platform role.</summary>
    Task<Result<RoleDetails>> SetAvailabilityAsync(Guid roleId, SetRoleAvailabilityCommand command, CancellationToken cancellationToken = default);
}
