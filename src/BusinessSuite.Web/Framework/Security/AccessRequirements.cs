using BusinessSuite.Application.Common.Security;
using Microsoft.AspNetCore.Authorization;

namespace BusinessSuite.Web.Framework.Security;

/// <summary>The user must hold <paramref name="Permission"/> (one of <see cref="Permissions"/>) in the current organization.</summary>
public sealed record PermissionRequirement(string Permission) : IAuthorizationRequirement;

/// <summary>The user must hold at least one of <paramref name="AnyOf"/> (each one of <see cref="Permissions"/>).</summary>
public sealed record AnyPermissionRequirement(IReadOnlyList<string> AnyOf) : IAuthorizationRequirement;

/// <summary>The user's role in the current organization must be one of <paramref name="Roles"/>.</summary>
public sealed record OrganizationRoleRequirement(IReadOnlyList<string> Roles) : IAuthorizationRequirement;

/// <summary>The user must be a Super Admin.</summary>
public sealed record SuperAdminRequirement : IAuthorizationRequirement;

/// <summary>
/// Permission-based authorization: <c>[RequirePermission(Permissions.Invoices.Create)]</c> on an action or controller.
/// Several attributes: ALL permissions are required. Super Admins always pass.
/// </summary>
/// <remarks>
/// Combine with an <c>[Authorize(AuthenticationSchemes = ...)]</c> that says HOW the user signs in.
/// Prefer permissions over roles: a new role then needs no controller change.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
public sealed class RequirePermissionAttribute(string permission) : Attribute, IAuthorizationRequirementData
{
    public string Permission { get; } = permission;

    public IEnumerable<IAuthorizationRequirement> GetRequirements() => [new PermissionRequirement(Permission)];
}

/// <summary>
/// Permission-based authorization where ANY of several permissions is enough:
/// <c>[RequireAnyPermission(Permissions.Organization.View, Permissions.Platform.OrganizationsManage)]</c>, e.g. an endpoint
/// shared by organization users and platform operators. Super Admins always pass. To require several permissions at
/// once, stack <see cref="RequirePermissionAttribute"/>s instead.
/// </summary>
/// <remarks>
/// Whatever the permissions let a caller through to, the Application service must still decide WHICH data the caller
/// reaches (an organization user holding only the organization permission must stay inside their organization).
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
public sealed class RequireAnyPermissionAttribute(params string[] permissions) : Attribute, IAuthorizationRequirementData
{
    public IReadOnlyList<string> Permissions { get; } = permissions;

    public IEnumerable<IAuthorizationRequirement> GetRequirements() => [new AnyPermissionRequirement(Permissions)];
}

/// <summary>
/// Role-based authorization inside an organization: <c>[RequireOrganizationRole(OrganizationRoles.Owner, OrganizationRoles.Administrator)]</c>.
/// The user's role in the current organization must be ANY of the listed roles. Super Admins always pass.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
public sealed class RequireOrganizationRoleAttribute(params string[] roles) : Attribute, IAuthorizationRequirementData
{
    public IReadOnlyList<string> Roles { get; } = roles;

    public IEnumerable<IAuthorizationRequirement> GetRequirements() => [new OrganizationRoleRequirement(Roles)];
}

/// <summary>Only Super Admins: <c>[RequireSuperAdmin]</c>.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class RequireSuperAdminAttribute : Attribute, IAuthorizationRequirementData
{
    public IEnumerable<IAuthorizationRequirement> GetRequirements() => [new SuperAdminRequirement()];
}

/// <summary>Why access was denied, with a stable error code for API clients.</summary>
public sealed class AccessDeniedReason(IAuthorizationHandler handler, string code, string message)
    : AuthorizationFailureReason(handler, message)
{
    /// <summary>E.g. <c>Auth.PermissionDenied</c>.</summary>
    public string Code { get; } = code;
}
