using System.Security.Claims;
using System.Text.Encodings.Web;
using BusinessSuite.Application.Common.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BusinessSuite.Tests.Integration.Support;

/// <summary>
/// Test-only authentication: a request with an <see cref="UserIdHeader"/> header is treated as
/// signed in. Registered only by <see cref="BusinessSuiteWebApplicationFactory"/>; it never exists
/// in the real application.
/// </summary>
public sealed class TestAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";
    public const string SelectorSchemeName = "TestOrIdentity";
    public const string UserIdHeader = "X-Test-User-Id";
    public const string UserNameHeader = "X-Test-User-Name";
    public const string DisplayNameHeader = "X-Test-Display-Name";
    public const string RolesHeader = "X-Test-Roles";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var userId = Request.Headers[UserIdHeader].ToString();
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId),
            new(ClaimTypes.Name, Request.Headers[UserNameHeader].ToString() is { Length: > 0 } name ? name : "test.user@example.com"),
        };

        var displayName = Request.Headers[DisplayNameHeader].ToString();
        if (!string.IsNullOrWhiteSpace(displayName))
        {
            claims.Add(new Claim(AppClaimTypes.DisplayName, displayName));
        }

        var roles = Request.Headers[RolesHeader].ToString();
        foreach (var role in roles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }
}
