using System.ComponentModel.DataAnnotations;
using BusinessSuite.Application.Features.Accounts;

namespace BusinessSuite.Web.Models.Api.Auth;

/// <summary>JSON body of <c>POST /api/v1/auth/reset-password</c>.</summary>
public sealed class ResetPasswordRequest
{
    [Required(ErrorMessage = "Enter your e-mail address.")]
    [EmailAddress(ErrorMessage = "Enter a valid e-mail address.")]
    [StringLength(256)]
    public string Email { get; set; } = string.Empty;

    /// <summary>The code from the password-reset or account set-up e-mail.</summary>
    [Required(ErrorMessage = "Enter the code from the e-mail.")]
    [StringLength(2048)]
    public string Code { get; set; } = string.Empty;

    /// <summary>Checked against the password policy (by default 12+ characters with upper, lower, digit and symbol).</summary>
    [Required(ErrorMessage = "Enter a new password.")]
    [StringLength(128)]
    public string NewPassword { get; set; } = string.Empty;

    public ResetPasswordCommand ToCommand() => new(Email, Code, NewPassword);
}
