using BusinessSuite.Application.Common.Abstractions;
using BusinessSuite.Web.Framework.Api;
using BusinessSuite.Web.Framework.Diagnostics;
using BusinessSuite.Web.Framework.Navigation;
using BusinessSuite.Web.Framework.RateLimiting;
using BusinessSuite.Web.Framework.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;

namespace BusinessSuite.Web.Framework.Hosting;

/// <summary>
/// Registers everything the Web layer provides. Called once from <c>Program.cs</c>.
/// </summary>
public static class WebServiceCollectionExtensions
{
    /// <summary>
    /// Adds MVC, the current-user accessor, cookies, the JSON API (bearer tokens, problem details),
    /// authorization, rate limiting, security headers and data protection.
    /// </summary>
    public static IServiceCollection AddWebPresentation(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, HttpContextCurrentUser>();
        services.AddScoped<IRequestInfo, HttpRequestInfo>();
        services.AddScoped<NavigationAuthorizer>();

        services.AddControllersWithViews(options =>
        {
            // CSRF protection: every POST/PUT/PATCH/DELETE must carry a valid anti-forgery token.
            // Forms built with <form asp-action="..."> include the token automatically.
            options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute());
        });

        services.AddSecureCookies();
        services.AddApi();
        services.AddApiDocumentation();      // OpenAPI document; only mapped in Development (Program.cs)
        services.AddAuthorizationPolicies();
        services.AddRateLimitingPolicies();
        services.AddSecurityHeaders();

        // Keys that encrypt cookies. One application name keeps keys stable across deployments.
        // Production with several servers: persist keys to shared storage (docs/SECURITY.md).
        services.AddDataProtection().SetApplicationName("BusinessSuite");

        return services;
    }
}
