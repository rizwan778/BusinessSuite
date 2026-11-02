using System.Security.Claims;
using BusinessSuite.Application.Common.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace BusinessSuite.Infrastructure.Identity;

/// <summary>
/// Builds the claims stored in the authentication cookie when a user signs in.
/// </summary>
/// <remarks>
/// Identity already adds the user id, user name, security stamp and roles. This factory adds
/// the display name so the UI can greet the user without a database query on every page.
/// Keep additions small: the cookie travels with every request.
/// </remarks>
public sealed class ApplicationUserClaimsPrincipalFactory(
    UserManager<ApplicationUser> userManager,
    RoleManager<ApplicationRole> roleManager,
    IOptions<IdentityOptions> options)
    : UserClaimsPrincipalFactory<ApplicationUser, ApplicationRole>(userManager, roleManager, options)
{
    /// <inheritdoc />
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(ApplicationUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);

        if (!string.IsNullOrWhiteSpace(user.DisplayName))
        {
            identity.AddClaim(new Claim(AppClaimTypes.DisplayName, user.DisplayName));
        }

        return identity;
    }
}
