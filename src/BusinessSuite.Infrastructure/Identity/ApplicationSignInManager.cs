using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BusinessSuite.Infrastructure.Identity;

/// <summary>
/// Identity's sign-in manager plus one BusinessSuite rule: deactivated users cannot sign in.
/// </summary>
/// <remarks>
/// <see cref="SignInManager{TUser}.CanSignInAsync"/> is called by every sign-in path
/// (password, two-factor, external login), so enforcing the rule here covers all of them.
/// The sign-in then fails with <c>SignInResult.NotAllowed</c>, and the login page shows the
/// same generic message as for a wrong password (no account-state disclosure).
/// </remarks>
public sealed class ApplicationSignInManager(
    UserManager<ApplicationUser> userManager,
    IHttpContextAccessor contextAccessor,
    IUserClaimsPrincipalFactory<ApplicationUser> claimsFactory,
    IOptions<IdentityOptions> optionsAccessor,
    ILogger<SignInManager<ApplicationUser>> logger,
    IAuthenticationSchemeProvider schemes,
    IUserConfirmation<ApplicationUser> confirmation)
    : SignInManager<ApplicationUser>(userManager, contextAccessor, claimsFactory, optionsAccessor, logger, schemes, confirmation)
{
    /// <inheritdoc />
    public override async Task<bool> CanSignInAsync(ApplicationUser user)
    {
        ArgumentNullException.ThrowIfNull(user);

        if (!user.IsActive)
        {
            Logger.LogWarning("Sign-in blocked for user {UserId}: the account is deactivated.", user.Id);
            return false;
        }

        return await base.CanSignInAsync(user);
    }
}
