using BusinessSuite.Application.Features.Dashboard;
using BusinessSuite.Web.Framework.Navigation;

namespace BusinessSuite.Web.Models.Dashboard;

/// <summary>
/// Everything the dashboard view needs, already formatted-ready. Views receive view models,
/// never EF Core entities.
/// </summary>
public sealed class DashboardViewModel
{
    public required string GreetingName { get; init; }

    public required DateTimeOffset LocalNow { get; init; }

    public required string TimeZoneId { get; init; }

    public required string EnvironmentName { get; init; }

    public required string Version { get; init; }

    /// <summary>Modules the user may see (already filtered by authorization), for the roadmap overview.</summary>
    public required IReadOnlyList<NavigationSection> Modules { get; init; }

    /// <summary>Maps the Application-layer DTO plus host information to the view model.</summary>
    public static DashboardViewModel From(
        DashboardOverview overview,
        IReadOnlyList<NavigationSection> visibleModules,
        string environmentName,
        string version)
    {
        ArgumentNullException.ThrowIfNull(overview);
        ArgumentNullException.ThrowIfNull(visibleModules);

        return new DashboardViewModel
        {
            GreetingName = overview.GreetingName,
            LocalNow = overview.LocalNow,
            TimeZoneId = overview.TimeZoneId,
            EnvironmentName = environmentName,
            Version = version,
            Modules = visibleModules,
        };
    }
}
