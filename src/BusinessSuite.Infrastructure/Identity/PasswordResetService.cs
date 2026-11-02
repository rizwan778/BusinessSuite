using BusinessSuite.Application.Common.Results;
using BusinessSuite.Application.Features.Accounts;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

namespace BusinessSuite.Infrastructure.Identity;

/// <summary>
/// <see cref="IPasswordResetService"/> on ASP.NET Core Identity. Codes come from <see cref="PasswordResetMailer"/>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Unknown e-mail, malformed, wrong, used or expired code: the same <see cref="AccountErrors.InvalidResetCode"/>.</item>
/// <item>The password policy is only reported once the code is valid, so it reveals nothing about accounts.</item>
/// <item>Success changes the security stamp (Identity) and revokes every API session: all old tokens stop working.</item>
/// </list>
/// </remarks>
internal sealed class PasswordResetService(
    UserManager<ApplicationUser> userManager,
    IUserSessionStore sessions,
    ILogger<PasswordResetService> logger) : IPasswordResetService
{
    public async Task<Result> ResetPasswordAsync(ResetPasswordCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var user = await userManager.FindByEmailAsync(command.Email.Trim());
        if (user is null || !PasswordResetMailer.TryDecode(command.Code, out var token))
        {
            logger.LogInformation("Password reset rejected: unknown e-mail address or malformed code.");
            return AccountErrors.InvalidResetCode;
        }

        var result = await userManager.ResetPasswordAsync(user, token, command.NewPassword);
        if (!result.Succeeded)
        {
            if (result.Errors.Any(error => error.Code == nameof(IdentityErrorDescriber.InvalidToken)))
            {
                logger.LogInformation("Password reset rejected for user {UserId}: the code is wrong, used or expired.", user.Id);
                return AccountErrors.InvalidResetCode;
            }

            logger.LogInformation("Password reset rejected for user {UserId} by Identity: {IdentityErrorCodes}.",
                user.Id, string.Join(", ", result.Errors.Select(error => error.Code)));
            return UserRegistrationService.ToError(result.Errors.ToList());
        }

        // Receiving the code proves the user owns the mailbox; they also chose a new password, so lift any lockout.
        if (!user.EmailConfirmed)
        {
            user.EmailConfirmed = true;
            await userManager.UpdateAsync(user);
        }

        await userManager.ResetAccessFailedCountAsync(user);
        await userManager.SetLockoutEndDateAsync(user, null);
        var revoked = await sessions.RevokeAllAsync(user.Id, cancellationToken);

        logger.LogInformation("User {UserId} reset their password; {RevokedCount} API session(s) revoked.", user.Id, revoked);
        return Result.Success();
    }
}
