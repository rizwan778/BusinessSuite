using System.Security.Claims;
using BusinessSuite.Application.Common.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;

namespace BusinessSuite.Web.Framework.Security;

/// <summary>
/// Adds the organization claims (<see cref="AppClaimTypes.OrganizationId"/>, <see cref="AppClaimTypes.OrganizationRole"/>),
/// one <see cref="AppClaimTypes.Permission"/> claim per permission and <see cref="AppClaimTypes.SuperAdmin"/> to every
/// authenticated principal, cookie or bearer token alike.
/// </summary>
/// <remarks>
/// <para>
/// The claims are computed from the database for each request and never stored in a cookie or token, so a role
/// change, removed membership or suspended organization takes effect on the very next request, and a client
/// cannot forge them. They live in a separate identity (<see cref="AppClaimTypes.AccessIdentityType"/>).
/// </para>
/// <para>
/// Skipped for anonymous endpoints (static files, health checks, sign-in page) and unknown routes, so those
/// cost no database query.
/// </para>
/// </remarks>
internal sealed class AccessClaimsTransformation(CurrentAccessProvider accessProvider, IHttpContextAccessor httpContextAccessor)
    : IClaimsTransformation
{
    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        if (principal.Identity?.IsAuthenticated != true
            || principal.Identities.Any(identity => identity.AuthenticationType == AppClaimTypes.AccessIdentityType))
        {
            return principal;
        }

        var httpContext = httpContextAccessor.HttpContext;
        var endpoint = httpContext?.GetEndpoint();
        if (endpoint is null || endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null)
        {
            return principal;
        }

        var access = await accessProvider.GetAsync(principal, httpContext!.RequestAborted);
        if (access is null)
        {
            return principal;
        }

        var claims = new List<Claim>();
        if (access.IsSuperAdmin)
        {
            claims.Add(new Claim(AppClaimTypes.SuperAdmin, "true"));
        }

        if (access.Organization is { } organization)
        {
            claims.Add(new Claim(AppClaimTypes.OrganizationId, organization.OrganizationId.ToString()));
            if (organization.Role is not null)
            {
                claims.Add(new Claim(AppClaimTypes.OrganizationRole, organization.Role));
            }
        }

        claims.AddRange(access.Permissions.Select(permission => new Claim(AppClaimTypes.Permission, permission)));

        // Never modify the incoming principal: it may be shared (e.g. a cached cookie ticket).
        var transformed = principal.Clone();
        transformed.AddIdentity(new ClaimsIdentity(claims, AppClaimTypes.AccessIdentityType));
        return transformed;
    }
}
