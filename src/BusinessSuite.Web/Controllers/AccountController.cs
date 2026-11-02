using BusinessSuite.Infrastructure.Identity;
using BusinessSuite.Web.Framework.Mvc;
using BusinessSuite.Web.Framework.RateLimiting;
using BusinessSuite.Web.Models.Account;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace BusinessSuite.Web.Controllers;

/// <summary>
/// Sign-in, sign-up, sign-out and access-denied pages, built on ASP.NET Core Identity.
/// </summary>
/// <remarks>
/// Security notes:
/// <list type="bullet">
/// <item>Every failure shows the same message, so attackers cannot discover which e-mails exist.</item>
/// <item>Failed passwords count towards account lockout (<c>lockoutOnFailure: true</c>).</item>
/// <item>Sign-in POSTs are rate-limited per IP address (<see cref="RateLimitingPolicies.Login"/>).</item>
/// <item>The return URL is only followed if it is local (no open redirects).</item>
/// <item>Anti-forgery tokens are validated globally for every POST.</item>
/// </list>
/// The sign-up page submits to the JSON API (<see cref="Api.AuthController.Register"/>). Password reset,
/// e-mail confirmation and two-factor sign-in arrive in Phase 2.
/// </remarks>
public sealed class AccountController(
    SignInManager<ApplicationUser> signInManager,
    UserManager<ApplicationUser> userManager,
    ILogger<AccountController> logger) : AppController
{
    internal const string InvalidSignInMessage = "Invalid e-mail address or password.";
    internal const string SignedUpMessage = "Your account has been created. Sign in to continue.";

    // GET /Account/Login
    [HttpGet]
    [AllowAnonymous]
    public IActionResult Login(string? returnUrl = null, bool signedUp = false)
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            return RedirectToLocal(returnUrl);
        }

        if (signedUp)
        {
            // Set by register.js after POST /api/auth/register succeeded.
            NotifySuccess(SignedUpMessage);
        }

        return View(new LoginViewModel { ReturnUrl = returnUrl });
    }

    // GET /Account/Register
    // The form posts JSON to POST /api/auth/register (wwwroot/js/register.js), which creates the
    // user, logs the outcome and handles errors; the browser then returns to the sign-in page.
    [HttpGet]
    [AllowAnonymous]
    public IActionResult Register([FromServices] IOptions<IdentityOptions> identityOptions)
    {
        ArgumentNullException.ThrowIfNull(identityOptions);

        if (User.Identity?.IsAuthenticated == true)
        {
            return RedirectToLocal(null);
        }

        return View(new RegisterViewModel { PasswordHint = DescribePasswordPolicy(identityOptions.Value.Password) });
    }

    // POST /Account/Login
    [HttpPost]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitingPolicies.Login)]
    public async Task<IActionResult> Login(LoginViewModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var user = await userManager.FindByEmailAsync(model.Email);
        if (user is null)
        {
            logger.LogInformation("Sign-in failed: no account matches the submitted e-mail address.");
            return InvalidSignIn(model);
        }

        var result = await signInManager.PasswordSignInAsync(user, model.Password, model.RememberMe, lockoutOnFailure: true);

        if (result.Succeeded)
        {
            logger.LogInformation("User {UserId} signed in.", user.Id);
            return RedirectToLocal(model.ReturnUrl);
        }

        if (result.IsLockedOut)
        {
            logger.LogWarning("User {UserId} is locked out after repeated failed sign-in attempts.", user.Id);
            return RedirectToAction(nameof(Lockout));
        }

        if (result.RequiresTwoFactor)
        {
            // Two-factor sign-in UI is planned for Phase 2. Until then, refuse safely.
            logger.LogWarning("User {UserId} requires two-factor sign-in, which is not available yet.", user.Id);
            return InvalidSignIn(model);
        }

        // Wrong password, deactivated account (NotAllowed) or unconfirmed e-mail: same message.
        logger.LogInformation("Sign-in failed for user {UserId} (not allowed: {IsNotAllowed}).", user.Id, result.IsNotAllowed);
        return InvalidSignIn(model);
    }

    // POST /Account/Logout
    [HttpPost]
    public async Task<IActionResult> Logout()
    {
        await signInManager.SignOutAsync();
        logger.LogInformation("User signed out.");
        return RedirectToAction(nameof(Login));
    }

    // GET /Account/AccessDenied
    [HttpGet]
    [AllowAnonymous]
    public IActionResult AccessDenied()
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        return View();
    }

    // GET /Account/Lockout
    [HttpGet]
    [AllowAnonymous]
    public IActionResult Lockout() => View();

    /// <summary>E.g. "At least 12 characters, with an upper-case letter, a lower-case letter, a digit and a symbol."</summary>
    internal static string DescribePasswordPolicy(PasswordOptions policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        var rules = new List<string>();
        if (policy.RequireUppercase)
        {
            rules.Add("an upper-case letter");
        }

        if (policy.RequireLowercase)
        {
            rules.Add("a lower-case letter");
        }

        if (policy.RequireDigit)
        {
            rules.Add("a digit");
        }

        if (policy.RequireNonAlphanumeric)
        {
            rules.Add("a symbol");
        }

        var length = $"At least {policy.RequiredLength} characters";
        return rules.Count switch
        {
            0 => length + ".",
            1 => $"{length}, with {rules[0]}.",
            _ => $"{length}, with {string.Join(", ", rules[..^1])} and {rules[^1]}.",
        };
    }

    private ViewResult InvalidSignIn(LoginViewModel model)
    {
        ModelState.AddModelError(string.Empty, InvalidSignInMessage);
        model.Password = string.Empty;
        return View(nameof(Login), model);
    }
}
