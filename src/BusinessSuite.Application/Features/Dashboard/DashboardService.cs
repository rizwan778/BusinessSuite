using BusinessSuite.Application.Common.Abstractions;
using BusinessSuite.Application.Common.Options;
using Microsoft.Extensions.Options;

namespace BusinessSuite.Application.Features.Dashboard;

/// <summary>
/// Default implementation of <see cref="IDashboardService"/>.
/// </summary>
/// <remarks>
/// A reference example of an application service:
/// dependencies arrive through the constructor (dependency injection), time comes from
/// <see cref="TimeProvider"/> (so tests can freeze the clock), and the result is a DTO.
/// </remarks>
internal sealed class DashboardService(
    ICurrentUser currentUser,
    TimeProvider timeProvider,
    IOptions<ApplicationOptions> options) : IDashboardService
{
    private const string FallbackGreetingName = "there";

    /// <inheritdoc />
    public Task<DashboardOverview> GetOverviewAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var settings = options.Value;
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(settings.DefaultTimeZone);
        var utcNow = timeProvider.GetUtcNow();
        var localNow = TimeZoneInfo.ConvertTime(utcNow, timeZone);

        var overview = new DashboardOverview(
            GreetingName: ChooseGreetingName(currentUser),
            ProductName: settings.ProductName,
            UtcNow: utcNow,
            LocalNow: localNow,
            TimeZoneId: timeZone.Id);

        return Task.FromResult(overview);
    }

    private static string ChooseGreetingName(ICurrentUser user)
    {
        if (!string.IsNullOrWhiteSpace(user.DisplayName))
        {
            return user.DisplayName.Trim();
        }

        return string.IsNullOrWhiteSpace(user.UserName) ? FallbackGreetingName : user.UserName.Trim();
    }
}
