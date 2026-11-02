using System.ComponentModel.DataAnnotations;
using BusinessSuite.Web.Framework.Api;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace BusinessSuite.Web.Framework.Security;

/// <summary>
/// Session settings, bound from the <c>"Authentication"</c> section of appsettings.json.
/// </summary>
public sealed class AuthenticationSettings
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Authentication";

    /// <summary>
    /// Minutes of inactivity before the user must sign in again (sliding expiration).
    /// Default 8 hours: one working day.
    /// </summary>
    [Range(5, 1440)]
    public int SessionTimeoutMinutes { get; set; } = 480;

    /// <summary>
    /// Lifetime of an API access token, in minutes. Short, because an access token stays valid until it
    /// expires even if the account is deactivated; clients renew it with the refresh token.
    /// </summary>
    [Range(5, 1440)]
    public int ApiAccessTokenMinutes { get; set; } = 60;

    /// <summary>
    /// Lifetime of an API refresh token, in days (sliding: each refresh starts a new period).
    /// Unused for this long, the session ends and the client must sign in again.
    /// </summary>
    [Range(1, 90)]
    public int ApiRefreshTokenDays { get; set; } = 14;

    /// <summary>
    /// Absolute lifetime of an API session, in days: however often it is refreshed, the user must sign in
    /// again this long after logging in.
    /// </summary>
    [Range(1, 365)]
    public int ApiSessionMaxDays { get; set; } = 30;
}

/// <summary>Configures the cookies used for sign-in, anti-forgery and TempData.</summary>
public static class AuthenticationCookieExtensions
{
    /// <summary>Cookie names. The <c>__Host-</c> prefix makes browsers enforce Secure, Path=/ and no Domain.</summary>
    public const string AuthCookieName = "__Host-BusinessSuite.Auth";

    /// <summary>Anti-forgery (CSRF) cookie name.</summary>
    public const string AntiforgeryCookieName = "__Host-BusinessSuite.Antiforgery";

    /// <summary>TempData (one-time messages) cookie name.</summary>
    public const string TempDataCookieName = "__Host-BusinessSuite.TempData";

    public static IServiceCollection AddSecureCookies(this IServiceCollection services)
    {
        services.AddOptions<AuthenticationSettings>()
            .BindConfiguration(AuthenticationSettings.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Sign-in cookie issued by ASP.NET Core Identity.
        services.ConfigureApplicationCookie(options =>
        {
            options.Cookie.Name = AuthCookieName;
            options.Cookie.HttpOnly = true;                       // not readable by JavaScript
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always; // HTTPS only
            options.Cookie.SameSite = SameSiteMode.Lax;           // not sent on cross-site POSTs
            options.SlidingExpiration = true;
            options.LoginPath = "/Account/Login";
            options.LogoutPath = "/Account/Logout";
            options.AccessDeniedPath = "/Account/AccessDenied";

            // API clients get a 401/403 status (rendered as problem details), not a redirect to an HTML page.
            var redirectToLogin = options.Events.OnRedirectToLogin;
            var redirectToAccessDenied = options.Events.OnRedirectToAccessDenied;
            options.Events.OnRedirectToLogin = context =>
                RedirectOrStatusCode(context, StatusCodes.Status401Unauthorized, redirectToLogin);
            options.Events.OnRedirectToAccessDenied = context =>
                RedirectOrStatusCode(context, StatusCodes.Status403Forbidden, redirectToAccessDenied);
        });

        // Session length comes from configuration, read when the options are first used.
        services.AddOptions<CookieAuthenticationOptions>(IdentityConstants.ApplicationScheme)
            .Configure<IOptions<AuthenticationSettings>>((cookie, settings) =>
                cookie.ExpireTimeSpan = TimeSpan.FromMinutes(settings.Value.SessionTimeoutMinutes));

        // Anti-forgery tokens protect every POST form (see AutoValidateAntiforgeryTokenAttribute).
        services.AddAntiforgery(options =>
        {
            options.Cookie.Name = AntiforgeryCookieName;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.HeaderName = "X-CSRF-TOKEN"; // for future AJAX calls

            // Antiforgery would otherwise add "X-Frame-Options: SAMEORIGIN" to every form page.
            // SecurityHeadersMiddleware already sends the stricter "DENY" (and CSP frame-ancestors 'none').
            options.SuppressXFrameOptionsHeader = true;
        });

        // TempData carries one-time alerts across a redirect ("Product saved").
        services.Configure<CookieTempDataProviderOptions>(options =>
        {
            options.Cookie.Name = TempDataCookieName;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.IsEssential = true;
        });

        return services;
    }

    private static Task RedirectOrStatusCode(
        RedirectContext<CookieAuthenticationOptions> context,
        int statusCode,
        Func<RedirectContext<CookieAuthenticationOptions>, Task> redirect)
    {
        if (context.Request.IsApiRequest())
        {
            context.Response.StatusCode = statusCode;
            return Task.CompletedTask;
        }

        return redirect(context);
    }
}
