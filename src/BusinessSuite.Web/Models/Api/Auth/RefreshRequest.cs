using System.ComponentModel.DataAnnotations;

namespace BusinessSuite.Web.Models.Api.Auth;

/// <summary>JSON body of <c>POST /api/auth/refresh</c>.</summary>
public sealed class RefreshRequest
{
    /// <summary>The <c>refreshToken</c> returned by login or by a previous refresh.</summary>
    [Required(ErrorMessage = "Send the refresh token.")]
    [StringLength(8192)]
    public string RefreshToken { get; set; } = string.Empty;
}
