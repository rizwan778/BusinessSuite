using System.ComponentModel.DataAnnotations;

namespace BusinessSuite.Web.Models.Account;

/// <summary>
/// Data posted by the sign-in form. Only these properties can be bound from the request,
/// which prevents over-posting.
/// </summary>
public sealed class LoginViewModel
{
    [Required(ErrorMessage = "Enter your e-mail address.")]
    [EmailAddress(ErrorMessage = "Enter a valid e-mail address.")]
    [StringLength(256)]
    [Display(Name = "E-mail")]
    public string Email { get; set; } = string.Empty;

    [Required(ErrorMessage = "Enter your password.")]
    [StringLength(128)]
    [DataType(DataType.Password)]
    [Display(Name = "Password")]
    public string Password { get; set; } = string.Empty;

    [Display(Name = "Keep me signed in on this device")]
    public bool RememberMe { get; set; }

    /// <summary>Page to return to after signing in. Validated as a local URL before use.</summary>
    public string? ReturnUrl { get; set; }
}
