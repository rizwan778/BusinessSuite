using System.ComponentModel.DataAnnotations;

namespace BusinessSuite.Web.Models.Api.Auth;

/// <summary>JSON body of <c>POST /api/auth/login</c>.</summary>
public sealed class LoginRequest
{
    [Required(ErrorMessage = "Enter your e-mail address.")]
    [EmailAddress(ErrorMessage = "Enter a valid e-mail address.")]
    [StringLength(256)]
    public string Email { get; set; } = string.Empty;

    [Required(ErrorMessage = "Enter your password.")]
    [StringLength(128)]
    public string Password { get; set; } = string.Empty;
}
