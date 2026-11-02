using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace BusinessSuite.Web.Framework.RateLimiting;

/// <summary>Names of rate-limiting policies, used with <c>[EnableRateLimiting(...)]</c>.</summary>
public static class RateLimitingPolicies
{
    /// <summary>Throttles sign-in attempts per client IP address (password-guessing defence).</summary>
    public const string Login = "login";

    /// <summary>Throttles account creation per client IP address (mass sign-up and e-mail probing defence).</summary>
    public const string Registration = "registration";
}

/// <summary>
/// Rate-limit settings, bound from the <c>"RateLimiting"</c> section of appsettings.json.
/// </summary>
public sealed class RateLimitingSettings
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "RateLimiting";

    /// <summary>Sign-in POSTs allowed per IP address within <see cref="LoginWindowSeconds"/>.</summary>
    [Range(1, 1000)]
    public int LoginPermitLimit { get; set; } = 10;

    /// <summary>Length of the sign-in rate-limit window, in seconds.</summary>
    [Range(1, 3600)]
    public int LoginWindowSeconds { get; set; } = 60;

    /// <summary>Sign-up requests allowed per IP address within <see cref="RegistrationWindowSeconds"/>.</summary>
    [Range(1, 1000)]
    public int RegistrationPermitLimit { get; set; } = 5;

    /// <summary>Length of the sign-up rate-limit window, in seconds.</summary>
    [Range(1, 86400)]
    public int RegistrationWindowSeconds { get; set; } = 900;
}

/// <summary>Registers ASP.NET Core's built-in rate limiter.</summary>
/// <remarks>
/// Works together with account lockout: lockout protects one account, the rate limit protects
/// the sign-in endpoint from one client trying many accounts. Behind a reverse proxy, configure
/// forwarded headers so the real client IP is used (see docs/SECURITY.md).
/// </remarks>
public static class RateLimitingExtensions
{
    public static IServiceCollection AddRateLimitingPolicies(this IServiceCollection services)
    {
        services.AddOptions<RateLimitingSettings>()
            .BindConfiguration(RateLimitingSettings.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = OnRejectedAsync;

            options.AddPolicy(RateLimitingPolicies.Login, httpContext =>
            {
                var settings = httpContext.RequestServices.GetRequiredService<IOptions<RateLimitingSettings>>().Value;
                return PerClientAddress(httpContext, settings.LoginPermitLimit, settings.LoginWindowSeconds);
            });

            options.AddPolicy(RateLimitingPolicies.Registration, httpContext =>
            {
                var settings = httpContext.RequestServices.GetRequiredService<IOptions<RateLimitingSettings>>().Value;
                return PerClientAddress(httpContext, settings.RegistrationPermitLimit, settings.RegistrationWindowSeconds);
            });
        });

        return services;
    }

    private static RateLimitPartition<string> PerClientAddress(HttpContext httpContext, int permitLimit, int windowSeconds)
    {
        var clientAddress = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        return RateLimitPartition.GetFixedWindowLimiter(clientAddress, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permitLimit,
            Window = TimeSpan.FromSeconds(windowSeconds),
            QueueLimit = 0,
            AutoReplenishment = true,
        });
    }

    /// <summary>Logs the rejection and tells the client when to retry. The 429 body comes from the error pages.</summary>
    private static ValueTask OnRejectedAsync(OnRejectedContext context, CancellationToken cancellationToken)
    {
        var httpContext = context.HttpContext;

        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            httpContext.Response.Headers.RetryAfter =
                ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        }

        httpContext.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(RateLimitingExtensions).FullName!)
            .LogWarning("Rate limit exceeded: {Method} {Path} was rejected with 429.", httpContext.Request.Method, httpContext.Request.Path);

        return ValueTask.CompletedTask;
    }
}
