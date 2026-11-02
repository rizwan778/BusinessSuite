using System.Buffers.Text;
using System.Text;
using BusinessSuite.Application.Common.Abstractions;
using BusinessSuite.Application.Common.Options;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BusinessSuite.Infrastructure.Identity;

/// <summary>
/// Creates a password-reset code (Identity's reset token) and e-mails it to the user. The code is only ever in
/// the e-mail: never returned by the API and never logged.
/// </summary>
/// <remarks>
/// The token is base64url-encoded so it survives copy/paste and URLs. It works once (the reset changes the
/// security stamp), and expires after <see cref="DataProtectionTokenProviderOptions.TokenLifespan"/> (1 day by default).
/// </remarks>
internal sealed class PasswordResetMailer(
    UserManager<ApplicationUser> userManager,
    IEmailSender emailSender,
    IOptions<ApplicationOptions> applicationOptions,
    IOptions<DataProtectionTokenProviderOptions> tokenOptions,
    ILogger<PasswordResetMailer> logger)
{
    /// <summary>E-mails a reset code. <paramref name="isNewAccount"/> words it as "choose your password".</summary>
    public async Task SendAsync(ApplicationUser user, bool isNewAccount, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        var token = await userManager.GeneratePasswordResetTokenAsync(user);
        var code = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(token));
        var product = applicationOptions.Value.ProductName;
        var hours = Math.Max(1, (int)Math.Round(tokenOptions.Value.TokenLifespan.TotalHours));

        var greeting = string.IsNullOrWhiteSpace(user.DisplayName) ? "Hello," : $"Hello {user.DisplayName},";
        var reason = isNewAccount
            ? $"An account has been created for you in {product}. Choose your password to sign in."
            : $"A password reset was requested for your {product} account by you or an administrator.";

        var body = $"""
            {greeting}

            {reason}

            E-mail: {user.Email}
            Code:   {code}

            Enter the code and your new password in {product} (API: POST /api/v1/auth/reset-password).
            The code works once and expires in {hours} hour(s).

            If you did not expect this e-mail, ignore it: your password has not changed.
            """;

        var subject = isNewAccount ? $"Set up your {product} password" : $"Reset your {product} password";
        await emailSender.SendAsync(new EmailMessage(user.Email!, subject, body), cancellationToken);

        logger.LogInformation("Password {Purpose} code e-mailed to user {UserId}.", isNewAccount ? "set-up" : "reset", user.Id);
    }

    /// <summary>Turns an e-mailed code back into Identity's token; <see langword="false"/> when it is not valid base64url.</summary>
    public static bool TryDecode(string? code, out string token)
    {
        token = string.Empty;
        if (string.IsNullOrWhiteSpace(code))
        {
            return false;
        }

        try
        {
            token = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(code.Trim()));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
