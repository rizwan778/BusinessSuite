using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace BusinessSuite.Web.Models.Account;

/// <summary>
/// The sign-up form. The browser sends it as JSON to <c>POST /api/auth/register</c>
/// (wwwroot/js/register.js); these attributes drive client-side validation only, and the
/// API validates everything again.
/// </summary>
public sealed class RegisterViewModel
{
    [Required(ErrorMessage = "Enter your e-mail address.")]
    [EmailAddress(ErrorMessage = "Enter a valid e-mail address.")]
    [StringLength(256)]
    [Display(Name = "E-mail")]
    public string Email { get; set; } = string.Empty;

    [Required(ErrorMessage = "Enter a password.")]
    [StringLength(128)]
    [DataType(DataType.Password)]
    [Display(Name = "Password")]
    public string Password { get; set; } = string.Empty;

    /// <summary>Password rules shown under the field, built from the configured password policy.</summary>
    [BindNever]
    public string PasswordHint { get; init; } = string.Empty;
}
