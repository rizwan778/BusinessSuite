using System.ComponentModel.DataAnnotations;
using BusinessSuite.Application.Features.Accounts;

namespace BusinessSuite.Web.Models.Api.Auth;

/// <summary>
/// JSON body of <c>POST /api/auth/register</c>. Only these properties can be bound from the
/// request, which prevents over-posting.
/// </summary>
public sealed class RegisterRequest
{
    [Required(ErrorMessage = "Enter your e-mail address.")]
    [EmailAddress(ErrorMessage = "Enter a valid e-mail address.")]
    [StringLength(256)]
    public string Email { get; set; } = string.Empty;

    /// <summary>Checked against the password policy (by default 12+ characters with upper, lower, digit and symbol).</summary>
    [Required(ErrorMessage = "Enter a password.")]
    [StringLength(128)]
    public string Password { get; set; } = string.Empty;

    [StringLength(100)]
    public string? DisplayName { get; set; }

    public RegisterUserCommand ToCommand() => new(Email, Password, DisplayName);
}
