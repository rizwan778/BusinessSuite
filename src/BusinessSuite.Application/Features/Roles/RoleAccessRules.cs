using BusinessSuite.Application.Common.Abstractions;
using BusinessSuite.Application.Common.Results;
using BusinessSuite.Application.Common.Security;

namespace BusinessSuite.Application.Features.Roles;

/// <summary>What the rules need to know about a role.</summary>
/// <param name="Id">The role.</param>
/// <param name="OrganizationId">Owning organization of a custom organization role; <see langword="null"/> for platform roles.</param>
/// <param name="IsSystem">A built-in role.</param>
/// <param name="IsOwner">The Owner system role.</param>
/// <param name="Permissions">The role's EFFECTIVE permissions (see <see cref="RoleAccessRules.EffectivePermissions"/>).</param>
public sealed record RoleFacts(Guid Id, Guid? OrganizationId, bool IsSystem, bool IsOwner, IReadOnlyCollection<string> Permissions);

/// <summary>
/// Who may see and change which roles. Pure and deterministic, so every rule is unit-tested.
/// </summary>
/// <remarks>
/// <list type="number">
/// <item><b>Organization scope:</b> organization users see the roles available to their organization
/// (<c>ICurrentUser.OrganizationId</c>, resolved on the server) and change only their organization's custom roles.</item>
/// <item><b>Super Admins</b> manage shared (platform) roles, including the system roles' permissions, and decide which
/// platform roles each organization may use.</item>
/// <item><b>No escalation:</b> a non-Super Admin can only grant permissions they hold, and cannot change a role that
/// grants more than they hold.</item>
/// <item>Roles grant organization permissions only; <c>platform.*</c> can never be granted by a role.</item>
/// <item>The Owner's permissions are fixed (every organization permission); system roles are never renamed or deleted
/// (enforced by the service and the domain).</item>
/// </list>
/// </remarks>
public static class RoleAccessRules
{
    /// <summary>
    /// The organization whose available roles are listed: <see langword="null"/> means every role (Super Admins only).
    /// </summary>
    public static Result<Guid?> ListScope(ICurrentUser actor, Guid? requestedOrganizationId)
    {
        ArgumentNullException.ThrowIfNull(actor);

        if (actor.IsSuperAdmin)
        {
            return requestedOrganizationId;
        }

        if (actor.OrganizationId is not { } organizationId)
        {
            return RoleErrors.OrganizationRequired;
        }

        if (requestedOrganizationId is { } requested && requested != organizationId)
        {
            return RoleErrors.OrganizationFilterNotAllowed;
        }

        return organizationId;
    }

    /// <summary>May <paramref name="actor"/> create a role in <paramref name="scope"/>?</summary>
    public static Result CheckCreate(ICurrentUser actor, RoleScope scope)
    {
        ArgumentNullException.ThrowIfNull(actor);

        if (scope == RoleScope.Platform)
        {
            return actor.IsSuperAdmin ? Result.Success() : RoleErrors.SuperAdminRequired;
        }

        return actor.OrganizationId is null ? RoleErrors.OrganizationRequired : Result.Success();
    }

    /// <summary>
    /// May <paramref name="actor"/> change <paramref name="role"/> (name, permissions, deletion)? The caller has already
    /// checked the role is visible to the actor.
    /// </summary>
    public static Result CheckManage(ICurrentUser actor, RoleFacts role)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(role);

        if (actor.IsSuperAdmin)
        {
            return Result.Success();
        }

        if (role.OrganizationId is null)
        {
            return RoleErrors.SuperAdminRequired;
        }

        if (role.OrganizationId != actor.OrganizationId)
        {
            return RoleErrors.NotFound;
        }

        return role.Permissions.All(actor.HasPermission) ? Result.Success() : RoleErrors.RoleExceedsYourPermissions;
    }

    /// <summary>
    /// May <paramref name="actor"/> give a role exactly <paramref name="permissions"/>? Every one must be an organization
    /// permission, and non-Super Admins must hold each themselves.
    /// </summary>
    public static Result CheckPermissions(ICurrentUser actor, IReadOnlyCollection<string> permissions)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(permissions);

        var unknown = permissions.Where(permission => !Permissions.IsOrganizationScoped(permission)).Distinct(StringComparer.Ordinal).ToList();
        if (unknown.Count > 0)
        {
            return RoleErrors.UnknownPermissions(unknown);
        }

        if (!actor.IsSuperAdmin)
        {
            var notHeld = permissions.Where(permission => !actor.HasPermission(permission)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            if (notHeld.Count > 0)
            {
                return RoleErrors.CannotGrantPermission(notHeld);
            }
        }

        return Result.Success();
    }

    /// <summary>
    /// What a role actually grants: every organization permission for the Owner; otherwise the stored permissions that
    /// still exist and are organization-scoped, sorted.
    /// </summary>
    public static IReadOnlyList<string> EffectivePermissions(bool isOwner, IEnumerable<string> storedPermissions)
    {
        ArgumentNullException.ThrowIfNull(storedPermissions);

        return isOwner
            ? Permissions.OrganizationScoped
            : storedPermissions.Where(Permissions.IsOrganizationScoped).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }
}
