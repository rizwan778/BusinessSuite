using BusinessSuite.Application.Common.Results;

namespace BusinessSuite.Application.Features.Accounts;

/// <summary>Completes a password reset with the code e-mailed to the user.</summary>
/// <remarks>
/// Codes are sent by user management (<c>POST /api/v1/users/{id}/reset-password</c>) and when a user is created
/// without a password. A code works once and expires (Identity's token lifespan, 1 day by default).
/// </remarks>
public interface IPasswordResetService
{
    /// <summary>
    /// Sets the new password, confirms the e-mail address, clears any lockout and signs the user out everywhere.
    /// </summary>
    /// <returns>
    /// Success, <see cref="AccountErrors.InvalidResetCode"/> (unknown e-mail, wrong, used or expired code: deliberately
    /// the same), or a weak-password failure.
    /// </returns>
    Task<Result> ResetPasswordAsync(ResetPasswordCommand command, CancellationToken cancellationToken = default);
}

/// <summary>Input for <see cref="IPasswordResetService.ResetPasswordAsync"/>.</summary>
/// <param name="Email">The account's e-mail address.</param>
/// <param name="Code">The code from the e-mail.</param>
/// <param name="NewPassword">The new password; checked against the password policy.</param>
public sealed record ResetPasswordCommand(string Email, string Code, string NewPassword)
{
    /// <summary>Leaves the code and password out (a record's default <c>ToString()</c> prints every property).</summary>
    public override string ToString() => $"{nameof(ResetPasswordCommand)} {{ Email = {Email} }}";
}
