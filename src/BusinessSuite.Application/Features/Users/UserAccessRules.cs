using BusinessSuite.Application.Common.Abstractions;
using BusinessSuite.Application.Common.Results;
using BusinessSuite.Domain.Organizations;

namespace BusinessSuite.Application.Features.Users;

/// <summary>A kind of change to a user, for <see cref="UserAccessRules.CheckChange"/>.</summary>
public enum UserChange
{
    /// <summary>Display name, phone number.</summary>
    Profile,

    /// <summary>Organization role.</summary>
    Role,

    /// <summary>Department or group.</summary>
    Department,

    /// <summary>Activate or deactivate.</summary>
    Status,

    /// <summary>E-mail a password-reset code.</summary>
    PasswordReset,
}

/// <summary>What the rules need to know about the user being changed.</summary>
/// <param name="UserId">The user.</param>
/// <param name="HasPlatformRole">The user holds a platform role (Super Admin or Platform Administrator).</param>
/// <param name="Role">The name of the user's role in the organization the request acts in; <see langword="null"/> if not a member.</param>
/// <param name="RolePermissions">The effective permissions of that role; <see langword="null"/> if not a member.</param>
public sealed record ManagedUser(Guid UserId, bool HasPlatformRole, string? Role, IReadOnlyCollection<string>? RolePermissions = null);

/// <summary>A role being assigned, resolved among the roles available to the organization.</summary>
/// <param name="Id">The role's id.</param>
/// <param name="Name">The role's name.</param>
/// <param name="IsOwner">The Owner system role.</param>
/// <param name="Permissions">The role's effective permissions.</param>
public sealed record AssignableRole(Guid Id, string Name, bool IsOwner, IReadOnlyCollection<string> Permissions);

/// <summary>
/// Who may see and change which users. Pure and deterministic, so every rule is unit-tested.
/// </summary>
/// <remarks>
/// <list type="number">
/// <item><b>Organization scope:</b> organization users work in <c>ICurrentUser.OrganizationId</c> only (resolved on the
/// server from their memberships). A client-supplied organization id never widens it.</item>
/// <item><b>Super Admins</b> may list every user or filter by any organization.</item>
/// <item><b>No self-service escalation:</b> nobody changes their own role or status.</item>
/// <item><b>Platform accounts</b> (Super Admin, Platform Administrator) are changed by Super Admins only.</item>
/// <item><b>Owners:</b> only an Owner (or a Super Admin) assigns the Owner role or changes an Owner.</item>
/// <item><b>No escalation through roles:</b> a non-Super Admin only assigns roles whose permissions they hold, and only
/// changes users whose role grants nothing beyond their own permissions.</item>
/// <item><b>Available roles only:</b> a role must be available to the organization (system/platform roles the Super Admin
/// enabled, or the organization's own custom roles); the service resolves it and unknown ones are invalid.</item>
/// <item>The last active Owner cannot be demoted or deactivated (checked against the database by the service).</item>
/// </list>
/// </remarks>
public static class UserAccessRules
{
    /// <summary>
    /// The organization a user list is limited to: <see langword="null"/> means every organization (Super Admins only).
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
            return UserErrors.OrganizationRequired;
        }

        if (requestedOrganizationId is { } requested && requested != organizationId)
        {
            return UserErrors.OrganizationFilterNotAllowed;
        }

        return organizationId;
    }

    /// <summary>
    /// The organization whose users the caller may READ one by one: their own, or <see langword="null"/> (any) for a
    /// Super Admin.
    /// </summary>
    public static Result<Guid?> ReadScope(ICurrentUser actor) => ListScope(actor, requestedOrganizationId: null);

    /// <summary>The organization a membership change applies to (the one the request acts in).</summary>
    public static Result<Guid> ActingOrganization(ICurrentUser actor)
    {
        ArgumentNullException.ThrowIfNull(actor);

        return actor.OrganizationId is { } organizationId
            ? organizationId
            : UserErrors.OrganizationRequired;
    }

    /// <summary>
    /// May <paramref name="actor"/> make <paramref name="change"/> to <paramref name="target"/>?
    /// The caller has already checked that the target is visible (a member of the actor's organization).
    /// </summary>
    /// <param name="actor">The current user.</param>
    /// <param name="target">The user being changed.</param>
    /// <param name="change">What is changed.</param>
    /// <param name="newRole">For <see cref="UserChange.Role"/>: the role being assigned.</param>
    public static Result CheckChange(ICurrentUser actor, ManagedUser target, UserChange change, AssignableRole? newRole = null)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(target);

        if (actor.UserId == target.UserId)
        {
            return change is UserChange.Role or UserChange.Status
                ? UserErrors.CannotChangeOwnAccess
                : Result.Success();
        }

        if (actor.IsSuperAdmin)
        {
            return Result.Success();
        }

        if (target.HasPlatformRole)
        {
            return UserErrors.PlatformAccountProtected;
        }

        if (!IsOwner(actor) && string.Equals(target.Role, OrganizationRoles.Owner, StringComparison.Ordinal))
        {
            return UserErrors.OwnerRequired;
        }

        if (target.RolePermissions is { } held && !held.All(actor.HasPermission))
        {
            return UserErrors.RoleExceedsYourPermissions;
        }

        return newRole is null ? Result.Success() : CheckAssign(actor, newRole);
    }

    /// <summary>May <paramref name="actor"/> create a user with <paramref name="role"/> in their organization?</summary>
    public static Result CheckCreate(ICurrentUser actor, AssignableRole role) => CheckAssign(actor, role);

    /// <summary>
    /// May <paramref name="actor"/> give someone <paramref name="role"/>? The Owner role needs an Owner; any other role
    /// must grant nothing the actor does not hold. Super Admins may assign any available role.
    /// </summary>
    public static Result CheckAssign(ICurrentUser actor, AssignableRole role)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(role);

        if (actor.IsSuperAdmin)
        {
            return Result.Success();
        }

        if (role.IsOwner && !IsOwner(actor))
        {
            return UserErrors.OwnerRequired;
        }

        return role.Permissions.All(actor.HasPermission) ? Result.Success() : UserErrors.RoleExceedsYourPermissions;
    }

    private static bool IsOwner(ICurrentUser actor) =>
        string.Equals(actor.OrganizationRole, OrganizationRoles.Owner, StringComparison.Ordinal);
}
