namespace BusinessSuite.Application.Features.Accounts;

/// <summary>The account created by <see cref="IUserRegistrationService.RegisterAsync"/>.</summary>
public sealed record RegisteredUser(Guid UserId, string Email, string? DisplayName);
