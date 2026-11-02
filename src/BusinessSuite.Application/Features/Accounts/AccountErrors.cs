using BusinessSuite.Application.Common.Results;

namespace BusinessSuite.Application.Features.Accounts;

/// <summary>
/// Errors returned by account operations. The codes are part of the API contract: do not rename them.
/// </summary>
public static class AccountErrors
{
    /// <summary>Code of <see cref="InvalidEmail"/>.</summary>
    public const string InvalidEmailCode = "Account.InvalidEmail";

    /// <summary>Code of <see cref="WeakPassword"/>.</summary>
    public const string WeakPasswordCode = "Account.WeakPassword";

    /// <summary>Code of <see cref="RegistrationRejected"/>.</summary>
    public const string RegistrationRejectedCode = "Account.RegistrationRejected";

    /// <summary>An account with this e-mail address already exists.</summary>
    public static readonly Error DuplicateEmail =
        Error.Conflict("Account.DuplicateEmail", "An account with this e-mail address already exists.");

    /// <summary>
    /// The password-reset code is wrong, used or expired, or the e-mail address is unknown (deliberately the same,
    /// so the endpoint cannot be used to discover accounts).
    /// </summary>
    public static readonly Error InvalidResetCode =
        Error.Validation("Account.InvalidResetCode", "The e-mail address or reset code is not valid, or the code has expired. Ask for a new code.");

    /// <summary>The e-mail address cannot be used as a sign-in name.</summary>
    public static Error InvalidEmail(string description) => Error.Validation(InvalidEmailCode, description);

    /// <summary>The password breaks the password policy; <paramref name="description"/> lists every rule broken.</summary>
    public static Error WeakPassword(string description) => Error.Validation(WeakPasswordCode, description);

    /// <summary>Any other reason the account was refused.</summary>
    public static Error RegistrationRejected(string description) => Error.Validation(RegistrationRejectedCode, description);
}
