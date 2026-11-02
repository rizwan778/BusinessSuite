using BusinessSuite.Application.Common.Results;
using BusinessSuite.Application.Features.Accounts;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BusinessSuite.Infrastructure.Identity;

/// <summary>
/// <see cref="IUserRegistrationService"/> backed by ASP.NET Core Identity's <see cref="UserManager{TUser}"/>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Identity enforces the password policy and unique e-mail addresses (configured in
/// <c>InfrastructureServiceCollectionExtensions</c>); its errors become <see cref="Result"/> failures.</item>
/// <item>Two simultaneous sign-ups with the same e-mail can both pass Identity's duplicate check;
/// the database's unique index stops the second one, which is reported as a duplicate, not a crash.</item>
/// <item>Logs contain user ids and Identity error codes only, never e-mail addresses or passwords.</item>
/// </list>
/// </remarks>
internal sealed class UserRegistrationService(
    UserManager<ApplicationUser> userManager,
    ILogger<UserRegistrationService> logger) : IUserRegistrationService
{
    // SQL Server: "Cannot insert duplicate key row" (unique index) and "Violation of UNIQUE KEY constraint".
    private const int UniqueIndexViolation = 2601;
    private const int UniqueConstraintViolation = 2627;

    /// <inheritdoc />
    public async Task<Result<RegisteredUser>> RegisterAsync(RegisterUserCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();

        var email = command.Email.Trim();
        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            DisplayName = string.IsNullOrWhiteSpace(command.DisplayName) ? null : command.DisplayName.Trim(),
        };

        IdentityResult result;
        try
        {
            result = await userManager.CreateAsync(user, command.Password);
        }
        catch (DbUpdateException exception) when (IsUniqueKeyViolation(exception))
        {
            logger.LogWarning("Registration rejected: a concurrent request created an account with the same e-mail address.");
            return AccountErrors.DuplicateEmail;
        }

        if (!result.Succeeded)
        {
            var errorCodes = string.Join(", ", result.Errors.Select(error => error.Code));
            logger.LogInformation("Registration rejected by Identity: {IdentityErrorCodes}.", errorCodes);
            return ToError(result.Errors.ToList());
        }

        logger.LogInformation("User {UserId} registered.", user.Id);
        return new RegisteredUser(user.Id, email, user.DisplayName);
    }

    /// <summary>Turns Identity's errors into one <see cref="Error"/>, keeping every message for the user.</summary>
    internal static Error ToError(IReadOnlyCollection<IdentityError> errors)
    {
        if (errors.Any(error => error.Code is nameof(IdentityErrorDescriber.DuplicateEmail) or nameof(IdentityErrorDescriber.DuplicateUserName)))
        {
            return AccountErrors.DuplicateEmail;
        }

        var description = string.Join(" ", errors.Select(error => error.Description).Distinct(StringComparer.Ordinal));

        if (errors.All(error => error.Code.StartsWith("Password", StringComparison.Ordinal)))
        {
            return AccountErrors.WeakPassword(description);
        }

        // The user name is the e-mail address, so an invalid user name is an invalid e-mail.
        if (errors.All(error => error.Code is nameof(IdentityErrorDescriber.InvalidEmail) or nameof(IdentityErrorDescriber.InvalidUserName)))
        {
            return AccountErrors.InvalidEmail(description);
        }

        return AccountErrors.RegistrationRejected(description);
    }

    internal static bool IsUniqueKeyViolation(DbUpdateException exception) =>
        exception.InnerException is SqlException { Number: UniqueIndexViolation or UniqueConstraintViolation };
}
