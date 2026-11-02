using System.Security.Claims;
using BusinessSuite.Application.Common.Security;
using BusinessSuite.Infrastructure.Identity;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace BusinessSuite.Web.Framework.Api;

/// <summary>
/// Fails bearer authentication for access tokens that must no longer work, so revocation is immediate
/// instead of waiting for the token to expire:
/// <list type="bullet">
/// <item>the API session was logged out, signed out everywhere, or reached its (absolute) expiry;</item>
/// <item>the user was deactivated;</item>
/// <item>the user's security stamp changed (password change, "sign out everywhere").</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// Hooked into the bearer handler (<see cref="BearerTokenEvents.OnMessageReceived"/>) so that a revoked
/// token is "not authenticated" (401), exactly like a missing or expired one.
/// </para>
/// <para>
/// Cost: one indexed query per API request (the handler caches its result for the request).
/// Tokens the protector cannot read are left to the handler, which rejects them itself.
/// </para>
/// </remarks>
internal static class ApiSessionValidator
{
    private const string BearerPrefix = "Bearer ";

    public static async Task ValidateAsync(MessageReceivedContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var header = context.Request.Headers.Authorization.ToString();
        if (!header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return; // no token: the handler reports "no result" and authorization challenges
        }

        var ticket = context.Options.BearerTokenProtector.Unprotect(header[BearerPrefix.Length..].Trim());
        if (ticket is null)
        {
            return; // forged or corrupted: the handler fails it
        }

        var services = context.HttpContext.RequestServices;
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(ApiSessionValidator).FullName!);
        var principal = ticket.Principal;

        if (!Guid.TryParse(principal.FindFirstValue(AppClaimTypes.SessionId), out var sessionId)
            || !Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
        {
            logger.LogInformation("Access token rejected: it carries no API session (issued before sessions existed). The client must sign in again.");
            context.Fail("The access token has no session.");
            return;
        }

        var stampClaimType = services.GetRequiredService<IOptions<IdentityOptions>>().Value.ClaimsIdentity.SecurityStampClaimType;
        var securityStamp = principal.FindFirstValue(stampClaimType);

        var sessions = services.GetRequiredService<IUserSessionStore>();
        if (!await sessions.IsActiveAsync(sessionId, userId, securityStamp, context.HttpContext.RequestAborted))
        {
            logger.LogInformation(
                "Access token rejected for user {UserId}: API session {SessionId} has ended (logged out, signed out everywhere, expired, password changed or account deactivated).",
                userId, sessionId);
            context.Fail("The API session has ended.");
        }
    }
}
