using BusinessSuite.Application.Common.Abstractions;
using BusinessSuite.Application.Common.Options;
using BusinessSuite.Application.Features.Dashboard;
using BusinessSuite.Tests.Unit.TestDoubles;
using Microsoft.Extensions.Options;

namespace BusinessSuite.Tests.Unit.Application.Dashboard;

public sealed class DashboardServiceTests
{
    private static readonly DateTimeOffset FixedUtcNow = new(2026, 1, 15, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Greets_the_user_by_display_name()
    {
        var service = CreateService(FakeCurrentUser.SignedIn("sara@example.com", "Sara"));

        var overview = await service.GetOverviewAsync();

        Assert.Equal("Sara", overview.GreetingName);
    }

    [Fact]
    public async Task Falls_back_to_the_user_name_when_there_is_no_display_name()
    {
        var service = CreateService(FakeCurrentUser.SignedIn("sara@example.com", displayName: "  "));

        var overview = await service.GetOverviewAsync();

        Assert.Equal("sara@example.com", overview.GreetingName);
    }

    [Fact]
    public async Task Uses_a_neutral_greeting_for_unknown_users()
    {
        var service = CreateService(FakeCurrentUser.Anonymous());

        var overview = await service.GetOverviewAsync();

        Assert.Equal("there", overview.GreetingName);
    }

    [Fact]
    public async Task Converts_utc_time_to_the_configured_time_zone()
    {
        // Pakistan Standard Time is UTC+05:00 all year (no daylight saving).
        var service = CreateService(FakeCurrentUser.Anonymous(), timeZone: "Asia/Karachi");

        var overview = await service.GetOverviewAsync();

        Assert.Equal(FixedUtcNow, overview.UtcNow);
        Assert.Equal(TimeSpan.FromHours(5), overview.LocalNow.Offset);
        Assert.Equal(15, overview.LocalNow.Hour);
        Assert.Equal(FixedUtcNow, overview.LocalNow.ToUniversalTime());
    }

    [Fact]
    public async Task Honours_cancellation()
    {
        var service = CreateService(FakeCurrentUser.Anonymous());
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetOverviewAsync(cancellation.Token));
    }

    private static DashboardService CreateService(ICurrentUser user, string timeZone = "UTC")
    {
        var options = Options.Create(new ApplicationOptions { ProductName = "Test Suite", DefaultTimeZone = timeZone });
        return new DashboardService(user, new FixedTimeProvider(FixedUtcNow), options);
    }
}
