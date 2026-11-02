using System.Security.Claims;
using BusinessSuite.Application.Common.Abstractions;
using BusinessSuite.Application.Common.Security;
using BusinessSuite.Application.Features.Accounts;
using BusinessSuite.Infrastructure.Identity;
using BusinessSuite.Web.Framework.Api;
using BusinessSuite.Web.Framework.RateLimiting;
using BusinessSuite.Web.Framework.Security;
using BusinessSuite.Web.Models.Api.Auth;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace BusinessSuite.Web.Controllers.Api;

/// <summary>
/// JSON API for sign-up, sign-in, token renewal, sign-out and the current user's permissions, built on
/// ASP.NET Core Identity bearer tokens. Version 1: <c>/api/v1/auth</c> (the old <c>/api/auth</c> paths still work).
/// </summary>
/// <remarks>
/// <para>
/// Login returns <c>{ tokenType, accessToken, expiresIn, refreshToken }</c>. Send the access token as
/// <c>Authorization: Bearer &lt;accessToken&gt;</c>; when it expires, exchange the refresh token at
/// <c>/api/v1/auth/refresh</c>. See docs/API.md and docs/AUTHORIZATION.md.
/// </para>
/// <para>
/// Each login is recorded as a <see cref="UserSession"/> whose id is inside both tokens. Logout revokes
/// the session; <see cref="ApiSessionValidator"/> then rejects its access token and refresh refuses its
/// refresh token, so the tokens are effectively removed although they are stateless.
/// </para>
/// Security notes (same rules as <see cref="AccountController"/>):
/// <list type="bullet">
/// <item>Every sign-in failure returns the same 401 response, so callers cannot discover which e-mails exist.</item>
/// <item>Failed passwords count towards account lockout; deactivated users cannot sign in or refresh.</item>
/// <item>Sign-in and sign-up are rate-limited per IP address.</item>
/// <item>Every request re-checks the session, the security stamp and that the account is active.</item>
/// <item>Passwords, tokens and e-mail addresses are never logged; user ids are.</item>
/// </list>
/// </remarks>
[Route("api/v1/auth")]
public sealed class AuthController(
    SignInManager<ApplicationUser> signInManager,
    UserManager<ApplicationUser> userManager,
    IUserRegistrationService registration,
    IUserSessionStore sessions,
    IOptionsMonitor<BearerTokenOptions> bearerTokenOptions,
    IOptions<AuthenticationSettings> authenticationSettings,
    TimeProvider timeProvider,
    ILogger<AuthController> logger) : AppApiController
{
    internal const string InvalidCredentialsCode = "Auth.InvalidCredentials";
    internal const string InvalidCredentialsMessage = "Invalid e-mail address or password.";
    internal const string LockedOutCode = "Auth.LockedOut";
    internal const string InvalidRefreshTokenCode = "Auth.InvalidRefreshToken";
    internal const string InvalidSessionCode = "Auth.InvalidSession";
    internal const string OrganizationAccessDeniedCode = "Organization.AccessDenied";

    // POST /api/v1/auth/register
    /// <summary>Creates an account. Does not sign in: call <c>/api/v1/auth/login</c> afterwards.</summary>
    /// <response code="201">The account was created.</response>
    /// <response code="400">Invalid input (<c>Request.ValidationFailed</c>) or the password breaks the policy (<c>Account.WeakPassword</c>).</response>
    /// <response code="409">An account with this e-mail address already exists (<c>Account.DuplicateEmail</c>).</response>
    /// <response code="429">Too many sign-ups from this IP address (<c>Request.RateLimited</c>).</response>
    [HttpPost("register")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitingPolicies.Registration)]
    [ProducesResponseType<UserResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests, ProblemContentType)]
    public async Task<IActionResult> Register(RegisterRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await registration.RegisterAsync(request.ToCommand(), cancellationToken);
        if (result.IsFailure)
        {
            return ErrorResult(result.Error);
        }

        var user = result.Value;
        return StatusCode(StatusCodes.Status201Created, new UserResponse(user.UserId, user.Email, user.DisplayName));
    }

    // POST /api/v1/auth/login
    /// <summary>Signs in and returns an access token (send as <c>Authorization: Bearer</c>) and a refresh token.</summary>
    /// <response code="200">Signed in. <c>expiresIn</c> is the access-token lifetime in seconds.</response>
    /// <response code="400">Invalid input (<c>Request.ValidationFailed</c>).</response>
    /// <response code="401">Wrong e-mail or password (<c>Auth.InvalidCredentials</c>) or account locked (<c>Auth.LockedOut</c>).</response>
    /// <response code="429">Too many sign-in attempts from this IP address (<c>Request.RateLimited</c>).</response>
    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitingPolicies.Login)]
    [ProducesResponseType<AccessTokenResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests, ProblemContentType)]
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var user = await userManager.FindByEmailAsync(request.Email);
        if (user is null)
        {
            logger.LogInformation("API sign-in failed: no account matches the submitted e-mail address.");
            return InvalidCredentials();
        }

        // Checks the password, lockout and "can sign in" (deactivated users) without issuing anything yet.
        var result = await signInManager.CheckPasswordSignInAsync(user, request.Password, lockoutOnFailure: true);

        if (result.IsLockedOut)
        {
            logger.LogWarning("User {UserId} is locked out after repeated failed sign-in attempts (API).", user.Id);
            return ProblemResult(StatusCodes.Status401Unauthorized, LockedOutCode,
                "This account is temporarily locked after too many failed sign-in attempts. Try again later.");
        }

        if (!result.Succeeded)
        {
            // Wrong password, deactivated account (NotAllowed) or unconfirmed e-mail: same response.
            logger.LogInformation("API sign-in failed for user {UserId} (not allowed: {IsNotAllowed}).", user.Id, result.IsNotAllowed);
            return InvalidCredentials();
        }

        if (await userManager.GetTwoFactorEnabledAsync(user))
        {
            // Two-factor sign-in is planned for Phase 2. Until then, refuse safely.
            logger.LogWarning("User {UserId} requires two-factor sign-in, which the API does not support yet.", user.Id);
            return InvalidCredentials();
        }

        // Record the sign-in so it can expire and be revoked, then put the session id inside both tokens.
        var now = timeProvider.GetUtcNow();
        var sessionId = await sessions.StartAsync(
            user.Id,
            RefreshTokenExpiry(),
            now.AddDays(authenticationSettings.Value.ApiSessionMaxDays),
            cancellationToken);
        var principal = await CreatePrincipalAsync(user, sessionId);

        logger.LogInformation("User {UserId} signed in through the API (session {SessionId}).", user.Id, sessionId);
        return SignIn(principal, BearerScheme); // the bearer handler writes the token response
    }

    // POST /api/v1/auth/refresh
    /// <summary>Exchanges a refresh token for a new access token and refresh token.</summary>
    /// <response code="200">New tokens, same shape as login.</response>
    /// <response code="400">Invalid input (<c>Request.ValidationFailed</c>).</response>
    /// <response code="401">The refresh token is invalid or expired, the session ended, or the account changed (<c>Auth.InvalidRefreshToken</c>).</response>
    [HttpPost("refresh")]
    [AllowAnonymous]
    [ProducesResponseType<AccessTokenResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, ProblemContentType)]
    public async Task<IActionResult> Refresh(RefreshRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var protector = bearerTokenOptions.Get(BearerScheme).RefreshTokenProtector;
        var ticket = protector.Unprotect(request.RefreshToken); // null when tampered with or not a refresh token

        if (ticket?.Properties.ExpiresUtc is not { } expiresUtc || timeProvider.GetUtcNow() >= expiresUtc)
        {
            logger.LogInformation("Token refresh rejected: the refresh token is invalid or expired.");
            return InvalidRefreshToken();
        }

        // Null when the user no longer exists or the security stamp changed (password change, sign out everywhere).
        var user = await signInManager.ValidateSecurityStampAsync(ticket.Principal);
        if (user is null)
        {
            logger.LogInformation("Token refresh rejected: the user no longer exists or their security stamp changed.");
            return InvalidRefreshToken();
        }

        if (!await signInManager.CanSignInAsync(user) || await userManager.IsLockedOutAsync(user))
        {
            logger.LogWarning("Token refresh rejected for user {UserId}: the account is deactivated or locked out.", user.Id);
            return InvalidRefreshToken();
        }

        // The session must still be active (not logged out, not past its absolute limit); refreshing extends it.
        if (!TryReadSession(ticket.Principal, out _, out var sessionId)
            || !await sessions.ExtendAsync(sessionId, user.Id, RefreshTokenExpiry(), cancellationToken))
        {
            logger.LogInformation("Token refresh rejected for user {UserId}: the API session has ended (logged out or expired).", user.Id);
            return InvalidRefreshToken();
        }

        var principal = await CreatePrincipalAsync(user, sessionId);
        logger.LogInformation("Tokens refreshed for user {UserId} (session {SessionId}).", user.Id, sessionId);
        return SignIn(principal, BearerScheme); // writes a new token response
    }

    // POST /api/v1/auth/reset-password
    /// <summary>
    /// Sets a new password with the code from a password-reset or account set-up e-mail
    /// (sent by <c>POST /api/v1/users/{id}/reset-password</c> or when an administrator creates the user).
    /// </summary>
    /// <remarks>
    /// Success confirms the e-mail address, lifts any lockout and signs the user out everywhere. Then call
    /// <c>/api/v1/auth/login</c> with the new password.
    /// </remarks>
    /// <response code="204">The password was changed.</response>
    /// <response code="400">Invalid input (<c>Request.ValidationFailed</c>), unknown e-mail or wrong/used/expired code (<c>Account.InvalidResetCode</c>, deliberately the same), or the password breaks the policy (<c>Account.WeakPassword</c>).</response>
    /// <response code="429">Too many attempts from this IP address (<c>Request.RateLimited</c>).</response>
    [HttpPost("reset-password")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitingPolicies.Login)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests, ProblemContentType)]
    public async Task<IActionResult> ResetPassword(
        ResetPasswordRequest request,
        [FromServices] IPasswordResetService passwordReset,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(passwordReset);

        var result = await passwordReset.ResetPasswordAsync(request.ToCommand(), cancellationToken);
        return result.IsSuccess ? NoContent() : ErrorResult(result.Error);
    }

    // POST /api/v1/auth/logout
    /// <summary>
    /// Signs out this device: ends the API session, so its access token and refresh token stop working
    /// immediately. Other devices stay signed in.
    /// </summary>
    /// <response code="204">Signed out.</response>
    /// <response code="401">No valid access token, or the session has already ended (<c>Auth.Unauthorized</c>).</response>
    [HttpPost("logout")]
    [Authorize(AuthenticationSchemes = BearerScheme)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, ProblemContentType)]
    public async Task<IActionResult> Logout(CancellationToken cancellationToken)
    {
        if (!TryReadSession(User, out var userId, out var sessionId))
        {
            // Unreachable with current tokens (ApiSessionValidator rejects tokens without a session); defensive.
            logger.LogWarning("API logout for user {UserId} refused: the access token carries no session id.", userId);
            return ProblemResult(StatusCodes.Status401Unauthorized, InvalidSessionCode,
                "This access token cannot be signed out. Sign in again to get a new one.");
        }

        if (await sessions.RevokeAsync(sessionId, userId, cancellationToken))
        {
            logger.LogInformation("User {UserId} signed out of the API (session {SessionId} revoked).", userId, sessionId);
        }
        else
        {
            // Normally unreachable (ApiSessionValidator rejects ended sessions); kept idempotent for races.
            logger.LogInformation("API logout for user {UserId}: session {SessionId} had already ended.", userId, sessionId);
        }

        return NoContent();
    }

    // POST /api/v1/auth/logout-all
    /// <summary>
    /// Signs out everywhere: revokes every API session of the user and changes their security stamp, so all
    /// access and refresh tokens stop working immediately and website sessions end within 5 minutes.
    /// </summary>
    /// <response code="204">Signed out on every device.</response>
    /// <response code="401">No valid access token (<c>Auth.Unauthorized</c>).</response>
    [HttpPost("logout-all")]
    [Authorize(AuthenticationSchemes = BearerScheme)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, ProblemContentType)]
    public async Task<IActionResult> LogoutEverywhere(CancellationToken cancellationToken)
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null)
        {
            return Unauthorized();
        }

        var revoked = await sessions.RevokeAllAsync(user.Id, cancellationToken);

        var stamp = await userManager.UpdateSecurityStampAsync(user);
        if (!stamp.Succeeded)
        {
            // The sessions are revoked already; report the failure so the client does not assume full success.
            throw new InvalidOperationException(
                $"Sign out everywhere for user {user.Id}: sessions revoked but the security stamp could not be changed ({string.Join(", ", stamp.Errors.Select(error => error.Code))}).");
        }

        logger.LogInformation("User {UserId} signed out everywhere: {RevokedCount} API session(s) revoked and security stamp changed.", user.Id, revoked);
        return NoContent();
    }

    // GET /api/v1/auth/me
    /// <summary>
    /// Returns the signed-in user: the owner of the access token, or the user signed in on the website
    /// (sign-in cookie). Read-only, so accepting the cookie is safe.
    /// </summary>
    /// <response code="200">The signed-in user.</response>
    /// <response code="401">Not signed in: no valid access token and no sign-in cookie (<c>Auth.Unauthorized</c>).</response>
    [HttpGet("me")]
    [Authorize(AuthenticationSchemes = BearerOrCookieSchemes)]
    [ProducesResponseType<UserResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, ProblemContentType)]
    public IActionResult Me([FromServices] ICurrentUser currentUser)
    {
        ArgumentNullException.ThrowIfNull(currentUser);

        if (currentUser.UserId is not { } userId)
        {
            return Unauthorized();
        }

        return Ok(new UserResponse(userId, currentUser.UserName ?? string.Empty, currentUser.DisplayName));
    }

    // GET /api/v1/auth/me/permissions
    /// <summary>
    /// The current user's permissions: Super Admin flag, platform roles, the organization this request acts in
    /// (choose one with the <c>X-Organization-Id</c> header), the role there, every organization the user belongs
    /// to, and the resulting permission list. Clients use it to show or hide features; the server enforces the
    /// same permissions on every endpoint.
    /// </summary>
    /// <response code="200">The user's access for this request.</response>
    /// <response code="401">Not signed in (<c>Auth.Unauthorized</c>).</response>
    /// <response code="403">The <c>X-Organization-Id</c> organization is not one the user can act in (<c>Organization.AccessDenied</c>).</response>
    [HttpGet("me/permissions")]
    [Authorize(AuthenticationSchemes = BearerOrCookieSchemes)]
    [ProducesResponseType<UserPermissionsResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, ProblemContentType)]
    public async Task<IActionResult> MyPermissions(
        [FromServices] CurrentAccessProvider accessProvider,
        [FromServices] ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(accessProvider);
        ArgumentNullException.ThrowIfNull(currentUser);

        // Already computed for this request by AccessClaimsTransformation; served from the request cache.
        var access = await accessProvider.GetAsync(User, cancellationToken);
        if (access is null)
        {
            return Unauthorized();
        }

        if (access.Selection == OrganizationSelection.Denied)
        {
            return ProblemResult(StatusCodes.Status403Forbidden, OrganizationAccessDeniedCode,
                $"You cannot act in the organization given in the {CurrentAccessProvider.OrganizationHeader} header: you are not an active member, or it does not exist or is suspended.");
        }

        return Ok(UserPermissionsResponse.From(access, currentUser.UserName ?? string.Empty, currentUser.DisplayName));
    }

    /// <summary>The user's claims plus the session id and "signed in with a password" (as Identity's own sign-in adds).</summary>
    private async Task<ClaimsPrincipal> CreatePrincipalAsync(ApplicationUser user, Guid sessionId)
    {
        var principal = await signInManager.CreateUserPrincipalAsync(user);
        var identity = (ClaimsIdentity)principal.Identity!;
        identity.AddClaim(new Claim(AppClaimTypes.SessionId, sessionId.ToString()));
        identity.AddClaim(new Claim("amr", "pwd"));
        return principal;
    }

    private DateTimeOffset RefreshTokenExpiry() =>
        timeProvider.GetUtcNow() + bearerTokenOptions.Get(BearerScheme).RefreshTokenExpiration;

    private static bool TryReadSession(ClaimsPrincipal principal, out Guid userId, out Guid sessionId)
    {
        _ = Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out userId);
        return Guid.TryParse(principal.FindFirstValue(AppClaimTypes.SessionId), out sessionId) && userId != Guid.Empty;
    }

    private ObjectResult InvalidCredentials() =>
        ProblemResult(StatusCodes.Status401Unauthorized, InvalidCredentialsCode, InvalidCredentialsMessage);

    private ObjectResult InvalidRefreshToken() =>
        ProblemResult(StatusCodes.Status401Unauthorized, InvalidRefreshTokenCode, "The refresh token is invalid or has expired. Sign in again.");
}
