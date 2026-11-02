using BusinessSuite.Application.Common.Results;

namespace BusinessSuite.Application.Features.Accounts;

/// <summary>Creates new user accounts (self-service sign-up).</summary>
/// <remarks>
/// Implemented in Infrastructure on top of ASP.NET Core Identity, so business code never
/// depends on Identity types.
/// </remarks>
public interface IUserRegistrationService
{
    /// <summary>
    /// Creates an account whose sign-in name is <see cref="RegisterUserCommand.Email"/>.
    /// </summary>
    /// <returns>
    /// The new user, or a failure: <see cref="AccountErrors.DuplicateEmail"/> (Conflict), or an invalid
    /// e-mail, weak password or other rejection (Validation; see <see cref="AccountErrors"/>).
    /// </returns>
    Task<Result<RegisteredUser>> RegisterAsync(RegisterUserCommand command, CancellationToken cancellationToken = default);
}
