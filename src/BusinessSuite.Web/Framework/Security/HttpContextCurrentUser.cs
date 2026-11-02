using System.Security.Claims;
using BusinessSuite.Application.Common.Abstractions;
using BusinessSuite.Application.Common.Security;

namespace BusinessSuite.Web.Framework.Security;

/// <summary>
/// <see cref="ICurrentUser"/> for web requests: reads the signed-in user from the authenticated
/// principal (sign-in cookie or bearer token) and the per-request access claims added by
/// <see cref="AccessClaimsTransformation"/>. Never trusts ids sent in forms or query strings.
/// </summary>
internal sealed class HttpContextCurrentUser(IHttpContextAccessor httpContextAccessor) : ICurrentUser
{
    private ClaimsPrincipal? Principal => httpContextAccessor.HttpContext?.User;

    /// <inheritdoc />
    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated ?? false;

    /// <inheritdoc />
    public Guid? UserId =>
        IsAuthenticated && Guid.TryParse(Principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
            ? id
            : null;

    /// <inheritdoc />
    public string? UserName => IsAuthenticated ? Principal?.Identity?.Name : null;

    /// <inheritdoc />
    public string? DisplayName => IsAuthenticated ? Principal?.FindFirstValue(AppClaimTypes.DisplayName) : null;

    /// <inheritdoc />
    public Guid? OrganizationId =>
        IsAuthenticated && Guid.TryParse(Principal?.FindFirstValue(AppClaimTypes.OrganizationId), out var id)
            ? id
            : null;

    /// <inheritdoc />
    public string? OrganizationRole => IsAuthenticated ? Principal?.FindFirstValue(AppClaimTypes.OrganizationRole) : null;

    /// <inheritdoc />
    public bool IsSuperAdmin => IsAuthenticated && Principal!.HasClaim(AppClaimTypes.SuperAdmin, "true");

    /// <inheritdoc />
    public bool HasPermission(string permission) => IsAuthenticated && Principal!.HasClaim(AppClaimTypes.Permission, permission);
}
