using BusinessSuite.Application.Common.Options;
using BusinessSuite.Application.Features.Dashboard;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BusinessSuite.Application;

/// <summary>
/// Registers everything the Application layer provides. Called once from <c>Program.cs</c>.
/// </summary>
public static class ApplicationServiceCollectionExtensions
{
    /// <summary>
    /// Adds application options, the system clock and all feature services.
    /// </summary>
    /// <remarks>
    /// When you add a feature, register its service at the bottom of this method,
    /// one line per service, grouped by module.
    /// </remarks>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<ApplicationOptions>()
            .BindConfiguration(ApplicationOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // One clock for the whole application. Tests replace it with a fixed clock.
        services.TryAddSingleton(TimeProvider.System);

        // ---- Feature services ---------------------------------------------------
        // Dashboard
        services.AddScoped<IDashboardService, DashboardService>();

        return services;
    }
}
