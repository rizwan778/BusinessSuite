namespace BusinessSuite.Application.Features.Dashboard;

/// <summary>
/// Data shown at the top of the dashboard (a DTO: plain data, no behaviour).
/// </summary>
/// <param name="GreetingName">Name used to greet the user.</param>
/// <param name="ProductName">Configured product name.</param>
/// <param name="UtcNow">Current instant in UTC.</param>
/// <param name="LocalNow">Same instant converted to the display time zone.</param>
/// <param name="TimeZoneId">Display time zone id, e.g. "Asia/Karachi".</param>
public sealed record DashboardOverview(
    string GreetingName,
    string ProductName,
    DateTimeOffset UtcNow,
    DateTimeOffset LocalNow,
    string TimeZoneId);
