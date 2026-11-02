namespace BusinessSuite.Application.Features.Accounts;

/// <summary>Input for <see cref="IUserRegistrationService.RegisterAsync"/>.</summary>
/// <param name="Email">E-mail address, also used as the sign-in name.</param>
/// <param name="Password">Plain-text password. Checked against the password policy; never logged or stored.</param>
/// <param name="DisplayName">Optional friendly name shown in the UI.</param>
public sealed record RegisterUserCommand(string Email, string Password, string? DisplayName)
{
    /// <summary>Leaves the password out (a record's default <c>ToString()</c> prints every property).</summary>
    public override string ToString() => $"{nameof(RegisterUserCommand)} {{ Email = {Email}, DisplayName = {DisplayName} }}";
}
