namespace BusinessSuite.Web.Models.Api.Auth;

/// <summary>
/// A user account as returned by <c>POST /api/auth/register</c> and <c>GET /api/auth/me</c>.
/// </summary>
public sealed record UserResponse(Guid UserId, string Email, string? DisplayName);
