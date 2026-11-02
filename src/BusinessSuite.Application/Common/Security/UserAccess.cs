namespace BusinessSuite.Application.Common.Security;

/// <summary>
/// What one user may do in one request: platform roles, the organization they act in, their role
/// there and the resulting permissions. Built by <see cref="UserAccessCalculator"/>.
/// </summary>
public sealed record UserAccess(
    Guid UserId,
    bool IsSuperAdmin,
    IReadOnlyList<string> PlatformRoles,
    OrganizationSummary? Organization,
    OrganizationSelection Selection,
    IReadOnlyList<OrganizationSummary> Memberships,
    IReadOnlyList<string> Permissions)
{
    /// <summary><see langword="true"/> when the user holds <paramref name="permission"/> for this request.</summary>
    public bool HasPermission(string permission) => Permissions.Contains(permission, StringComparer.Ordinal);
}

/// <summary>An organization the user can act in.</summary>
/// <param name="OrganizationId">The organization's id.</param>
/// <param name="Name">The organization's display name.</param>
/// <param name="Role">
/// The name of the user's role there; <see langword="null"/> for a Super Admin who is not a member. System role names
/// are reserved, so "Owner" always means the Owner system role.
/// </param>
/// <param name="RolePermissions">
/// The permissions the role grants, as stored in the database. <see langword="null"/>: the code defaults of the system
/// role named <paramref name="Role"/> (<see cref="Security.RolePermissions"/>).
/// </param>
public sealed record OrganizationSummary(
    Guid OrganizationId,
    string Name,
    string? Role,
    IReadOnlyCollection<string>? RolePermissions = null);

/// <summary>How the organization of a request was determined.</summary>
public enum OrganizationSelection
{
    /// <summary>No organization: none requested and the user is not a member of exactly one.</summary>
    None = 0,

    /// <summary>The requested organization, or the user's only one.</summary>
    Selected = 1,

    /// <summary>An organization was requested that the user may not act in (not a member, inactive, or unknown).</summary>
    Denied = 2,
}
