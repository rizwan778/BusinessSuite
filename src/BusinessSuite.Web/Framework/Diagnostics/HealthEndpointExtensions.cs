using BusinessSuite.Infrastructure.Diagnostics;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

namespace BusinessSuite.Web.Framework.Diagnostics;

/// <summary>
/// Maps the health-check endpoints used by monitoring tools and load balancers.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><c>/health/live</c>: "is the process running?" Runs no checks; always fast.</item>
/// <item><c>/health/ready</c>: "can it serve requests?" Runs checks tagged
/// <see cref="HealthCheckTags.Ready"/> (currently: SQL Server reachable).</item>
/// </list>
/// Responses contain only <c>Healthy</c>, <c>Degraded</c> or <c>Unhealthy</c>; no internal details.
/// </remarks>
public static class HealthEndpointExtensions
{
    /// <summary>Live endpoint path.</summary>
    public const string LivePath = "/health/live";

    /// <summary>Readiness endpoint path.</summary>
    public const string ReadyPath = "/health/ready";

    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHealthChecks(LivePath, new HealthCheckOptions { Predicate = _ => false })
            .AllowAnonymous();

        endpoints.MapHealthChecks(ReadyPath, new HealthCheckOptions
            {
                Predicate = check => check.Tags.Contains(HealthCheckTags.Ready),
            })
            .AllowAnonymous();

        return endpoints;
    }
}
