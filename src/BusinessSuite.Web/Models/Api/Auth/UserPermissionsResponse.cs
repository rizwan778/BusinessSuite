using BusinessSuite.Application.Common.Security;

namespace BusinessSuite.Web.Models.Api.Auth;

/// <summary>Response of <c>GET /api/v1/auth/me/permissions</c>.</summary>
/// <param name="UserId">The signed-in user.</param>
/// <param name="Email">Sign-in e-mail address.</param>
/// <param name="DisplayName">Friendly name, if set.</param>
/// <param name="IsSuperAdmin"><see langword="true"/> for a Super Admin: every permission in every organization.</param>
/// <param name="PlatformRoles">Platform roles, e.g. <c>SuperAdmin</c>, <c>PlatformAdministrator</c>.</param>
/// <param name="Organization">The organization this request acts in; <see langword="null"/> when none is selected.</param>
/// <param name="Organizations">Every organization the user is an active member of (for an organization switcher).</param>
/// <param name="Permissions">Everything the user may do in this request, sorted.</param>
public sealed record UserPermissionsResponse(
    Guid UserId,
    string Email,
    string? DisplayName,
    bool IsSuperAdmin,
    IReadOnlyList<string> PlatformRoles,
    OrganizationResponse? Organization,
    IReadOnlyList<OrganizationResponse> Organizations,
    IReadOnlyList<string> Permissions)
{
    public static UserPermissionsResponse From(UserAccess access, string email, string? displayName)
    {
        ArgumentNullException.ThrowIfNull(access);

        return new UserPermissionsResponse(
            access.UserId,
            email,
            displayName,
            access.IsSuperAdmin,
            access.PlatformRoles,
            access.Organization is { } organization ? OrganizationResponse.From(organization) : null,
            access.Memberships.Select(OrganizationResponse.From).ToArray(),
            access.Permissions);
    }
}

/// <summary>An organization and the user's role in it (<see langword="null"/> for a Super Admin who is not a member).</summary>
public sealed record OrganizationResponse(Guid Id, string Name, string? Role)
{
    public static OrganizationResponse From(OrganizationSummary organization)
    {
        ArgumentNullException.ThrowIfNull(organization);
        return new OrganizationResponse(organization.OrganizationId, organization.Name, organization.Role);
    }
}
