using System.Security.Claims;
using BusinessSuite.Application.Common.Security;

namespace BusinessSuite.Web.Framework.Security;

/// <summary>
/// Resolves the <see cref="UserAccess"/> of the current request once and caches it for the rest of the request.
/// </summary>
/// <remarks>
/// The organization to act in comes from the <see cref="OrganizationHeader"/> request header and is ALWAYS
/// checked against the user's memberships in the database; a value the user may not use selects nothing
/// (<see cref="OrganizationSelection.Denied"/>). Without the header the user's only organization is used.
/// </remarks>
public sealed class CurrentAccessProvider(IUserAccessService accessService, IHttpContextAccessor httpContextAccessor)
{
    /// <summary>Request header naming the organization to act in, e.g. <c>X-Organization-Id: 3f2c...</c>.</summary>
    public const string OrganizationHeader = "X-Organization-Id";

    private readonly Dictionary<Guid, UserAccess> _cache = [];

    /// <summary>The access of <paramref name="principal"/>, or <see langword="null"/> when it carries no user id.</summary>
    public async Task<UserAccess?> GetAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);

        if (!Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
        {
            return null;
        }

        if (!_cache.TryGetValue(userId, out var access))
        {
            access = await accessService.GetAccessAsync(userId, RequestedOrganizationId(), cancellationToken);
            _cache[userId] = access;
        }

        return access;
    }

    /// <summary>The header's organization id; <see cref="Guid.Empty"/> (never a real organization) when it is not a GUID.</summary>
    private Guid? RequestedOrganizationId()
    {
        var header = httpContextAccessor.HttpContext?.Request.Headers[OrganizationHeader].ToString();
        if (string.IsNullOrWhiteSpace(header))
        {
            return null;
        }

        return Guid.TryParse(header, out var organizationId) ? organizationId : Guid.Empty;
    }
}
