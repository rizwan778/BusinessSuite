using BusinessSuite.Application.Common.Results;
using BusinessSuite.Application.Features.Accounts;
using BusinessSuite.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;

namespace BusinessSuite.Tests.Unit.Infrastructure;

/// <summary>How ASP.NET Core Identity's sign-up errors become stable API errors.</summary>
public sealed class UserRegistrationErrorTests
{
    private static readonly IdentityErrorDescriber Describer = new();

    [Fact]
    public void Duplicate_user_name_is_a_duplicate_email_conflict()
    {
        var error = UserRegistrationService.ToError([Describer.DuplicateUserName("a@example.com"), Describer.DuplicateEmail("a@example.com")]);

        Assert.Equal(AccountErrors.DuplicateEmail, error);
        Assert.Equal(ErrorType.Conflict, error.Type);
    }

    [Fact]
    public void Password_errors_are_combined_into_one_weak_password_error()
    {
        var error = UserRegistrationService.ToError([Describer.PasswordTooShort(12), Describer.PasswordRequiresDigit()]);

        Assert.Equal(AccountErrors.WeakPasswordCode, error.Code);
        Assert.Equal(ErrorType.Validation, error.Type);
        Assert.Contains(Describer.PasswordTooShort(12).Description, error.Description, StringComparison.Ordinal);
        Assert.Contains(Describer.PasswordRequiresDigit().Description, error.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Invalid_user_name_is_reported_as_an_invalid_email()
    {
        var error = UserRegistrationService.ToError([Describer.InvalidUserName("a b@example.com")]);

        Assert.Equal(AccountErrors.InvalidEmailCode, error.Code);
        Assert.Equal(ErrorType.Validation, error.Type);
    }

    [Fact]
    public void Mixed_errors_are_a_generic_rejection_listing_every_message()
    {
        var error = UserRegistrationService.ToError([Describer.InvalidEmail("x"), Describer.PasswordRequiresUpper()]);

        Assert.Equal(AccountErrors.RegistrationRejectedCode, error.Code);
        Assert.Contains(Describer.PasswordRequiresUpper().Description, error.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Session_expiry_never_exceeds_its_absolute_limit()
    {
        var now = DateTimeOffset.UtcNow;

        var session = UserSession.Start(Guid.NewGuid(), now, expiresAtUtc: now.AddDays(14), absoluteExpiresAtUtc: now.AddDays(7));

        Assert.Equal(now.AddDays(7), session.ExpiresAtUtc);
        Assert.Equal(now.AddDays(7), session.AbsoluteExpiresAtUtc);
        Assert.Null(session.RevokedAtUtc);
    }

    [Fact]
    public void Register_command_never_prints_the_password()
    {
        var command = new RegisterUserCommand("a@example.com", "Secret-Password-1!", "Ann");

        Assert.DoesNotContain("Secret-Password-1!", command.ToString(), StringComparison.Ordinal);
    }
}
