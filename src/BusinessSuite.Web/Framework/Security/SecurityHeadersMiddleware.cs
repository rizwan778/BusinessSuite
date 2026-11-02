using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace BusinessSuite.Web.Framework.Security;

/// <summary>
/// Security header settings, bound from the <c>"SecurityHeaders"</c> section of appsettings.json.
/// </summary>
public sealed class SecurityHeadersOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "SecurityHeaders";

    /// <summary>
    /// Strict default Content-Security-Policy: only files served by this application may run or load.
    /// Consequence for developers: no inline <c>&lt;script&gt;</c> blocks, no inline <c>style="..."</c>
    /// attributes and no CDN links. Put JavaScript in <c>wwwroot/js</c> and CSS in <c>wwwroot/css</c>.
    /// </summary>
    public const string DefaultContentSecurityPolicy =
        "default-src 'self'; base-uri 'self'; object-src 'none'; frame-ancestors 'none'; " +
        "form-action 'self'; img-src 'self' data:; font-src 'self'; style-src 'self'; " +
        "script-src 'self'; connect-src 'self'";

    /// <summary>The Content-Security-Policy header value.</summary>
    [Required]
    public string ContentSecurityPolicy { get; set; } = DefaultContentSecurityPolicy;
}

/// <summary>
/// Adds browser security headers to every response (pages, files and error pages).
/// </summary>
/// <remarks>
/// Headers already set by an endpoint are left untouched, so a specific page can relax a
/// header deliberately if it ever needs to.
/// </remarks>
public sealed class SecurityHeadersMiddleware(RequestDelegate next, IOptions<SecurityHeadersOptions> options)
{
    public Task InvokeAsync(HttpContext context)
    {
        var contentSecurityPolicy = options.Value.ContentSecurityPolicy;

        // OnStarting runs just before the response headers are sent, after error handling.
        context.Response.OnStarting(() =>
        {
            ApplyHeaders(context.Response.Headers, contentSecurityPolicy);
            return Task.CompletedTask;
        });

        return next(context);
    }

    internal static void ApplyHeaders(IHeaderDictionary headers, string contentSecurityPolicy)
    {
        SetIfMissing(headers, "Content-Security-Policy", contentSecurityPolicy);
        SetIfMissing(headers, "X-Content-Type-Options", "nosniff");
        SetIfMissing(headers, "X-Frame-Options", "DENY");
        SetIfMissing(headers, "Referrer-Policy", "strict-origin-when-cross-origin");
        SetIfMissing(headers, "Cross-Origin-Opener-Policy", "same-origin");
        SetIfMissing(headers, "Permissions-Policy",
            "accelerometer=(), camera=(), geolocation=(), gyroscope=(), magnetometer=(), microphone=(), payment=(), usb=()");
    }

    private static void SetIfMissing(IHeaderDictionary headers, string name, string value)
    {
        if (!headers.ContainsKey(name))
        {
            headers[name] = value;
        }
    }
}

/// <summary>Registration helpers for <see cref="SecurityHeadersMiddleware"/>.</summary>
public static class SecurityHeadersExtensions
{
    public static IServiceCollection AddSecurityHeaders(this IServiceCollection services)
    {
        services.AddOptions<SecurityHeadersOptions>()
            .BindConfiguration(SecurityHeadersOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // HTTP Strict Transport Security: browsers remember to use HTTPS only (production only).
        services.AddHsts(options =>
        {
            options.MaxAge = TimeSpan.FromDays(180);
            options.IncludeSubDomains = false; // enable once every sub-domain serves HTTPS
            options.Preload = false;
        });

        return services;
    }

    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.UseMiddleware<SecurityHeadersMiddleware>();
}
